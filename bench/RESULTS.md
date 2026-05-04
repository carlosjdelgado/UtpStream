# UtpStream throughput benchmark

Single-process loopback transfer of N bytes from a sender to a receiver,
comparing pure libutp (`bench/native_bench/`, C) against UtpStream
(`bench/UtpStreamBench/`, .NET 10). Both endpoints use the same protocol
settings (UTP_SNDBUF/RCVBUF = 64 MiB, ~1 ms tick) so the gap is the
managed layer's overhead, not protocol tuning.

## Environment

| | |
|---|---|
| Date | 2026-05-04 12:12:41 CEST |
| Kernel | `Linux 6.17.0-23-generic x86_64` |
| CPU | AMD Ryzen 5 7430U with Radeon Graphics |
| RAM | 15.0 GiB |
| .NET SDK | 10.0.107 |
| UtpStream rev | `8424ad7` |
| libutp rev | `2b364cb` |
| Iterations per size | 20 |
| Loopback latency (unshare + tc netem) | 5ms |

## Results

Throughput in MiB/s. Each cell shows mean (min – max) over 20 runs.

| Payload | native libutp (C) | UtpStream (.NET) | Ratio |
|--------:|:------------------|:-----------------|------:|
| **100 MiB** | 53.38 (53.27 – 53.48) | 51.53 (21.92 – 53.17) | 97% |
| **300 MiB** | 83.30 (43.00 – 92.05) | 83.33 (39.87 – 91.03) | 100% |
| **500 MiB** | 84.31 (50.67 – 113.86) | 83.59 (40.16 – 112.67) | 99% |
| **800 MiB** | 90.99 (61.91 – 126.98) | 64.27 (40.77 – 120.99) | 71% |
| **1024 MiB** | 74.72 (54.97 – 128.01) | 65.12 (48.33 – 80.36) | 87% |

## Reproducing

```bash
# Build native libutp + native_bench
cmake -S native -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release -j

# Build managed bench tool
dotnet build bench/UtpStreamBench/UtpStreamBench.csproj -c Release

# Regenerate this report (add --latency=5ms for stable LEDBAT conditions)
bench/generate_report.sh [--latency=<delay>]
```

_Numbers fluctuate run-to-run with kernel scheduling, GC and µTP's
delay-based congestion control — the relative ratio is the meaningful
figure, not the absolute MiB/s. Pass `--latency=5ms` to apply an
artificial loopback RTT via `tc netem` (no sudo — uses `unshare -rn`)
and get a more stable baseline._
