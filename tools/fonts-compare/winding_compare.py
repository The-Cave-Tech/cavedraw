#!/usr/bin/env python3
"""
Winding of the file's own subpaths versus ours, for the glyphs that render solid.

Builds the paths from the content stream exactly as written, and reports the signed area
of each subpath so we can see whether the counter was reversed on the way in.
"""
import json
import pathlib
import re

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
text = (ROOT / "artifacts" / "ref" / "orig-qdf.pdf").read_text(encoding="latin-1")
objects = dict((int(m.group(1)), m.group(2))
               for m in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S))
form = re.search(r"stream\n(.*?)\nendstream", objects[45], re.S).group(1)

PAGE_HEIGHT = 792.0
TOKEN = re.compile(r"""
    (?P<num>[-+]?\d*\.?\d+)
  | (?P<name>/[^\s/\[\]()<>{}]*)
  | (?P<op>[A-Za-z*'"]+)
  | (?P<other>\S)
""", re.X)


def mul(m, n):
    return (m[0]*n[0]+m[2]*n[1], m[1]*n[0]+m[3]*n[1],
            m[0]*n[2]+m[2]*n[3], m[1]*n[2]+m[3]*n[3],
            m[0]*n[4]+m[2]*n[5]+m[4], m[1]*n[4]+m[3]*n[5]+m[5])


def area(pts):
    total = 0.0
    for i in range(len(pts)):
        x0, y0 = pts[i]
        x1, y1 = pts[(i + 1) % len(pts)]
        total += x0 * y1 - x1 * y0
    return total / 2.0


ctm = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
stack = []
ops = []
subpaths = []
current = None
file_paths = []

for match in TOKEN.finditer(form):
    kind, tok = match.lastgroup, match.group()
    if kind == "num":
        ops.append(float(tok))
    elif kind in ("name", "op"):
        if tok == "q":
            stack.append(ctm)
        elif tok == "Q":
            if stack:
                ctm = stack.pop()
        elif tok == "cm" and len(ops) >= 6:
            ctm = mul(tuple(ops[-6:]), ctm)
        elif tok == "m" and len(ops) >= 2:
            x, y = ops[-2], ops[-1]
            current = [(ctm[0]*x + ctm[2]*y + ctm[4], PAGE_HEIGHT - (ctm[1]*x + ctm[3]*y + ctm[5]))]
            subpaths.append(current)
        elif tok in ("l", "c", "v", "y") and len(ops) >= 2:
            x, y = ops[-2], ops[-1]
            pt = (ctm[0]*x + ctm[2]*y + ctm[4], PAGE_HEIGHT - (ctm[1]*x + ctm[3]*y + ctm[5]))
            if current is not None:
                current.append(pt)
        elif tok in ("f", "f*", "F") and subpaths:
            file_paths.append([area(s) for s in subpaths])
            subpaths = []
            current = None
        elif tok == "n":
            subpaths = []
            current = None
        ops.clear()

region = [a for a in file_paths if any(abs(x - 341) < 12 for x in [0])]
print("file subpath windings near x 330-350 (building from the stream):")
shown = 0
for areas in file_paths:
    if len(areas) > 1:
        signs = {1 if a > 0 else -1 for a in areas if abs(a) > 1e-9}
        print(f"   areas=[{', '.join(f'{a:.2f}' for a in areas)}] "
              f"{'SAME winding' if len(signs) == 1 else 'opposite (hole)'}")
        shown += 1
    if shown >= 12:
        break

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


def sub_area(sub):
    pts = [(n["Anchor"]["X"], n["Anchor"]["Y"]) for n in (sub.get("Nodes") or []) if n.get("Anchor")]
    return area(pts)


print("\nour model's multi-subpath windings (all page 10):")
shown = 0
same = 0
for path in paths:
    subs = path.get("SubPaths") or []
    if len(subs) < 2:
        continue
    areas = [sub_area(s) for s in subs]
    signs = {1 if a > 0 else -1 for a in areas if abs(a) > 1e-9}
    if len(signs) == 1:
        same += 1
        if shown < 12:
            print(f"   areas=[{', '.join(f'{a:.2f}' for a in areas)}] SAME winding")
            shown += 1
print(f"   ... {same} multi-subpath glyph(s) with same winding in total")

# Compare the counts directly.
file_multi = [a for a in file_paths if len(a) > 1]
file_same = sum(1 for a in file_multi
                if len({1 if v > 0 else -1 for v in a if abs(v) > 1e-9}) == 1)
print(f"\nFILE: {len(file_multi)} multi-subpath fills, {file_same} with same winding")
ours_multi = [p for p in paths if len(p.get('SubPaths') or []) > 1]
print(f"OURS: {len(ours_multi)} multi-subpath paths, {same} with same winding")
