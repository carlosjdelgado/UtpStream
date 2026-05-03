using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using UtpStream.Internal;

namespace UtpStream;

/// <summary>
/// A µTP socket — analogous to <see cref="System.Net.Sockets.Socket"/> but
/// over the µTP/UDP protocol. Use <see cref="ConnectAsync"/> to dial an
/// endpoint, or obtain one from <see cref="UtpListener.AcceptAsync"/>.
/// Wrap with <see cref="GetStream"/> to do reads/writes via a
/// <see cref="System.IO.Stream"/>.
/// </summary>
public sealed class UtpSocket : IDisposable
{
    private readonly UtpContext _ctx;
    private readonly bool _ownsContext;
    private readonly TaskCompletionSource _connectTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Receive path is hot: every datagram libutp hands us turns into one
    // RxChunk. We rent the backing array from ArrayPool so we don't churn
    // the GC under sustained transfer.
    private readonly Channel<RxChunk> _rx =
        Channel.CreateUnbounded<RxChunk>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<PendingWrite> _writeQueue = new();
    // Concurrent inbox for new writes — touched by callers from any
    // thread. The pump drains this on each iteration without paying for
    // a delegate allocation per WriteAsync.
    private readonly ConcurrentQueue<PendingWrite> _pendingInbox = new();
    private int _writeFlushScheduled;

    private RxChunk _rxCurrent;
    private bool _hasRxCurrent;
    private long _bufferedBytes;
    private Exception? _terminal;
    private volatile bool _disposed;
    private GCHandle _selfGCHandle;

    private struct RxChunk
    {
        public byte[] Buffer;
        public int Offset;
        public int Length;
    }

    internal nint NativeHandle { get; private set; }
    internal UtpContext Context => _ctx;


    /// <summary>
    /// Bind the libutp socket to this managed wrapper via utp_set_userdata.
    /// Callbacks then resolve us with a single utp_get_userdata + GCHandle
    /// deref instead of a Dictionary lookup per packet.
    /// </summary>
    internal void AttachNative(nint native)
    {
        NativeHandle = native;
        _selfGCHandle = GCHandle.Alloc(this);
        LibUtp.utp_set_userdata(native, GCHandle.ToIntPtr(_selfGCHandle));
    }

    private void DetachNative()
    {
        NativeHandle = 0;
        if (_selfGCHandle.IsAllocated)
            _selfGCHandle.Free();
    }

    /// <summary>The remote endpoint this socket is connected to.</summary>
    public IPEndPoint? RemoteEndPoint { get; private set; }

    /// <summary>True once the µTP three-way handshake has completed.</summary>
    public bool Connected { get; private set; }

    private UtpSocket(UtpContext ctx, bool ownsContext)
    {
        _ctx = ctx;
        _ownsContext = ownsContext;
    }

    /// <summary>
    /// Dial a remote µTP endpoint. Completes when the µTP handshake finishes.
    /// </summary>
    public static async Task<UtpSocket> ConnectAsync(IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);

        var ctx = UtpContext.Ephemeral(remote.AddressFamily);
        var sock = new UtpSocket(ctx, ownsContext: true) { RemoteEndPoint = remote };

        ctx.Post(() => sock.StartConnect(remote));

        try
        {
            await using var _ = cancellationToken.Register(() =>
                sock._connectTcs.TrySetCanceled(cancellationToken)).ConfigureAwait(false);
            await sock._connectTcs.Task.ConfigureAwait(false);
            return sock;
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }

    private unsafe void StartConnect(IPEndPoint remote)
    {
        var native = LibUtp.utp_create_socket(_ctx.Native);
        if (native == 0)
        {
            _connectTcs.TrySetException(new InvalidOperationException("utp_create_socket failed"));
            return;
        }
        AttachNative(native);

        var sa = remote.Serialize();
        fixed (byte* p = MemoryMarshal.AsBytes(sa.Buffer.Span))
        {
            int rc = LibUtp.utp_connect(native, (nint)p, sa.Size);
            if (rc < 0)
                _connectTcs.TrySetException(new InvalidOperationException($"utp_connect failed (rc={rc})"));
        }
    }

