#!/usr/bin/env python3
"""Dump the /TPL10 form XObject that draws page 10's labels."""
import pathlib
import re
import sys

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
QDF = ROOT / "artifacts" / "ref" / "orig-qdf.pdf"
text = QDF.read_text(encoding="latin-1")

objects: dict[int, str] = {}
for match in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S):
    objects[int(match.group(1))] = match.group(2)


def stream_of(number: int) -> str:
    body = objects.get(number, "")
    inner = re.search(r"stream\n(.*?)\nendstream", body, re.S)
    return inner.group(1) if inner else ""


# Find the XObject dictionary entry for TPL10.
for number, body in objects.items():
    if "/TPL10" not in body:
        continue
    ref = re.search(r"/TPL10\s+(\d+)\s+0\s+R", body)
    if not ref:
        continue
    target = int(ref.group(1))
    print(f"/TPL10 is object {target}")
    print("dict:", objects[target][:200].replace("\n", " "))
    content = stream_of(target)
    print(f"form content length: {len(content)}")
    print()

    # Show the beginning, and the region that draws the label row.
    print("=== first 2500 characters ===")
    print(content[:2500])
    break
else:
    print("TPL10 not found; searching resources")
