#!/usr/bin/env python3
"""
Simulate /TPL10 and record where every painted path actually lands, then compare that
with where our importer put it.

The glyph `cm`s are relative offsets composed onto a running CTM, so a row grouping of
raw `cm` values is meaningless — only the accumulated transform says where a path is.
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
    return (
        m[0] * n[0] + m[2] * n[1],
        m[1] * n[0] + m[3] * n[1],
        m[0] * n[2] + m[2] * n[3],
        m[1] * n[2] + m[3] * n[3],
        m[0] * n[4] + m[2] * n[5] + m[4],
        m[1] * n[4] + m[3] * n[5] + m[5],
    )


ctm = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
stack = []
operands = []
pending = []          # model-space points of the path being built
file_positions = []   # first point of each painted path, in model space

for match in TOKEN.finditer(form):
    kind, tok = match.lastgroup, match.group()
    if kind == "num":
        operands.append(float(tok))
    elif kind in ("name", "op"):
        if tok == "q":
            stack.append(ctm)
        elif tok == "Q":
            if stack:
                ctm = stack.pop()
        elif tok == "cm" and len(operands) >= 6:
            ctm = mul(tuple(operands[-6:]), ctm)
        elif tok == "m" and len(operands) >= 2:
            x, y = operands[-2], operands[-1]
            px = ctm[0] * x + ctm[2] * y + ctm[4]
            py = ctm[1] * x + ctm[3] * y + ctm[5]
            pending.append((px, PAGE_HEIGHT - py))
        elif tok in ("f", "f*", "F", "B*", "B", "b*", "b") and pending:
            file_positions.append(pending[0])
            pending = []
        elif tok == "n":
            pending = []
        operands.clear()

print(f"file paints {len(file_positions)} paths")

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

ours = []
for path in paths:
    for sub in path.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                ours.append((a["X"], a["Y"]))
                break
        else:
            continue
        break

print(f"we imported {len(ours)} paths")

file_only = sorted(file_positions)
our_sorted = sorted(ours)


def nearest(target, pool, used):
    best, best_i = None, -1
    for i, p in enumerate(pool):
        if i in used:
            continue
        d = abs(p[0] - target[0]) + abs(p[1] - target[1])
        if best is None or d < best:
            best, best_i = d, i
    return best, best_i


used = set()
unmatched_file = []
for target in file_only:
    d, i = nearest(target, our_sorted, used)
    if i < 0 or d is None or d > 1.0:
        unmatched_file.append(target)
    else:
        used.add(i)

print(f"\nfile paths with NO matching model path within 1pt: {len(unmatched_file)}")
for p in unmatched_file[:15]:
    print(f"    file has a path at ({p[0]:8.2f}, {p[1]:8.2f})")
