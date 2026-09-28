#!/usr/bin/env python3
"""Does our model have a path where the reference draws the missing glyphs?"""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
document = model["result"]
artboard = document["Artboards"][9]  # page 10


def bounds(item):
    xs, ys = [], []

    def walk(paths):
        for sub in paths or []:
            for node in sub.get("Nodes") or []:
                pt = node.get("Point") or node.get("P") or {}
                if "X" in pt:
                    xs.append(pt["X"])
                    ys.append(pt["Y"])
            for key in ("Points", "Segments"):
                for seg in sub.get(key) or []:
                    pt = seg if "X" in seg else (seg.get("Point") or {})
                    if "X" in pt:
                        xs.append(pt["X"])
                        ys.append(pt["Y"])

    walk(item.get("SubPaths"))
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys)


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

print(f"page 10: {len(paths)} path items in our model")

# The missing 'O' of "DOS" sits at about x=242, y=82 in page-local PDF points.
probes = {
    "O of DOS (242,82)": (242, 82),
    "C of CÔTÉS (168,80)": (168, 80),
    "0 of 150 (135,158)": (135, 158),
}

for label, (px, py) in probes.items():
    hits = []
    for path in paths:
        box = bounds(path)
        if box and box[0] - 3 <= px <= box[2] + 3 and box[1] - 3 <= py <= box[3] + 3:
            hits.append((box, path.get("Fill", {}).get("Rule"), path.get("Fill", {}).get("Visible")))
    print(f"  {label}: {len(hits)} path(s) cover it")
    for box, rule, visible in hits[:4]:
        print(f"      box=({box[0]:.1f},{box[1]:.1f})-({box[2]:.1f},{box[3]:.1f}) "
              f"rule={rule} fillVisible={visible}")
