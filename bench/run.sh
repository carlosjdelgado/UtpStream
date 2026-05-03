#!/usr/bin/env bash
# Throughput benchmark: pure libutp (native_bench, C) vs UtpStream (managed
# .NET 10). Both run in-process, transfer SIZE bytes of zeros over loopback,
# discard at the receiver. Same protocol settings on both sides
# (UTP_SNDBUF/RCVBUF=64 MiB, ~1 ms tick) so the gap is the .NET layer's
# overhead, not protocol tuning.
#
# Usage: bench/run.sh [size_mib] [iterations]   default 500 / 3

set -uo pipefail
export LC_ALL=C  # ensure printf "%f" accepts dot decimals regardless of locale

SIZE_MIB="${1:-500}"
ITERATIONS="${2:-3}"
SIZE_BYTES=$(( SIZE_MIB * 1024 * 1024 ))
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

NATIVE_BIN="$ROOT/build/native_bench"
LIBDIR="$ROOT/build"
MANAGED_BIN="$ROOT/bench/UtpStreamBench/bin/Release/net10.0/UtpStreamBench"

if [[ ! -x "$NATIVE_BIN" ]]; then
    echo "error: native_bench not built — run 'cmake --build build --config Release'" >&2
    exit 1
fi
if [[ ! -x "$MANAGED_BIN" ]]; then
    echo "error: managed bench not built — run 'dotnet build bench/UtpStreamBench -c Release'" >&2
    exit 1
fi

run_one() {
    local label=$1; shift
    local secs mibps
    local out
    out=$("$@" "$SIZE_BYTES" 2>&1) || { echo "[$label] FAILED: $out" >&2; return 1; }
    # Parse "X bytes in Y.YY s (Z.ZZ MiB/s)"
    secs=$(echo "$out" | sed -nE 's/.* in ([0-9.]+) s.*/\1/p')
    mibps=$(echo "$out" | sed -nE 's/.* \(([0-9.]+) MiB\/s\)/\1/p')
    printf "  [%s] %6.2f s | %7.2f MiB/s\n" "$label" "$secs" "$mibps" >&2
    echo "$mibps"
}

echo "============================================="
echo " UtpStream throughput benchmark"
echo " payload: ${SIZE_MIB} MiB, iterations: ${ITERATIONS}"
echo "============================================="
echo

# JIT warmup for the managed binary (first run pays JIT cost)
echo "warming up .NET..."
"$MANAGED_BIN" $((1024 * 1024)) > /dev/null 2>&1 || true
echo

declare -a NATIVE_RATES
declare -a MANAGED_RATES

for i in $(seq 1 "$ITERATIONS"); do
    echo "iteration $i:"
    rate=$(run_one "native " env LD_LIBRARY_PATH="$LIBDIR" "$NATIVE_BIN")
    NATIVE_RATES+=("$rate")
    rate=$(run_one "managed" "$MANAGED_BIN")
    MANAGED_RATES+=("$rate")
    echo
done

# Compute averages
avg() {
    local sum=0 n=0
    for v in "$@"; do sum=$(awk -v a="$sum" -v b="$v" 'BEGIN { print a + b }'); n=$((n+1)); done
    awk -v s="$sum" -v n="$n" 'BEGIN { print s / n }'
}

native_avg=$(avg "${NATIVE_RATES[@]}")
managed_avg=$(avg "${MANAGED_RATES[@]}")
ratio=$(awk -v m="$managed_avg" -v n="$native_avg" 'BEGIN { printf "%.1f", m * 100 / n }')

echo "============================================="
printf "  native  avg: %7.2f MiB/s\n" "$native_avg"
printf "  managed avg: %7.2f MiB/s  (%s%% of native)\n" "$managed_avg" "$ratio"
echo "============================================="
