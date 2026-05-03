using System.Diagnostics;
using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace UtpStream.Tests;

public class LoopbackTests(ITestOutputHelper output)
{
    private static IPEndPoint AnyLoopback() => new(IPAddress.Loopback, 0);

    [Fact(Timeout = 15000)]
    public async Task Connect_And_Exchange_Bytes()
    {
        using var listener = UtpListener.Listen(AnyLoopback());
        var localEp = listener.LocalEndPoint;

        var acceptTask = listener.AcceptAsync().AsTask();
        using var client = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, localEp.Port));
        using var server = await acceptTask;

        Assert.True(client.Connected);
        Assert.True(server.Connected);

        await using var clientStream = client.GetStream();
        await using var serverStream = server.GetStream();

        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await clientStream.WriteAsync(payload);

        var recv = new byte[payload.Length];
        int total = 0;
        while (total < recv.Length)
        {
            int n = await serverStream.ReadAsync(recv.AsMemory(total, recv.Length - total));
            Assert.True(n > 0, "server stream closed before all bytes received");
            total += n;
        }

        Assert.Equal(payload, recv);
    }

    [Fact(Timeout = 15000)]
    public async Task Sync_Write_Async_Read()
    {
        using var listener = UtpListener.Listen(AnyLoopback());
        var port = listener.LocalEndPoint.Port;

        var acceptTask = listener.AcceptAsync().AsTask();
        using var client = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        using var server = await acceptTask;

        await using var clientStream = client.GetStream();
        await using var serverStream = server.GetStream();

        var data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        await Task.Run(() => clientStream.Write(data, 0, data.Length));
        output.WriteLine("sync write returned");

        var recv = new byte[4];
        int total = 0;
        while (total < 4)
        {
            int n = await serverStream.ReadAsync(recv.AsMemory(total));
            output.WriteLine($"async read returned {n}");
            Assert.True(n > 0);
            total += n;
        }
        Assert.Equal(data, recv);
    }

    [Fact(Timeout = 15000)]
    public async Task Async_Write_Sync_Read()
    {
        using var listener = UtpListener.Listen(AnyLoopback());
        var port = listener.LocalEndPoint.Port;

        var acceptTask = listener.AcceptAsync().AsTask();
        using var client = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        using var server = await acceptTask;

        await using var clientStream = client.GetStream();
        await using var serverStream = server.GetStream();

        var data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        await clientStream.WriteAsync(data);
        output.WriteLine("async write returned");

        // give pump time to deliver
        await Task.Delay(500);

        var recv = new byte[4];
        int got = await Task.Run(() => serverStream.Read(recv, 0, 4));
        output.WriteLine($"sync read returned {got}");
        Assert.Equal(4, got);
        Assert.Equal(data, recv);
    }

    [Fact(Timeout = 15000)]
    public async Task BinaryReader_BinaryWriter_RoundTrip()
    {
        using var listener = UtpListener.Listen(AnyLoopback());
        var port = listener.LocalEndPoint.Port;

        var acceptTask = listener.AcceptAsync().AsTask();
        using var client = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        using var server = await acceptTask;

        await using var clientStream = client.GetStream();
        await using var serverStream = server.GetStream();

        await Task.Run(() =>
        {
            using var bw = new BinaryWriter(clientStream, System.Text.Encoding.UTF8, leaveOpen: true);
            bw.Write(42);
            bw.Write(3.14);
            bw.Write("hola µTP");
            bw.Flush();
        });

        await Task.Delay(300);

        await Task.Run(() =>
        {
            using var br = new BinaryReader(serverStream, System.Text.Encoding.UTF8, leaveOpen: true);
            Assert.Equal(42, br.ReadInt32());
            Assert.Equal(3.14, br.ReadDouble());
            Assert.Equal("hola µTP", br.ReadString());
        });
    }

    /// <summary>
    /// Streams a deterministic 500 MiB pseudo-random payload from client to
    /// server in 64 KiB chunks and verifies every byte. Both sides drive a
    /// <see cref="Random"/> seeded identically, so we never have to hold the
    /// full payload in memory or on disk.
    /// </summary>
    [Fact(Timeout = 10 * 60 * 1000)]
    [Trait("Category", "Slow")]
    public async Task LargePayload_500MiB_RoundTrip()
    {
        const long TotalSize = 500L * 1024 * 1024;
        const int ChunkSize = 1 * 1024 * 1024;
        const int Seed = 0x5EAD;

        using var listener = UtpListener.Listen(AnyLoopback());
        var port = listener.LocalEndPoint.Port;

        var acceptTask = listener.AcceptAsync().AsTask();
        using var client = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        using var server = await acceptTask;

        await using var clientStream = client.GetStream();
        await using var serverStream = server.GetStream();

        var sw = Stopwatch.StartNew();

        var writeTask = Task.Run(async () =>
        {
            var rng = new Random(Seed);
            var buffer = new byte[ChunkSize];
            long sent = 0;
            while (sent < TotalSize)
            {
                int n = (int)Math.Min(ChunkSize, TotalSize - sent);
                rng.NextBytes(buffer.AsSpan(0, n));
                await clientStream.WriteAsync(buffer.AsMemory(0, n));
                sent += n;
            }
            return sent;
        });

        var readTask = Task.Run(async () =>
        {
            var rng = new Random(Seed);
            var expected = new byte[ChunkSize];
            var actual = new byte[ChunkSize];
            long received = 0;
            while (received < TotalSize)
            {
                int n = (int)Math.Min(ChunkSize, TotalSize - received);
                await serverStream.ReadExactlyAsync(actual.AsMemory(0, n));
                rng.NextBytes(expected.AsSpan(0, n));
                if (!actual.AsSpan(0, n).SequenceEqual(expected.AsSpan(0, n)))
                {
                    int firstMismatch = 0;
                    while (firstMismatch < n && actual[firstMismatch] == expected[firstMismatch])
                        firstMismatch++;
                    throw new Xunit.Sdk.XunitException(
                        $"Payload mismatch at byte offset {received + firstMismatch:N0} " +
                        $"(expected 0x{expected[firstMismatch]:X2}, got 0x{actual[firstMismatch]:X2})");
                }
                received += n;
            }
            return received;
        });

        var sent = await writeTask;
        var received = await readTask;
        sw.Stop();

        Assert.Equal(TotalSize, sent);
        Assert.Equal(TotalSize, received);

        double mibPerSecond = TotalSize / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
        output.WriteLine(
            $"Transferred {TotalSize:N0} bytes in {sw.Elapsed.TotalSeconds:F2}s " +
            $"({mibPerSecond:F2} MiB/s)");
    }

    /// <summary>
    /// Same 500 MiB payload, but submitted with a single
    /// <c>WriteAsync(payload)</c> call — no application-level chunking. The
    /// goal is to confirm the library's internal backpressure handling
    /// (partial <c>utp_write</c> + <c>UTP_STATE_WRITABLE</c>-driven
    /// drainage) works for arbitrarily large payloads.
    /// </summary>
    [Fact(Timeout = 10 * 60 * 1000)]
    [Trait("Category", "Slow")]
    public async Task LargePayload_500MiB_SingleWrite()
    {
        const long TotalSize = 500L * 1024 * 1024;
        const int Seed = 0x5EAD;

        using var listener = UtpListener.Listen(AnyLoopback());
        var port = listener.LocalEndPoint.Port;

        var acceptTask = listener.AcceptAsync().AsTask();
        using var client = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        using var server = await acceptTask;

        await using var clientStream = client.GetStream();
        await using var serverStream = server.GetStream();

        // Materialize the entire payload up-front. ~500 MiB resident on the
        // writer side; the library will copy it once internally during
        // WriteAsync, briefly doubling that.
        var payload = new byte[TotalSize];
        new Random(Seed).NextBytes(payload);

        var sw = Stopwatch.StartNew();

        var writeTask = Task.Run(async () =>
        {
            await clientStream.WriteAsync(payload);
            return (long)payload.Length;
        });

        var readTask = Task.Run(async () =>
        {
            // We can verify in chunks against a re-seeded RNG even though
            // the producer wrote in one shot — Random.NextBytes is
            // deterministic for a given seed regardless of call sizes.
            const int VerifyChunk = 1 * 1024 * 1024;
            var rng = new Random(Seed);
            var expected = new byte[VerifyChunk];
            var actual = new byte[VerifyChunk];
            long received = 0;
            while (received < TotalSize)
            {
                int n = (int)Math.Min(VerifyChunk, TotalSize - received);
                await serverStream.ReadExactlyAsync(actual.AsMemory(0, n));
                rng.NextBytes(expected.AsSpan(0, n));
                if (!actual.AsSpan(0, n).SequenceEqual(expected.AsSpan(0, n)))
                {
                    int firstMismatch = 0;
                    while (firstMismatch < n && actual[firstMismatch] == expected[firstMismatch])
                        firstMismatch++;
                    throw new Xunit.Sdk.XunitException(
                        $"Payload mismatch at byte offset {received + firstMismatch:N0} " +
                        $"(expected 0x{expected[firstMismatch]:X2}, got 0x{actual[firstMismatch]:X2})");
                }
                received += n;
            }
            return received;
        });

        var sent = await writeTask;
        var received = await readTask;
        sw.Stop();

        Assert.Equal(TotalSize, sent);
        Assert.Equal(TotalSize, received);

        double mibPerSecond = TotalSize / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
        output.WriteLine(
            $"Single WriteAsync({TotalSize:N0}) completed in {sw.Elapsed.TotalSeconds:F2}s " +
            $"({mibPerSecond:F2} MiB/s)");
    }

    /// <summary>
    /// Reader pulls bytes in pseudo-random small chunks (1..2048 bytes) while
    /// the writer sends a large deterministic payload. Stresses the internal
    /// RX cursor (<c>_rxCurrent.Offset</c>/<c>Length</c>) — failures here
    /// would mean bytes are lost, duplicated, or reordered when a chunk
    /// boundary doesn't align with a Read.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Read_With_Random_Small_Sizes_Preserves_Bytes()
    {
        const long TotalSize = 16L * 1024 * 1024; // 16 MiB
        const int Seed = 0xBEEF;
        const int ReadSizeSeed = 0xC0FFEE;

        using var listener = UtpListener.Listen(AnyLoopback());
        var port = listener.LocalEndPoint.Port;

        var acceptTask = listener.AcceptAsync().AsTask();
        using var client = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        using var server = await acceptTask;

        await using var clientStream = client.GetStream();
        await using var serverStream = server.GetStream();

        var writeTask = Task.Run(async () =>
        {
            var rng = new Random(Seed);
            var buffer = new byte[64 * 1024];
            long sent = 0;
            while (sent < TotalSize)
            {
                int n = (int)Math.Min(buffer.Length, TotalSize - sent);
                rng.NextBytes(buffer.AsSpan(0, n));
                await clientStream.WriteAsync(buffer.AsMemory(0, n));
                sent += n;
            }
        });

        var readTask = Task.Run(async () =>
        {
            var contentRng = new Random(Seed);
            var sizeRng = new Random(ReadSizeSeed);
            var biggest = new byte[2048];
            // Maintain a rolling expected window — we only need enough
            // to cover the next read.
            var expectedTrail = new byte[2048];
            long received = 0;
            while (received < TotalSize)
            {
                int desired = sizeRng.Next(1, biggest.Length + 1);
                int toRead = (int)Math.Min(desired, TotalSize - received);
                await serverStream.ReadExactlyAsync(biggest.AsMemory(0, toRead));
                contentRng.NextBytes(expectedTrail.AsSpan(0, toRead));
                if (!biggest.AsSpan(0, toRead).SequenceEqual(expectedTrail.AsSpan(0, toRead)))
                {
                    int firstMismatch = 0;
                    while (firstMismatch < toRead &&
                           biggest[firstMismatch] == expectedTrail[firstMismatch])
                        firstMismatch++;
                    throw new Xunit.Sdk.XunitException(
                        $"Mismatch at byte offset {received + firstMismatch:N0} " +
                        $"(read size was {toRead}, expected 0x{expectedTrail[firstMismatch]:X2}, " +
                        $"got 0x{biggest[firstMismatch]:X2})");
                }
                received += toRead;
            }
            return received;
        });

        await writeTask;
        var received = await readTask;
        Assert.Equal(TotalSize, received);
    }

    /// <summary>
    /// Writer issues many small WriteAsync calls; reader pulls big chunks.
    /// Each small write is &lt; MTU so libutp may coalesce; the reader receives
    /// arbitrary chunk boundaries. Verifies that bytes appear in the right
    /// order with no loss / duplication, and exercises the
    /// <c>_writeQueue</c> + <c>UTP_STATE_WRITABLE</c> partial-write drain
    /// path under steady backpressure.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Many_Small_Writes_Then_Big_Reads_Preserve_Order()
    {
        const long TotalSize = 4L * 1024 * 1024; // 4 MiB across many small writes
        const int SmallWriteSize = 37;           // odd size, won't align with anything
        const int BigReadSize = 64 * 1024;
        const int Seed = 0xFADE;

        using var listener = UtpListener.Listen(AnyLoopback());
        var port = listener.LocalEndPoint.Port;

        var acceptTask = listener.AcceptAsync().AsTask();
        using var client = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        using var server = await acceptTask;

        await using var clientStream = client.GetStream();
        await using var serverStream = server.GetStream();

        var writeTask = Task.Run(async () =>
        {
            var rng = new Random(Seed);
            var buf = new byte[SmallWriteSize];
            long sent = 0;
            while (sent < TotalSize)
            {
                int n = (int)Math.Min(SmallWriteSize, TotalSize - sent);
                rng.NextBytes(buf.AsSpan(0, n));
                await clientStream.WriteAsync(buf.AsMemory(0, n));
                sent += n;
            }
        });

        var readTask = Task.Run(async () =>
        {
            var rng = new Random(Seed);
            var actual = new byte[BigReadSize];
            var expected = new byte[BigReadSize];
            long received = 0;
            while (received < TotalSize)
            {
                int n = (int)Math.Min(BigReadSize, TotalSize - received);
                await serverStream.ReadExactlyAsync(actual.AsMemory(0, n));
                rng.NextBytes(expected.AsSpan(0, n));
                if (!actual.AsSpan(0, n).SequenceEqual(expected.AsSpan(0, n)))
                {
                    int firstMismatch = 0;
                    while (firstMismatch < n && actual[firstMismatch] == expected[firstMismatch])
                        firstMismatch++;
                    throw new Xunit.Sdk.XunitException(
                        $"Order broken at byte offset {received + firstMismatch:N0}");
                }
                received += n;
            }
            return received;
        });

        await writeTask;
        var received = await readTask;
        Assert.Equal(TotalSize, received);
    }

    /// <summary>
    /// Multiple clients connect to the same listener and each transfers its
    /// own deterministic payload identified by a per-connection seed. The
    /// server verifies every connection received exactly its own bytes in
    /// order — no cross-talk between sockets sharing the same UDP context.
    ///
    /// <para>
    /// <b>Note:</b> handshakes are staggered (~50 ms apart). When two
    /// inbound SYNs land within a couple of milliseconds of each other on
    /// the same listener, libutp upstream has a routing race that mixes
    /// data between the two new sockets — see "Limitations" in the README.
    /// In production, real network jitter and per-client connection start
    /// times naturally provide the gap; this test makes that explicit so
    /// the assertion exercises only the framing/integrity invariants and
    /// not libutp's accept-time race.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Multiple_Concurrent_Connections_Stay_Isolated()
    {
        const int ConnectionCount = 4;
        const int BytesPerConnection = 1 * 1024 * 1024;
        const int HandshakeStaggerMs = 50;

        using var listener = UtpListener.Listen(AnyLoopback());
        var port = listener.LocalEndPoint.Port;

        // Server side: accept N connections concurrently, verify each.
        var serverTasks = new Task<(int seed, long bytes)>[ConnectionCount];
        for (int i = 0; i < ConnectionCount; i++)
        {
            serverTasks[i] = Task.Run(async () =>
            {
                using var sock = await listener.AcceptAsync();
                await using var s = sock.GetStream();

                var header = new byte[4];
                await s.ReadExactlyAsync(header);
                int seed = BitConverter.ToInt32(header);

                var rng = new Random(seed);
                var actual = new byte[64 * 1024];
                var expected = new byte[64 * 1024];
                long got = 0;
                while (got < BytesPerConnection)
                {
                    int n = (int)Math.Min(actual.Length, BytesPerConnection - got);
                    await s.ReadExactlyAsync(actual.AsMemory(0, n));
                    rng.NextBytes(expected.AsSpan(0, n));
                    if (!actual.AsSpan(0, n).SequenceEqual(expected.AsSpan(0, n)))
                    {
                        throw new Xunit.Sdk.XunitException(
                            $"Cross-talk detected: seed={seed} got wrong bytes at offset {got}");
                    }
                    got += n;
                }
                return (seed, got);
            });
        }

        var clientTasks = new Task[ConnectionCount];
        for (int i = 0; i < ConnectionCount; i++)
        {
            int seed = 0x10000 + i;
            // Stagger the handshakes — see method doc comment for the why.
            if (i > 0) await Task.Delay(HandshakeStaggerMs);
            clientTasks[i] = Task.Run(async () =>
            {
                using var sock = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
                await using var s = sock.GetStream();

                await s.WriteAsync(BitConverter.GetBytes(seed));

                var rng = new Random(seed);
                var buf = new byte[64 * 1024];
                long sent = 0;
                while (sent < BytesPerConnection)
                {
                    int n = (int)Math.Min(buf.Length, BytesPerConnection - sent);
                    rng.NextBytes(buf.AsSpan(0, n));
                    await s.WriteAsync(buf.AsMemory(0, n));
                    sent += n;
                }
            });
        }

        await Task.WhenAll(clientTasks);
        var serverResults = await Task.WhenAll(serverTasks);

        var seedsSeen = serverResults.Select(r => r.seed).OrderBy(s => s).ToArray();
        var seedsExpected = Enumerable.Range(0, ConnectionCount).Select(i => 0x10000 + i).ToArray();
        Assert.Equal(seedsExpected, seedsSeen);
        foreach (var (_, bytes) in serverResults)
            Assert.Equal(BytesPerConnection, bytes);
    }
}
