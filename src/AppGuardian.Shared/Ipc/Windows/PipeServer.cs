using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace AppGuardian.Shared.Ipc;

/// <summary>
/// Named pipe server. API Design §2.1–2.4, SRS §10.
/// </summary>
/// <remarks>
/// One instance per pipe name. The accept loop keeps a bounded number of concurrent server
/// instances alive so a client never finds the pipe missing between connections — with a single
/// instance, a client connecting while the previous one is still being served gets a hard failure
/// rather than a queue.
/// <para>
/// There is deliberately no network listener anywhere in AppGuardian (NFR-S1). A named pipe created
/// with <see cref="NamedPipeServerStream"/> is local-only; remote access would require an explicit
/// share of IPC$ and is additionally prevented by the DACL in <see cref="PipeSecurityFactory"/>.
/// Note that <c>PipeOptions.None</c> does not contribute to that (see SC-15f) — this server uses
/// <c>PipeOptions.Asynchronous</c> because the async accept loop requires it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly PipeSecurity _security;
    private readonly MessageDispatcher _dispatcher;
    private readonly int _maxConcurrentInstances;
    private readonly Action<string, Exception?>? _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = new();

    public PipeServer(
        string pipeName,
        PipeSecurity security,
        MessageDispatcher dispatcher,
        int maxConcurrentInstances = 8,
        Action<string, Exception?>? log = null)
    {
        _pipeName = pipeName;
        _security = security;
        _dispatcher = dispatcher;
        _maxConcurrentInstances = maxConcurrentInstances;
        _log = log;
    }

    /// <summary>
    /// Starts the accept loops. Returns once they are running; call <see cref="DisposeAsync"/> to stop.
    /// </summary>
    public void Start()
    {
        for (var i = 0; i < _maxConcurrentInstances; i++)
        {
            _workers.Add(Task.Run(() => AcceptLoopAsync(_shutdown.Token)));
        }

        _log?.Invoke($"Pipe server listening on '{_pipeName}' with {_maxConcurrentInstances} instances.", null);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;

            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    _maxConcurrentInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                    inBufferSize: PipeNames.MaxMessageBytes,
                    outBufferSize: PipeNames.MaxMessageBytes,
                    _security);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                await ServeConnectionAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (IOException ex)
            {
                // A client that dies mid-request produces this. Not worth escalating; the loop
                // recreates the instance.
                _log?.Invoke("Pipe connection dropped.", ex);
            }
            catch (Exception ex)
            {
                // Anything else is unexpected. Log and back off briefly rather than spinning a
                // tight failing loop that would peg a core.
                _log?.Invoke("Pipe accept loop error.", ex);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (pipe is not null)
                {
                    try
                    {
                        if (pipe.IsConnected)
                        {
                            pipe.Disconnect();
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException)
                    {
                        // Already gone.
                    }

                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Serves one connection. The connection is kept open for multiple messages so the dashboard's
    /// polling and the service's event pushes do not pay reconnect cost per message.
    /// </summary>
    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // Identity is established once per connection. A pipe client cannot change its token
        // mid-connection, so re-impersonating per message would only add cost.
        var sid = PipeSecurityFactory.GetClientUserSid(pipe);
        var isElevated = PipeSecurityFactory.IsElevatedClient(pipe);

        var adapter = new RealPipeStreamAdapter(pipe);

        while (pipe.IsConnected && !ct.IsCancellationRequested)
        {
            string? json;

            try
            {
                json = await PipeFraming.ReadMessageAsync(adapter, ct).ConfigureAwait(false);
            }
            catch (MessageTooLargeException ex)
            {
                // The stream was drained, so the connection is still usable. Tell the client which
                // limit it broke instead of dropping it silently.
                _log?.Invoke("Rejected oversized inbound message.", ex);

                await TryWriteAsync(
                    adapter,
                    IpcEnvelope.Fail(
                        requestType: null,
                        correlationId: null,
                        code: ErrorCodes.BadRequest,
                        message: $"Message exceeded the {ex.LimitBytes} byte limit."),
                    ct).ConfigureAwait(false);

                continue;
            }

            if (json is null)
            {
                return; // clean disconnect
            }

            var envelope = JsonPayload.TryDeserialize<IpcEnvelope>(json);

            if (envelope is null)
            {
                // Malformed JSON: we have no correlationId to echo, so the client matches this by
                // being the only outstanding request. Better than silence, which would look like a
                // hang until the client's timeout fires.
                await TryWriteAsync(
                    adapter,
                    IpcEnvelope.Fail(null, null, ErrorCodes.BadRequest, "The message was not valid JSON."),
                    ct).ConfigureAwait(false);

                continue;
            }

            var response = await _dispatcher
                .DispatchAsync(envelope, sid, isElevated, ct)
                .ConfigureAwait(false);

            if (response is not null)
            {
                await TryWriteAsync(adapter, response, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task TryWriteAsync(PipeStreamAdapter adapter, IpcEnvelope envelope, CancellationToken ct)
    {
        try
        {
            await PipeFraming
                .WriteMessageAsync(adapter, JsonPayload.Serialize(envelope), ct)
                .ConfigureAwait(false);
        }
        catch (MessageTooLargeException ex)
        {
            // A handler built a response too large for the transport. Downgrade to an error the
            // client can act on rather than dropping the connection (SC-15c).
            _log?.Invoke($"Response for '{envelope.Type}' exceeded the transport limit.", ex);

            try
            {
                await PipeFraming.WriteMessageAsync(
                    adapter,
                    JsonPayload.Serialize(IpcEnvelope.Fail(
                        envelope.Type,
                        envelope.CorrelationId,
                        ErrorCodes.Internal,
                        "The result was too large to return. Narrow the request.")),
                    ct).ConfigureAwait(false);
            }
            catch (Exception inner) when (inner is IOException or ObjectDisposedException)
            {
                // Client gone.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log?.Invoke("Client disconnected before the response could be written.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            // Connect-and-drop against our own pipe would be needed to unblock a synchronous
            // WaitForConnection; the async overload honours the token, so a bounded wait suffices.
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _log?.Invoke("Pipe server shutdown timed out; abandoning accept loops.", ex);
        }

        _shutdown.Dispose();
    }
}
