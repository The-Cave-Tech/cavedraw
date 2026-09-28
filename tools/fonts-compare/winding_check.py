#!/usr/bin/env python3
"""
Signed area (winding) of every subpath in the label rows.

The file fills with non-zero and gets holes, so an outer contour and its counter must be
wound in opposite directions. If ours come out the same way, the counter fills solid.
"""
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


def signed_area(sub):
    pts = []
    for node in sub.get("Nodes") or []:
        a = node.get("Anchor")
        if a:
            pts.append((a["X"], a["Y"]))
    total = 0.0
    for i in range(len(pts)):
        x0, y0 = pts[i]
        x1, y1 = pts[(i + 1) % len(pts)]
        total += x0 * y1 - x1 * y0
    return total / 2.0


def bounds(path):
    xs, ys = [], []
    for sub in path.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                xs.append(a["X"])
                ys.append(a["Y"])
    return (min(xs), min(ys), max(xs), max(ys)) if xs else None


# The "BA K" row and the "150 cm" row sit around model y 76..104.
print("subpath windings in the label rows (y 70..105):")
multi = 0
same_winding = 0
for path in paths:
    b = bounds(path)
    if not b or not (70 <= b[1] <= 105 and 180 <= b[0] <= 400):
        continue
    subs = path.get("SubPaths") or []
    areas = [signed_area(s) for s in subs]
    if len(subs) > 1:
        multi += 1
        signs = {1 if a > 0 else -1 for a in areas if abs(a) > 1e-9}
        if len(signs) == 1:
            same_winding += 1
            flag = "  <-- SAME WINDING: counter will fill"
        else:
            flag = "  (opposite winding: hole)"
    else:
        flag = ""
    print(f"  x {b[0]:7.2f} y {b[1]:6.2f} {b[2]-b[0]:5.2f}x{b[3]-b[1]:5.2f} "
          f"subpaths={len(subs)} areas=[{', '.join(f'{a:.2f}' for a in areas)}]{flag}")

print()
print(f"paths with >1 subpath: {multi};   all-same-winding among them: {same_winding}")
