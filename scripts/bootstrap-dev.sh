#!/usr/bin/env bash
# VCCad one-shot environment bootstrap.
#
# Installs the .NET wasm-tools workload (needed to build the browser host) and,
# optionally, fetches the optional verification assets the accuracy tests use:
#   * the veraPDF corpus (import-fidelity sweep)
#   * the veraPDF validator (PDF/A conformance)
#
# Usage: ./scripts/bootstrap-dev.sh [--with-corpus] [--with-verapdf]
set -euo pipefail
cd "$(dirname "$0")/.."

WITH_CORPUS=0
WITH_VERAPDF=0
for arg in "$@"; do
  case "$arg" in
    --with-corpus) WITH_CORPUS=1 ;;
    --with-verapdf) WITH_VERAPDF=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

echo "==> Checking .NET SDK"
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet not found. Install the .NET 8 SDK first." >&2
  exit 1
fi
dotnet --version

echo "==> Installing wasm-tools workload (browser host)"
dotnet workload install wasm-tools

if [ "$WITH_CORPUS" = 1 ]; then
  CORPUS="${VCCAD_VERAPDF_CORPUS:-/tmp/opencode/veraPDF-corpus}"
  if [ ! -d "$CORPUS" ]; then
    echo "==> Cloning veraPDF corpus → $CORPUS (large, ~2 GB)"
    mkdir -p "$(dirname "$CORPUS")"
    git clone --depth 1 https://github.com/veraPDF/veraPDF-corpus.git "$CORPUS"
  else
    echo "==> Corpus already present at $CORPUS"
  fi
  echo "    export VCCAD_VERAPDF_CORPUS=$CORPUS"
fi

if [ "$WITH_VERAPDF" = 1 ]; then
  echo "==> veraPDF installer is interactive (izpack). Download and install with:"
  echo "    mkdir -p /tmp/opencode/vpdf && cd /tmp/opencode/vpdf"
  echo "    curl -O https://software.verapdf.org/releases/verapdf-installer.zip"
  echo "    unzip -q verapdf-installer.zip && cd verapdf-greenfield-*/ && java -jar verapdf-izpack-installer-*.jar"
  echo "    export VCCAD_VERAPDF=/tmp/opencode/vpdf/install/verapdf"
fi

echo
echo "==> Next steps"
echo "    dotnet build VCCad.sln -c Release"
echo "    dotnet test  VCCad.sln -c Release"
echo "    ./scripts/dev.sh run          # http://127.0.0.1:5099"
