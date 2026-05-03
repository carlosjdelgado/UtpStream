using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UtpStream.Internal;

/// <summary>
/// Owns a <c>utp_context*</c> together with its UDP socket and the pump
/// thread that drives both. There is one <see cref="UtpContext"/> per
/// (local UDP endpoint) — outbound connections create a context with an
/// ephemeral bind, listeners create one with the requested bind.
///
/// All libutp entry points must be called from the pump thread; we route
/// requests from public APIs through <see cref="Post"/>.
/// </summary>
internal sealed class UtpContext : IDisposable
{
    private readonly Socket _udp;
    private readonly nint _ctx;
    private readonly UdpPump _pump;
    private readonly GCHandle _selfHandle;

    // Each utp_socket carries its UtpSocket wrapper via utp_set_userdata
    // (see UtpSocket.AttachNative), so callbacks resolve the wrapper with
    // utp_get_userdata + GCHandle deref — no dictionary lookup per packet.
    private readonly ConcurrentQueue<UtpSocket> _writeFlushPending = new();
    private UtpListener? _listener;

    // Reused SocketAddress instances for the SENDTO callback hot path.
    // libutp invokes the callback once per outgoing datagram; allocating
    // here would mean ~one alloc per ~1.4 KiB on a saturated link. The
    // callback always runs on the pump thread, so a non-thread-safe
    // scratch buffer is safe.
    private readonly SocketAddress _sendScratchV4 = new(AddressFamily.InterNetwork);
    private readonly SocketAddress _sendScratchV6 = new(AddressFamily.InterNetworkV6);

    public IPEndPoint LocalEndPoint { get; }
    public Socket Udp => _udp;
    public nint Native => _ctx;

