#!/usr/bin/env bash
# Publish issue media: encode a frame capture and attach it to the repo's `issue-media` release.
#
#   capture-issue-media.sh <frames-dir> <asset-name> [fps]
#
# Why a release rather than the repository: the pictures are evidence about a change, not part of it, and binary
# files in the tree are weight that never gets removed. The release is a stable public URL that a GitHub comment
# renders - an image link animates a GIF, which is the format to prefer because it needs no player.
#
# The frames come from a throwaway harness in the test projects: hold the window headlessly, drive the interaction
# with `InputInjection` (Press/Move/Release/Type), and render one `RenderTargetBitmap` per step. Delete the harness
# afterwards - the repository wants assertions, and a capture has none.
set -euo pipefail

frames="${1:?frames directory holding frame-NNN.png}"
name="${2:?asset base name, e.g. 128-selection}"
fps="${3:-12}"

repo="${VCCAD_REPO:-The-Cave-Tech/cavedraw}"
release="${VCCAD_MEDIA_RELEASE:-issue-media}"
out="$(mktemp -d)"

ffmpeg -y -loglevel error -framerate "$fps" -i "$frames/frame-%03d.png" \
    -vf "split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3" \
    -loop 0 "$out/$name.gif"

# Offered as well because it is smaller and scrubbable; the GIF is the one to embed.
ffmpeg -y -loglevel error -framerate "$fps" -i "$frames/frame-%03d.png" \
    -c:v libx264 -pix_fmt yuv420p -movflags +faststart "$out/$name.mp4"

gh release upload "$release" "$out/$name.gif" "$out/$name.mp4" --repo "$repo" --clobber >/dev/null

base="https://github.com/$repo/releases/download/$release"
echo "embedded in an issue as:"
echo "  ![$name]($base/$name.gif)"
echo "linked, for anyone who wants to scrub:"
echo "  [$name.mp4]($base/$name.mp4)"
