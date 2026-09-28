#!/usr/bin/env python3
"""
Does our exported PDF contain the glyph paths that fail to appear?

If the export writes them, the fault is upstream of the writer (the model as painted).
If it does not, the writer is dropping them.
"""
import pathlib
import re
import subprocess

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
OURS = ROOT / "artifacts" / "catalogue" / "ours.pdf"
QDF = ROOT / "artifacts" / "catalogue" / "ours-qdf.pdf"

subprocess.run(
    ["wsl.exe", "bash", "-lc",
     "cd /mnt/c/Users/submu/vccad-win && qpdf --qdf --object-streams=disable "
     "artifacts/catalogue/ours.pdf artifacts/catalogue/ours-qdf.pdf"],
    check=True,
)

text = QDF.read_text(encoding="latin-1")
objects = dict((int(m.group(1)), m.group(2))
               for m in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S))

pages = [n for n, body in objects.items() if re.search(r"/Type\s*/Page(?![s])", body)]
print(f"pages in our export: {len(pages)}")
if len(pages) < 10:
    raise SystemExit("fewer than 10 pages in the export")

body = objects[pages[9]]
ref = re.search(r"/Contents\s+(\d+)\s+0\s+R", body)
content = ""
if ref:
    part = objects.get(int(ref.group(1)), "")
    inner = re.search(r"stream\n(.*?)\nendstream", part, re.S)
    content = inner.group(1) if inner else part
else:
    array = re.search(r"/Contents\s*\[(.*?)\]", body, re.S)
    for number in re.findall(r"(\d+)\s+0\s+R", array.group(1) if array else ""):
        part = objects.get(int(number), "")
        inner = re.search(r"stream\n(.*?)\nendstream", part, re.S)
        content += inner.group(1) if inner else ""

print(f"page 10 content length: {len(content)}")

# The label row sits at PDF y ~ 690..720 (model y 72..102).
numbers = re.findall(r"-?\d+\.?\d*", content)
hits = [float(n) for n in numbers if 685 <= float(n) <= 725]
print(f"coordinates in the label band (685..725): {len(hits)}")

f_ops = len(re.findall(r"(?<![A-Za-z])(f\*|f|F)(?![A-Za-z])", content))
print(f"fill operators on page 10: {f_ops}")

# Show a window of the content where the label-band coordinates appear.
for match in re.finditer(r"-?\d+\.?\d*", content):
    value = float(match.group())
    if 690 <= value <= 715:
        start = max(0, match.start() - 500)
        print("\n--- content near the label row ---")
        print(content[start:match.end() + 700])
        break
