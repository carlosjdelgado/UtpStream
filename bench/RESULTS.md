# UtpStream throughput benchmark

Single-process loopback transfer of N bytes from a sender to a receiver,
comparing pure libutp (`bench/native_bench/`, C) against UtpStream
(`bench/UtpStreamBench/`, .NET 10). Both endpoints use the same protocol
settings (UTP_SNDBUF/RCVBUF = 64 MiB, ~1 ms tick) so the gap is the
managed layer's overhead, not protocol tuning.

## Environment

| | |
|---|---|
| Date | 2026-05-03 21:35:52 CEST |
| Kernel | `Linux 6.17.0-23-generic x86_64` |
| CPU | AMD Ryzen 5 7430U with Radeon Graphics |
| RAM | 15.0 GiB |
| .NET SDK | 10.0.107 |
| UtpStream rev | `(uncommitted)` |
| libutp rev | `2b364cb` |
| Iterations per size | 5 |

## Results

Throughput in MiB/s. Each cell shows mean (min – max) over 5 runs.

| Payload | native libutp (C) | UtpStream (.NET) | Ratio |
|--------:|:------------------|:-----------------|------:|
| ** 100 MiB** | 263.61 (257.78 – 272.99) | 192.52 (184.37 – 208.56) | 73% |
| ** 300 MiB** | 237.93 (222.28 – 246.81) | 190.65 (183.50 – 197.16) | 80% |
| ** 500 MiB** | 240.18 (235.84 – 242.11) | 192.10 (181.00 – 200.35) | 80% |
| ** 800 MiB** | 233.68 (228.99 – 237.06) | 181.32 (170.53 – 191.10) | 78% |
| **1024 MiB** | 232.88 (229.45 – 235.59) | 184.98 (181.82 – 186.77) | 79% |

## Reproducing

```bash
# Build native libutp + native_bench
cmake -S native -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release -j

# Build managed bench tool
dotnet build bench/UtpStreamBench/UtpStreamBench.csproj -c Release

# Regenerate this report
bench/generate_report.sh
```

_Numbers fluctuate run-to-run with kernel scheduling, GC and µTP's
delay-based congestion control — the relative ratio is the meaningful
figure, not the absolute MiB/s._
