#!/usr/bin/env python3
"""
Match every fill the file paints against the path we imported for it, and compare the
number of subpaths.

The measurement so far: the file draws 132 fills carrying more than one subpath, we
produce 98. This finds *which* fills lose a subpath and where they sit on the page, which
is what names the operator responsible.
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


ctm = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
stack = []
ops = []
subpaths = []
current = None
fills = []

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
        elif tok in ("m", "re") and ops:
            if tok == "m" and len(ops) >= 2:
                x, y = ops[-2], ops[-1]
                pt = (ctm[0]*x + ctm[2]*y + ctm[4], PAGE_HEIGHT - (ctm[1]*x + ctm[3]*y + ctm[5]))
                current = [pt]
                subpaths.append(current)
            elif tok == "re" and len(ops) >= 4:
                x, y = ops[-4], ops[-3]
                pt = (ctm[0]*x + ctm[2]*y + ctm[4], PAGE_HEIGHT - (ctm[1]*x + ctm[3]*y + ctm[5]))
                subpaths.append([pt])
        elif tok in ("l", "c", "v", "y") and len(ops) >= 2:
            x, y = ops[-2], ops[-1]
            pt = (ctm[0]*x + ctm[2]*y + ctm[4], PAGE_HEIGHT - (ctm[1]*x + ctm[3]*y + ctm[5]))
            if current is not None:
                current.append(pt)
        elif tok in ("f", "f*", "F", "B*", "B", "b*", "b") and subpaths:
            fills.append((subpaths[0][0], len(subpaths)))
            subpaths = []
            current = None
        elif tok == "n":
            subpaths = []
            current = None
        ops.clear()

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


def first_point(path):
    for sub in path.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                return (a["X"], a["Y"])
    return None


ours = []
for path in paths:
    fp = first_point(path)
    if fp:
        ours.append((fp, len(path.get("SubPaths") or [])))

print(f"file fills: {len(fills)}   model paths: {len(ours)}")

multi_file = [f for f in fills if f[1] > 1]
multi_ours = [o for o in ours if o[1] > 1]
print(f"multi-subpath: file {len(multi_file)}   ours {len(multi_ours)}")
print()

# Match by nearest first point and compare counts.
lost = []
used = set()
for (fp, count) in fills:
    best, best_i, best_d = None, -1, 1e9
    for i, (op, oc) in enumerate(ours):
        if i in used:
            continue
        d = abs(op[0] - fp[0]) + abs(op[1] - fp[1])
        if d < best_d:
            best, best_i, best_d = (op, oc), i, d
    if best_i >= 0 and best_d <= 1.0:
        used.add(best_i)
        if best[1] < count:
            lost.append((fp, count, best[1]))

print(f"fills where the file has MORE subpaths than our path: {len(lost)}")
for fp, want, got in lost[:25]:
    print(f"   at ({fp[0]:8.2f},{fp[1]:8.2f})  file {want} subpath(s)  ours {got}")
