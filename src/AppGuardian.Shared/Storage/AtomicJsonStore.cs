using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppGuardian.Shared.Ipc;

namespace AppGuardian.Shared.Storage;

/// <summary>
/// Atomic JSON file persistence. SRS NFR-R4, §9.1.
/// </summary>
/// <remarks>
/// Writes go to a temporary file in the same directory, are flushed to disk, and then replace the
/// target in one filesystem operation. This is what makes a power loss mid-write leave either the
/// old document or the new one, never a truncated file — which is the failure TC-11 and NFR-R4 care
/// about, since a corrupt policy.json would silently unprotect every app.
/// <para>
/// Same-directory temp files matter: <c>File.Replace</c> and rename are only atomic within a single
/// volume.
/// </para>
/// </remarks>
public sealed class AtomicJsonStore<T> where T : class, new()
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options;

    public AtomicJsonStore(string path, JsonSerializerOptions? options = null)
    {
        _path = path;
        _options = options ?? JsonPayload.StoreOptions;
    }

    public string Path => _path;

    /// <summary>
    /// Loads the document, returning a fresh instance when the file is absent.
    /// </summary>
    /// <remarks>
    /// A corrupt file is not silently replaced with defaults — that would be indistinguishable from
    /// "the user had no rules" and would quietly drop protection. The corrupt file is preserved
    /// alongside as <c>.corrupt-{timestamp}</c> and the failure is reported to the caller.
    /// </remarks>
    public async Task<StoreLoadResult<T>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return StoreLoadResult<T>.Fresh(new T());
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(_path, Encoding.UTF8, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                return StoreLoadResult<T>.Failed(new T(), $"could not read {_path}: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return StoreLoadResult<T>.Fresh(new T());
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(json, _options);
                return value is null
                    ? StoreLoadResult<T>.Failed(new T(), "document deserialized to null")
                    : StoreLoadResult<T>.Loaded(value);
            }
            catch (JsonException ex)
            {
                var quarantine = QuarantineCorrupt();
                return StoreLoadResult<T>.Failed(
                    new T(),
                    $"malformed JSON in {_path}: {ex.Message}. " +
                    (quarantine is null ? "Original could not be preserved." : $"Original kept at {quarantine}."));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes atomically: temp file in the same directory, flush, then replace.</summary>
    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(value, _options);
            var temp = _path + ".tmp";

            // Flush through to the device before the replace, otherwise the rename can land while
            // the new content is still only in the OS write cache.
            await using (var stream = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                // Replace preserves ACLs on the destination, which matters because the policy file's
                // admin-only-write ACL is a security control (NFR-S3). A plain Move would give the
                // new file the temp file's inherited permissions instead.
                File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// SHA-256 over the canonical JSON form, for the tamper check in
    /// <see cref="Models.PolicyDocument.IntegrityHash"/>.
    /// </summary>
    /// <remarks>
    /// Not a MAC, deliberately — see the note on <c>PolicyDocument.IntegrityHash</c>. The ACL is the
    /// real control; this catches an edit that got past it.
    /// </remarks>
    public static string ComputeIntegrityHash(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonPayload.Options);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private string? QuarantineCorrupt()
    {
        try
        {
            var target = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_path, target, overwrite: false);
            return target;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Outcome of a load, distinguishing "absent" from "corrupt".</summary>
public sealed class StoreLoadResult<T> where T : class
{
    public required T Value { get; init; }

    /// <summary>True when an existing document was read successfully.</summary>
    public bool WasExisting { get; init; }

    /// <summary>
    /// Set when the file existed but could not be used. The caller must decide whether to proceed
    /// with defaults; for policy, proceeding silently would drop protection, so the service logs
    /// and reports a degraded status instead.
    /// </summary>
    public string? Error { get; init; }

    public bool IsFailure => Error is not null;

    public static StoreLoadResult<T> Loaded(T value) =>
        new() { Value = value, WasExisting = true };

    public static StoreLoadResult<T> Fresh(T value) =>
        new() { Value = value, WasExisting = false };

    public static StoreLoadResult<T> Failed(T fallback, string error) =>
        new() { Value = fallback, WasExisting = true, Error = error };
}