    internal static UtpSocket AdoptIncoming(UtpContext ctx, nint native, SocketAddress remote)
    {
        var sock = new UtpSocket(ctx, ownsContext: false)
        {
            Connected = true,
            RemoteEndPoint = ParseEndpoint(remote),
        };
        sock.AttachNative(native);
        sock._connectTcs.TrySetResult();
        return sock;
    }

    private static IPEndPoint? ParseEndpoint(SocketAddress sa)
    {
        try
        {
            var template = sa.Family == AddressFamily.InterNetworkV6
                ? new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0);
            return (IPEndPoint)template.Create(sa);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Wrap this socket in a <see cref="System.IO.Stream"/>.</summary>
    public UtpStream GetStream() => new(this);

    // ----- inbound (called by UtpContext callbacks on the pump thread) -----

    internal void OnRead(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty) return;
        var rented = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(rented);
        Interlocked.Add(ref _bufferedBytes, data.Length);
        var chunk = new RxChunk { Buffer = rented, Offset = 0, Length = data.Length };
        if (!_rx.Writer.TryWrite(chunk))
            ArrayPool<byte>.Shared.Return(rented);
    }

    internal void OnStateChange(int state)
    {
        switch (state)
        {
            case LibUtp.UTP_STATE_CONNECT:
                Connected = true;
                _connectTcs.TrySetResult();
                FlushWriteQueue();
                break;
            case LibUtp.UTP_STATE_WRITABLE:
                FlushWriteQueue();
                break;
            case LibUtp.UTP_STATE_EOF:
                _rx.Writer.TryComplete();
                break;
            case LibUtp.UTP_STATE_DESTROYING:
                _rx.Writer.TryComplete(_terminal);
                FailPendingWrites(_terminal ?? new ObjectDisposedException(nameof(UtpSocket)));
                DetachNative();
                break;
        }
    }

    internal void OnError(int errorCode)
    {
        _terminal = new UtpException((UtpErrorCode)errorCode);
        _connectTcs.TrySetException(_terminal);
        _rx.Writer.TryComplete(_terminal);
        FailPendingWrites(_terminal);
    }

    internal long AvailableInboundBuffer
    {
        // Reported to libutp via UTP_GET_READ_BUFFER_SIZE. libutp interprets
        // this as "bytes already buffered awaiting consumption" and computes
        // the advertised receive window as `opt_rcvbuf - this`. So we report
        // bytes still pending in our channel — return 0 means "I'm caught
        // up, peer can send freely".
        get => Math.Max(0, Volatile.Read(ref _bufferedBytes));
    }

    // ----- read path (used by UtpStream) -----

