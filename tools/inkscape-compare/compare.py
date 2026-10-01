#!/usr/bin/env python3
"""Compare two renders of the same page, or rasterise an SVG so there is something to compare.

Issue #131. This is the command-line half of the Inkscape comparison harness. The test half lives in
``tests/VCCad.App.Tests/InkscapeComparisonTests.cs`` and measures the same quantities; this one
exists so a number can be reproduced outside the test runner and looked at by a person, and so the
measurement does not depend on our own PNG decoder being right.

Two subcommands:

  render   Rasterise an SVG with Inkscape at a dpi expressed in *model points*.

           VCCad's coordinates are PDF points, and the SVG writer writes them as SVG user units,
           which SVG defines as px at 96 to the inch. Inkscape's ``--export-dpi`` is dots per inch
           of the physical page, so one pixel per model point is ``--export-dpi = point_dpi * 96/72``
           and not ``point_dpi``. Getting that wrong renders one side at 75% and every comparison
           then measures the size mismatch instead of the artwork.

  compare  Measure two PNGs: size, mean absolute channel difference, the largest channel difference,
           and the share of pixels that differ by more than a tolerance.

The background is forced to white: VCCad renders the page over white paper, so an Inkscape export
left transparent would measure white against alpha rather than artwork against artwork.

Exit codes: 0 whether or not the images differ (a difference is data, not an error), 2 for a
missing file, a missing Inkscape, or a decode failure.

Deterministic and quiet: no display, no network, no clock, no working directory assumed.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

# No unit bridge: the writer states pt on the root extent, so one model point is one point on the page.

INKSCAPE_CANDIDATES = (
    r"C:\Program Files\Inkscape\bin\inkscape.exe",
    r"C:\Program Files (x86)\Inkscape\bin\inkscape.exe",
    "/usr/bin/inkscape",
    "/usr/local/bin/inkscape",
    "/snap/bin/inkscape",
    "/Applications/Inkscape.app/Contents/MacOS/inkscape",
)


def find_inkscape() -> str | None:
    """Inkscape, or None. ``VCCAD_INKSCAPE`` wins; ``VCCAD_NO_INKSCAPE`` pretends there is none."""
    if os.environ.get("VCCAD_NO_INKSCAPE"):
        return None

    configured = os.environ.get("VCCAD_INKSCAPE")
    if configured and Path(configured).is_file():
        return configured

    for candidate in INKSCAPE_CANDIDATES:
        if Path(candidate).is_file():
            return candidate

    found = shutil.which("inkscape")
    return found


def rasterise(svg_path: Path, png_path: Path, point_dpi: float, inkscape: str) -> None:
    export_dpi = point_dpi
    command = [
        inkscape,
        "--export-type=png",
        "--export-area-page",
        f"--export-dpi={export_dpi:.4f}",
        "--export-background=#ffffff",
        "--export-background-opacity=255",
        f"--export-filename={png_path}",
        str(svg_path),
    ]
    completed = subprocess.run(
        command, capture_output=True, text=True, timeout=120, check=False
    )
    if not png_path.is_file():
        raise RuntimeError(
            f"Inkscape exited {completed.returncode} without writing {png_path}\n"
            f"stdout: {completed.stdout}\nstderr: {completed.stderr}"
        )


def load(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"), dtype=np.int16)


def measure(a: np.ndarray, b: np.ndarray, tolerance: int, inset: int = 0) -> dict:
    height = min(a.shape[0], b.shape[0]) - 2 * inset
    width = min(a.shape[1], b.shape[1]) - 2 * inset
    if width <= 0 or height <= 0:
        raise ValueError(f"{inset} px of inset leaves nothing of a {a.shape[1]}x{a.shape[0]} image")
    overlap_a = a[inset:inset + height, inset:inset + width]
    overlap_b = b[inset:inset + height, inset:inset + width]

    delta = np.abs(overlap_a - overlap_b)
    per_pixel = delta.max(axis=2)
    pixels = int(width) * int(height)

    return {
        "width": int(a.shape[1]),
        "height": int(a.shape[0]),
        "otherWidth": int(b.shape[1]),
        "otherHeight": int(b.shape[0]),
        "sizesMatch": a.shape == b.shape,
        "comparedPixels": pixels,
        "meanAbsoluteError": float(delta.mean()) if pixels else 0.0,
        "maxChannelDifference": int(per_pixel.max()) if pixels else 0,
        "differingPixels": int((per_pixel > tolerance).sum()),
        "differingProportion": float((per_pixel > tolerance).sum()) / pixels if pixels else 0.0,
        "tolerance": tolerance,
        "inset": inset,
    }


def describe(result: dict) -> str:
    match = "match" if result["sizesMatch"] else "MISMATCH"
    return (
        f"sizes {result['width']}x{result['height']} vs "
        f"{result['otherWidth']}x{result['otherHeight']} ({match}), "
        f"compared {result['comparedPixels']} px, "
        f"mean|delta| {result['meanAbsoluteError']:.4f}/255, "
        f"max|delta| {result['maxChannelDifference']}, "
        f"differing {result['differingPixels']}/{result['comparedPixels'] or 1} "
        f"({result['differingProportion'] * 100:.3f}%), "
        f"tolerance {result['tolerance']}"
        + (f", {result['inset']} px inset ignored" if result["inset"] else "")
    )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="command", required=True)

    render = sub.add_parser("render", help="rasterise an SVG with Inkscape")
    render.add_argument("svg", type=Path)
    render.add_argument("-o", "--out", type=Path, required=True)
    render.add_argument(
        "--dpi",
        type=float,
        default=72.0,
        help="pixels per model point; 72 means one pixel per point (default 72)",
    )
    render.add_argument("--inkscape", default=None)

    compare = sub.add_parser("compare", help="measure two PNGs")
    compare.add_argument("a", type=Path)
    compare.add_argument("b", type=Path)
    compare.add_argument(
        "--tolerance",
        type=int,
        default=8,
        help="a pixel differs when any channel moves by more than this (default 8)",
    )
    compare.add_argument(
        "--inset",
        type=int,
        default=0,
        help="ignore this many pixels along every edge before comparing (default 0). The "
             "editor's page render carries the artboard frame it draws on screen and Inkscape's "
             "does not, so cropping the outer ring measures the artwork rather than the chrome.",
    )
    compare.add_argument("--json", action="store_true")

    args = parser.parse_args(argv)

    if args.command == "render":
        inkscape = args.inkscape or find_inkscape()
        if not inkscape:
            print(
                "no Inkscape found: set VCCAD_INKSCAPE to the executable, or install Inkscape",
                file=sys.stderr,
            )
            return 2
        if not args.svg.is_file():
            print(f"no such SVG: {args.svg}", file=sys.stderr)
            return 2
        args.out.parent.mkdir(parents=True, exist_ok=True)
        rasterise(args.svg, args.out, args.dpi, inkscape)
        with Image.open(args.out) as image:
            print(f"{args.out}: {image.width}x{image.height} px at {args.dpi} point-dpi")
        return 0

    for path in (args.a, args.b):
        if not path.is_file():
            print(f"no such image: {path}", file=sys.stderr)
            return 2

    try:
        result = measure(load(args.a), load(args.b), args.tolerance, args.inset)
    except Exception as error:  # noqa: BLE001 - a decode failure is a reportable outcome here
        print(f"could not compare: {error}", file=sys.stderr)
        return 2

    if args.json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        print(describe(result))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
