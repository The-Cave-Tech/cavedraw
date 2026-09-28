#!/usr/bin/env python3
"""
Row-by-row: what the file draws versus what we imported.

Groups the form's glyph placements by y and counts them, then does the same for our
model's paths, so a row that loses geometry stands out.
"""
import json
import pathlib
import re

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
text = (ROOT / "artifacts" / "ref" / "orig-qdf.pdf").read_text(encoding="latin-1")
objects = dict((int(m.group(1)), m.group(2))
               for m in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S))
form = re.search(r"stream\n(.*?)\nendstream", objects[45], re.S).group(1)

placements = {}
for match in re.finditer(r"(?m)^1 0 0 1 ([-\d.]+) ([-\d.]+) cm$", form):
    x, y = float(match.group(1)), float(match.group(2))
    key = round(792.0 - y, 1)          # model-space y (top-left origin)
    placements.setdefault(key, []).append(x)

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


ours = {}
for path in paths:
    for sub in path.get("SubPaths") or []:
        for node in sub.get("Nodes") or []:
            a = node.get("Anchor")
            if a:
                ours.setdefault(round(a["Y"], 1), []).append(a["X"])
                break
        break

print(f"form rows: {len(placements)}   model rows: {len(ours)}")
print()
print(f"{'model y':>9} {'file':>6} {'ours':>6}   note")
rows = sorted(set(placements) | set(ours))
losses = 0
for row in rows:
    f_count = len(placements.get(row, []))
    o_count = len(ours.get(row, []))
    note = ""
    if f_count and o_count < f_count:
        note = "<-- FEWER THAN THE FILE"
        losses += 1
    print(f"{row:>9} {f_count:>6} {o_count:>6}   {note}")

print()
print(f"rows where we imported fewer paths than the file places: {losses}")
