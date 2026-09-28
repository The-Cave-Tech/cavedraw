#!/usr/bin/env python3
"""What colour are the glyph paths at the label? Invisible fill = missing text."""
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


# The label row sits at model y 96.5..102.2, x from 195 to 250.
row = [(p, geom(p)) for p in paths]
row = [(p, g) for p, g in row if g and 70 <= g[1] <= 105 and 185 <= g[0] <= 260]
row.sort(key=lambda pg: (round(pg[1][1]), pg[1][0]))

print(f"{len(row)} paths in the label band")
for path, g in row:
    fill = path.get("Fill") or {}
    stroke = path.get("Stroke") or {}
    colour = fill.get("Color") or {}
    sc = stroke.get("Color") or {}
    print(f"  x {g[0]:7.2f} y {g[1]:6.2f} {g[2]-g[0]:5.2f}x{g[3]-g[1]:5.2f} | "
          f"fillVisible={fill.get('Visible')} rgb=({colour.get('R')},{colour.get('G')},{colour.get('B')}) "
          f"a={colour.get('A')} rule={fill.get('Rule')} | "
          f"strokeVisible={stroke.get('Visible')} srgb=({sc.get('R')},{sc.get('G')},{sc.get('B')})")

# colour histogram across the whole page
hist = {}
for path in paths:
    fill = path.get("Fill") or {}
    c = fill.get("Color") or {}
    key = (fill.get("Visible"), c.get("R"), c.get("G"), c.get("B"))
    hist[key] = hist.get(key, 0) + 1
print("\npage 10 fill colour histogram (visible, r, g, b) -> count:")
for key, count in sorted(hist.items(), key=lambda kv: -kv[1])[:10]:
    print(f"   {key} : {count}")
