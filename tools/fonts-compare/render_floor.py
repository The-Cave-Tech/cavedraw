"""Score our export against a renderer floor, page by page.

A catalogue that compares our export to the original with one rasteriser measures two things
at once: our mistakes, and the fact that two renderers disagree about antialiasing. The
second can be larger than the first on a page of hairlines.

This renders the ORIGINAL with two engines and reports that difference as the floor, then
renders our export with the same engine as one of them and reports the excess. A page at the
floor has nothing left in it worth chasing.

Usage:  python render_floor.py <original.pdf> <ours.pdf> [pages] [dpi]
"""
import glob
import pathlib
import shutil
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image


def to_wsl(path):
    text = pathlib.Path(path).as_posix()
    if text.startswith("C:/"):
        return "/mnt/c/" + text[3:]
    marker = "wsl.localhost/Ubuntu/"
    if marker in text:
        return "/" + text.split(marker, 1)[1]
    return text


def render(tool, path, page, dpi, out):
    """Rasterise one page with poppler or mutool, and return the image.

    The output goes to a directory both sides can see, because the engine runs in WSL and
    the image is read on Windows: asking mutool for a path under /tmp writes it where the
    glob on this side will never look.
    """
    folder = pathlib.Path(out)
    folder.mkdir(parents=True, exist_ok=True)
    for stale in folder.glob("*.png"):
        stale.unlink()

    wsl_out = to_wsl(folder)
    if tool == "poppler":
        subprocess.run(
            ["wsl", "-e", "pdftoppm", "-r", str(dpi), "-png", "-f", str(page), "-l", str(page),
             to_wsl(path), f"{wsl_out}/page"],
            capture_output=True)
    else:
        subprocess.run(
            ["wsl", "-e", "mutool", "draw", "-r", str(dpi), "-o", f"{wsl_out}/page-%d.png",
             to_wsl(path), str(page)],
            capture_output=True)

    matches = sorted(folder.glob("*.png"))
    if not matches:
        return None
    return np.array(Image.open(matches[0]).convert("L"), dtype=float)


def main():
    original, ours = sys.argv[1], sys.argv[2]
    pages = int(sys.argv[3]) if len(sys.argv) > 3 else 12
    dpi = int(sys.argv[4]) if len(sys.argv) > 4 else 150

    work = pathlib.Path("artifacts/floor")
    print("        noise floor            ours vs original        excess over the noise")
    print("page    mean   rmse       mean    rmse              mean    rmse")
    for page in range(1, pages + 1):
        a = render("poppler", original, page, dpi, work / f"a{page}")
        b = render("mutool", original, page, dpi, work / f"b{page}")
        c = render("poppler", ours, page, dpi, work / f"c{page}")
        d = render("mutool", ours, page, dpi, work / f"d{page}")

        if any(x is None for x in (a, b, c, d)) or a.shape != b.shape or a.shape != c.shape:
            print(f"{page:4d}   (could not line the rasters up)")
            continue

        # How much two renderers disagree about the ORIGINAL: what no amount of work on our
        # content can remove, because it is not about our content.
        noise_mean = float(np.abs(a - b).mean())
        noise_rmse = float(np.sqrt(((a - b) ** 2).mean()))

        # Our content, seen by one renderer on both sides.
        ours_mean = float(np.abs(a - c).mean())
        ours_rmse = float(np.sqrt(((a - c) ** 2).mean()))

        # And our own file's renderer sensitivity, for scale: a file that two renderers read
        # the same way is a file that says clearly what it means.
        self_mean = float(np.abs(c - d).mean())

        print(f"{page:4d}   {noise_mean:5.2f}  {noise_rmse:6.2f}    {ours_mean:6.2f}  {ours_rmse:6.2f}"
              f"          {ours_mean - noise_mean:+6.2f}  {ours_rmse - noise_rmse:+6.2f}"
              f"   (our file's own noise {self_mean:.2f})")


if __name__ == "__main__":
    main()
