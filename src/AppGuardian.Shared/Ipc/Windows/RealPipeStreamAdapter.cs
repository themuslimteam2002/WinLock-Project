using System.IO.Pipes;
using System.Runtime.Versioning;

namespace AppGuardian.Shared.Ipc;

/// <summary>
/// <see cref="PipeStreamAdapter"/> over a real <see cref="PipeStream"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealPipeStreamAdapter : PipeStreamAdapter
{
    private readonly PipeStream _pipe;

    public RealPipeStreamAdapter(PipeStream pipe) => _pipe = pipe;

    public override bool IsMessageComplete => _pipe.IsMessageComplete;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        _pipe.ReadAsync(buffer, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) =>
        _pipe.WriteAsync(buffer, ct);

    public override Task FlushAsync(CancellationToken ct) => _pipe.FlushAsync(ct);
}
