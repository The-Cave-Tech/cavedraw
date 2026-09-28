#!/usr/bin/env python3
"""Where are the white-filled paths, and do they cover the label glyphs?"""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
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


def geom(item):
    xs, ys = [], []
    for sub in item.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor") or {}
            if "X" in a:
                xs.append(a["X"])
                ys.append(a["Y"])
    return (min(xs), min(ys), max(xs), max(ys)) if xs else None


def colour(path):
    c = (path.get("Fill") or {}).get("Color") or {}
    return (c.get("R"), c.get("G"), c.get("B"))


white = [(p, geom(p)) for p in paths if colour(p) == (1, 1, 1)]
white = [(p, g) for p, g in white if g]
print(f"page 10: {len(white)} white-filled paths")

label = [(p, g) for p, g in white if 60 <= g[1] <= 110 and 150 <= g[0] <= 300]
print(f"white paths overlapping the label band (x 150-300, y 60-110): {len(label)}")
for p, g in sorted(label, key=lambda pg: (pg[1][1], pg[1][0])):
    print(f"   x {g[0]:7.2f}..{g[2]:7.2f}  y {g[1]:6.2f}..{g[3]:6.2f}  "
          f"{g[2]-g[0]:5.2f}x{g[3]-g[1]:5.2f}")

print()
print("all white paths (first 25):")
for p, g in sorted(white, key=lambda pg: (pg[1][1], pg[1][0]))[:25]:
    print(f"   x {g[0]:7.2f}..{g[2]:7.2f}  y {g[1]:6.2f}..{g[3]:6.2f}  "
          f"{g[2]-g[0]:6.2f}x{g[3]-g[1]:6.2f}")
