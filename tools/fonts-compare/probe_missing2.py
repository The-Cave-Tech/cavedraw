#!/usr/bin/env python3
"""Probe the missing glyphs again, in document coordinates this time."""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
artboard = model["result"]["Artboards"][9]  # page 10

ax, ay = artboard.get("X", 0), artboard.get("Y", 0)
print(f"page 10 artboard origin in document space: ({ax}, {ay}) size "
      f"{artboard['Width']}x{artboard['Height']}")


def bounds(item):
    xs, ys = [], []
    for sub in item.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            for key in ("Anchor", "InHandle", "OutHandle"):
                pt = node.get(key)
                if isinstance(pt, dict) and "X" in pt:
                    xs.append(pt["X"])
                    ys.append(pt["Y"])
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

boxes = [(b, p) for p, b in ((p, bounds(p)) for p in paths) if b]
print(f"paths with geometry: {len(boxes)} of {len(paths)}")
print("page-local overall bounds:",
      tuple(round(v, 1) for v in
            (min(b[0] for b, _ in boxes) - ax, min(b[1] for b, _ in boxes) - ay,
             max(b[2] for b, _ in boxes) - ax, max(b[3] for b, _ in boxes) - ay)))

probes = {
    "O of 'DOS'": (242, 82),
    "S of 'DOS'": (258, 82),
    "C of 'CÔTÉS'": (168, 80),
    "0 of '150'": (135, 158),
    "D of 'DOS'": (232, 82),
}
for label, (px, py) in probes.items():
    dx, dy = px, py
    hits = [(b, p) for b, p in boxes
            if b[0] - 3 <= dx <= b[2] + 3 and b[1] - 3 <= dy <= b[3] + 3]
    print(f"  {label}: {len(hits)} path(s)")
    for b, p in hits[:3]:
        print(f"      local box=({b[0]-ax:.1f},{b[1]-ay:.1f})-({b[2]-ax:.1f},{b[3]-ay:.1f}) "
              f"rule={p.get('Fill', {}).get('Rule')} subpaths={len(p.get('SubPaths') or [])}")
