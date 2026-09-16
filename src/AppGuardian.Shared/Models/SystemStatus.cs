namespace AppGuardian.Shared.Models;

/// <summary>
/// Aggregate status shown on the dashboard. API Design §5; SRS FR-800, FR-804, FR-805.
/// </summary>
/// <remarks>
/// SPEC CONFLICT SC-12: SRS §9 never persists these fields. They are computed per-request, never
/// stored — a cached "service running" flag would be wrong exactly when it matters most.
/// </remarks>
public sealed class SystemStatus
{
    public bool ServiceRunning { get; set; }

    public bool AgentRunning { get; set; }

    public int ProtectedCount { get; set; }

    public int LockedCount { get; set; }

    public int HiddenCount { get; set; }

    public int ThrottledCount { get; set; }

    /// <summary>
    /// Feature-detect result from <c>IVirtualDesktopAdapter.IsSupported</c> (ADR-007).
    /// False means hiding degrades to the disclosed fallback rather than failing silently.
    /// </summary>
    public bool VirtualDesktopSupported { get; set; }

    public bool WindowsHelloAvailable { get; set; }

    /// <summary>Global pause state. FR-806.</summary>
    public bool ProtectionPaused { get; set; }

    /// <summary>
    /// Set when protection is degraded and the user should be told why — for example the agent is
    /// down, or the virtual desktop adapter could not initialise on this Windows build.
    /// Surfaced verbatim in the dashboard, so it must be user-readable (NFR §7.5).
    /// </summary>
    public string? DegradedReason { get; set; }

    /// <summary>Service uptime, for diagnosing a watchdog restart loop.</summary>
    public TimeSpan? ServiceUptime { get; set; }

    public string? ServiceVersion { get; set; }

    public string? AgentVersion { get; set; }

    /// <summary>True when everything needed for full enforcement is present.</summary>
    public bool IsFullyOperational =>
        ServiceRunning && AgentRunning && !ProtectionPaused && DegradedReason is null;
}

/// <summary>Heartbeat reply payload for <c>system.ping</c>. SRS FR-205, NFR-R1/R2.</summary>
public sealed class PingResult
{
    public string Component { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public DateTimeOffset Utc { get; set; } = DateTimeOffset.UtcNow;

    public TimeSpan Uptime { get; set; }
}