    private UtpContext(IPEndPoint requested)
    {
        _udp = new Socket(requested.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        // Larger kernel UDP buffers — when libutp is allowed to emit in
        // bursts (e.g. one big WriteAsync filling cwnd in a single call),
        // a small SO_SNDBUF will silently drop datagrams and force the
        // protocol into retransmit/timeout cycles that look like deadlock.
        try
        {
            // Comfortable kernel UDP buffer — 4 MiB gives enough headroom
            // for bursts at ~200 MiB/s without inviting the heisenbug
            // we observed at 64 MiB (under a specific scheduling pattern,
            // libutp + the kernel can deadlock with both peers idle in
            // poll() and no further datagrams flowing).
            _udp.SendBufferSize = 4 * 1024 * 1024;
            _udp.ReceiveBufferSize = 4 * 1024 * 1024;
        }
        catch (SocketException) { }
        _udp.Bind(requested);
        LocalEndPoint = (IPEndPoint)_udp.LocalEndPoint!;

        _ctx = LibUtp.utp_init(LibUtp.UtpVersion);
        if (_ctx == 0)
        {
            _udp.Dispose();
            throw new InvalidOperationException("utp_init failed");
        }

        _selfHandle = GCHandle.Alloc(this);
        _ = LibUtp.utp_context_set_userdata(_ctx, GCHandle.ToIntPtr(_selfHandle));

        // Bump send/receive buffers well above libutp's 1 MiB defaults.
        //
        // SNDBUF caps the congestion window: too small and the protocol
        // can't fill a fast link.
        //
        // RCVBUF is more subtle. Our UTP_GET_READ_BUFFER_SIZE callback
        // reports how many bytes are queued for the application to read,
        // and libutp advertises (RCVBUF − queued) as the receive window.
        // If the application reader is briefly slower than the wire (e.g.
        // doing per-chunk verification or a sync I/O hop), the queue
        // fills, the advertised window hits zero, and the peer falls into
        // the zero-window-probe cycle (~15 s of stall per probe). 64 MiB
        // gives a wide cushion — at 100 MiB/s wire speed it tolerates
        // ~640 ms of reader slowdown before throttling kicks in.
        const int LargeBuffer = 64 * 1024 * 1024;
        LibUtp.utp_context_set_option(_ctx, LibUtp.UTP_SNDBUF, LargeBuffer);
        LibUtp.utp_context_set_option(_ctx, LibUtp.UTP_RCVBUF, LargeBuffer);
        // We deliberately leave UTP_TARGET_DELAY at its 100 ms default —
        // raising it would deactivate LEDBAT's delay-based congestion
        // control and break µTP's "yield to TCP" semantics. Slow path on
        // loopback is the price of that being correct.

        RegisterCallbacks();

        _pump = new UdpPump(this);
        _pump.Start();
    }

    public static UtpContext Bind(IPEndPoint endpoint) => new(endpoint);

    public static UtpContext Ephemeral(AddressFamily family) =>
        new(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

    public void AttachListener(UtpListener listener)
    {
        if (Interlocked.CompareExchange(ref _listener, listener, null) is not null)
            throw new InvalidOperationException("Context already has a listener attached.");
    }

    public void DetachListener(UtpListener listener)
    {
        Interlocked.CompareExchange(ref _listener, null, listener);
    }

    /// <summary>
    /// Hot path for <see cref="UtpSocket.WriteAsync"/>: schedules the
    /// socket to have its pending write inbox drained by the pump
    /// thread without allocating a delegate.
    /// </summary>
    internal void EnqueueWriteFlush(UtpSocket sock) => _writeFlushPending.Enqueue(sock);

    internal void DrainPendingWriteFlushes()
    {
        while (_writeFlushPending.TryDequeue(out var sock))
        {
            try { sock.DrainPendingInbox(); }
            catch { /* observed via individual TCSes */ }
        }
    }

    /// <summary>
    /// Run an action on the pump thread. libutp is not thread-safe; every
    /// call into the native API must be serialized through this.
    /// </summary>
    public void Post(Action action) => _pump.Post(action);

    public bool IsOnPumpThread => _pump.IsOnPumpThread;

    public void Dispose()
    {
        _pump.Stop();
        // utp_destroy must run on the pump thread; UdpPump.Stop drains it then
        // calls our finalizer hook below.
    }

    internal void DestroyNative()
    {
        if (_ctx != 0)
            LibUtp.utp_destroy(_ctx);
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
        _udp.Dispose();
    }

    // ----- callback wiring -----

    private unsafe void RegisterCallbacks()
    {
        SetCb(LibUtp.UTP_SENDTO, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbSendTo);
        SetCb(LibUtp.UTP_ON_READ, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbOnRead);
        SetCb(LibUtp.UTP_ON_STATE_CHANGE, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbOnStateChange);
        SetCb(LibUtp.UTP_ON_ERROR, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbOnError);
        SetCb(LibUtp.UTP_ON_FIREWALL, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbOnFirewall);
        SetCb(LibUtp.UTP_ON_ACCEPT, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbOnAccept);
        SetCb(LibUtp.UTP_GET_READ_BUFFER_SIZE, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbGetReadBufferSize);
        // UTP_GET_RANDOM is *critical*: libutp uses it to pick a 16-bit
        // conn_id seed when a new socket is created. If we don't register
        // it, libutp's `utp_call_get_random` returns 0 unconditionally —
        // and every socket sharing a context gets the same conn_id, which
        // makes libutp deliver inbound datagrams to the wrong socket once
        // you have two or more concurrent connections per listener.
        SetCb(LibUtp.UTP_GET_RANDOM, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbGetRandom);
        SetCb(LibUtp.UTP_GET_MICROSECONDS, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbGetMicroseconds);
        SetCb(LibUtp.UTP_GET_MILLISECONDS, (nint)(delegate* unmanaged[Cdecl]<UtpCallbackArguments*, ulong>)&CbGetMilliseconds);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetCb(int id, nint fp) => LibUtp.utp_set_callback(_ctx, id, fp);

    /// <summary>
    /// Cached on the pump thread by <see cref="UdpPump"/> before each loop
    /// iteration. Callbacks are always invoked from the pump thread, so we
    /// avoid the GCHandle.FromIntPtr + utp_context_get_userdata round-trip
    /// per callback by reading this <see cref="ThreadStaticAttribute"/>.
    /// </summary>
    [ThreadStatic]
    internal static UtpContext? PumpCurrent;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe UtpContext? FromArgs(UtpCallbackArguments* args)
    {
        // Fast path: pump thread sets PumpCurrent before invoking libutp,
        // so callbacks read it directly. Fall back to userdata for safety
        // in case a callback ever fires outside that window.
        var cur = PumpCurrent;
        if (cur is not null) return cur;
        var raw = LibUtp.utp_context_get_userdata(args->context);
        return raw == 0 ? null : (UtpContext?)GCHandle.FromIntPtr(raw).Target;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe UtpSocket? SocketFromArgs(UtpCallbackArguments* args)
    {
        // We stash a GCHandle in utp_set_userdata at AttachNative time, so
        // each callback only pays a single utp_get_userdata + GCHandle deref
        // — no Dictionary, no hashing, no lock.
        var raw = LibUtp.utp_get_userdata(args->socket);
        return raw == 0 ? null : (UtpSocket?)GCHandle.FromIntPtr(raw).Target;
    }

    private static unsafe SocketAddress ReadSockAddr(nint ptr, int len)
    {
        var family = (AddressFamily)Marshal.ReadInt16(ptr);
        var sa = new SocketAddress(family, len);
        new ReadOnlySpan<byte>((void*)ptr, len).CopyTo(sa.Buffer.Span);
        return sa;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbSendTo(UtpCallbackArguments* args)
    {
        var self = FromArgs(args);
        if (self is null) return 0;

        var span = new ReadOnlySpan<byte>((void*)args->buf, (int)args->len);
        // Pick the right scratch by reading the first 2 bytes of the
        // sockaddr — sa_family is portable across Linux/macOS/Windows
        // for IPv4 and IPv6 (the only families libutp uses).
        var family = (AddressFamily)(*(short*)args->address);
        var sa = family == AddressFamily.InterNetworkV6
            ? self._sendScratchV6
            : self._sendScratchV4;
        new ReadOnlySpan<byte>((void*)args->address, args->address_len)
            .CopyTo(sa.Buffer.Span);
        try
        {
            self._udp.SendTo(span, SocketFlags.None, sa);
        }
        catch (SocketException) { /* drop — libutp will retransmit */ }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbOnRead(UtpCallbackArguments* args)
    {
        var sock = SocketFromArgs(args);
        if (sock is null) return 0;

        var span = new ReadOnlySpan<byte>((void*)args->buf, (int)args->len);
        sock.OnRead(span);
        LibUtp.utp_read_drained(args->socket);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbOnStateChange(UtpCallbackArguments* args)
    {
        var sock = SocketFromArgs(args);
        if (sock is not null) sock.OnStateChange(args->state);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbOnError(UtpCallbackArguments* args)
    {
        var sock = SocketFromArgs(args);
        if (sock is not null) sock.OnError(args->error_code);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbOnFirewall(UtpCallbackArguments* args)
    {
        var self = FromArgs(args);
        // 0 = accept the incoming SYN, non-zero = reject.
        return self?._listener is not null ? 0UL : 1UL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbOnAccept(UtpCallbackArguments* args)
    {
        var self = FromArgs(args);
        var listener = self?._listener;
        if (self is null || listener is null) return 0;

        var addr = ReadSockAddr(args->address, args->address_len);
        var sock = UtpSocket.AdoptIncoming(self, args->socket, addr);
        listener.Enqueue(sock);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbGetRandom(UtpCallbackArguments* args)
        => (ulong)Random.Shared.NextInt64();

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbGetMicroseconds(UtpCallbackArguments* args)
        => (ulong)(Stopwatch.GetTimestamp() * (long)1_000_000.0 / Stopwatch.Frequency);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbGetMilliseconds(UtpCallbackArguments* args)
        => (ulong)Environment.TickCount64;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe ulong CbGetReadBufferSize(UtpCallbackArguments* args)
    {
        var sock = SocketFromArgs(args);
        if (sock is null) return 0;
        return (ulong)sock.AvailableInboundBuffer;
    }
}
