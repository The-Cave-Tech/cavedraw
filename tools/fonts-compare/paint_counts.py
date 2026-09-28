#!/usr/bin/env python3
"""
Count painted paths per page in the original and in our export.

If our export paints far fewer paths on a page, we are dropping geometry on import
or on export — which is what a missing glyph looks like.
"""
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
ORIGINAL = ROOT / "samples" / "3464_LILLIE_View_A_Sides_color.pdf"
OURS = ROOT / "artifacts" / "ours.pdf"


def qdf(pdf: pathlib.Path, out: pathlib.Path) -> pathlib.Path:
    subprocess.run(
        ["wsl.exe", "bash", "-lc",
         f"cd /mnt/c/Users/submu/vccad-win && qpdf --qdf --object-streams=disable "
         f"'{pdf.relative_to(ROOT).as_posix()}' 'artifacts/ref/{out.name}'"],
        check=True,
    )
    return ROOT / "artifacts" / "ref" / out.name


def per_page_streams(qdf_path: pathlib.Path):
    text = qdf_path.read_text(encoding="latin-1")
    streams = re.findall(r"stream\n(.*?)\nendstream", text, re.S)
    return [s for s in streams if re.search(r"\b(BT|re|m|l|c)\b", s)]


def paint_counts(streams):
    out = []
    for stream in streams:
        n = len(re.findall(r"(?<![A-Za-z])(f\*|f|F|B\*|B|b\*|b)(?![A-Za-z])", stream))
        out.append(n)
    return out


ref_qdf = qdf(ORIGINAL, pathlib.Path("orig-qdf.pdf"))
our_qdf = qdf(OURS, pathlib.Path("ours-qdf.pdf"))

a = paint_counts(per_page_streams(ref_qdf))
b = paint_counts(per_page_streams(our_qdf))

print(f"original: {len(a)} content streams, {sum(a)} painted paths")
print(f"ours    : {len(b)} content streams, {sum(b)} painted paths")
print()
print(f"{'#':>3} {'original':>9} {'ours':>7} {'delta':>7}")
for index in range(max(len(a), len(b))):
    x = a[index] if index < len(a) else "-"
    y = b[index] if index < len(b) else "-"
    delta = (y - x) if isinstance(x, int) and isinstance(y, int) else "-"
    print(f"{index + 1:>3} {str(x):>9} {str(y):>7} {str(delta):>7}")
