#!/usr/bin/env bash
# Run the throughput benchmark for several payload sizes and emit a
# Markdown report at bench/RESULTS.md. Re-run any time to refresh.
#
# Usage: bench/generate_report.sh [--latency=<delay>]
#
#   --latency=<delay>   Apply artificial loopback RTT via tc netem to
#                       stabilise LEDBAT's congestion control. No sudo needed:
#                       the script re-execs itself inside a private network
#                       namespace (unshare -rn) where your user appears as
#                       root and tc works without real privileges.
#                       Example: --latency=5ms
#                       Without this flag the loopback RTT is ~0 µs, which
#                       causes LEDBAT to oscillate between fast and slow modes
#                       and makes native vs managed comparisons unreliable.

set -uo pipefail
export LC_ALL=C

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NATIVE_BIN="$ROOT/build/native_bench"
LIBDIR="$ROOT/build"
MANAGED_BIN="$ROOT/bench/UtpStreamBench/bin/Release/net10.0/UtpStreamBench"
OUT="$ROOT/bench/RESULTS.md"

SIZES_MIB=(100 300 500 800 1024)
ITERATIONS=20
LATENCY="5ms"

for arg in "$@"; do
    case "$arg" in
        --latency=*) LATENCY="${arg#--latency=}" ;;
        *) echo "unknown argument: $arg" >&2; exit 1 ;;
    esac
done

if [[ ! -x "$NATIVE_BIN" ]]; then
    echo "error: native_bench not built — run 'cmake --build build --config Release'" >&2
    exit 1
fi
if [[ ! -x "$MANAGED_BIN" ]]; then
    echo "error: managed bench not built — run 'dotnet build bench/UtpStreamBench -c Release'" >&2
    exit 1
fi

# Apply artificial loopback latency to stabilise LEDBAT's congestion control.
# We use unshare -rn to get a private network namespace where the current user
# maps to UID 0, so tc netem works without real root. The namespace — and the
# netem config — disappear automatically when the script exits. No cleanup trap
# needed and the host loopback is never touched.
if [[ -n "$LATENCY" && -z "${_UTPBENCH_NETNS:-}" ]]; then
    echo "Applying loopback latency: $LATENCY (unshare netns, no sudo)" >&2
    exec env _UTPBENCH_NETNS=1 unshare -rn bash -- "${BASH_SOURCE[0]}" "$@"
fi
if [[ -n "$LATENCY" && -n "${_UTPBENCH_NETNS:-}" ]]; then
    ip link set lo up
    tc qdisc add dev lo root netem delay "$LATENCY"
fi

run_one() {
    local bin_kind=$1 size_bytes=$2
    local out rc
    # Hard wall-clock cap per run: 30s easily covers 1 GiB at >34 MiB/s.
    # If a run hangs (the race we observed under kernel-buffer-uncapped),
    # we'd rather skip the iteration than block the whole report.
    if [[ "$bin_kind" == "native" ]]; then
        out=$(LD_LIBRARY_PATH="$LIBDIR" timeout 30 "$NATIVE_BIN" "$size_bytes" 2>&1)
    else
        out=$(timeout 30 "$MANAGED_BIN" "$size_bytes" 2>&1)
    fi
    rc=$?
    if [[ $rc -ne 0 ]]; then
        echo ""    # caller treats empty as missing data
        return 1
    fi
    sed -nE 's/.* \(([0-9.]+) MiB\/s\)/\1/p' <<< "$out"
}

avg() {
    awk 'BEGIN { s=0; n=0 } { s += $1; n++ } END { if (n) printf "%.2f", s / n; else print "0" }' <<< "$(printf '%s\n' "$@")"
}

minv() {
    awk 'BEGIN { m=1e18 } { if ($1 < m) m=$1 } END { printf "%.2f", m }' <<< "$(printf '%s\n' "$@")"
}

maxv() {
    awk 'BEGIN { m=0 } { if ($1 > m) m=$1 } END { printf "%.2f", m }' <<< "$(printf '%s\n' "$@")"
}

# JIT warmup
echo "warming up .NET..." >&2
"$MANAGED_BIN" $((1024 * 1024)) > /dev/null 2>&1 || true

# Collect system info up-front
NOW=$(date '+%Y-%m-%d %H:%M:%S %Z')
KERNEL=$(uname -srm)
CPU=$(awk -F: '/^model name/ { gsub(/^ +/, "", $2); print $2; exit }' /proc/cpuinfo 2>/dev/null || echo "unknown")
MEM=$(awk '/^MemTotal/ { printf "%.1f GiB", $2 / 1024 / 1024 }' /proc/meminfo 2>/dev/null || echo "unknown")
DOTNET_VER=$(dotnet --version 2>/dev/null || echo "unknown")
LIBUTP_REV=$(git -C "$ROOT/native/libutp" rev-parse --short HEAD 2>/dev/null || echo "unknown")
GIT_REV=$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo "(uncommitted)")

