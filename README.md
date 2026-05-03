# UtpStream

A .NET library that exposes the [µTP / Micro Transport Protocol](https://www.bittorrent.org/beps/bep_0029.html) through familiar `Socket` and `Stream` abstractions, backed by [BitTorrent's `libutp`](https://github.com/bittorrent/libutp).

µTP is a UDP-based reliable transport with TCP-like semantics (ordered, reliable, congestion-controlled) and built-in LEDBAT congestion control. It's used by every major BitTorrent client and is well suited as a transport when you want to avoid TCP head-of-line blocking, deal with strict NATs, or yield to other traffic on the same link.

This package gives you µTP in idiomatic .NET: a `UtpSocket` analogous to `Socket`, a `UtpListener` analogous to `TcpListener`, and a `UtpStream : Stream` analogous to `NetworkStream` — plug a `BinaryReader`/`BinaryWriter` on top and you have a working communication channel.

---

## Status

Early but functional. The public API is small on purpose; if you have a use case that needs more knobs (per-socket buffer sizes, congestion-control tuning, IPv6 dual-stack, etc.), please open an issue.

---

## Performance

UtpStream tracks pure libutp closely. On a 500 MiB loopback transfer the .NET layer typically sits within ~13 % of a C reference using the same libutp build with identical protocol settings — the gap is the unavoidable cost of P/Invoke crossings, callback marshalling, and the managed RX queue.

See **[bench/RESULTS.md](bench/RESULTS.md)** for the full report (100 / 300 / 500 / 800 / 1024 MiB payloads, mean / min / max across multiple runs, and the system info the numbers were collected on). Regenerate any time with:

```bash
bench/generate_report.sh
```

---

## Installation

```bash
dotnet add package UtpStream
```

The NuGet package ships precompiled native binaries for these runtime identifiers under `runtimes/<rid>/native/`:

| Operating system | x64        | arm32 (ARMv7 hard-float) | arm64        |
|------------------|------------|--------------------------|--------------|
| Linux            | linux-x64  | linux-arm                | linux-arm64  |
| macOS            | osx-x64    | —                        | osx-arm64    |
| Windows          | win-x64    | —                        | win-arm64    |

The `linux-arm` build covers Raspberry Pi 2 / 3 / 4 / Zero 2 W running a 32-bit OS. Pi 3+ on a 64-bit OS uses `linux-arm64` instead. The original Pi 1 / Pi Zero / Pi Zero W (ARMv6) are **not supported** because .NET 10 itself drops ARMv6.

If your target RID is not listed, see [Building from source](#building-from-source) below.

---

## Quick start

### Server

```csharp
using System.Net;
using UtpStream;

using var listener = UtpListener.Listen(new IPEndPoint(IPAddress.Any, 9000));
Console.WriteLine($"Listening on {listener.LocalEndPoint}");

while (true)
{
    using var socket = await listener.AcceptAsync();
    _ = HandleClientAsync(socket);
}

static async Task HandleClientAsync(UtpSocket socket)
{
    await using var stream = socket.GetStream();
    var buffer = new byte[1024];
    int n;
    while ((n = await stream.ReadAsync(buffer)) > 0)
    {
        await stream.WriteAsync(buffer.AsMemory(0, n)); // echo back
    }
}
```

### Client

```csharp
using System.Net;
using System.Text;
using UtpStream;

using var socket = await UtpSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 9000));
await using var stream = socket.GetStream();

var payload = Encoding.UTF8.GetBytes("hello µTP\n");
await stream.WriteAsync(payload);

var buffer = new byte[1024];
int n = await stream.ReadAsync(buffer);
Console.WriteLine($"Echoed back: {Encoding.UTF8.GetString(buffer, 0, n)}");
```

---

## Examples

### BinaryReader / BinaryWriter

`UtpStream` is a regular `System.IO.Stream`, so any of the standard wrappers work on top of it.

```csharp
using var server = await listener.AcceptAsync();
await using var stream = server.GetStream();

using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
writer.Write(42);
writer.Write(3.14);
writer.Write("hola µTP");
writer.Flush();
```

```csharp
using var client = await UtpSocket.ConnectAsync(remote);
await using var stream = client.GetStream();

using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
int    i = reader.ReadInt32();
double d = reader.ReadDouble();
string s = reader.ReadString();
```

### Length-prefixed messages

A common pattern when you want discrete messages instead of a raw byte stream:

```csharp
static async Task SendMessageAsync(Stream s, byte[] message, CancellationToken ct = default)
{
    var header = BitConverter.GetBytes(message.Length);
    await s.WriteAsync(header, ct);
    await s.WriteAsync(message, ct);
}

static async Task<byte[]> ReceiveMessageAsync(Stream s, CancellationToken ct = default)
{
    var header = new byte[4];
    await s.ReadExactlyAsync(header, ct);
    int len = BitConverter.ToInt32(header);
    var body = new byte[len];
    await s.ReadExactlyAsync(body, ct);
    return body;
}
```

### Cancellation and timeouts

Every async operation accepts a `CancellationToken`. Combine with `CancellationTokenSource` for timeouts:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
using var socket = await UtpSocket.ConnectAsync(remote, cts.Token);
```

---

## Public API

The library exposes four public types — everything else (the libutp P/Invoke layer, the UDP pump thread, the callback marshalling) is `internal`.

### `UtpSocket`

```csharp
public sealed class UtpSocket : IDisposable
{
    public IPEndPoint? RemoteEndPoint { get; }
    public bool        Connected      { get; }

    public static Task<UtpSocket> ConnectAsync(IPEndPoint remote, CancellationToken ct = default);

    public UtpStream GetStream();
    public void      Dispose();
}
```

Use `ConnectAsync` to dial. Use `Dispose` to close. Inbound sockets are produced by `UtpListener.AcceptAsync` — you do not construct them directly.

### `UtpListener`

```csharp
public sealed class UtpListener : IDisposable
{
    public IPEndPoint LocalEndPoint { get; }

    public static UtpListener Listen(IPEndPoint endpoint);

    public ValueTask<UtpSocket> AcceptAsync(CancellationToken ct = default);
    public void                 Dispose();
}
```

Bind to `IPAddress.Any` (or a specific interface) on whatever port you want; pass `0` to let the OS pick.

### `UtpStream`

```csharp
public sealed class UtpStream : Stream
{
    public UtpStream(UtpSocket socket, bool ownsSocket = true);
    // standard Stream members (Read/Write/ReadAsync/WriteAsync/Dispose...)
}
```

`CanSeek == false`, `Length`/`Position`/`Seek`/`SetLength` throw — same contract as `NetworkStream`. `Flush` is a no-op (µTP has no application-level buffer above the protocol stack).

### `UtpException`

```csharp
public sealed class UtpException : IOException
{
    public UtpErrorCode Code { get; }
}

public enum UtpErrorCode
{
    ConnectionRefused = 0,
    ConnectionReset   = 1,
    TimedOut          = 2,
}
```

Thrown by `ReadAsync`/`WriteAsync` and faulted on `ConnectAsync` when libutp surfaces a transport-level error.

---

## Architecture

```
+------------------+   +------------------+
|   UtpStream      |   |   UtpStream      |
+--------+---------+   +--------+---------+
         |                      |
+--------v---------+   +--------v---------+        public API
|   UtpSocket      |   |   UtpSocket      |
+--------+---------+   +--------+---------+
         |                      |
=========|======================|=================== internal boundary
         |                      |
+--------v----------------------v---------+
|              UtpContext                  |   (one per local UDP port)
|   - utp_context*                         |
|   - UDP socket                           |
|   - utp_socket* -> UtpSocket map         |
|   - registered libutp callbacks          |
+--------+---------------------------------+
         |
+--------v---------+
|     UdpPump      |   dedicated thread:
|   (one thread)   |     1. recv UDP -> utp_process_udp
|                  |     2. drain action inbox
|                  |     3. utp_check_timeouts every 500 ms
+--------+---------+
         |
+--------v---------+
|     libutp       |   native shared library (utp.dll / libutp.so / libutp.dylib)
+------------------+
```

A few decisions worth knowing about:

- **One pump thread per `UtpContext`.** libutp is not thread-safe, so every call into the native API (write, close, process_udp, check_timeouts) is serialized on the pump thread. Public APIs queue actions onto an inbox that the pump drains each iteration.
- **Outbound vs inbound contexts.** Outbound `UtpSocket.ConnectAsync` creates a fresh context bound to an ephemeral UDP port. A `UtpListener` creates a context bound to its requested endpoint and shares it with every accepted socket — the same way a TCP listening socket "owns" all the UDP packets on its port.
- **Backpressure goes both ways.** Outbound: `utp_write` may accept fewer bytes than requested when the send window is full; the remainder waits for `UTP_STATE_WRITABLE` from libutp. Inbound: the `UTP_GET_READ_BUFFER_SIZE` callback reports how many bytes are buffered awaiting the application, which libutp uses to compute the advertised receive window.
- **Native library resolution.** A `[ModuleInitializer]` installs a `DllImportResolver` that looks for `utp` under `runtimes/<rid>/native/` next to the assembly, trying both the SDK-reported RID and a portable fallback (so distro-specific RIDs like `ubuntu.24.04-x64` still resolve to the `linux-x64` payload shipped in the package).

---

## Building from source

You need:

- .NET SDK 10.0 or later
- CMake 3.16 or later
- A C++11 compiler (gcc/clang/MSVC)
- git (for the libutp submodule)

```bash
git clone --recurse-submodules https://github.com/kibarai/UtpStream
cd UtpStream

# 1. Build the native library for your machine
cmake -S native -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release -j

# 2. Stage it where the .NET project expects it
mkdir -p src/UtpStream/runtimes/linux-x64/native     # adjust RID to taste
cp build/libutp.so src/UtpStream/runtimes/linux-x64/native/

# 3. Build and test
dotnet test UtpStream.slnx -c Release
```

The `runtimes/<rid>/native/` directory is `.gitignore`d — CI is responsible for populating it across all six supported RIDs before producing the NuGet package.

### Producing a NuGet package locally

After staging native binaries for every RID you care about:

```bash
dotnet pack src/UtpStream/UtpStream.csproj -c Release -o nupkg
```

The resulting `.nupkg` contains all the staged binaries under `runtimes/<rid>/native/`. The .NET runtime picks the right one at load time on the consumer's machine.

---

## Continuous integration

The repository ships a single GitHub Actions workflow at `.github/workflows/build.yml` that does the same thing CI-wide:

1. **`native`** matrix job: compiles libutp on each of the six target runners (`ubuntu-24.04`, `ubuntu-24.04-arm`, `macos-13`, `macos-14`, `windows-2022`, `windows-11-arm`) and uploads the resulting shared library as an artifact.
2. **`pack`** job: downloads every native artifact, stages them under `src/UtpStream/runtimes/<rid>/native/`, runs `dotnet test` (linux-x64), then `dotnet pack`.
3. On a tag push (`v*`), if the `NUGET_API_KEY` repo secret is set, publishes to nuget.org.

---

## Project layout

```
UtpStream/
├── native/
│   ├── libutp/                     git submodule of bittorrent/libutp
│   └── CMakeLists.txt              builds libutp as a shared library
├── src/UtpStream/
│   ├── UtpSocket.cs                public — Socket-like API
│   ├── UtpListener.cs              public — TcpListener-like API
│   ├── UtpStream.cs                public — Stream over a UtpSocket
│   ├── UtpException.cs             public — transport errors
│   ├── Internal/
│   │   ├── LibUtp.cs               P/Invoke (LibraryImport) over utp.h
│   │   ├── Callbacks.cs            utp_callback_arguments + delegate type
│   │   ├── UtpContext.cs           context + callback registration
│   │   ├── UdpPump.cs              UDP recv + libutp tick loop
│   │   └── NativeLibraryResolver.cs runtimes/<rid>/native/ resolver
│   └── UtpStream.csproj
├── tests/UtpStream.Tests/          xUnit loopback tests
├── .github/workflows/build.yml     native matrix + pack + (optional) publish
├── Directory.Build.props
└── UtpStream.slnx
```

---

## FAQ

**Q: Why a callback-based native library instead of writing µTP from scratch in C#?**
libutp is the reference implementation, has been deployed at scale by every major BitTorrent client for over a decade, and is small (~3.5k LOC). Reimplementing the protocol in pure C# would be a substantial effort and is unlikely to match libutp's congestion-control behavior bit-for-bit.

**Q: Does `UtpStream` support IPv6?**
Yes — pass an `IPEndPoint` with an IPv6 address to `Listen` or `ConnectAsync`. Each context is bound to a single address family; there is currently no dual-stack mode.

**Q: How does this interact with NAT traversal?**
µTP runs over UDP, so the typical UDP NAT-traversal techniques (STUN-style hole punching) work — but you have to coordinate the punch out-of-band; this library does not do it for you.

**Q: Can I run multiple `UtpListener` instances in the same process?**
Yes, on different ports. Each listener owns its own UDP socket, libutp context, and pump thread.

**Q: Are there any known limitations with multiple concurrent connections to the same listener?**
Yes — there is a libutp upstream race when two inbound SYNs land within ~1-5 ms of each other on the same listener: data ends up partially mixed between the two newly accepted sockets. We mitigate it by registering all the libutp callbacks the upstream defaults zero out (`UTP_GET_RANDOM`, `UTP_GET_MICROSECONDS`, `UTP_GET_MILLISECONDS`), which makes the per-socket `conn_id`s actually unique. In real-world deployments, network jitter and per-client connection start times naturally provide enough spacing; under intense synthetic burst tests (multiple `ConnectAsync` calls back-to-back), space them by ≥20 ms or accept the listener serially. Single-connection workloads are not affected.

**Q: Is the library AOT/trimming-friendly?**
The P/Invoke layer uses `LibraryImport` source generators (no reflection), and the public API is small. It should trim cleanly, but full AOT validation isn't part of CI yet — please report issues.

---

## License

This project is licensed under the MIT License. The bundled libutp upstream is also MIT licensed (see `native/libutp/LICENSE`).
