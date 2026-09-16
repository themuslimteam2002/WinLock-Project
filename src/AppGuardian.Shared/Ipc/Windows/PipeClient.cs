using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using AppGuardian.Shared.Models;

namespace AppGuardian.Shared.Ipc;

/// <summary>
/// Named pipe client with correlated request/response, timeouts, and retry. API Design §2.1–2.4.
/// </summary>
/// <remarks>
/// One connection is held open and shared. A single reader pump owns the read side and hands each
/// response to the waiter registered under its correlationId; requests are serialized behind a write
/// lock. This is why the client can be shared across the whole process: without the pump, two
/// concurrent callers reading from the same pipe would each consume the other's response.
/// <para>
/// Reconnect is lazy — a dropped connection is re-established on the next call rather than by a
/// background retry timer, so a stopped service costs nothing until something actually needs it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly MessageDispatcher? _inboundDispatcher;
    private readonly Action<string, Exception?>? _log;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> _pending =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _disposed = new();

    private NamedPipeClientStream? _pipe;
    private RealPipeStreamAdapter? _adapter;
    private Task? _pump;

    /// <param name="inboundDispatcher">
    /// Optional. Handles server-pushed events on this connection — the agent needs it for
    /// <c>process.appLaunched</c> and <c>lock.showOverlay</c>; the dashboard for <c>policy.changed</c>.
    /// </param>
    public PipeClient(
        string pipeName,
        MessageDispatcher? inboundDispatcher = null,
        Action<string, Exception?>? log = null)
    {
        _pipeName = pipeName;
        _inboundDispatcher = inboundDispatcher;
        _log = log;
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    // ---- public API ----

    /// <summary>
    /// Sends a request and awaits its response.
    /// </summary>
    /// <remarks>
    /// Never throws for a remote failure: a service-side error arrives as a response with
    /// <c>success == false</c>, and a transport failure is converted to one, so callers have exactly
    /// one error path to handle. Only cancellation propagates.
    /// </remarks>
    public async Task<IpcEnvelope> SendAsync(
        string type,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var request = IpcEnvelope.NewRequest(type, payload);
        var budget = timeout ?? DefaultTimeoutFor(type);

        var attempts = MessageTypes.IsIdempotent(type) ? 2 : 1;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var response = await SendOnceAsync(request, budget, ct).ConfigureAwait(false);

            if (response.Success == true || attempt == attempts)
            {
                return response;
            }

            // Retry only a transport-level failure on a read-only op. A service that answered with a
            // business error (E_AUTH_FAILED, E_NOT_FOUND) is not retried — repeating it would burn
            // an authentication attempt against the FR-306 lockout counter.
            if (response.Error is not { Retryable: true })
            {
                return response;
            }

            _log?.Invoke($"Retrying idempotent '{type}' after {response.Error.Code}.", null);

            // A fresh messageId, because the previous one may already be in the server's dedup cache.
            request = IpcEnvelope.NewRequest(type, payload);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        // Unreachable: the loop always returns.
        throw new InvalidOperationException("Retry loop exited without a response.");
    }

    /// <summary>Typed convenience wrapper. Returns null when the call failed or carried no payload.</summary>
    public async Task<T?> SendAsync<T>(
        string type,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
        where T : class
    {
        var response = await SendAsync(type, payload, timeout, ct).ConfigureAwait(false);

        if (response.Success != true)
        {
            return null;
        }

        try
        {
            return response.PayloadAs<T>();
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log?.Invoke($"Response payload for '{type}' did not match {typeof(T).Name}.", ex);
            return null;
        }
    }

    /// <summary>
    /// Fires a one-way event. Best-effort by definition (SRS §10.3) — a delivery failure is logged,
    /// not surfaced, so an event push can never block or fail the operation that triggered it.
    /// </summary>
    public async Task<bool> PublishAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        try
        {
            var adapter = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var envelope = IpcEnvelope.NewEvent(type, payload);

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                await PipeFraming
                    .WriteMessageAsync(adapter, JsonPayload.Serialize(envelope), ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Event '{type}' could not be delivered.", ex);
            Drop();
            return false;
        }
    }

    /// <summary>Liveness probe. FR-207.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var result = await SendAsync<PingResult>(
            MessageTypes.SystemPing,
            timeout: TimeSpan.FromSeconds(2),
            ct: ct).ConfigureAwait(false);

        return result is not null;
    }

    // ---- internals ----

    private static TimeSpan DefaultTimeoutFor(string type) =>
        // Authentication waits on a human: Windows Hello prompts, PIN entry, retries. The default
        // 5 s budget (NFR-P) applies to service calls, not to a dialog the user is looking at.
        type is MessageTypes.AuthVerifyWindowsHello
                or MessageTypes.AuthVerifyPin
                or MessageTypes.AuthSetup
                or MessageTypes.LockUnlock
            ? PolicyConstants.AuthRequestTimeout
            : PolicyConstants.DefaultRequestTimeout;

    private async Task<IpcEnvelope> SendOnceAsync(
        IpcEnvelope request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        PipeStreamAdapter adapter;

        try
        {
            adapter = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not connect to '{_pipeName}'.", ex);

            return IpcEnvelope.Fail(
                request.Type,
                request.MessageId,
                ErrorCodes.ServiceUnavailable,
                "The AppGuardian service is not responding.");
        }

        var waiter = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(request.MessageId, waiter))
        {
            // A duplicate messageId means a Guid collision or a caller reusing an envelope.
            return IpcEnvelope.Fail(
                request.Type,
                request.MessageId,
                ErrorCodes.Internal,
                "A request with this identifier is already in flight.");
        }

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                await PipeFraming
                    .WriteMessageAsync(adapter, JsonPayload.Serialize(request), ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);

            return await waiter.Task.WaitAsync(timeout, linked.Token).ConfigureAwait(false);
        }
        catch (MessageTooLargeException ex)
        {
            return IpcEnvelope.Fail(
                request.Type,
                request.MessageId,
                ErrorCodes.BadRequest,
                $"The request exceeded the {ex.LimitBytes} byte transport limit.");
        }
        catch (TimeoutException)
        {
            // The connection is left intact: the server may still answer, and the pump discards a
            // response whose waiter is gone. Only an I/O failure justifies dropping the connection.
            _log?.Invoke($"Request '{request.Type}' timed out after {timeout.TotalSeconds:0.#}s.", null);

            return IpcEnvelope.Fail(
                request.Type,
                request.MessageId,
                ErrorCodes.Timeout,
                "The operation took too long to complete.");
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            return IpcEnvelope.Fail(
                request.Type,
                request.MessageId,
                ErrorCodes.ServiceUnavailable,
                "The connection was closed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _log?.Invoke($"Transport failure sending '{request.Type}'.", ex);
            Drop();

            return IpcEnvelope.Fail(
                request.Type,
                request.MessageId,
                ErrorCodes.ServiceUnavailable,
                "The connection to the AppGuardian service was lost.");
        }
        finally
        {
            _pending.TryRemove(request.MessageId, out _);
        }
    }

    private async Task<PipeStreamAdapter> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_adapter is not null && _pipe?.IsConnected == true)
        {
            return _adapter;
        }

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Re-check: another caller may have connected while we waited.
            if (_adapter is not null && _pipe?.IsConnected == true)
            {
                return _adapter;
            }

            Drop();

            var pipe = new NamedPipeClientStream(
                serverName: ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                System.Security.Principal.TokenImpersonationLevel.Impersonation);

            // A short connect budget: the pipe is local, so a slow connect means the server is busy
            // or absent, and reporting E_SERVICE_UNAVAILABLE quickly beats a long stall.
            await pipe.ConnectAsync((int)TimeSpan.FromSeconds(3).TotalMilliseconds, ct).ConfigureAwait(false);

            // Must be set after connecting; the server created the pipe in message mode.
            pipe.ReadMode = PipeTransmissionMode.Message;

            _pipe = pipe;
            _adapter = new RealPipeStreamAdapter(pipe);
            // Forward _disposed.Token into the pump so a concurrent dispose cancels the read loop
            // instead of leaving a dangling Task.Run bound only to the threadpool. (CA2016: the
            // cancellation token must be the one tied to this object's lifetime, not a captured
            // outer scope.)
            _pump = Task.Run(() => PumpAsync(_adapter, _disposed.Token), _disposed.Token);

            _log?.Invoke($"Connected to '{_pipeName}'.", null);

            return _adapter;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Sole reader of the connection. Completes waiters for responses and routes inbound events.
    /// </summary>
    private async Task PumpAsync(PipeStreamAdapter adapter, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? json;

                try
                {
                    json = await PipeFraming.ReadMessageAsync(adapter, ct).ConfigureAwait(false);
                }
                catch (MessageTooLargeException ex)
                {
                    // The stream was drained, so keep reading; the affected request will time out.
                    _log?.Invoke("Discarded an oversized inbound message.", ex);
                    continue;
                }

                if (json is null)
                {
                    break; // server closed
                }

                var envelope = JsonPayload.TryDeserialize<IpcEnvelope>(json);

                if (envelope is null)
                {
                    _log?.Invoke("Discarded a malformed inbound message.", null);
                    continue;
                }

                if (envelope.Kind == MessageKind.Response)
                {
                    if (envelope.CorrelationId is not null &&
                        _pending.TryRemove(envelope.CorrelationId, out var waiter))
                    {
                        waiter.TrySetResult(envelope);
                    }
                    else if (envelope.CorrelationId == IpcEnvelope.UnknownCorrelationId && _pending.Count == 1)
                    {
                        // An unattributable transport error. Safe to attribute only when exactly one
                        // request is outstanding; otherwise let the timeouts decide.
                        var only = _pending.Keys.FirstOrDefault();

                        if (only is not null && _pending.TryRemove(only, out var lone))
                        {
                            lone.TrySetResult(envelope);
                        }
                    }
                    else
                    {
                        // Late response to a timed-out request. Expected, not an error.
                        _log?.Invoke($"Discarded a response for unknown correlationId '{envelope.CorrelationId}'.", null);
                    }

                    continue;
                }

                if (_inboundDispatcher is null)
                {
                    continue;
                }

                // Server-pushed request or event. The peer here is the service or agent on the far
                // side of an ACL'd pipe, so it is treated as elevated for dispatch purposes; the
                // dispatcher's own registration decides what that permits.
                var reply = await _inboundDispatcher
                    .DispatchAsync(envelope, clientUserSid: null, isElevated: true, ct)
                    .ConfigureAwait(false);

                if (reply is not null)
                {
                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);

                    try
                    {
                        await PipeFraming
                            .WriteMessageAsync(adapter, JsonPayload.Serialize(reply), ct)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _log?.Invoke("Read pump stopped.", ex);
        }
        finally
        {
            // Fail every outstanding waiter rather than leaving callers to time out one by one.
            FailAllPending("The connection to the AppGuardian service was lost.");
        }
    }

    private void FailAllPending(string message)
    {
        foreach (var key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var waiter))
            {
                waiter.TrySetResult(IpcEnvelope.Fail(
                    requestType: null,
                    correlationId: key,
                    ErrorCodes.ServiceUnavailable,
                    message));
            }
        }
    }

    private void Drop()
    {
        var pipe = _pipe;
        _pipe = null;
        _adapter = null;

        try
        {
            pipe?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Already gone.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposed.CancelAsync().ConfigureAwait(false);

        Drop();

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Abandon it; the process is going down.
            }
        }

        FailAllPending("The client was disposed.");

        _disposed.Dispose();
        _connectLock.Dispose();
        _writeLock.Dispose();
    }
}
