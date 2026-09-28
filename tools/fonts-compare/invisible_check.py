#!/usr/bin/env python3
"""How many imported paths are marked unfilled/unstroked — i.e. invisible?"""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
document = model["result"]

for page_index in (0, 9, 10):
    artboard = document["Artboards"][page_index]
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

    no_fill = [p for p in paths if not (p.get("Fill") or {}).get("Visible")]
    no_stroke = [p for p in paths if not (p.get("Stroke") or {}).get("Visible")]
    rules = {}
    for p in paths:
        rule = (p.get("Fill") or {}).get("Rule")
        rules[rule] = rules.get(rule, 0) + 1

    print(f"page {page_index + 1}: {len(paths)} paths | "
          f"fill invisible: {len(no_fill)} | stroke invisible: {len(no_stroke)} | rules: {rules}")

    # What do the unfilled ones look like?
    for p in no_fill[:5]:
        subs = p.get("SubPaths") or []
        nodes = sum(len(s.get("Nodes") or []) for s in subs)
        print(f"      invisible-fill path: subpaths {len(subs)} nodes {nodes} "
              f"strokeVisible={(p.get('Stroke') or {}).get('Visible')}")
