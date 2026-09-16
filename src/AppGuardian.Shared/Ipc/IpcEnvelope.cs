using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppGuardian.Shared.Ipc;

/// <summary>Envelope kind discriminator. API Design §2.3.</summary>
public static class MessageKind
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
}

/// <summary>
/// Structured error object. API Design §2.3.4.
/// </summary>
public sealed class IpcError
{
    /// <summary>One of <see cref="ErrorCodes"/>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = ErrorCodes.Internal;

    /// <summary>Human-readable, user-safe. Never contains a stack trace or secret (API Design §3).</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; }

    /// <summary>Optional structured detail, e.g. <c>{ "attemptsRemaining": 4 }</c>.</summary>
    [JsonPropertyName("details")]
    public Dictionary<string, object?>? Details { get; set; }

    public static IpcError Create(string code, string message, Dictionary<string, object?>? details = null) =>
        new()
        {
            Code = code,
            Message = message,
            Retryable = ErrorCodes.IsRetryableByDefault(code),
            Details = details,
        };
}

/// <summary>
/// The single wire envelope for all three message kinds. API Design §2.3.
/// </summary>
/// <remarks>
/// SC-02 RESOLVED 2026-08-25 (ADR-014). The SRS §10.2 defined an incompatible envelope
/// (<c>v</c>/<c>type</c>/<c>op</c>/<c>id</c>/<c>ts</c> with bare-string errors). The owner's ruling is that
/// the service's envelope — the API Design form implemented here, per ADR-009 — is the single source of
/// truth for all three peers. It separates messageId from correlationId (needed for write deduplication,
/// API Design §2.4), carries structured errors, and uses semantic version negotiation. The SRS §10.3
/// *rules* are all preserved; only the field names differ.
/// <para>
/// This class is the only definition of the wire format in the solution. The UI's <c>GuardianClient</c> and
/// the agent's dispatcher both go through it — neither builds or parses JSON of its own — so "matching the
/// service's expectations" is structural rather than a convention someone has to remember.
/// </para>
/// <para>
/// Unknown fields in a payload are ignored rather than rejected (API Design §7) to allow
/// forward-compatible additions.
/// </para>
/// </remarks>
public sealed class IpcEnvelope
{
    /// <summary>Semantic contract version. See <see cref="ApiVersion"/>.</summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = Ipc.ApiVersion.Current;

    /// <summary>One of <see cref="MessageKind"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = MessageKind.Request;

    /// <summary>Sender-generated UUID, unique per message. Used for server-side write dedup.</summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>On a response, the <see cref="MessageId"/> of the originating request.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>One of <see cref="MessageTypes"/>. Null is invalid on requests and events.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Present on responses only.</summary>
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    /// <summary>Op-specific DTO, held as raw JSON so the transport never needs to know payload types.</summary>
    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement? Payload { get; set; }

    [JsonPropertyName("error")]
    public IpcError? Error { get; set; }

    // ---- factories ----

    public static IpcEnvelope NewRequest(string type, object? payload = null) =>
        new()
        {
            Kind = MessageKind.Request,
            Type = type,
            Payload = payload is null ? null : JsonPayload.From(payload),
        };

    public static IpcEnvelope NewEvent(string type, object? payload = null) =>
        new()
        {
            Kind = MessageKind.Event,
            Type = type,
            Payload = payload is null ? null : JsonPayload.From(payload),
        };

    public IpcEnvelope Ok(object? payload = null) =>
        new()
        {
            Kind = MessageKind.Response,
            Type = Type,
            CorrelationId = MessageId,
            Success = true,
            Payload = payload is null ? null : JsonPayload.From(payload),
        };

    public IpcEnvelope Fail(IpcError error) =>
        new()
        {
            Kind = MessageKind.Response,
            Type = Type,
            CorrelationId = MessageId,
            Success = false,
            Error = error,
        };

    public IpcEnvelope Fail(string code, string message, Dictionary<string, object?>? details = null) =>
        Fail(IpcError.Create(code, message, details));

    /// <summary>
    /// Builds a failure response when no request envelope is available to derive it from — a message
    /// that would not parse, or one rejected by the transport before dispatch.
    /// </summary>
    /// <remarks>
    /// <see cref="Validate"/> requires a correlationId on every response, so an unattributable
    /// failure carries <see cref="UnknownCorrelationId"/> rather than null. A client that receives it
    /// matches it to whichever request is outstanding; the alternative is silence until the client's
    /// own timeout expires, which is indistinguishable from a hung service.
    /// </remarks>
    public static IpcEnvelope Fail(
        string? requestType,
        string? correlationId,
        string code,
        string message,
        Dictionary<string, object?>? details = null) =>
        new()
        {
            Kind = MessageKind.Response,
            Type = requestType,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? UnknownCorrelationId : correlationId,
            Success = false,
            Error = IpcError.Create(code, message, details),
        };

    /// <summary>Sentinel correlationId for a failure that could not be attributed to a request.</summary>
    public const string UnknownCorrelationId = "unattributed";

    /// <summary>
    /// Deserializes the payload to <typeparamref name="T"/>, or returns null when absent.
    /// Throws <see cref="System.Text.Json.JsonException"/> on a schema violation, which the
    /// dispatcher converts to <see cref="ErrorCodes.BadRequest"/>.
    /// </summary>
    public T? PayloadAs<T>() where T : class =>
        Payload is null || Payload.Value.ValueKind == System.Text.Json.JsonValueKind.Null
            ? null
            : Payload.Value.Deserialize<T>(JsonPayload.Options);

    /// <summary>Structural validation. Returns null when valid, otherwise the reason.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(MessageId))
        {
            return "messageId is required";
        }

        if (Kind is not (MessageKind.Request or MessageKind.Response or MessageKind.Event))
        {
            return $"unknown kind '{Kind}'";
        }

        if (Kind is MessageKind.Request or MessageKind.Event && string.IsNullOrWhiteSpace(Type))
        {
            return "type is required on requests and events";
        }

        if (Kind == MessageKind.Response)
        {
            if (string.IsNullOrWhiteSpace(CorrelationId))
            {
                return "correlationId is required on responses";
            }

            if (Success is null)
            {
                return "success is required on responses";
            }

            if (Success == false && Error is null)
            {
                return "a failed response must carry an error object";
            }
        }

        return null;
    }
}
