using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppGuardian.Shared.Ipc;

/// <summary>
/// Semantic contract version negotiation. API Design §7.
/// </summary>
/// <remarks>
/// Minor bumps add optional fields/messages and are backward compatible. Major bumps may remove
/// or rename. A server accepts any minor within the same major and rejects everything else with
/// <see cref="ErrorCodes.UnsupportedVersion"/>.
/// <para>
/// SPEC CONFLICT SC-15h: nothing in the spec says what happens when a client connects to the
/// <c>.v1</c> transport pipe but requests <c>apiVersion: "2.0"</c>. We reject it — transport and
/// contract majors move together.
/// </para>
/// </remarks>
public static class ApiVersion
{
    public const int Major = 1;
    public const int Minor = 0;
    public const string Current = "1.0";

    /// <summary>
    /// True when this server can serve a client advertising <paramref name="requested"/>.
    /// Same major, and a minor no higher than ours (we cannot honour fields we do not know).
    /// A newer client talking to an older server must downgrade its request.
    /// </summary>
    public static bool IsSupported(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }

        var parts = requested.Split('.');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
        {
            return false;
        }

        return major == Major && minor <= Minor;
    }
}

/// <summary>Shared JSON configuration and payload helpers.</summary>
public static class JsonPayload
{
    /// <summary>
    /// The single serializer configuration used on the wire and for persisted policy.
    /// <see cref="JsonSerializerOptions.WriteIndented"/> is false on the wire; the policy store
    /// uses <see cref="StoreOptions"/> for a human-readable file.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // API Design §7: unknown fields are ignored, not rejected, for forward compatibility.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Indented variant for on-disk files, so a user can inspect their own policy.</summary>
    public static readonly JsonSerializerOptions StoreOptions = new(Options)
    {
        WriteIndented = true,
    };

    /// <summary>Boxes an object into a <see cref="JsonElement"/> for the envelope payload slot.</summary>
    public static JsonElement From(object value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static string Serialize(IpcEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    /// <summary>
    /// Parses an envelope. Returns null on malformed JSON rather than throwing, so the transport
    /// can answer <see cref="ErrorCodes.BadRequest"/> without ever crashing the peer
    /// (SRS §10.3: "never crash the peer").
    /// </summary>
        public static T? TryDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}