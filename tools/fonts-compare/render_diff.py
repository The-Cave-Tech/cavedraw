#!/usr/bin/env python3
"""
Pixel-diff the original against VCCad's export, page by page.

Both rasters come from the same renderer (poppler), so any difference is ours. This is
the only comparison that can see a glyph with a filled counter or a letter that was
never drawn — text extraction cannot.
"""
import pathlib
import sys

import numpy as np
from PIL import Image

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
RD = ROOT / "artifacts" / "rd"


def load(prefix, index):
    path = RD / f"{prefix}-{index:02d}.png"
    return Image.open(path).convert("L") if path.exists() else None


def main():
    worst = []
    for index in range(1, 13):
        ref = load("ref", index)
        ours = load("ours", index)
        if ref is None or ours is None:
            print(f"page {index:2d}: missing raster")
            continue

        if ref.size != ours.size:
            ours = ours.resize(ref.size, Image.LANCZOS)

        a = np.asarray(ref, dtype=np.int16)
        b = np.asarray(ours, dtype=np.int16)
        diff = np.abs(a - b)
        rmse = float(np.sqrt((diff.astype(np.float64) ** 2).mean()))
        bad = int((diff > 80).sum())
        worst.append((rmse, bad, index))
        print(f"page {index:2d}: RMSE {rmse:6.2f}   strongly-different px {bad:7d}")

    worst.sort(reverse=True)
    print()
    print("worst pages:", [(i, round(r, 1), b) for r, b, i in worst[:5]])
    return worst


if __name__ == "__main__":
    sys.exit(0 if main() else 0)
