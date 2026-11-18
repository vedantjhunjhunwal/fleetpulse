#!/usr/bin/env bash
# Posts every truck-day log under DATA_DIR/logs to the FleetPulse API, oldest day first.
set -euo pipefail
API="${API:-http://localhost:5080}"
DATA_DIR="${1:-data}"
n=0; frames=0; errors=0
while IFS= read -r f; do
  r=$(curl -sf -X POST -H 'Content-Type: text/plain' --data-binary "@$f" "$API/api/ingest")
  frames=$((frames + $(echo "$r" | sed -E 's/.*"frames":([0-9]+).*/\1/')))
  errors=$((errors + $(echo "$r" | sed -E 's/.*"parseErrors":([0-9]+).*/\1/')))
  n=$((n + 1))
done < <(find "$DATA_DIR/logs" -name '*.log' | awk -F/ '{print $NF" "$0}' | sort | cut -d' ' -f2-)
echo "ingested $n files, $frames frames, $errors malformed lines skipped"
