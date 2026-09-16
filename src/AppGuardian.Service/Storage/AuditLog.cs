using System.Text;
using System.Text.Json;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Storage;

/// <summary>
/// Append-only JSON-lines audit log with size-based rotation. SRS FR-807, §9.3.
/// </summary>
/// <remarks>
/// One line of JSON per entry rather than a JSON array, so appending is a single write with no
/// read-modify-write of the whole file, and a truncated final line costs one entry rather than the
/// whole log.
/// <para>
/// The service is the only writer (ADR-005). The agent and the dashboard send <c>system.audit</c>
/// over IPC. That keeps the file's admin-only-write ACL intact, which is what stops a user-privilege
/// process from forging or truncating entries — the repudiation mitigation in SRS §11.
/// </para>
/// </remarks>
public sealed class AuditLog
{
    private readonly string _path;
    private readonly ILogger<AuditLog> _log;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly TimeProvider _clock;

    public AuditLog(ILogger<AuditLog> log, string? path = null, TimeProvider? clock = null)
    {
        _log = log;
        _path = path ?? StoragePaths.AuditFile;
        _clock = clock ?? TimeProvider.System;
    }

    public string Path => _path;

    /// <summary>
    /// Appends one entry.
    /// </summary>
    /// <remarks>
    /// Never throws. An audit write failure must not fail the operation being audited — refusing to
    /// lock an app because the log is full would turn a logging problem into a protection problem.
    /// The failure is reported to the Event Log instead, and surfaces as a degraded status.
    /// </remarks>
    public async Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            RotateIfNeeded();

            var line = JsonSerializer.Serialize(entry, JsonPayload.Options) + Environment.NewLine;

            await File.AppendAllTextAsync(_path, line, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Audit entry could not be written: {Action} {Result}.", entry.Action, entry.Result);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Convenience overload for the common case.</summary>
    public Task AppendAsync(
        AuditActor actor,
        AuditAction action,
        AuditResult result,
        string? appId = null,
        string? ruleId = null,
        string detail = "",
        CancellationToken ct = default)
    {
        var entry = AuditEntry.Create(actor, action, result, appId, ruleId, detail);
        entry.Utc = _clock.GetUtcNow();

        return AppendAsync(entry, ct);
    }

    /// <summary>
    /// Reads the most recent entries, newest first. <c>system.getAuditLog</c>, FR-807.
    /// </summary>
    /// <remarks>
    /// Reads from the end and stops once <paramref name="limit"/> entries have been collected, so a
    /// 5 MB log does not have to be parsed to answer a request for the last 100 entries. Rotated
    /// files are consulted only when the current file is too short to satisfy the request.
    /// </remarks>
    public async Task<IReadOnlyList<AuditEntry>> ReadRecentAsync(
        int limit,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxReadLimit);

        var collected = new List<AuditEntry>(limit);

        // Current file first, then progressively older rotations.
        foreach (var file in FilesNewestFirst())
        {
            if (collected.Count >= limit)
            {
                break;
            }

            await ReadFileNewestFirstAsync(file, limit, since, collected, ct).ConfigureAwait(false);
        }

        return collected;
    }

    private async Task ReadFileNewestFirstAsync(
        string file,
        int limit,
        DateTimeOffset? since,
        List<AuditEntry> collected,
        CancellationToken ct)
    {
        if (!File.Exists(file))
        {
            return;
        }

        List<string> lines;

        try
        {
            // FileShare.ReadWrite so a concurrent append does not make this fail; a line torn by a
            // simultaneous write is discarded by the per-line parse below.
            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            using var reader = new StreamReader(stream, Encoding.UTF8);

            lines = new List<string>();

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                lines.Add(line);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Audit file {File} could not be read.", file);
            return;
        }

        for (var i = lines.Count - 1; i >= 0 && collected.Count < limit; i--)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AuditEntry? entry;

            try
            {
                entry = JsonSerializer.Deserialize<AuditEntry>(line, JsonPayload.Options);
            }
            catch (JsonException)
            {
                // A single malformed line — a torn write, or a hand-edit. Skipped rather than
                // failing the whole read, which would make one bad line hide the entire history.
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            if (since is not null && entry.Utc < since)
            {
                // Lines are chronological within a file, so everything earlier is also too old.
                return;
            }

            collected.Add(entry);
        }
    }

    /// <summary>
    /// Rotates at the size threshold, keeping a bounded number of files. SRS §9.3.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose: an unbounded audit log on a machine with a busy app would grow without
    /// limit, and NFR-Pr wants a small install footprint. The cost is that history is finite, which is
    /// acceptable for a local utility log and is stated in the dashboard's audit page.
    /// </remarks>
    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);

            if (!info.Exists || info.Length < PolicyConstants.AuditRotateBytes)
            {
                return;
            }

            // Drop the oldest, then shift each file down one slot.
            var oldest = RotatedName(PolicyConstants.AuditKeepFiles);

            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = PolicyConstants.AuditKeepFiles - 1; i >= 1; i--)
            {
                var from = RotatedName(i);
                var to = RotatedName(i + 1);

                if (File.Exists(from))
                {
                    File.Move(from, to, overwrite: true);
                }
            }

            File.Move(_path, RotatedName(1), overwrite: true);

            _log.LogInformation("Audit log rotated at {Bytes} bytes.", info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rotation failure must not stop auditing; the log simply grows past the threshold until
            // the next attempt succeeds.
            _log.LogWarning(ex, "Audit log rotation failed.");
        }
    }

    private string RotatedName(int index) => $"{_path}.{index}";

    private IEnumerable<string> FilesNewestFirst()
    {
        yield return _path;

        for (var i = 1; i <= PolicyConstants.AuditKeepFiles; i++)
        {
            yield return RotatedName(i);
        }
    }

    /// <summary>
    /// Ceiling on a single read. A request for everything would build a response far past the 256 KB
    /// envelope cap (SC-15c), so the limit is enforced here rather than discovered at write time.
    /// </summary>
    public const int MaxReadLimit = 500;
}
