#!/usr/bin/env python3
"""
Compare our engine's page renders with another engine's, page by page.

Our engine: the running app's canvas, captured per page at 72 dpi.
Other engine: poppler at 72 dpi.

Locates the page rectangle in each screenshot automatically (a white sheet inside the
dark canvas) so the two rasters line up without hand-tuned crops.
"""
import pathlib
import subprocess
import sys

import numpy as np
from PIL import Image

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
ENG = ROOT / "artifacts" / "eng"
REF = ROOT / "artifacts" / "ref72"

REF.mkdir(parents=True, exist_ok=True)


# The canvas viewport: we centre the page, so it sits at the middle of the drawing area.
CANVAS = (170, 120, 840, 990)


def find_page(image):
    """Bounding box of the near-white page sheet, centred in the canvas."""
    a = np.asarray(image.convert("RGB"))
    x0, y0, x1, y1 = CANVAS
    region = a[y0:y1, x0:x1]
    white = (region > 235).all(axis=2)
    # Keep only full-width runs so the sheet is picked, not text inside it.
    mask = white.sum(axis=1) > 400
    rows = np.where(mask)[0]
    cols = np.where(white.sum(axis=0) > 500)[0]
    if len(rows) == 0 or len(cols) == 0:
        return None
    return (x0 + int(cols[0]), y0 + int(rows[0]), x0 + int(cols[-1]) + 1, y0 + int(rows[-1]) + 1)


def main():
    subprocess.run(
        ["wsl.exe", "bash", "-lc",
         "cd /mnt/c/Users/submu/vccad-win && rm -rf artifacts/ref72 && mkdir -p artifacts/ref72 && "
         "pdftoppm -r 72 -png samples/3464_LILLIE_View_A_Sides_color.pdf artifacts/ref72/ref"],
        check=True,
    )

    print(f"{'page':>4} {'our sheet px':>16} {'ref px':>12} {'RMSE':>8} {'bad px':>9}")
    results = []
    centre = None
    for index in range(1, 13):
        ours = Image.open(ENG / f"eng-{index:02d}.png").convert("RGB")
        ref = Image.open(REF / f"ref-{index:02d}.png").convert("RGB")

        # Every page is centred on the same viewport point, so calibrate once from
        # page 1 and crop an exact sheet-sized box for the rest.
        if centre is None:
            detected = find_page(ours)
            if detected is None:
                print(f"{index:>4}  could not locate the page sheet")
                continue
            centre = ((detected[0] + detected[2]) / 2.0, (detected[1] + detected[3]) / 2.0)
            print(f"      calibrated viewport centre -> {centre}")

        box = (int(centre[0] - 306), int(centre[1] - 396),
               int(centre[0] + 306), int(centre[1] + 396))
        sheet = ours.crop(box)

        a = np.asarray(sheet.resize(ref.size, Image.LANCZOS), dtype=np.int16)
        b = np.asarray(ref, dtype=np.int16)
        diff = np.abs(a - b)
        rmse = float(np.sqrt((diff.astype(np.float64) ** 2).mean()))
        bad = int((diff.max(axis=2) > 90).sum())
        results.append((rmse, bad, index))

        print(f"{index:>4} {str(sheet.size):>16} {str(ref.size):>12} {rmse:>8.2f} {bad:>9}")

        # Save the worst offenders as a visual triple.
        if index in (1, 7, 10):
            heat = np.asarray(sheet.resize(ref.size, Image.LANCZOS)).copy()
            heat[diff.max(axis=2) > 90] = [255, 0, 0]
            combo = Image.new("RGB", (ref.width * 3 + 16, ref.height), (240, 240, 240))
            combo.paste(ref, (0, 0))
            combo.paste(sheet.resize(ref.size, Image.LANCZOS), (ref.width + 8, 0))
            combo.paste(Image.fromarray(heat), (ref.width * 2 + 16, 0))
            combo.save(ROOT / "artifacts" / f"engine-vs-poppler-{index:02d}.png")

    results.sort(reverse=True)
    print()
    print("worst pages (rmse, bad px, page):", [(round(r, 1), b, i) for r, b, i in results[:5]])


if __name__ == "__main__":
    sys.exit(main())
