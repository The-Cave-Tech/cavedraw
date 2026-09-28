#!/usr/bin/env python3
"""Show the content-stream operators that draw the page-number table on page 1."""
import pathlib
import re
import zlib

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
PDF = ROOT / "samples" / "3464_LILLIE_View_A_Sides_color.pdf"
data = PDF.read_bytes()

# Collect every flate stream, keep the ones that look like page content.
streams = []
for match in re.finditer(rb"stream\r?\n", data):
    start = match.end()
    end = data.find(b"endstream", start)
    if end < 0:
        continue
    raw = data[start:end]
    try:
        decoded = zlib.decompress(raw)
    except Exception:
        continue
    if b"Tj" in decoded or b"TJ" in decoded:
        streams.append(decoded)

print(f"content streams found: {len(streams)}")

for stream in streams:
    text = stream.decode("latin-1")
    if "(123)" not in text and "123" not in text:
        continue
    # Print the text object that draws the digits.
    for block in re.finditer(r"BT(.*?)ET", text, re.S):
        body = block.group(1)
        if "123" not in body:
            continue
        print("=========== a text object containing 123 ===========")
        print(body.strip()[:2500])
        print()