    internal async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty) return 0;
        if (_terminal is not null) throw _terminal;

        if (!_hasRxCurrent)
        {
            if (!await _rx.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_terminal is not null) throw _terminal;
                return 0; // graceful EOF
            }
            if (!_rx.Reader.TryRead(out _rxCurrent))
                return 0;
            _hasRxCurrent = true;
        }

        int n = Math.Min(buffer.Length, _rxCurrent.Length);
        _rxCurrent.Buffer.AsSpan(_rxCurrent.Offset, n).CopyTo(buffer.Span);
        Interlocked.Add(ref _bufferedBytes, -n);

        if (n == _rxCurrent.Length)
        {
            ArrayPool<byte>.Shared.Return(_rxCurrent.Buffer);
            _hasRxCurrent = false;
        }
        else
        {
            _rxCurrent.Offset += n;
            _rxCurrent.Length -= n;
        }
        return n;
    }

    // ----- write path (used by UtpStream) -----

    // We don't copy the caller's buffer — same contract as NetworkStream:
    // the buffer must remain valid and unmodified until the returned task
    // completes. libutp copies into its own send buffer inside utp_write,
    // so we only need to keep the memory pinned for the duration of each
    // utp_write call (not across await points).
    private sealed class PendingWrite(ReadOnlyMemory<byte> data, TaskCompletionSource tcs)
    {
        public readonly ReadOnlyMemory<byte> Data = data;
        public readonly TaskCompletionSource Tcs = tcs;
        public int Offset;
    }

    internal Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_terminal is not null) return Task.FromException(_terminal);
        if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(UtpSocket)));
        if (data.IsEmpty) return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingInbox.Enqueue(new PendingWrite(data, tcs));

        // Hot-path: if we're already on the pump thread, drain inline (same
        // as the old _ctx.Post short-circuit). Otherwise schedule the
        // socket once on the pump's per-context flush list — no delegate,
        // no closure, no display class allocations.
        if (_ctx.IsOnPumpThread)
        {
            DrainPendingInbox();
        }
        else if (Interlocked.Exchange(ref _writeFlushScheduled, 1) == 0)
        {
            _ctx.EnqueueWriteFlush(this);
        }
        return cancellationToken.CanBeCanceled
            ? tcs.Task.WaitAsync(cancellationToken)
            : tcs.Task;
    }

    /// <summary>
    /// Called from the pump thread (via <see cref="UtpContext.DrainPendingWriteFlushes"/>)
    /// or inline from <see cref="WriteAsync"/> when already on the pump thread.
    /// </summary>
    internal void DrainPendingInbox()
    {
        // Reset the scheduling flag *before* draining so any concurrent
        // WriteAsync that beats us will re-schedule (preventing lost
        // wake-ups).
        Interlocked.Exchange(ref _writeFlushScheduled, 0);
        while (_pendingInbox.TryDequeue(out var pending))
        {
            SubmitWrite(pending);
        }
    }

    private void SubmitWrite(PendingWrite pending)
    {
        if (_writeQueue.Count > 0)
        {
            _writeQueue.Enqueue(pending);
            return;
        }
        if (TryFlushOne(pending))
            _writeQueue.Enqueue(pending);
    }

    private void FlushWriteQueue()
    {
        while (_writeQueue.Count > 0)
        {
            var head = _writeQueue.Peek();
            if (TryFlushOne(head))
                return; // still partial, leave at head
            _writeQueue.Dequeue();
        }
    }

    /// <summary>
    /// Returns true if <paramref name="pending"/> is still partial (some
    /// bytes remain to be written). Returns false if it's done — either
    /// because all bytes were accepted (TCS completed successfully) or an
    /// error occurred (TCS faulted).
    /// </summary>
    private unsafe bool TryFlushOne(PendingWrite pending)
    {
        if (NativeHandle == 0)
        {
            pending.Tcs.TrySetException(_terminal ?? new ObjectDisposedException(nameof(UtpSocket)));
            return false;
        }

        int len = pending.Data.Length - pending.Offset;
        nint written;
        // Fast path: if the caller's Memory<byte> is backed by a managed
        // array (the overwhelmingly common case — byte[]/AsMemory etc.),
        // 'fixed' is a JIT intrinsic with no allocation. Pin() would
        // otherwise allocate a MemoryHandle's pinning state per call.
        if (MemoryMarshal.TryGetArray(pending.Data, out ArraySegment<byte> seg))
        {
            fixed (byte* p = seg.Array)
            {
                written = LibUtp.utp_write(NativeHandle,
                    (nint)(p + seg.Offset + pending.Offset), (nuint)len);
            }
        }
        else
        {
            using var pin = pending.Data.Slice(pending.Offset).Pin();
            written = LibUtp.utp_write(NativeHandle, (nint)pin.Pointer, (nuint)len);
        }

        if (written < 0)
        {
            pending.Tcs.TrySetException(new IOException($"utp_write returned {written}"));
            return false;
        }

        pending.Offset += (int)written;
        if (pending.Offset >= pending.Data.Length)
        {
            pending.Tcs.TrySetResult();
            return false;
        }
        return true; // partial
    }

    private void FailPendingWrites(Exception ex)
    {
        while (_writeQueue.Count > 0)
            _writeQueue.Dequeue().Tcs.TrySetException(ex);
    }

    // ----- shutdown -----

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connectTcs.TrySetCanceled();

        var native = NativeHandle;
        if (native != 0)
        {
            _ctx.Post(() =>
            {
                if (NativeHandle != 0)
                    LibUtp.utp_close(NativeHandle);
            });
        }

        if (_ownsContext)
            _ctx.Dispose();
    }
}
