#!/usr/bin/env python3
"""Crop the same region from the reference and from our export, side by side, magnified."""
import pathlib
import sys

from PIL import Image

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
RD = ROOT / "artifacts" / "rd"


def main():
    index = int(sys.argv[1])
    x0, y0, x1, y1 = (int(v) for v in sys.argv[2:6])
    scale = int(sys.argv[6]) if len(sys.argv) > 6 else 4

    ref = Image.open(RD / f"ref-{index:02d}.png").convert("RGB").crop((x0, y0, x1, y1))
    ours = Image.open(RD / f"ours-{index:02d}.png").convert("RGB").crop((x0, y0, x1, y1))

    combo = Image.new("RGB", (ref.width, ref.height * 2 + 8), (200, 200, 200))
    combo.paste(ref, (0, 0))
    combo.paste(ours, (0, ref.height + 8))
    combo = combo.resize((combo.width * scale, combo.height * scale), Image.LANCZOS)
    out = ROOT / "artifacts" / f"crop-{index}-{x0}-{y0}.png"
    combo.save(out)
    print(f"wrote {out.name} {combo.size}  (top = reference, bottom = ours)")


if __name__ == "__main__":
    main()
