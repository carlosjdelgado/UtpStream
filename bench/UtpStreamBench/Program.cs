using System.Diagnostics;
using System.Net;
using UtpStream;

// Single-process throughput benchmark with diagnostic tracing.
// Set BENCH_TRACE=1 to get per-second progress dumps to stderr.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: bench <bytes>");
    return 2;
}

long total = long.Parse(args[0]);
bool trace = Environment.GetEnvironmentVariable("BENCH_TRACE") == "1";

void TraceLine(string s)
{
    if (trace) Console.Error.WriteLine($"[t+{Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency:F2}] {s}");
}

TraceLine("start");

using var listener = UtpListener.Listen(new IPEndPoint(IPAddress.Loopback, 0));
var port = listener.LocalEndPoint.Port;
TraceLine($"listener bound on {port}");

var acceptTask = listener.AcceptAsync().AsTask();
TraceLine("calling ConnectAsync");
using var client = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
TraceLine($"connected: client={client.RemoteEndPoint}");
using var server = await acceptTask;
TraceLine($"accepted: server.remote={server.RemoteEndPoint}");

await using var clientStream = client.GetStream();
await using var serverStream = server.GetStream();

var payload = new byte[total];
TraceLine($"payload allocated ({total / 1048576} MiB)");

long bytesSentReturned = 0;
long bytesReceived = 0;

var sw = Stopwatch.StartNew();

var sendTask = Task.Run(async () =>
{
    TraceLine("sender: WriteAsync starting");
    await clientStream.WriteAsync(payload);
    TraceLine("sender: WriteAsync returned");
    Interlocked.Exchange(ref bytesSentReturned, payload.Length);
});

var recvTask = Task.Run(async () =>
{
    var buf = new byte[64 * 1024];
    long got = 0;
    while (got < total)
    {
        int n = await serverStream.ReadAsync(buf);
        if (n <= 0) break;
        got += n;
        Interlocked.Exchange(ref bytesReceived, got);
    }
    TraceLine($"receiver: done, got={got}");
    return got;
});

// Trace heartbeat (only fires if BENCH_TRACE=1)
var monitorCts = new CancellationTokenSource();
var monitorTask = trace ? Task.Run(async () =>
{
    while (!monitorCts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(1000, monitorCts.Token); } catch { break; }
        TraceLine($"heartbeat: sent_returned={Volatile.Read(ref bytesSentReturned):N0} received={Volatile.Read(ref bytesReceived):N0}");
    }
}) : Task.CompletedTask;

await sendTask;
TraceLine("await sendTask done");
var received = await recvTask;
TraceLine("await recvTask done");
sw.Stop();
monitorCts.Cancel();
try { await monitorTask; } catch { }

Console.WriteLine(
    $"managed: received {received:N0} bytes in {sw.Elapsed.TotalSeconds:F2} s " +
    $"({received / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0):F2} MiB/s)");
return received == total ? 0 : 1;
