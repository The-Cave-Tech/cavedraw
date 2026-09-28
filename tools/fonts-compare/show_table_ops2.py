#!/usr/bin/env python3
"""Show the operators that draw the page-number table, from a qdf-decompressed PDF."""
import pathlib
import re
import sys

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
QDF = ROOT / "artifacts" / "ref" / "qdf.pdf"

if not QDF.exists():
    sys.exit("run:  qpdf --qdf --object-streams=disable samples/...  artifacts/ref/qdf.pdf")

text = QDF.read_text(encoding="latin-1")

# Only look inside BT..ET blocks that sit near a plain "(123)" string.
hits = 0
for block in re.finditer(r"BT(.*?)ET", text, re.S):
    body = block.group(1)
    if not re.search(r"\(1\s*2\s*3\)|\(123\)|1\b.*2\b.*3\b", body):
        continue
    if "Tf" not in body:
        continue
    hits += 1
    print(f"=========== text object {hits} ===========")
    print(body.strip()[:1800])
    print()
    if hits >= 3:
        break

if hits == 0:
    print("no BT/ET block matched; searching for the literal string instead")
    for match in re.finditer(r"\(123\)", text):
        start = max(0, match.start() - 400)
        print(repr(text[start:match.end() + 120]))
        break
