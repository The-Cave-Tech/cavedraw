#!/usr/bin/env python3
"""Count what the importer produced per page, by item kind."""
import collections
import json
import pathlib
import sys

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
document = model["result"]

KIND = "$kind"


def walk(items, counter):
    for item in items or []:
        counter[item.get(KIND)] += 1
        for key in ("Items", "Children"):
            if item.get(key):
                walk(item[key], counter)


print(f"{'page':>4}  {'paths':>6} {'groups':>7} {'text':>5}   top-level keys")
for index, artboard in enumerate(document["Artboards"], start=1):
    counter = collections.Counter()
    for layer in artboard["Layers"]:
        walk(layer["Items"], counter)
    interesting = {k: v for k, v in counter.items() if k}
    print(f"{index:>4}  {counter.get('path', 0):>6} {counter.get('group', 0):>7} "
          f"{len(artboard['Layers']):>5}   {list(interesting)[:8]}")

# What does a path item look like?
artboard = document["Artboards"][9]
sample = None


def find(items):
    global sample
    for item in items or []:
        if item.get(KIND) == "path" and sample is None:
            sample = item
        for key in ("Items", "Children"):
            if item.get(key):
                find(item[key])


for layer in artboard["Layers"]:
    find(layer["Items"])
if sample:
    print()
    print("a path item on page 10 has keys:", list(sample.keys()))
    for key in ("Fill", "Stroke", "Style"):
        if key in sample:
            print(f"  {key}:", json.dumps(sample[key])[:220])
