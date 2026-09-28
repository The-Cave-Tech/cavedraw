#!/usr/bin/env python3
"""Split the label form into per-glyph blocks and show how each is built."""
import pathlib
import re

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
QDF = ROOT / "artifacts" / "ref" / "orig-qdf.pdf"
text = QDF.read_text(encoding="latin-1")
objects = dict((int(m.group(1)), m.group(2))
               for m in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S))
form = re.search(r"stream\n(.*?)\nendstream", objects[45], re.S).group(1)

# Each glyph is  q <cm> <colour> <gs> <path> <paint> Q
blocks = re.findall(r"q\n(1 0 0 1 [-\d.]+ [-\d.]+ cm)\n(.*?)\nQ", form, re.S)
print(f"glyph blocks: {len(blocks)}")

shown = 0
for cm, body in blocks:
    if "c\n" not in body and " c" not in body:
        continue
    numbers = re.findall(r"1 0 0 1 ([-\d.]+) ([-\d.]+) cm", cm)
    paint = re.findall(r"(?m)^(f\*|f|B|b|S|n)\s*$", body)
    ops = re.findall(r"(?m)^(m|l|c|v|y|h|re)\b", body)
    count = {}
    for op in ops:
        count[op] = count.get(op, 0) + 1
    print(f"  cm={cm[9:]:24s} paint={paint} ops={count}")
    shown += 1
    if shown >= 14:
        break

# And print one block in full so the shape is visible.
for cm, body in blocks:
    if "c\n" in body:
        print("\n=== full block (a curved glyph) ===")
        print("1 0 0 1" + cm[9:])
        print(body[:900])
        break
