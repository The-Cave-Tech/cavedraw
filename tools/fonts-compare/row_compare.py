#!/usr/bin/env python3
"""
Every glyph the file places in the "DEVANT ET DOS" row, versus what our model has.

The label row is drawn at PDF y = 695.48, i.e. model y = 792 - 695.48 = 96.52.
"""
import json
import pathlib
import re

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
text = (ROOT / "artifacts" / "ref" / "orig-qdf.pdf").read_text(encoding="latin-1")
objects = dict((int(m.group(1)), m.group(2))
               for m in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S))
form = re.search(r"stream\n(.*?)\nendstream", objects[45], re.S).group(1)

# Simple, reliable structural counts first.
print("structural counts in /TPL10:")
print("   'q' lines :", len(re.findall(r"(?m)^q$", form)))
print("   'Q' lines :", len(re.findall(r"(?m)^Q$", form)))
print("   cm ops    :", len(re.findall(r"(?m)^[-\d. ]+ cm$", form)))
print("   f ops     :", len(re.findall(r"(?m)^f$", form)))
print("   f* ops    :", len(re.findall(r"(?m)^f\*$", form)))
print()

targets = []
for match in re.finditer(r"(?m)^1 0 0 1 ([-\d.]+) ([-\d.]+) cm$", form):
    x, y = float(match.group(1)), float(match.group(2))
    if 694.0 <= y <= 697.0:
        targets.append(x)

targets.sort()
print(f"glyph placements in the label row (PDF y ~695.48): {len(targets)}")
print("   x values:", ", ".join(f"{v:.2f}" for v in targets))
print()

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


def anchor_first(item):
    for sub in item.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                return a["X"], a["Y"]
    return None


ours = []
for path in paths:
    pt = anchor_first(path)
    if pt and 94.0 <= pt[1] <= 104.0:
        ours.append(pt[0])

ours.sort()
print(f"paths we imported in that row (model y 94-104): {len(ours)}")
print("   x values:", ", ".join(f"{v:.2f}" for v in ours))
print()
print(f"MISSING: {len(targets) - len(ours)} of {len(targets)} glyph placements")
