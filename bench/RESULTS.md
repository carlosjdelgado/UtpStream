# UtpStream throughput benchmark

Single-process loopback transfer of N bytes from a sender to a receiver,
comparing pure libutp (`bench/native_bench/`, C) against UtpStream
(`bench/UtpStreamBench/`, .NET 10). Both endpoints use the same protocol
settings (UTP_SNDBUF/RCVBUF = 64 MiB, ~1 ms tick) so the gap is the
managed layer's overhead, not protocol tuning.

## Environment

| | |
|---|---|
| Date | 2026-05-04 11:11:51 CEST |
| Kernel | `Linux 6.17.0-23-generic x86_64` |
| CPU | AMD Ryzen 5 7430U with Radeon Graphics |
| RAM | 15.0 GiB |
| .NET SDK | 10.0.107 |
| UtpStream rev | `9e4ea7e` |
| libutp rev | `2b364cb` |
| Iterations per size | 5 |

## Results

Throughput in MiB/s. Each cell shows mean (min – max) over 5 runs.

| Payload | native libutp (C) | UtpStream (.NET) | Ratio |
|--------:|:------------------|:-----------------|------:|
| **100 MiB** | 274.88 (272.06 – 278.34) | 206.79 (200.80 – 210.47) | 75% |
| **300 MiB** | 195.75 (142.62 – 275.51) | 120.12 (120.00 – 120.23) | 61% |
| **500 MiB** | 250.58 (177.44 – 273.96) | 206.88 (142.89 – 223.48) | 83% |
| **800 MiB** | 220.84 (160.61 – 271.70) | 220.64 (177.80 – 233.04) | 100% |
| **1024 MiB** | 234.55 (212.19 – 265.76) | 220.15 (186.18 – 234.72) | 94% |

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
