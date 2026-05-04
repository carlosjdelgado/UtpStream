using System.Diagnostics;
using System.Net;
using UtpStream;

// Single-process throughput benchmark with diagnostic tracing.
// Set BENCH_TRACE=1 to get per-second progress dumps to stderr.
//
// Throughput is measured over the final (1 - SKIP_FRAC) of the transfer,
// starting the clock from the receiver's perspective once SKIP_FRAC bytes
// have arrived. This discards the LEDBAT slow-start phase and reports only
// the steady-state window, mirroring native_bench's measurement approach.

const double SkipFrac = 0.20;

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

var sendTask = Task.Run(async () =>
{
    TraceLine("sender: WriteAsync starting");
    await clientStream.WriteAsync(payload);
    TraceLine("sender: WriteAsync returned");
});

long skipBytes = (long)(total * SkipFrac);

var recvTask = Task.Run(async () =>
{
    var buf = new byte[64 * 1024];
    var sw = new Stopwatch();
    long got = 0;

    while (got < total)
    {
        int n = await serverStream.ReadAsync(buf);
        if (n <= 0) break;
        got += n;

        // Start timing once we've crossed the skip threshold — the CC
        // has had enough time to leave slow-start and reach steady state.
        if (!sw.IsRunning && got >= skipBytes)
            sw.Start();
    }

    sw.Stop();
    TraceLine($"receiver: done, got={got}");
    return (Bytes: got - skipBytes, Elapsed: sw.Elapsed.TotalSeconds);
});

await sendTask;
TraceLine("await sendTask done");
var (steadyBytes, secs) = await recvTask;
TraceLine("await recvTask done");

double mibps = steadyBytes / secs / (1024.0 * 1024.0);
Console.WriteLine(
    $"managed: received {steadyBytes:N0} bytes in {secs:F2} s ({mibps:F2} MiB/s)");
return steadyBytes > 0 ? 0 : 1;
