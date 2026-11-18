#!/usr/bin/env bash
# End-to-end FleetPulse run on Linux/macOS:
#   simulate fleet -> C decoder -> .NET API ingest -> R risk model -> API maintenance queue.
# Requires: cmake + a C compiler, Python 3, .NET 8 SDK, R (jsonlite), curl.
# Usage: ./run_pipeline.sh [--keep]   (--keep leaves the API running on :5080 for the dashboard)
set -euo pipefail
cd "$(dirname "$0")"
API=http://localhost:5080

echo "== 1/6 build the C decoder"
cmake -S decoder -B decoder/build -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build decoder/build -j >/dev/null
./decoder/build/test_j1939 | tail -1

echo "== 2/6 simulate 24 trucks x 60 days of J1939 traffic"
[ -d data/logs ] || python3 simulator/fleetsim.py --out data --trucks 24 --days 60 --seed 42
tools/check_bench.sh data 1.0

echo "== 3/6 start the .NET API"
dotnet build api/FleetPulse.Api -c Release -v quiet -nologo >/dev/null
dotnet api/FleetPulse.Api/bin/Release/net8.0/FleetPulse.Api.dll --urls "$API" >/tmp/fleetpulse-api.log 2>&1 &
API_PID=$!
trap '[ "${KEEP:-0}" = 1 ] || kill $API_PID 2>/dev/null' EXIT
for _ in $(seq 30); do curl -sf "$API/api/health" >/dev/null && break; sleep 0.5; done

echo "== 4/6 ingest every truck-day through the API (C library via P/Invoke)"
time tools/ingest_logs.sh data

echo "== 5/6 R: features, grouped cross-validation, risk scores -> API"
mkdir -p analytics/out
curl -sf "$API/api/export/daily-features.csv" > analytics/out/daily_features.csv
Rscript analytics/run_analytics.R analytics/out/daily_features.csv analytics/out "$API"

echo "== 6/6 maintenance queue"
curl -sf "$API/api/maintenance"; echo

if [ "${1:-}" = "--keep" ]; then
  KEEP=1
  echo "API left running on $API (pid $API_PID). Start the dashboard: cd dashboard && npm start"
fi
