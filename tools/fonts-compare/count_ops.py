#!/usr/bin/env python3
"""Count the path-painting operators in the decompressed PDF, per page."""
import pathlib
import re
import sys

QDF = pathlib.Path(r"C:\Users\submu\vccad-win\artifacts\ref\qdf.pdf")
if not QDF.exists():
    sys.exit("run qpdf --qdf --object-streams=disable first")

text = QDF.read_text(encoding="latin-1")

# Content streams in a qdf are the ones between 'stream'/'endstream' with text ops.
streams = re.findall(r"stream\n(.*?)\nendstream", text, re.S)
content = [s for s in streams if re.search(r"\b(BT|re|m|l|c)\b", s)]
print(f"content streams: {len(content)}")

total = {}
for stream in content:
    for op in re.findall(r"(?<![A-Za-z])(f\*|f|F|B\*|B|b\*|b|S|s|n|W)(?![A-Za-z])", stream):
        total[op] = total.get(op, 0) + 1

print("path-painting operator counts across the document:")
for op in ("f", "f*", "F", "B", "B*", "b", "b*", "S", "s", "n", "W"):
    if op in total:
        print(f"   {op:>2}: {total[op]}")
