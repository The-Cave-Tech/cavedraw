#!/usr/bin/env python3
"""Dump the text items our import produced for one page, with origin and per-run font."""
import json
import pathlib
import sys

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
PAGE = int(sys.argv[1]) if len(sys.argv) > 1 else 1

model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
document = model["result"]
artboard = document["Artboards"][PAGE - 1]

items = []


def walk(nodes):
    for node in nodes or []:
        if node.get("Runs") is not None:
            items.append(node)
        for key in ("Items", "Children"):
            if node.get(key):
                walk(node[key])


for layer in artboard["Layers"]:
    walk(layer["Items"])

items.sort(key=lambda i: (i["Origin"]["Y"], i["Origin"]["X"]))
print(f"page {PAGE}: {len(items)} text items   (artboard {artboard['Width']:.0f}x{artboard['Height']:.0f})")
print()
for item in items:
    origin = item["Origin"]
    runs = item.get("Runs") or []
    text = "".join(r.get("Text", "") for r in runs)
    fonts = {(r.get("SourceFont"), r.get("FontFamily"), r.get("FontSize"),
              r.get("Bold"), r.get("Italic"),
              "embedded" if r.get("EmbeddedFont") else "no-programme",
              len(r.get("GlyphIds") or [])) for r in runs}
    print(f"  x={origin['X']:7.1f} y={origin['Y']:7.1f}  {text!r}")
    for f in fonts:
        print(f"        source={f[0]!r} family={f[1]!r} size={f[2]} bold={f[3]} italic={f[4]} "
              f"{f[5]} glyphs={f[6]}")
