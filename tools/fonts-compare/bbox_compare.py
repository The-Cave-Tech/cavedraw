#!/usr/bin/env python3
"""
Compare every fill's bounding box: the file's against our model's.

A fill's first point is not its top-left, so earlier probes were ambiguous. The bounding
box is what identifies a shape unambiguously.
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
pts = []
file_boxes = []

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

    if op == "m" and len(values) >= 2:
        x, y = values[-2], values[-1]
    elif op == "l" and len(values) >= 2:
        x, y = values[-2], values[-1]
    elif op == "c" and len(values) >= 6:
        x, y = values[-2], values[-1]
    elif op in ("v", "y") and len(values) >= 4:
        x, y = values[-2], values[-1]
    elif op == "re" and len(values) >= 4:
        x, y = values[-2], values[-1]
    elif op in ("f", "f*", "F", "B", "B*", "b", "b*"):
        if pts:
            xs = [p[0] for p in pts]
            ys = [p[1] for p in pts]
            file_boxes.append((round(min(xs), 2), round(min(ys), 2),
                               round(max(xs), 2), round(max(ys), 2)))
        pts = []
        continue
    elif op == "n":
        pts = []
        continue
    else:
        continue

    px = ctm[0]*x + ctm[2]*y + ctm[4]
    py = ctm[1]*x + ctm[3]*y + ctm[5]
    pts.append((px, PAGE_HEIGHT - py))

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

our_boxes = []
for path in paths:
    xs, ys = [], []
    for sub in path.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                xs.append(a["X"])
                ys.append(a["Y"])
    if xs:
        our_boxes.append((round(min(xs), 2), round(min(ys), 2),
                          round(max(xs), 2), round(max(ys), 2)))

print(f"file fills: {len(file_boxes)}   model paths: {len(our_boxes)}")


def key(box, tol=0.6):
    return (round(box[0] / tol), round(box[1] / tol))


file_keys = {}
for box in file_boxes:
    file_keys.setdefault(key(box), []).append(box)
our_keys = {}
for box in our_boxes:
    our_keys.setdefault(key(box), []).append(box)

missing = []
for k, boxes in file_keys.items():
    have = len(our_keys.get(k, []))
    if have < len(boxes):
        missing.extend(boxes[have:])

print(f"fills with no matching model path (bbox within 0.6pt): {len(missing)}")
missing.sort(key=lambda b: (b[1], b[0]))
for box in missing[:30]:
    print(f"   box x {box[0]:8.2f}..{box[2]:8.2f}  y {box[1]:7.2f}..{box[3]:7.2f}  "
          f"({box[2]-box[0]:.2f}x{box[3]-box[1]:.2f})")
