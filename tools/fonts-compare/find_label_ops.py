#!/usr/bin/env python3
"""
Find the content-stream operators that draw one label glyph, in the original PDF.

The "DEVANT ET DOS" label sits at about x 168-248, y 700-720 in PDF user space
(bottom-left origin). Page 10's content stream is the one whose path data lands there.
"""
import pathlib
import re

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
QDF = ROOT / "artifacts" / "ref" / "orig-qdf.pdf"
text = QDF.read_text(encoding="latin-1")

streams = re.findall(r"stream\n(.*?)\nendstream", text, re.S)
content = [s for s in streams if re.search(r"\b(BT|re|m|l|c)\b", s)]

NUMBER = re.compile(r"-?\d+\.?\d*")


def hits_label(stream):
    """Numbers that fall in the label's box."""
    found = 0
    for match in NUMBER.finditer(stream):
        value = float(match.group())
        if 690 <= value <= 730:
            found += 1
    return found


scored = sorted(((hits_label(s), i, s) for i, s in enumerate(content)), reverse=True)
print("streams with coordinates in the label band (y 690-730):")
for score, index, _ in scored[:5]:
    print(f"   stream #{index}: {score} hits")

if not scored or scored[0][0] == 0:
    raise SystemExit("no stream landed in the label band")

best = scored[0][2]
print(f"\n=== operators around the first label-band coordinate (stream #{scored[0][1]}) ===")
for match in NUMBER.finditer(best):
    if 690 <= float(match.group()) <= 730:
        start = max(0, match.start() - 700)
        print(best[start:match.end() + 900])
        break
