#!/usr/bin/env python3
"""Show where a page differs: reference, ours, and the difference highlighted in red."""
import pathlib
import sys

import numpy as np
from PIL import Image

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
RD = ROOT / "artifacts" / "rd"


def main():
    index = int(sys.argv[1]) if len(sys.argv) > 1 else 10
    ref = Image.open(RD / f"ref-{index:02d}.png").convert("RGB")
    ours = Image.open(RD / f"ours-{index:02d}.png").convert("RGB")
    if ref.size != ours.size:
        ours = ours.resize(ref.size, Image.LANCZOS)

    a = np.asarray(ref, dtype=np.int16)
    b = np.asarray(ours, dtype=np.int16)
    diff = np.abs(a - b).max(axis=2)

    heat = np.asarray(ref).copy()
    heat[diff > 80] = [255, 0, 0]

    combo = Image.new("RGB", (ref.width * 3 + 20, ref.height), (240, 240, 240))
    combo.paste(ref, (0, 0))
    combo.paste(ours, (ref.width + 10, 0))
    combo.paste(Image.fromarray(heat), (ref.width * 2 + 20, 0))
    out = ROOT / "artifacts" / f"page-{index}-diff.png"
    combo.save(out)
    print(f"wrote {out.name}  {combo.size}  (reference | ours | difference in red)")

    # Where are the differences concentrated?
    rows = diff.mean(axis=1)
    cols = diff.mean(axis=0)
    bands = []
    in_band = False
    for y, value in enumerate(rows):
        if value > 8 and not in_band:
            start, in_band = y, True
        elif value <= 8 and in_band:
            bands.append((start, y))
            in_band = False
    if in_band:
        bands.append((start, len(rows)))
    print("bands with differences (y px):", bands[:12])


if __name__ == "__main__":
    main()
