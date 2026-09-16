using System.Buffers;
using System.Text;

namespace AppGuardian.Shared.Ipc;

/// <summary>
/// Reads and writes envelopes over a pipe stream. API Design §2.1, §2.2.
/// </summary>
/// <remarks>
/// The pipes are created in message mode, so each write is a discrete message and a read ends at a
/// message boundary. Message mode does not, however, guarantee that a single <c>Read</c> returns the
/// whole message — a message larger than the supplied buffer is delivered in parts with
/// <c>IsMessageComplete</c> false until the last one. This class handles that accumulation, which is
/// the usual source of intermittent IPC corruption in message-mode code.
/// <para>
/// A message exceeding <see cref="PipeNames.MaxMessageBytes"/> is drained and rejected rather than
/// buffered, so an oversized or hostile write cannot exhaust memory in the LocalSystem process.
/// </para>
/// </remarks>
public static class PipeFraming
{
    private const int ChunkSize = 8192;

    /// <summary>
    /// Reads one complete message. Returns null on a clean disconnect.
    /// </summary>
    /// <exception cref="MessageTooLargeException">The message exceeded the size cap.</exception>
    public static async Task<string?> ReadMessageAsync(
        PipeStreamAdapter pipe,
        CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(ChunkSize);
        MemoryStream? accumulator = null;

        try
        {
            var read = await pipe.ReadAsync(rented.AsMemory(0, ChunkSize), ct).ConfigureAwait(false);

            if (read == 0)
            {
                return null; // peer closed
            }

            if (pipe.IsMessageComplete)
            {
                return Encoding.UTF8.GetString(rented, 0, read);
            }

            // Multi-part message: accumulate until the boundary.
            accumulator = new MemoryStream(ChunkSize * 2);
            accumulator.Write(rented, 0, read);

            while (!pipe.IsMessageComplete)
            {
                read = await pipe.ReadAsync(rented.AsMemory(0, ChunkSize), ct).ConfigureAwait(false);

                if (read == 0)
                {
                    // Peer vanished mid-message. A partial message is not usable.
                    return null;
                }

                if (accumulator.Length + read > PipeNames.MaxMessageBytes)
                {
                    // Drain the rest of the message so the stream stays aligned for the next one,
                    // then reject. Without the drain, the remainder would be read as a new message.
                    await DrainAsync(pipe, rented, ct).ConfigureAwait(false);
                    throw new MessageTooLargeException(PipeNames.MaxMessageBytes);
                }

                accumulator.Write(rented, 0, read);
            }

            return Encoding.UTF8.GetString(accumulator.GetBuffer(), 0, (int)accumulator.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            accumulator?.Dispose();
        }
    }

    /// <summary>Writes one complete message and flushes it.</summary>
    /// <exception cref="MessageTooLargeException">The serialized envelope exceeded the size cap.</exception>
    public static async Task WriteMessageAsync(
        PipeStreamAdapter pipe,
        string json,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        if (bytes.Length > PipeNames.MaxMessageBytes)
        {
            // Caught locally rather than sent, so we never put a message on the wire that the peer
            // is obliged to reject. Response builders that risk this must paginate (see SC-15c).
            throw new MessageTooLargeException(PipeNames.MaxMessageBytes);
        }

        await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task DrainAsync(PipeStreamAdapter pipe, byte[] buffer, CancellationToken ct)
    {
        while (!pipe.IsMessageComplete)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
        }
    }
}

/// <summary>Raised when a message exceeds <see cref="PipeNames.MaxMessageBytes"/>.</summary>
public sealed class MessageTooLargeException : Exception
{
    public MessageTooLargeException(int limitBytes)
        : base($"IPC message exceeded the {limitBytes} byte limit.") =>
        LimitBytes = limitBytes;

    public int LimitBytes { get; }
}

/// <summary>
/// Thin wrapper over a pipe stream so framing can be unit tested without a real pipe.
/// </summary>
/// <remarks>
/// <see cref="System.IO.Pipes.PipeStream.IsMessageComplete"/> is not virtual and message mode is
/// Windows-only, so testing the multi-part accumulation path requires this seam. The alternative —
/// testing framing only against a real pipe — would mean the accumulation logic is exercised on
/// Windows only, and it is exactly the logic that fails intermittently when wrong.
/// </remarks>
public abstract class PipeStreamAdapter
{
    public abstract bool IsMessageComplete { get; }

    public abstract ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);

    public abstract Task FlushAsync(CancellationToken ct);
}
