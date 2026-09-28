#!/usr/bin/env python3
"""List our model's paths in the label band of page 10, with bounds and node counts."""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
artboard = model["result"]["Artboards"][9]  # page 10


def geometry(item):
    xs, ys, nodes = [], [], 0
    for sub in item.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor") or {}
            if "X" in a:
                xs.append(a["X"])
                ys.append(a["Y"])
                nodes += 1
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys), nodes


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

boxes = [(p, geometry(p)) for p in paths]
boxes = [(p, g) for p, g in boxes if g]

xs = [g[0] for _, g in boxes]
ys = [g[1] for _, g in boxes]
print(f"page 10 raw path bounds: x {min(xs):.1f}..{max(xs):.1f}  y {min(ys):.1f}..{max(ys):.1f}")
print()

# The label row. PDF y 695 in bottom-left = model y 97 from the top; glyph height ~5.7.
print("paths with x in 190..260 and y in 74..100 (the 'DEVANT ET DOS' row):")
row = [(p, g) for p, g in boxes if 185 <= g[0] <= 265 and 70 <= g[1] <= 100]
row.sort(key=lambda pg: pg[1][0])
for path, g in row:
    print(f"   x {g[0]:7.2f}..{g[2]:7.2f}  y {g[1]:6.2f}..{g[3]:6.2f}  "
          f"w {g[2]-g[0]:5.2f} h {g[3]-g[1]:5.2f}  nodes {g[4]:3d}  "
          f"subpaths {len(path.get('SubPaths') or [])}  fillVisible {path.get('Fill', {}).get('Visible')}")
print(f"  ({len(row)} paths)")
