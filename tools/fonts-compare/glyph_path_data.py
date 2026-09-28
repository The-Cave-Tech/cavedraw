#!/usr/bin/env python3
"""
Find the glyph paths at the O/S positions and inspect the data the renderer consumes.

File fills in that row start at (225.18,87.17), (226.71,77.18), (233.82,77.03),
(235.54,85.95). Look for glyph-sized model paths whose box contains those points.
"""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
TARGETS = [(225.18, 87.17), (226.71, 77.18), (233.82, 77.03), (235.54, 85.95)]

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


def box(path):
    xs, ys = [], []
    for sub in path.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                xs.append(a["X"])
                ys.append(a["Y"])
    return (min(xs), min(ys), max(xs), max(ys), len(xs)) if xs else None


print("glyph-sized model paths containing the target points (box <= 12pt):")
found = 0
for tx, ty in TARGETS:
    hits = []
    for path in paths:
        b = box(path)
        if not b:
            continue
        if b[2] - b[0] > 12 or b[3] - b[1] > 12:
            continue
        if b[0] - 0.5 <= tx <= b[2] + 0.5 and b[1] - 0.5 <= ty <= b[3] + 0.5:
            hits.append((b, path))
    print(f"\n({tx},{ty}): {len(hits)} candidate(s)")
    for b, path in hits[:4]:
        subs = path.get("SubPaths") or []
        print(f"   box ({b[0]:.2f},{b[1]:.2f})-({b[2]:.2f},{b[3]:.2f}) nodes={b[4]}")
        for i, sub in enumerate(subs):
            nodes = sub.get("Nodes") or []
            closed = sub.get("Closed")
            print(f"      subpath {i}: nodes={len(nodes)} closed={closed}")
            for n in nodes[:6]:
                a = n.get("Anchor") or {}
                ih = n.get("InHandle") or {}
                oh = n.get("OutHandle") or {}
                print(f"         a=({a.get('X',0):.2f},{a.get('Y',0):.2f}) "
                      f"in=({ih.get('X',0):.2f},{ih.get('Y',0):.2f}) "
                      f"out=({oh.get('X',0):.2f},{oh.get('Y',0):.2f})")
        found += 1
print(f"\ntotal candidates: {found}")
