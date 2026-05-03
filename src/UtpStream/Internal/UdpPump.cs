using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace UtpStream.Internal;

/// <summary>
/// Drives the libutp event loop on a dedicated thread:
/// <list type="bullet">
///   <item>Receives UDP datagrams and feeds them to <c>utp_process_udp</c>.</item>
///   <item>Calls <c>utp_check_timeouts</c> every ~10ms — libutp uses this
///         tick to open the congestion window and emit pacing-deferred
///         packets, so a low cadence is critical for throughput.</item>
///   <item>Drains posted actions from public APIs (everything libutp-related
///         must run here — libutp is not thread-safe).</item>
/// </list>
/// </summary>
internal sealed class UdpPump
{
    // Worst-case latency floor for posted actions and timeout ticks. The
    // µTP throughput in low-latency environments (loopback, LAN) is gated
    // by how often we drive utp_check_timeouts — each tick gives libutp a
    // chance to emit pacing-deferred packets and update the congestion
    // window, so this acts as the effective minimum RTT seen by µTP.
    // 1ms keeps idle CPU negligible (~1000 syscalls/s) while uncapping
    // the protocol on a fast link.
    private const int RecvTimeoutMs = 1;
    private const int TimeoutCheckIntervalMs = 1;

    private readonly UtpContext _ctx;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly ManualResetEventSlim _shutdownDone = new(false);
    private volatile bool _stopRequested;

    public UdpPump(UtpContext ctx)
    {
        _ctx = ctx;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"UtpPump@{ctx.LocalEndPoint}",
        };
    }

    public bool IsOnPumpThread => Thread.CurrentThread == _thread;

    public void Start() => _thread.Start();

    public void Post(Action action)
    {
        if (IsOnPumpThread)
        {
            action();
            return;
        }
        _inbox.Enqueue(action);
    }

    public void Stop()
    {
        if (_stopRequested) return;
        _stopRequested = true;
        // Unblock any pending Receive by closing the socket from outside;
        // the loop's catch block will exit cleanly.
        _shutdownDone.Wait();
    }

    private void Run()
    {
        // Cache the context for the lifetime of the pump thread so callbacks
        // can read UtpContext.PumpCurrent instead of paying for a userdata
        // P/Invoke + GCHandle dereference each time libutp invokes them.
        UtpContext.PumpCurrent = _ctx;
        try
        {
            _ctx.Udp.ReceiveTimeout = RecvTimeoutMs;
            var buf = new byte[65536];
            var saScratch = new SocketAddress(_ctx.Udp.AddressFamily);

            long nextTimeoutCheck = Environment.TickCount64 + TimeoutCheckIntervalMs;

            while (!_stopRequested)
            {
                DrainInbox();

                // Drain everything pending on the UDP socket without blocking.
                // After each datagram we tick libutp immediately — that lets
                // ACK-driven cwnd updates happen at native RTT instead of
                // queueing up behind a batch.
                bool gotAny = false;
                while (_ctx.Udp.Available > 0)
                {
                    int n = TryReceive(buf, saScratch);
                    if (n <= 0) break;
                    ProcessDatagram(buf, n, saScratch);
                    LibUtp.utp_check_timeouts(_ctx.Native);
                    gotAny = true;
                }

                if (!gotAny)
                {
                    // Kernel queue empty — block briefly so the thread doesn't
                    // spin, then tick regardless to advance retransmit timers.
                    int n = TryReceive(buf, saScratch);
                    if (n > 0)
                    {
                        ProcessDatagram(buf, n, saScratch);
                        LibUtp.utp_check_timeouts(_ctx.Native);
                    }

                    long now = Environment.TickCount64;
                    if (now >= nextTimeoutCheck)
                    {
                        LibUtp.utp_check_timeouts(_ctx.Native);
                        nextTimeoutCheck = now + TimeoutCheckIntervalMs;
                    }
                }

                LibUtp.utp_issue_deferred_acks(_ctx.Native);
            }

            DrainInbox();
            _ctx.DestroyNative();
        }
        finally
        {
            UtpContext.PumpCurrent = null;
            _shutdownDone.Set();
        }
    }

    private void DrainInbox()
    {
        while (_inbox.TryDequeue(out var action))
        {
            try { action(); }
            catch { /* swallowed: callers observe outcomes via TCS/Channel */ }
        }
        // Allocation-free dispatch path for hot operations like WriteAsync —
        // sockets schedule themselves on the context's flush queue and we
        // call DrainPendingInbox() on each one without going through a
        // delegate.
        _ctx.DrainPendingWriteFlushes();
    }

    private int TryReceive(byte[] buf, SocketAddress sa)
    {
        try
        {
            return _ctx.Udp.ReceiveFrom(buf, SocketFlags.None, sa);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut
                                     || e.SocketErrorCode == SocketError.WouldBlock)
        {
            return 0;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset
                                     || e.SocketErrorCode == SocketError.MessageSize)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            _stopRequested = true;
            return 0;
        }
    }

    private unsafe void ProcessDatagram(byte[] buf, int n, SocketAddress sa)
    {
        fixed (byte* pBuf = buf)
        fixed (byte* pSa = MemoryMarshal.AsBytes(sa.Buffer.Span))
        {
            LibUtp.utp_process_udp(_ctx.Native, (nint)pBuf, (nuint)n, (nint)pSa, sa.Size);
        }
    }
}
