#!/usr/bin/env python3
"""What are the paths in the DOS row that fail to render?"""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
TARGETS = [(225.18, 87.17), (226.71, 77.18), (233.82, 77.03), (235.54, 85.95),
           (202.21, 76.25), (196.80, 76.25)]

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


def info(path):
    nodes = 0
    xs, ys = [], []
    areas = []
    for sub in path.get("SubPaths") or []:
        pts = []
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                pts.append((a["X"], a["Y"]))
                xs.append(a["X"])
                ys.append(a["Y"])
        nodes += len(pts)
        total = 0.0
        for i in range(len(pts)):
            x0, y0 = pts[i]
            x1, y1 = pts[(i + 1) % len(pts)]
            total += x0 * y1 - x1 * y0
        areas.append(total / 2.0)
    if not xs:
        return None
    return (min(xs), min(ys), max(xs), max(ys), nodes, len(path.get("SubPaths") or []), areas)


for tx, ty in TARGETS:
    hits = []
    for path in paths:
        g = info(path)
        if not g:
            continue
        if abs(g[0] - tx) < 0.05 and abs(g[1] - ty) < 0.05:
            hits.append((g, path))
    print(f"({tx},{ty}): {len(hits)} path(s)")
    for g, path in hits:
        fill = path.get("Fill") or {}
        c = fill.get("Color") or {}
        print(f"    box {g[2]-g[0]:.2f}x{g[3]-g[1]:.2f}  nodes={g[4]} subpaths={g[5]} "
              f"areas={[round(a,2) for a in g[6]]}")
        print(f"    fillVisible={fill.get('Visible')} rule={fill.get('Rule')} "
              f"rgb=({c.get('R')},{c.get('G')},{c.get('B')}) a={c.get('A')} "
              f"strokeVisible={(path.get('Stroke') or {}).get('Visible')}")
