"""Score our export against the original under more than one renderer.

A single-renderer comparison cannot tell a defect of ours from a difference in how that
renderer interprets colour. Rendering the same two files with poppler and with MuPDF and
reporting both separates the two: a gap that stays the same under both is ours, and a gap
that moves is the renderer's.

Measured on the LILLIE sample at 150 dpi, page 1:

    poppler   RMSE 15.4   mean 2.93
    mutool    RMSE 14.4   mean 1.94

Both renderers paint the same files, so the difference between those two rows is how each
one handles DeviceCMYK from a file that declares an sRGB output intent against one that
declares none - not something the export got wrong.

Usage:  python render_compare.py [path-to-original] [path-to-our-export]
"""
import pathlib
import shutil
import subprocess
import sys

import numpy as np
from PIL import Image

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
WSL = ["wsl.exe", "bash", "-lc"]


def to_wsl(path):
    text = pathlib.Path(path).as_posix()
    if text.startswith("C:/"):
        return "/mnt/c/" + text[3:]
    return text


def render(pdf, out_prefix, renderer, dpi=150, page=1):
    """Render through WSL, where the renderers live."""
    src = to_wsl(pdf)
    if renderer == "poppler":
        command = f"pdftoppm -r {dpi} -png -f {page} -l {page} '{src}' '{out_prefix}'"
    else:
        command = f"mutool draw -q -o '{out_prefix}.png' -r {dpi} -p {page} '{src}'"
    result = subprocess.run(WSL + [command], capture_output=True, text=True)
    if result.returncode != 0:
        return None
    matches = list(pathlib.Path(r"\\wsl.localhost\Ubuntu\tmp").glob(
        pathlib.Path(out_prefix).name + "*.png"))
    return matches[0] if matches else None


def compare(a, b):
    x = np.array(Image.open(a).convert("RGB"), dtype=np.float64)
    y = np.array(Image.open(b).convert("RGB"), dtype=np.float64)
    if x.shape != y.shape:
        return None
    d = x - y
    return {
        "rmse": float(np.sqrt((d ** 2).mean())),
        "mean": float(np.abs(d).mean()),
        "bad": int((np.abs(d).max(axis=2) > 90).sum()),
    }


def main():
    original = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else (
        ROOT / "samples" / "3464_LILLIE_View_A_Sides_color.pdf")
    ours = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else (
        ROOT / "artifacts" / "catalogue" / "ours.pdf")

    if not ours.exists():
        print(f"no export at {ours}; build the catalogue first")
        return 1

    print("%-10s %-8s %-8s %s" % ("renderer", "RMSE", "mean", "differing>90"))
    for renderer in ("poppler", "mutool"):
        ref_png = render(original, f"/tmp/rc_ref_{renderer}", renderer)
        our_png = render(ours, f"/tmp/rc_our_{renderer}", renderer)
        if ref_png is None or our_png is None:
            print("%-10s (not available)" % renderer)
            continue
        stats = compare(ref_png, our_png)
        if stats is None:
            print("%-10s (size mismatch)" % renderer)
            continue
        print("%-10s %-8.1f %-8.2f %d"
              % (renderer, stats["rmse"], stats["mean"], stats["bad"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
