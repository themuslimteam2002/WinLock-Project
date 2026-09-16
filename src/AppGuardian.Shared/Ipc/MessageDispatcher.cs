using AppGuardian.Shared.Models;

namespace AppGuardian.Shared.Ipc;

/// <summary>
/// Context passed to a message handler, carrying what the dispatcher established about the caller.
/// </summary>
public sealed class RequestContext
{
    public required IpcEnvelope Request { get; init; }

    /// <summary>
    /// Windows SID of the calling user, or null when it could not be established. Used to key
    /// per-user credentials (SC-12).
    /// </summary>
    public string? ClientUserSid { get; init; }

    /// <summary>
    /// True when the caller is LocalSystem or an Administrator. Required only for operations that
    /// bypass authentication itself — <c>auth.reset</c> and repair (ADR-006).
    /// </summary>
    public bool IsElevated { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Convenience typed payload access; throws on a schema violation.</summary>
    public T? PayloadAs<T>() where T : class => Request.PayloadAs<T>();
}

/// <summary>Handles one message type.</summary>
public delegate Task<IpcEnvelope> MessageHandler(RequestContext context);

/// <summary>
/// Routes an envelope to a registered handler, enforcing the protocol rules in API Design §2.3–2.4
/// and SRS §10.3 so that individual handlers never have to.
/// </summary>
/// <remarks>
/// Every failure path here returns a well-formed error response rather than throwing, because SRS
/// §10.3 requires that an unknown op or version mismatch never crash the peer. A handler that throws
/// is converted to <see cref="ErrorCodes.Internal"/> with the detail logged locally and withheld
/// from the wire (API Design §3).
/// </remarks>
public sealed class MessageDispatcher
{
    private readonly Dictionary<string, MessageHandler> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MessageHandler> _eventHandlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _requiresElevation = new(StringComparer.Ordinal);
    private readonly Action<string, Exception?>? _log;

    /// <summary>
    /// Remembers the results of recent non-idempotent requests by messageId, so a client retry that
    /// crossed with a slow response is not applied twice (API Design §2.4).
    /// </summary>
    private readonly DedupCache _dedup = new(capacity: 256, ttl: TimeSpan.FromMinutes(2));

    public MessageDispatcher(Action<string, Exception?>? log = null) => _log = log;

    /// <summary>Registers a request handler. <paramref name="requiresElevation"/> per ADR-006.</summary>
    public MessageDispatcher On(string type, MessageHandler handler, bool requiresElevation = false)
    {
        _handlers[type] = handler;

        if (requiresElevation)
        {
            _requiresElevation.Add(type);
        }

        return this;
    }

    /// <summary>
    /// Registers an event handler. Events are one-way and best-effort (SRS §10.3), so the returned
    /// envelope is discarded.
    /// </summary>
    public MessageDispatcher OnEvent(string type, MessageHandler handler)
    {
        _eventHandlers[type] = handler;
        return this;
    }

    public bool Handles(string type) => _handlers.ContainsKey(type) || _eventHandlers.ContainsKey(type);

    /// <summary>
    /// Dispatches one envelope. Returns the response to send, or null when nothing should be sent
    /// (events, and responses arriving at a server).
    /// </summary>
    public async Task<IpcEnvelope?> DispatchAsync(
        IpcEnvelope envelope,
        string? clientUserSid,
        bool isElevated,
        CancellationToken ct)
    {
        var structural = envelope.Validate();
        if (structural is not null)
        {
            return envelope.Fail(ErrorCodes.BadRequest, structural);
        }

        if (!ApiVersion.IsSupported(envelope.ApiVersion))
        {
            // SC-15h: transport and contract majors move together, so a v1 pipe rejects a 2.x client.
            return envelope.Fail(
                ErrorCodes.UnsupportedVersion,
                $"This server supports apiVersion {ApiVersion.Current}; the client requested {envelope.ApiVersion}.");
        }

        var type = envelope.Type!;

        if (envelope.Kind == MessageKind.Event)
        {
            if (_eventHandlers.TryGetValue(type, out var eventHandler))
            {
                try
                {
                    await eventHandler(BuildContext(envelope, clientUserSid, isElevated, ct)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best-effort by definition; log and move on rather than tearing down the pipe.
                    _log?.Invoke($"Event handler for '{type}' failed.", ex);
                }
            }

            return null;
        }

        if (envelope.Kind == MessageKind.Response)
        {
            // A server does not answer responses. Reaching here means a peer is confused.
            _log?.Invoke($"Ignoring unsolicited response for correlationId {envelope.CorrelationId}.", null);
            return null;
        }

        if (!_handlers.TryGetValue(type, out var handler))
        {
            return envelope.Fail(ErrorCodes.BadRequest, $"Unsupported message type '{type}'.");
        }

        if (_requiresElevation.Contains(type) && !isElevated)
        {
            return envelope.Fail(
                ErrorCodes.Unauthorized,
                "This operation requires an administrator.");
        }

        // Deduplicate retried writes. Idempotent reads are cheap to repeat and are not cached, so a
        // stale read is never served.
        var isIdempotent = MessageTypes.IsIdempotent(type);
        if (!isIdempotent && _dedup.TryGet(envelope.MessageId, out var cached))
        {
            _log?.Invoke($"Returning deduplicated result for messageId {envelope.MessageId}.", null);
            return cached;
        }

        IpcEnvelope response;
        try
        {
            response = await handler(BuildContext(envelope, clientUserSid, isElevated, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Text.Json.JsonException ex)
        {
            // A payload that does not match its DTO is the client's error, not a server fault.
            _log?.Invoke($"Bad payload for '{type}'.", ex);
            return envelope.Fail(ErrorCodes.BadRequest, "The request payload did not match the expected schema.");
        }
        catch (Exception ex)
        {
            // API Design §3: no stack traces or secrets on the wire; detail goes to the local log.
            _log?.Invoke($"Handler for '{type}' faulted.", ex);
            return envelope.Fail(ErrorCodes.Internal, "The operation failed unexpectedly. See the local log.");
        }

        if (!isIdempotent)
        {
            _dedup.Add(envelope.MessageId, response);
        }

        return response;
    }

    private static RequestContext BuildContext(
        IpcEnvelope envelope,
        string? sid,
        bool isElevated,
        CancellationToken ct) =>
        new()
        {
            Request = envelope,
            ClientUserSid = sid,
            IsElevated = isElevated,
            CancellationToken = ct,
        };
}

/// <summary>
/// Bounded, time-limited cache of recent write results, keyed by messageId.
/// </summary>
/// <remarks>
/// Bounded because this runs in the LocalSystem process and an unbounded cache keyed by
/// client-supplied ids would be a trivial memory-exhaustion vector.
/// </remarks>
internal sealed class DedupCache
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, (IpcEnvelope Response, DateTimeOffset Added)> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _sync = new();

    public DedupCache(int capacity, TimeSpan ttl)
    {
        _capacity = capacity;
        _ttl = ttl;
    }

    public bool TryGet(string key, out IpcEnvelope? response)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (DateTimeOffset.UtcNow - entry.Added <= _ttl)
                {
                    response = entry.Response;
                    return true;
                }

                _entries.Remove(key);
            }

            response = null;
            return false;
        }
    }

    public void Add(string key, IpcEnvelope response)
    {
        lock (_sync)
        {
            if (_entries.ContainsKey(key))
            {
                return;
            }

            _entries[key] = (response, DateTimeOffset.UtcNow);
            _order.Enqueue(key);

            while (_order.Count > _capacity)
            {
                _entries.Remove(_order.Dequeue());
            }
        }
    }
}
