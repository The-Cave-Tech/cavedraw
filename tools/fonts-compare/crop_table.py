#!/usr/bin/env python3
"""Crop the size table from a screenshot and pair it with the same region of poppler's render."""
import pathlib
import subprocess
import sys

from PIL import Image

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
SHOT = ROOT / "artifacts" / "table-fixed.png"
REF = ROOT / "artifacts" / "ref" / "ref-p1.png"
OUT = ROOT / "artifacts" / "table-final-compare.png"

sys.path.insert(0, str(ROOT / "tools" / "fonts-compare"))

# Rebuild poppler's page-1 render at a matching scale if it is missing.
if not REF.exists():
    subprocess.run(
        ["wsl.exe", "bash", "-lc",
         "cd /mnt/c/Users/submu/vccad-win && pdftoppm -f 1 -l 1 -r 180 -png "
         "samples/3464_LILLIE_View_A_Sides_color.pdf artifacts/ref/ref-p1"],
        check=True,
    )

app = Image.open(SHOT).convert("RGB")
ref = Image.open(REF).convert("RGB")
print("screenshot", app.size, "reference", ref.size)
app.save(OUT.parent / "shot-full.png")
