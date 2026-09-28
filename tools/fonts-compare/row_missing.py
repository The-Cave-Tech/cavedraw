#!/usr/bin/env python3
"""
Which fills in the 'DEVANT ET DOS' row does our model not have?

Matches strictly: the file fill's first point must coincide with a model path's first
anchor to within 0.05pt, and the model path must be glyph-sized (not a page-spanning
path that would match everything).
"""
import json
import pathlib
import re

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
PAGE_HEIGHT = 792.0

text = (ROOT / "artifacts" / "ref" / "orig-qdf.pdf").read_text(encoding="latin-1")
objects = dict((int(m.group(1)), m.group(2))
               for m in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S))
form = re.search(r"stream\n(.*?)\nendstream", objects[45], re.S).group(1)
lines = [l.strip() for l in form.split("\n")]


def mul(m, n):
    return (m[0]*n[0]+m[2]*n[1], m[1]*n[0]+m[3]*n[1],
            m[0]*n[2]+m[2]*n[3], m[1]*n[2]+m[3]*n[3],
            m[0]*n[4]+m[2]*n[5]+m[4], m[1]*n[4]+m[3]*n[5]+m[5])


ctm = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
stack = []
first = None
fills = []

for line in lines:
    if not line:
        continue
    parts = line.split()
    op = parts[-1]
    try:
        values = [float(v) for v in parts[:-1]]
    except ValueError:
        values = []

    if op == "q":
        stack.append(ctm)
        continue
    if op == "Q":
        if stack:
            ctm = stack.pop()
        continue
    if op == "cm" and len(values) == 6:
        ctm = mul(tuple(values), ctm)
        continue
    if op in ("m", "re") and first is None:
        if op == "m" and len(values) >= 2:
            x, y = values[-2], values[-1]
        elif op == "re" and len(values) >= 4:
            x, y = values[-4], values[-3]
        else:
            continue
        px = ctm[0]*x + ctm[2]*y + ctm[4]
        py = ctm[1]*x + ctm[3]*y + ctm[5]
        first = (px, PAGE_HEIGHT - py)
        continue
    if op in ("f", "f*", "F", "B", "B*", "b", "b*"):
        if first is not None:
            fills.append(first)
        first = None
        continue
    if op == "n":
        first = None

# The label row: model y 70..90, x 180..280.
row = [f for f in fills if 70 <= f[1] <= 90 and 180 <= f[0] <= 280]
row.sort()
print(f"file fills whose first point is in the DOS row (x 180..280, y 70..90): {len(row)}")

model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
artboard = model["result"]["Artboards"][9]
paths = []


def collect(items):
    for item in items or []:
        if item.get("$kind") == "path":
            paths.append(item)
        for key in ("Items", "Children"):
            if item.get(key):
                collect(item[key])


for layer in artboard["Layers"]:
    collect(layer["Items"])

anchors = []
for path in paths:
    nodes = 0
    first_anchor = None
    for sub in path.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                nodes += 1
                if first_anchor is None:
                    first_anchor = (a["X"], a["Y"])
    if first_anchor and nodes <= 40:
        anchors.append(first_anchor)

print(f"glyph-sized model path anchors (<=40 nodes): {len(anchors)}")
print()

for point in row:
    close = [a for a in anchors
             if abs(a[0] - point[0]) < 0.05 and abs(a[1] - point[1]) < 0.05]
    near = [a for a in anchors
            if abs(a[0] - point[0]) < 2.0 and abs(a[1] - point[1]) < 2.0]
    status = "PRESENT" if close else ("near-miss" if near else "*** ABSENT ***")
    print(f"  ({point[0]:8.2f},{point[1]:8.2f})  {status}")
