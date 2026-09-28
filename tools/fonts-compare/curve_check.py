#!/usr/bin/env python3
"""Are curve segments (c/curveto) surviving into our model?"""
import json
import pathlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
model = json.loads((ROOT / "artifacts" / "model.json").read_text(encoding="utf-8"))
document = model["result"]


def classify(node):
    anchor = node.get("Anchor") or {}
    a_in = node.get("InHandle") or anchor
    a_out = node.get("OutHandle") or anchor
    same_in = abs(a_in.get("X", 0) - anchor.get("X", 0)) < 1e-6 and \
        abs(a_in.get("Y", 0) - anchor.get("Y", 0)) < 1e-6
    same_out = abs(a_out.get("X", 0) - anchor.get("X", 0)) < 1e-6 and \
        abs(a_out.get("Y", 0) - anchor.get("Y", 0)) < 1e-6
    return same_in and same_out


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

    total_nodes = 0
    curved_nodes = 0
    paths_with_curves = 0
    multi_subpath = 0

    for path in paths:
        subs = path.get("SubPaths") or []
        if len(subs) > 1:
            multi_subpath += 1
        curved_here = False
        for sub in subs:
            for node in sub.get("Nodes") or []:
                total_nodes += 1
                if not classify(node):
                    curved_nodes += 1
                    curved_here = True
        if curved_here:
            paths_with_curves += 1

    print(f"page {page_index + 1}: {len(paths)} paths, {total_nodes} nodes, "
          f"{curved_nodes} curved ({100.0 * curved_nodes / max(total_nodes, 1):.1f}%), "
          f"{paths_with_curves} paths with curves, {multi_subpath} with >1 subpath")
