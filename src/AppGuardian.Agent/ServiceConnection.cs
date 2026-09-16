using System.Collections.Concurrent;
using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent;

/// <summary>
/// The agent's outbound connection to the service, with audit buffering. FR-903, SRS §10.4.
/// </summary>
/// <remarks>
/// A thin wrapper over <see cref="PipeClient"/> that adds two things the agent specifically needs.
/// <list type="bullet">
/// <item>
/// Every send returns null instead of throwing when the service is unreachable. The agent has to keep
/// running through a service restart — the user's windows are hidden and only this process can bring them
/// back — so an outage must be a value, not an exception.
/// </item>
/// <item>
/// Audit entries are buffered while the service is down and flushed on the next successful call. The
/// agent cannot write the log itself: it lives under %ProgramData% with admin-only write, and widening
/// that ACL so an unelevated process could append would let any user-level process forge entries
/// (ADR-005). Buffering is the only way an event during an outage survives at all.
/// </item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ServiceConnection : IAsyncDisposable
{
    /// <summary>
    /// Cap on buffered entries.
    /// </summary>
    /// <remarks>
    /// Bounded because an outage lasting hours with a chatty app would otherwise grow without limit in a
    /// process the user cannot see. When full, the oldest entry is dropped and the drop is itself logged
    /// locally, so the gap is explicable rather than invisible.
    /// </remarks>
    private const int MaxBufferedAudits = 200;

    private readonly PipeClient _client;
    private readonly ILogger<ServiceConnection> _log;
    private readonly ConcurrentQueue<AuditEntry> _buffer = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private int _droppedAudits;
    private string? _authorizationToken;

    /// <summary>Raised when a protected operation needs a fresh PIN or Windows Hello verification.</summary>
    public event EventHandler? AuthenticationRequired;

    public ServiceConnection(MessageDispatcher inbound, ILogger<ServiceConnection> log)
    {
        _log = log;

        // The dispatcher is passed in so the service can push lock.showOverlay and policy.changed back
        // down this same connection. Opening a second one for pushes would double the handle count and
        // give the service two identities for one agent.
        _client = new PipeClient(
            PipeNames.Service,
            inbound,
            (message, ex) =>
            {
                if (ex is null)
                {
                    log.LogDebug("{Message}", message);
                }
                else
                {
                    log.LogDebug(ex, "{Message}", message);
                }
            });
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Sends a request. Returns null when the service could not be reached.</summary>
    public async Task<IpcEnvelope?> SendAsync(
        string type,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        try
        {
            AttachAuthorizationToken(payload);
            var response = await _client.SendAsync(type, payload, timeout, ct).ConfigureAwait(false);

            CaptureAuthorizationToken(response);

            if (response.Success != true && response.Error?.Code == ErrorCodes.Unauthorized)
            {
                Interlocked.Exchange(ref _authorizationToken, null);
                _log.LogInformation("A protected service operation requires fresh authentication.");
                AuthenticationRequired?.Invoke(this, EventArgs.Empty);
            }

            if (response.Success != true && response.Error?.Code == ErrorCodes.ServiceUnavailable)
            {
                return null;
            }

            // A successful round trip is the signal that the service is back. Flushing here rather than
            // on a timer means the buffer drains at the first opportunity without a second moving part.
            if (!_buffer.IsEmpty)
            {
                _ = FlushAuditsAsync(CancellationToken.None);
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "'{Type}' could not be delivered to the service.", type);
            return null;
        }
    }

    private void AttachAuthorizationToken(object? payload)
    {
        if (payload is IAuthorizationTokenRequest protectedRequest &&
            string.IsNullOrWhiteSpace(protectedRequest.AuthorizationToken))
        {
            protectedRequest.AuthorizationToken = Volatile.Read(ref _authorizationToken);
        }
    }

    private void CaptureAuthorizationToken(IpcEnvelope response)
    {
        if (response.Success != true)
        {
            return;
        }

        try
        {
            var result = response.PayloadAs<AuthVerifyResult>();
            if (!string.IsNullOrWhiteSpace(result?.AuthorizationToken))
            {
                Interlocked.Exchange(ref _authorizationToken, result.AuthorizationToken);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Most responses are not authentication results.
        }
    }

    /// <summary>Typed send. Null for both "call failed" and "no payload" — callers treat them alike.</summary>
    public async Task<T?> SendAsync<T>(
        string type,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
        where T : class
    {
        var response = await SendAsync(type, payload, timeout, ct).ConfigureAwait(false);

        if (response?.Success != true)
        {
            return null;
        }

        try
        {
            return response.PayloadAs<T>();
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log.LogWarning(ex, "The response to '{Type}' did not match {Type2}.", type, typeof(T).Name);
            return null;
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(
            MessageTypes.SystemPing,
            timeout: TimeSpan.FromSeconds(2),
            ct: ct).ConfigureAwait(false);

        return response?.Success == true;
    }

    /// <summary>
    /// Records an audit entry, buffering it if the service is unreachable. FR-903.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget from the caller's point of view: auditing an action must never be able to fail the
    /// action. A lock that refused to engage because the log was unwritable would be strictly worse than
    /// a lock with a gap in its history.
    /// </remarks>
    public async Task AuditAsync(AuditEntry entry, CancellationToken ct = default)
    {
        entry.Actor = AuditActor.Agent;

        var response = await SendAsync(
            MessageTypes.SystemAudit,
            new Shared.Contracts.AuditRequest { Entry = entry },
            ct: ct).ConfigureAwait(false);

        if (response?.Success == true)
        {
            return;
        }

        Buffer(entry);
    }

    private void Buffer(AuditEntry entry)
    {
        // Stamped so the service can tell, when the entry finally lands, that its timestamp came from a
        // client during an outage and is therefore less trustworthy than one the service wrote itself.
        entry.BufferedUtc = DateTimeOffset.UtcNow;

        _buffer.Enqueue(entry);

        while (_buffer.Count > MaxBufferedAudits && _buffer.TryDequeue(out _))
        {
            // Oldest-first, because the newest entries describe what the user just did and are the ones
            // they would look for. Counted so the loss appears in the local log.
            _droppedAudits++;
        }

        if (_droppedAudits > 0 && _droppedAudits % 50 == 0)
        {
            _log.LogWarning(
                "{Count} audit entries have been dropped while the service was unreachable.",
                _droppedAudits);
        }
    }

    private async Task FlushAuditsAsync(CancellationToken ct)
    {
        // Non-blocking: a flush already in progress will drain whatever this call would have sent, and
        // two concurrent flushes would interleave entries out of order.
        if (!await _flushGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (_buffer.TryDequeue(out var entry))
            {
                var response = await SendAsync(
                    MessageTypes.SystemAudit,
                    new Shared.Contracts.AuditRequest { Entry = entry },
                    ct: ct).ConfigureAwait(false);

                if (response?.Success != true)
                {
                    // Put it back at the tail rather than the head. Out-of-order arrival is acceptable —
                    // every entry carries its own timestamp and the log is sorted on read — whereas
                    // re-queueing at the head would spin on the same failing entry forever.
                    _buffer.Enqueue(entry);
                    return;
                }
            }

            if (_droppedAudits > 0)
            {
                _log.LogInformation(
                    "Buffered audit entries have been flushed; {Count} were lost to the buffer cap.",
                    _droppedAudits);

                _droppedAudits = 0;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "The audit buffer could not be flushed.");
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // One last attempt to drain, bounded so shutdown cannot hang on a dead service. Anything still
        // queued is lost, which is stated plainly in the log rather than left as a silent gap.
        if (!_buffer.IsEmpty)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            try
            {
                await FlushAuditsAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the service is down at shutdown.
            }

            if (!_buffer.IsEmpty)
            {
                _log.LogWarning(
                    "{Count} audit entries were not delivered before shutdown.",
                    _buffer.Count);
            }
        }

        await _client.DisposeAsync().ConfigureAwait(false);
        _flushGate.Dispose();
    }
}
