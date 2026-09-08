#!/usr/bin/env bash
# VCCad developer helper: build, test, publish editor+api, run locally.
# Usage: ./scripts/dev.sh [test|wasm|run|all]
set -euo pipefail
cd "$(dirname "$0")/.."

OUT=/tmp/vccad-dev
mkdir -p "$OUT"

cmd="${1:-all}"

case "$cmd" in
  test)
    dotnet test VCCad.sln -c Release ;;
  wasm)
    dotnet publish src/VCCad.App.Browser -c Release -o "$OUT/wasm"
    echo "wasm bundle: $OUT/wasm/wwwroot"
    ;;
  run)
    dotnet publish src/VCCad.Api -c Release -o "$OUT/api" >/dev/null
    dotnet publish src/VCCad.App.Browser -c Release -o "$OUT/wasm" >/dev/null
    rm -rf "$OUT/api/wwwroot"
    cp -r "$OUT/wasm/wwwroot" "$OUT/api/wwwroot"
    echo "serving on http://127.0.0.1:5099  (Ctrl+C to stop)"
    ASPNETCORE_URLS=http://127.0.0.1:5099 dotnet "$OUT/api/VCCad.Api.dll"
    ;;
  all)
    "$0" test
    "$0" run
    ;;
  *)
    echo "usage: $0 [test|wasm|run|all]"
    exit 2 ;;
esac
