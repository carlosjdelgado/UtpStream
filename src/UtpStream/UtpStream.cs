namespace UtpStream;

/// <summary>
/// A <see cref="Stream"/> over a µTP <see cref="UtpSocket"/>. Reads and writes
/// translate to libutp send/receive operations under the hood. Idiomatic
/// counterpart to <see cref="System.Net.Sockets.NetworkStream"/>.
/// </summary>
public sealed class UtpStream : Stream
{
    private readonly UtpSocket _socket;
    private readonly bool _ownsSocket;
    private bool _disposed;

    /// <param name="socket">The connected µTP socket to wrap.</param>
    /// <param name="ownsSocket">If true, disposing the stream disposes the socket.</param>
    public UtpStream(UtpSocket socket, bool ownsSocket = true)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _ownsSocket = ownsSocket;
    }

    public override bool CanRead => !_disposed;
    public override bool CanWrite => !_disposed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() { /* µTP has no app-level buffer to flush */ }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _socket.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArgs(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ValueTask(_socket.WriteAsync(buffer, cancellationToken));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArgs(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing && _ownsSocket)
            _socket.Dispose();
        base.Dispose(disposing);
    }
}
