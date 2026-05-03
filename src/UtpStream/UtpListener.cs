using System.Net;
using System.Threading.Channels;
using UtpStream.Internal;

namespace UtpStream;

/// <summary>
/// Listens for incoming µTP connections on a local <see cref="IPEndPoint"/>.
/// Idiomatic counterpart to <see cref="System.Net.Sockets.TcpListener"/>.
/// </summary>
public sealed class UtpListener : IDisposable
{
    private readonly UtpContext _ctx;
    // Multiple concurrent AcceptAsync callers are explicitly supported (a
    // common server pattern is N parallel accept loops), so we don't pin
    // SingleReader=true. The producer is always the pump callback, so we
    // can keep SingleWriter=true for a small fast-path optimisation.
    private readonly Channel<UtpSocket> _accepted =
        Channel.CreateUnbounded<UtpSocket>(new UnboundedChannelOptions { SingleWriter = true });
    private volatile bool _disposed;

    /// <summary>The local endpoint the listener is bound to.</summary>
    public IPEndPoint LocalEndPoint => _ctx.LocalEndPoint;

    private UtpListener(UtpContext ctx)
    {
        _ctx = ctx;
        ctx.AttachListener(this);
    }

    /// <summary>Bind a listener to <paramref name="endpoint"/> and start accepting.</summary>
    public static UtpListener Listen(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var ctx = UtpContext.Bind(endpoint);
        return new UtpListener(ctx);
    }

    /// <summary>Wait for the next inbound µTP connection.</summary>
    public async ValueTask<UtpSocket> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void Enqueue(UtpSocket sock) => _accepted.Writer.TryWrite(sock);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accepted.Writer.TryComplete();
        _ctx.DetachListener(this);
        _ctx.Dispose();
    }
}
