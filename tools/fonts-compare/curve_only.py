#!/usr/bin/env python3
"""
Are the missing glyphs exactly the fills built only from curves?

Walks the form, records every fill along with whether it used any straight-line operator,
then checks whether our model has a path at that position. If pure-curve fills are the
ones absent, the fault is in curve handling rather than subpath handling.
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
start = None
first = None
has_line = False
has_curve = False
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

    if op in ("m", "re"):
        if start is None:
            start = True
            if op == "m" and len(values) >= 2:
                x, y = values[-2], values[-1]
            else:
                x, y = values[-4], values[-3]
            px = ctm[0]*x + ctm[2]*y + ctm[4]
            py = ctm[1]*x + ctm[3]*y + ctm[5]
            first = (px, PAGE_HEIGHT - py)
            has_line = False
            has_curve = False
        continue

    if op in ("l", "re"):
        has_line = True
        continue
    if op in ("c", "v", "y"):
        has_curve = True
        continue

    if op in ("f", "f*", "F", "B", "B*", "b", "b*"):
        if first is not None:
            fills.append((first, has_line, has_curve))
        start = None
        first = None
        continue

    if op == "n":
        start = None
        first = None

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

model_pts = []
for path in paths:
    for sub in path.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                model_pts.append((a["X"], a["Y"]))
                break
        break


def present(pt):
    for mp in model_pts:
        if abs(mp[0] - pt[0]) < 1.0 and abs(mp[1] - pt[1]) < 1.0:
            return True
    return False


pure_curve = [f for f in fills if f[2] and not f[1]]
straight = [f for f in fills if f[1]]
print(f"fills total          : {len(fills)}")
print(f"  pure-curve (no 'l'): {len(pure_curve)}")
print(f"  with a line       : {len(straight)}")
print()

missing_pure = [f for f in pure_curve if not present(f[0])]
missing_line = [f for f in straight if not present(f[0])]
print(f"pure-curve fills MISSING from our model: {len(missing_pure)} of {len(pure_curve)}")
print(f"line-bearing fills MISSING from our model: {len(missing_line)} of {len(straight)}")
print()
for pt, _, _ in missing_pure[:12]:
    print(f"   missing pure-curve fill at ({pt[0]:8.2f},{pt[1]:8.2f})")
