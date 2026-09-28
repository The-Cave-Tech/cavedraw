#!/usr/bin/env bash
# =============================================================================
# publish-desktop.sh — build self-contained VCCad desktop artifacts.
#
# Produces one standalone bundle per supported desktop platform from this
# Linux/WSL host (no Windows machine required — the .NET SDK cross-publishes):
#
#   artifacts/desktop/win-x64/VCCad.App.Desktop.exe   (PE apphost + deps)
#   artifacts/desktop/linux-x64/VCCad.App.Desktop     (ELF apphost + deps)
#
# Usage:
#   scripts/publish-desktop.sh [tag]
#
#   tag   optional version/tag (e.g. 0.2.0, v0.2.0 or refs/tags/v0.2.0) that is
#         stamped onto both bundles; defaults to the Version in
#         Directory.Build.props. A tag that does not look like a version is
#         rejected rather than passed to MSBuild (CI branch names are not
#         versions).
#
# Environment:
#   VCCAD_DESKTOP_SINGLE_FILE=1  bundle each platform into one executable
#                                (-p:PublishSingleFile=true plus native
#                                self-extraction, see below).
#   VCCAD_DESKTOP_RIDS           RID list to publish (default: win-x64 linux-x64).
#   VCCAD_BUILD_LOCK             lock file serialising dotnet against other
#                                agents/jobs (default: /tmp/vccad-build.lock).
#
# Why single-file is OFF by default:
#   SkiaSharp and HarfBuzz ship native libraries. A truly single file therefore
#   needs -p:IncludeNativeLibrariesForSelfExtract=true, which makes the first
#   launch unpack several MB into a temp directory before the window can appear.
#   The plain folder layout keeps startup predictable, keeps the native
#   dependencies inspectable (`file`, ldd), and zips just as well for release.
#   Set VCCAD_DESKTOP_SINGLE_FILE=1 when a one-file drop is genuinely wanted.
#
# Why trimming is NOT offered:
#   The lossless document serializer still uses reflection-based
#   System.Text.Json, so trimming breaks document loading at runtime. This is
#   the same constraint VCCad.App.Browser documents.
# =============================================================================
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO"

PROJECT="src/VCCad.App.Desktop"
OUT_ROOT="artifacts/desktop"

TAG="${1:-}"
VERSION_ARGS=()
if [ -n "$TAG" ]; then
  # MSBuild/NuGet versions reject a leading "v" and a "refs/tags/" prefix.
  VERSION="${TAG#refs/tags/}"
  VERSION="${VERSION#v}"
  if ! printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+([-.+][0-9A-Za-z.+-]+)?$'; then
    echo "publish-desktop: '$TAG' is not a version tag like 0.2.0 or v0.2.0" >&2
    exit 2
  fi
  VERSION_ARGS=("-p:Version=$VERSION" "-p:InformationalVersion=$VERSION")
fi

RIDS="${VCCAD_DESKTOP_RIDS:-win-x64 linux-x64}"
LOCK="${VCCAD_BUILD_LOCK:-/tmp/vccad-build.lock}"

SINGLE_FILE_ARGS=()
if [ "${VCCAD_DESKTOP_SINGLE_FILE:-0}" = "1" ]; then
  SINGLE_FILE_ARGS=(
    "-p:PublishSingleFile=true"
    "-p:IncludeNativeLibrariesForSelfExtract=true"
  )
fi

# Serialise with any concurrent build/test/publish in this repository
# (optional: the lock is a coordination nicety, not a requirement).
run_locked() {
  if command -v flock >/dev/null 2>&1; then
    flock "$LOCK" "$@"
  else
    "$@"
  fi
}

echo "publish-desktop: project       = $PROJECT"
echo "publish-desktop: rids          = $RIDS"
echo "publish-desktop: self-contained = true, single-file = ${VCCAD_DESKTOP_SINGLE_FILE:-0}, trimmed = false"
if [ -n "$TAG" ]; then
  echo "publish-desktop: version       = ${VERSION_ARGS[0]#-p:Version=} (from '$TAG')"
fi

rm -rf "$OUT_ROOT"

for rid in $RIDS; do
  out="$OUT_ROOT/$rid"
  echo
  echo "==> dotnet publish $PROJECT -c Release -r $rid --self-contained true -o $out"
  run_locked dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishTrimmed=false \
    ${SINGLE_FILE_ARGS[@]+"${SINGLE_FILE_ARGS[@]}"} \
    ${VERSION_ARGS[@]+"${VERSION_ARGS[@]}"} \
    -o "$out"
done

echo
echo "publish-desktop: artifacts"
failed=0
for rid in $RIDS; do
  out="$OUT_ROOT/$rid"
  launcher="$out/VCCad.App.Desktop"
  if [ -f "$launcher.exe" ]; then
    launcher="$launcher.exe"
  fi
  if [ ! -f "$launcher" ]; then
    echo "  $rid: MISSING launcher (expected VCCad.App.Desktop[.exe] in $out)" >&2
    failed=1
    continue
  fi

  launcher_bytes="$(stat -c '%s' "$launcher")"
  launcher_mib="$(awk -v b="$launcher_bytes" 'BEGIN { printf "%.1f", b / 1048576 }')"
  bundle_bytes="$(du -sb "$out" | cut -f1)"
  bundle_mib="$(awk -v b="$bundle_bytes" 'BEGIN { printf "%.1f", b / 1048576 }')"
  printf '  %-10s %s\n' "$rid" "$launcher"
  printf '  %-10s launcher %s bytes (%s MiB), bundle %s bytes (%s MiB)\n' \
    "" "$launcher_bytes" "$launcher_mib" "$bundle_bytes" "$bundle_mib"

  if command -v file >/dev/null 2>&1; then
    kind="$(file -b "$launcher")"
    printf '  %-10s %s\n' "" "$kind"
    case "$rid" in
      win-*)
        case "$kind" in
          *PE32*) ;;
          *) echo "  $rid: expected a PE32 executable" >&2; failed=1 ;;
        esac
        ;;
      linux-*)
        case "$kind" in
          *ELF*) ;;
          *) echo "  $rid: expected an ELF executable" >&2; failed=1 ;;
        esac
        ;;
    esac
  fi
done

if [ "$failed" -ne 0 ]; then
  echo "publish-desktop: FAILED — missing or unexpected launcher(s)" >&2
  exit 1
fi

echo
echo "publish-desktop: OK"