# Header
{
    echo "# UtpStream throughput benchmark"
    echo
    echo "Single-process loopback transfer of N bytes from a sender to a receiver,"
    echo "comparing pure libutp (\`bench/native_bench/\`, C) against UtpStream"
    echo "(\`bench/UtpStreamBench/\`, .NET 10). Both endpoints use the same protocol"
    echo "settings (UTP_SNDBUF/RCVBUF = 64 MiB, ~1 ms tick) so the gap is the"
    echo "managed layer's overhead, not protocol tuning."
    echo
    echo "## Environment"
    echo
    echo "| | |"
    echo "|---|---|"
    echo "| Date | $NOW |"
    echo "| Kernel | \`$KERNEL\` |"
    echo "| CPU | $CPU |"
    echo "| RAM | $MEM |"
    echo "| .NET SDK | $DOTNET_VER |"
    echo "| UtpStream rev | \`$GIT_REV\` |"
    echo "| libutp rev | \`$LIBUTP_REV\` |"
    echo "| Iterations per size | $ITERATIONS |"
    if [[ -n "$LATENCY" ]]; then
        echo "| Loopback latency (unshare + tc netem) | $LATENCY |"
    fi
    echo
    echo "## Results"
    echo
    echo "Throughput in MiB/s. Each cell shows mean (min – max) over $ITERATIONS runs."
    echo
    echo "| Payload | native libutp (C) | UtpStream (.NET) | Ratio |"
    echo "|--------:|:------------------|:-----------------|------:|"
} > "$OUT"

for size_mib in "${SIZES_MIB[@]}"; do
    size_bytes=$(( size_mib * 1024 * 1024 ))
    echo "── ${size_mib} MiB ──" >&2

    declare -a NRATES=() MRATES=()
    # Interleave native/managed pairs so both measurements see the same
    # LEDBAT congestion-control state. Running all native first then all
    # managed means the CC warms up differently for each, making the ratio
    # unreliable. Pairs share the same protocol epoch.
    for i in $(seq 1 "$ITERATIONS"); do
        sleep 2
        echo "  native iter $i..." >&2
        rate=$(run_one native "$size_bytes")
        if [[ -n "$rate" ]]; then
            NRATES+=("$rate"); echo "    -> $rate MiB/s" >&2
        else
            echo "    -> SKIPPED (timeout/error)" >&2
        fi

        sleep 2
        echo "  managed iter $i..." >&2
        rate=$(run_one managed "$size_bytes")
        if [[ -n "$rate" ]]; then
            MRATES+=("$rate"); echo "    -> $rate MiB/s" >&2
        else
            echo "    -> SKIPPED (timeout/error)" >&2
        fi
    done

    n_avg=$(avg "${NRATES[@]}");  n_min=$(minv "${NRATES[@]}");  n_max=$(maxv "${NRATES[@]}")
    m_avg=$(avg "${MRATES[@]}");  m_min=$(minv "${MRATES[@]}");  m_max=$(maxv "${MRATES[@]}")
    ratio=$(awk -v m="$m_avg" -v n="$n_avg" 'BEGIN { if (n > 0) printf "%.0f%%", m * 100 / n; else print "—" }')

    printf "| **%d MiB** | %s (%s – %s) | %s (%s – %s) | %s |\n" \
        "$size_mib" "$n_avg" "$n_min" "$n_max" "$m_avg" "$m_min" "$m_max" "$ratio" \
        >> "$OUT"
done

{
    echo
    echo "## Reproducing"
    echo
    echo '```bash'
    echo '# Build native libutp + native_bench'
    echo 'cmake -S native -B build -DCMAKE_BUILD_TYPE=Release'
    echo 'cmake --build build --config Release -j'
    echo
    echo '# Build managed bench tool'
    echo 'dotnet build bench/UtpStreamBench/UtpStreamBench.csproj -c Release'
    echo
    echo '# Regenerate this report (add --latency=5ms for stable LEDBAT conditions)'
    echo 'bench/generate_report.sh [--latency=<delay>]'
    echo '```'
    echo
    echo "_Numbers fluctuate run-to-run with kernel scheduling, GC and µTP's"
    echo "delay-based congestion control — the relative ratio is the meaningful"
    echo "figure, not the absolute MiB/s. Pass \`--latency=5ms\` to apply an"
    echo "artificial loopback RTT via \`tc netem\` (no sudo — uses \`unshare -rn\`)"
    echo "and get a more stable baseline._"
} >> "$OUT"

echo >&2
echo "report written to $OUT" >&2
cat "$OUT"
