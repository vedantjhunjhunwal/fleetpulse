#!/usr/bin/env bash
# REQ: NFR-01 - the C decoder must sustain >= 1M frames/s on one core (output disabled).
# Usage: tools/check_bench.sh [data_dir] [min_mfps]
set -euo pipefail
DATA="${1:-data}"
MIN="${2:-1.0}"
BIN="$(dirname "$0")/../decoder/build/j1939dec"
out=$("$BIN" --bench $(find "$DATA/logs" -name '*.log' | sort) 2>&1 | tail -1)
echo "$out"
mfps=$(echo "$out" | sed -E 's/.*= ([0-9.]+) M frames\/s.*/\1/')
awk -v m="$mfps" -v min="$MIN" 'BEGIN { if (m + 0 < min + 0) { print "FAIL: " m " < " min " M frames/s"; exit 1 } print "PASS: " m " >= " min " M frames/s" }'
