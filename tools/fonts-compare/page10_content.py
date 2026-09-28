#!/usr/bin/env python3
"""Extract page 10's content stream properly, via the page object's /Contents."""
import pathlib
import re
import sys

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
QDF = ROOT / "artifacts" / "ref" / "orig-qdf.pdf"
text = QDF.read_text(encoding="latin-1")

# Split into numbered objects.
objects: dict[int, str] = {}
for match in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S):
    objects[int(match.group(1))] = match.group(2)

pages = []
for number, body in objects.items():
    if re.search(r"/Type\s*/Page(?![s])", body):
        pages.append(number)

print(f"page objects found: {len(pages)}")
if len(pages) < 10:
    sys.exit("fewer than 10 pages")

page_number = pages[9]
body = objects[page_number]
contents = re.search(r"/Contents\s*(\d+)\s+0\s+R", body)
if not contents:
    array = re.search(r"/Contents\s*\[(.*?)\]", body, re.S)
    if not array:
        sys.exit("no /Contents on page 10")
    refs = re.findall(r"(\d+)\s+0\s+R", array.group(1))
else:
    refs = [contents.group(1)]

print(f"page 10 content object(s): {refs}")

stream = ""
for ref in refs:
    part = objects.get(int(ref), "")
    inner = re.search(r"stream\n(.*?)\nendstream", part, re.S)
    stream += (inner.group(1) if inner else part)

print(f"content stream length: {len(stream)}")
print()
print("=== first 4000 characters ===")
print(stream[:4000])
