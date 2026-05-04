# UtpStream throughput benchmark

Single-process loopback transfer of N bytes from a sender to a receiver,
comparing pure libutp (`bench/native_bench/`, C) against UtpStream
(`bench/UtpStreamBench/`, .NET 10). Both endpoints use the same protocol
settings (UTP_SNDBUF/RCVBUF = 64 MiB, ~1 ms tick) so the gap is the
managed layer's overhead, not protocol tuning.

## Environment

| | |
|---|---|
| Date | 2026-05-04 10:12:54 CEST |
| Kernel | `Linux 6.17.0-23-generic x86_64` |
| CPU | AMD Ryzen 5 7430U with Radeon Graphics |
| RAM | 15.0 GiB |
| .NET SDK | 10.0.107 |
| UtpStream rev | `4e4dec1` |
| libutp rev | `2b364cb` |
| Iterations per size | 5 |

## Results

Throughput in MiB/s. Each cell shows mean (min – max) over 5 runs.

| Payload | native libutp (C) | UtpStream (.NET) | Ratio |
|--------:|:------------------|:-----------------|------:|
| **100 MiB** | 228.62 (66.68 – 274.74) | 164.96 (50.03 – 196.07) | 72% |
| **300 MiB** | 221.34 (143.40 – 276.29) | 174.71 (120.07 – 213.20) | 79% |
| **500 MiB** | 254.68 (174.90 – 275.18) | 198.06 (142.88 – 229.79) | 78% |
| **800 MiB** | 270.27 (268.30 – 273.13) | 216.04 (200.11 – 221.28) | 80% |
| **1024 MiB** | 247.72 (212.91 – 272.71) | 209.46 (172.84 – 233.02) | 85% |

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
