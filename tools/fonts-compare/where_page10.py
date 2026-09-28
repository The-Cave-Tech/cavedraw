#!/usr/bin/env python3
"""Where are our page-10 paths, and does anything live in the label band?"""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
artboard = model["result"]["Artboards"][9]


def bounds(item):
    xs, ys = [], []

    def walk(paths):
        for sub in paths or []:
            for key in ("Nodes", "Points", "Segments"):
                for node in sub.get(key) or []:
                    pt = node.get("Point") or node
                    if isinstance(pt, dict) and "X" in pt:
                        xs.append(pt["X"])
                        ys.append(pt["Y"])

    walk(item.get("SubPaths"))
    return (min(xs), min(ys), max(xs), max(ys)) if xs else None


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

boxes = [b for b in (bounds(p) for p in paths) if b]
print(f"paths with geometry: {len(boxes)} of {len(paths)}")
print("overall bounds:", tuple(round(v, 1) for v in
                               (min(b[0] for b in boxes), min(b[1] for b in boxes),
                                max(b[2] for b in boxes), max(b[3] for b in boxes))))

# Anything in the label band (y 60..200)?
band = [b for b in boxes if b[1] < 200 and b[3] > 60]
print(f"paths overlapping y 60..200: {len(band)}")
for b in sorted(band, key=lambda b: b[1])[:20]:
    print("   ", tuple(round(v, 1) for v in b))

# How many subpaths does a path have? Do our paths have geometry at all?
print()
sizes = {}
for p in paths:
    n = len(p.get("SubPaths") or [])
    sizes[n] = sizes.get(n, 0) + 1
print("subpath-count histogram:", dict(sorted(sizes.items())[:10]))
