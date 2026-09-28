#!/usr/bin/env bash
# fetch-corpora.sh — populate the local PDF/AI test corpora used by the VCCad
# corpus-driven test suites.
#
# None of these corpora are vendored into the repository (their licences differ
# from VCCad's MIT): they are downloaded on demand into a cache directory that
# the test suites probe by default. Every corpus test skips cleanly (xUnit skip
# sentinel) when its directory is absent, so `dotnet test` never fails merely
# because a developer has no network.
#
# Usage:
#   ./scripts/fetch-corpora.sh [all|ghostscript|ai|pdfjs|verapdf]
#
# Environment:
#   VCCAD_CORPUS_CACHE   cache root (default: ${XDG_CACHE_HOME:-$HOME/.cache}/vccad-corpora)
#
# Sources:
#   ghostscript  https://github.com/ArtifexSoftware/tests        (AGPL-3.0)
#                The public Ghostscript/MuPDF test-file corpus: 208 PDFs across
#                pdf/, pdf/safedocs, Ghent_V3.0 (Ghent PDF Output Suite 3),
#                Ghent_V5.0 (Ghent Output Suite 5), ps/, eps/, pcl/.
#   ai           https://gitlab.com/inkscape/extras/extension-ai (GPL-2.0-or-later)
#                tests/data/ai: ~60 genuine Adobe Illustrator files covering
#                AI8 PostScript, AI9-CS zlib blocks, CC-Legacy and AI24 zstd,
#                with gradients, opacity groups, layers, text and raster art.
#                Also pulls opendesigndev/illustrator-parser-pdfcpu fixtures.
#   pdfjs        https://github.com/mozilla/pdf.js                 (Apache-2.0)
#                test/pdfs sparse checkout (a broad real-world PDF feature corpus).
#   verapdf      https://github.com/veraPDF/veraPDF-corpus         (CC-BY-4.0)
#                ISO 32000 / PDF 2.0 conformance corpus (2,906 files, ~250 MB).
set -euo pipefail

CACHE="${VCCAD_CORPUS_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/vccad-corpora}"
WHAT="${1:-all}"

log() { printf '\033[1m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33mwarn:\033[0m %s\n' "$*" >&2; }

have() { command -v "$1" >/dev/null 2>&1; }

require_git() {
  if ! have git; then
    warn "git not found; cannot fetch $1"
    return 1
  fi
}

# Shallow, single-branch clone that is safe to re-run.
fetch_git() { # url dir
  local url="$1" dir="$2"
  if [ -d "$dir/.git" ]; then
    log "already present: $dir"
    return 0
  fi
  require_git "$dir" || return 1
  log "cloning $url -> $dir"
  git clone --depth 1 --single-branch "$url" "$dir"
}

fetch_ghostscript() {
  local dir="$CACHE/ghostscript"
  fetch_git https://github.com/ArtifexSoftware/tests.git "$dir" || return 1
  log "ghostscript corpus: $(find "$dir" -name '*.pdf' | wc -l) PDFs"
}

fetch_ai() {
  local dir="$CACHE/ai" tmp
  mkdir -p "$dir"
  if [ ! -f "$dir/.complete" ]; then
    tmp="$(mktemp -d)"
    log "downloading Inkscape extension-ai tests/data/ai"
    if curl -fsSL --max-time 300 \
        "https://gitlab.com/api/v4/projects/inkscape%2Fextras%2Fextension-ai/repository/archive.tar.gz?path=tests/data/ai" \
        -o "$tmp/ai.tar.gz"; then
      tar -xzf "$tmp/ai.tar.gz" -C "$tmp"
      find "$tmp" -name '*.ai' -exec cp -n {} "$dir/" \;
      find "$tmp" -name '*.ai.license' -exec cp -n {} "$dir/" \;
      : > "$dir/.complete"
    else
      warn "could not download the Inkscape AI fixtures"
    fi
    rm -rf "$tmp"
  fi

  # LFS-backed fixtures: use the media endpoint, not raw.githubusercontent.
  local od="$dir/opendesign"
  mkdir -p "$od"
  for f in one.ai VectorApple.ai; do
    [ -f "$od/$f" ] && continue
    curl -fsSL --max-time 120 \
      "https://media.githubusercontent.com/media/opendesigndev/illustrator-parser-pdfcpu/HEAD/__tests__/fixtures/$f" \
      -o "$od/$f" || warn "missing $f"
  done

  log "ai corpus: $(find "$dir" -name '*.ai' -size +1k | wc -l) .ai files"
}

fetch_pdfjs() {
  local dir="$CACHE/pdfjs"
  if [ -d "$dir/.git" ]; then log "already present: $dir"; return 0; fi
  require_git pdfjs || return 1
  log "sparse-cloning mozilla/pdf.js test/pdfs"
  git clone --depth 1 --filter=blob:none --sparse https://github.com/mozilla/pdf.js.git "$dir"
  git -C "$dir" sparse-checkout set test/pdfs
  log "pdfjs corpus: $(find "$dir/test/pdfs" -name '*.pdf' | wc -l) PDFs"
}

fetch_verapdf() {
  fetch_git https://github.com/veraPDF/veraPDF-corpus.git "$CACHE/verapdf" || return 1
  log "veraPDF corpus: $(find "$CACHE/verapdf" -name '*.pdf' | wc -l) PDFs"
}

mkdir -p "$CACHE"
case "$WHAT" in
  all)
    fetch_ghostscript || true
    fetch_ai || true
    fetch_pdfjs || true
    fetch_verapdf || true
    ;;
  ghostscript) fetch_ghostscript ;;
  ai) fetch_ai ;;
  pdfjs) fetch_pdfjs ;;
  verapdf) fetch_verapdf ;;
  *)
    warn "unknown corpus '$WHAT' (expected all|ghostscript|ai|pdfjs|verapdf)"
    exit 2
    ;;
esac

echo
log "corpus cache: $CACHE"
for d in ghostscript ai pdfjs verapdf; do
  [ -d "$CACHE/$d" ] && printf '  %-12s %s PDF/AI files\n' "$d" "$(find "$CACHE/$d" \( -name '*.pdf' -o -name '*.ai' \) -size +1k | wc -l)"
done
echo
echo "Point the test suites at it with:"
echo "  export VCCAD_GS_CORPUS=$CACHE/ghostscript"
echo "  export VCCAD_AI_CORPUS=$CACHE/ai"
echo "  export VCCAD_PDFJS_CORPUS=$CACHE/pdfjs/test/pdfs"
echo "  export VCCAD_VERAPDF_CORPUS=$CACHE/verapdf"
