#!/usr/bin/env python3
"""Compare poppler's text extraction against our importer's, for the LILLIE document."""
import re
import sys
import pathlib

REF = pathlib.Path(r"C:\Users\submu\vccad-win\artifacts\ref\lillie-p1.xml")
text = REF.read_text(encoding="utf-8")

m = re.search(r'<page width="([\d.]+)" height="([\d.]+)"', text)
print("reference page size:", m.groups() if m else None)

words = re.findall(
    r'<word xMin="([\d.]+)" yMin="([\d.]+)" xMax="([\d.]+)" yMax="([\d.]+)">([^<]*)</word>',
    text,
)
print("reference words:", len(words))
for x0, y0, x1, y1, word in words:
    print(f"  x={float(x0):7.1f} y={float(y0):7.1f}  {word!r}")
