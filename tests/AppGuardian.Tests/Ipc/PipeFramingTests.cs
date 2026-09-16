using System.Text;
using AppGuardian.Shared.Ipc;

namespace AppGuardian.Tests.Ipc;

/// <summary>
/// In-memory <see cref="PipeStreamAdapter"/> that reproduces message-mode semantics.
/// </summary>
/// <remarks>
/// The point of this fake is <c>IsMessageComplete</c>. A real message-mode pipe returns a partial
/// message with that flag false when the caller's buffer is smaller than the message, and the
/// accumulation path in <see cref="PipeFraming"/> exists solely to handle it. That path is only
/// reachable on Windows with a message-mode pipe, so without this fake it would be untested — and it
/// is exactly the code whose failure mode is intermittent JSON corruption under load.
/// </remarks>
internal sealed class FakePipeStream : PipeStreamAdapter
{
    private readonly Queue<byte[]> _inbound = new();
    private byte[] _current = Array.Empty<byte>();
    private int _offset;
    private bool _complete = true;

    public List<byte[]> Written { get; } = new();

    public bool FlushCalled { get; private set; }

    /// <summary>
    /// When true, the stream reports 0 bytes after the first partial read, simulating a peer that
    /// died part-way through sending a message.
    /// </summary>
    public bool TruncateAfterFirstChunk { get; init; }

    private bool _firstChunkDelivered;

    /// <summary>Queues a message the reader will receive, split at the buffer size it asks for.</summary>
    public void EnqueueMessage(string text) => _inbound.Enqueue(Encoding.UTF8.GetBytes(text));

    public void EnqueueRaw(byte[] bytes) => _inbound.Enqueue(bytes);

    public override bool IsMessageComplete => _complete;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (TruncateAfterFirstChunk && _firstChunkDelivered)
        {
            return ValueTask.FromResult(0);
        }

        if (_offset >= _current.Length)
        {
            if (_inbound.Count == 0)
            {
                _complete = true;
                return ValueTask.FromResult(0); // peer closed
            }

            _current = _inbound.Dequeue();
            _offset = 0;
        }

        var take = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, take).CopyTo(buffer.Span);
        _offset += take;
        _firstChunkDelivered = true;

        // Mirrors the real flag: false until the final chunk of this message has been handed over.
        _complete = _offset >= _current.Length;

        return ValueTask.FromResult(take);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        Written.Add(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override Task FlushAsync(CancellationToken ct)
    {
        FlushCalled = true;
        return Task.CompletedTask;
    }
}

public sealed class PipeFramingTests
{
    [Fact(Timeout = 5000)]
    public async Task ReadMessage_returns_null_on_clean_disconnect()
    {
        var pipe = new FakePipeStream();

        var result = await PipeFraming.ReadMessageAsync(pipe, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact(Timeout = 5000)]
    public async Task ReadMessage_reads_a_message_that_fits_in_one_chunk()
    {
        var pipe = new FakePipeStream();
        pipe.EnqueueMessage("""{"kind":"request"}""");

        var result = await PipeFraming.ReadMessageAsync(pipe, CancellationToken.None);

        Assert.Equal("""{"kind":"request"}""", result);
    }

    [Fact(Timeout = 5000)]
    public async Task ReadMessage_reassembles_a_message_larger_than_the_read_chunk()
    {
        // 8 KB is the internal chunk size, so 30 KB forces several partial reads.
        var payload = new string('x', 30 * 1024);
        var json = $"{{\"kind\":\"request\",\"blob\":\"{payload}\"}}";

        var pipe = new FakePipeStream();
        pipe.EnqueueMessage(json);

        var result = await PipeFraming.ReadMessageAsync(pipe, CancellationToken.None);

        Assert.Equal(json, result);
    }

    [Fact(Timeout = 5000)]
    public async Task ReadMessage_keeps_successive_messages_separate()
    {
        var first = $"{{\"a\":\"{new string('1', 20 * 1024)}\"}}";
        const string second = """{"b":2}""";

        var pipe = new FakePipeStream();
        pipe.EnqueueMessage(first);
        pipe.EnqueueMessage(second);

        var a = await PipeFraming.ReadMessageAsync(pipe, CancellationToken.None);
        var b = await PipeFraming.ReadMessageAsync(pipe, CancellationToken.None);

        Assert.Equal(first, a);
        Assert.Equal(second, b);
    }

    [Fact(Timeout = 5000)]
    public async Task ReadMessage_rejects_an_oversized_message_and_stays_aligned()
    {
        var oversized = new string('x', PipeNames.MaxMessageBytes + 4096);
        const string next = """{"ok":true}""";

        var pipe = new FakePipeStream();
        pipe.EnqueueMessage(oversized);
        pipe.EnqueueMessage(next);

        await Assert.ThrowsAsync<MessageTooLargeException>(
            () => PipeFraming.ReadMessageAsync(pipe, CancellationToken.None));

        // The critical assertion: the remainder of the rejected message was drained, so the next
        // read returns the following message rather than the tail of the discarded one.
        var recovered = await PipeFraming.ReadMessageAsync(pipe, CancellationToken.None);

        Assert.Equal(next, recovered);
    }

    [Fact(Timeout = 5000)]
    public async Task ReadMessage_returns_null_when_the_peer_dies_mid_message()
    {
        // A truncated message must not be handed to the dispatcher as if it were complete — that
        // would surface as a JSON parse error on a message the peer never finished sending.
        var pipe = new FakePipeStream { TruncateAfterFirstChunk = true };
        pipe.EnqueueRaw(Encoding.UTF8.GetBytes(new string('x', 20 * 1024)));

        var result = await PipeFraming.ReadMessageAsync(pipe, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact(Timeout = 5000)]
    public async Task WriteMessage_writes_once_and_flushes()
    {
        var pipe = new FakePipeStream();

        await PipeFraming.WriteMessageAsync(pipe, """{"kind":"response"}""", CancellationToken.None);

        // One write per message matters: message mode makes each write a message boundary, so
        // splitting a message across two writes would deliver two messages.
        var written = Assert.Single(pipe.Written);
        Assert.Equal("""{"kind":"response"}""", Encoding.UTF8.GetString(written));
        Assert.True(pipe.FlushCalled);
    }

    [Fact(Timeout = 5000)]
    public async Task WriteMessage_rejects_an_oversized_payload_locally()
    {
        var pipe = new FakePipeStream();
        var json = new string('x', PipeNames.MaxMessageBytes + 1);

        await Assert.ThrowsAsync<MessageTooLargeException>(
            () => PipeFraming.WriteMessageAsync(pipe, json, CancellationToken.None));

        // Nothing reached the wire, so the peer is never obliged to reject a message we could see
        // was too large.
        Assert.Empty(pipe.Written);
    }
}
