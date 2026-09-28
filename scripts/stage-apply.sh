#!/usr/bin/env bash
# stage-apply.sh — install files staged on the Windows host into this WSL repo.
#
# Why: the agent harness's write/edit tools use atomic hardlink+rename, which the
# 9p mount exposing this repository (\\wsl.localhost\Ubuntu\...) does not support.
# Agents therefore author files under a Windows staging root and call this script
# to copy them into the working tree with LF normalisation.
#
# Usage:
#   bash scripts/stage-apply.sh <relative-path> [<relative-path> ...]
#
# Environment:
#   VCCAD_STAGE_ROOT  staging root as seen from WSL
#                     (default: /mnt/c/Users/submu/AppData/Local/Temp/vccad-stage)
set -euo pipefail

ROOT="${VCCAD_STAGE_ROOT:-/mnt/c/Users/submu/AppData/Local/Temp/vccad-stage}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ "$#" -eq 0 ]; then
  echo "usage: $0 <relative-path> [...]" >&2
  exit 2
fi

for rel in "$@"; do
  src="$ROOT/$rel"
  dst="$REPO/$rel"
  if [ ! -f "$src" ]; then
    echo "stage-apply: missing staged file: $src" >&2
    exit 1
  fi
  mkdir -p "$(dirname "$dst")"
  install -m 0644 "$src" "$dst"
  # Repository text files are LF; strip CR the Windows editor may have added.
  case "$rel" in
    *.png|*.jpg|*.pdf|*.ttf|*.otf|*.zip|*.gz) ;;
    *) sed -i 's/\r$//' "$dst" ;;
  esac
  echo "staged: $rel"
done
