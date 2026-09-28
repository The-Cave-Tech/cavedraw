#!/usr/bin/env python3
"""
One multi-piece glyph from the form, checked against our model.

From the form:  a piece at PDF (150.2899933, 649.1436005) and a second piece at a
relative (3.8740997, -2.1036072). Model y = 792 - pdf y.
"""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
artboard = model["result"]["Artboards"][9]

PAGE_HEIGHT = 792.0
anchors = [
    ("piece 1", 150.2899933, PAGE_HEIGHT - 649.1436005),
    ("piece 2 (relative +3.874,-2.104)", 150.2899933 + 3.8740997, PAGE_HEIGHT - (649.1436005 - 2.1036072)),
]

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

print(f"page 10: {len(paths)} paths in our model")


def geom(item):
    xs, ys = [], []
    for sub in item.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor") or {}
            if "X" in a:
                xs.append(a["X"])
                ys.append(a["Y"])
    return (min(xs), min(ys), max(xs), max(ys)) if xs else None


for label, ax, ay in anchors:
    print(f"\n{label}: look for a path near ({ax:.2f}, {ay:.2f})")
    hits = []
    for path in paths:
        g = geom(path)
        if not g:
            continue
        if g[0] - 2 <= ax <= g[2] + 2 and g[1] - 2 <= ay <= g[3] + 2:
            hits.append(g)
    for g in hits[:6]:
        print(f"    box x {g[0]:8.2f}..{g[2]:8.2f}  y {g[1]:8.2f}..{g[3]:8.2f}")
    if not hits:
        print("    NO PATH FOUND")
