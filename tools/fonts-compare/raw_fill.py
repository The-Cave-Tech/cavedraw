#!/usr/bin/env python3
"""
Dump the raw operators of a fill that loses a subpath.

Walks the content stream keeping the CTM, records the line span of every fill, and prints
the span whose first point matches a target.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
PAGE_HEIGHT = 792.0

text = (ROOT / "artifacts" / "ref" / "orig-qdf.pdf").read_text(encoding="latin-1")
objects = dict((int(m.group(1)), m.group(2))
               for m in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S))
form = re.search(r"stream\n(.*?)\nendstream", objects[45], re.S).group(1)
lines = form.split("\n")

TARGET = (float(sys.argv[1]), float(sys.argv[2])) if len(sys.argv) > 2 else (156.38, 119.00)


def mul(m, n):
    return (m[0]*n[0]+m[2]*n[1], m[1]*n[0]+m[3]*n[1],
            m[0]*n[2]+m[2]*n[3], m[1]*n[2]+m[3]*n[3],
            m[0]*n[4]+m[2]*n[5]+m[4], m[1]*n[4]+m[3]*n[5]+m[5])


def to_model(x, y):
    px = ctm[0]*x + ctm[2]*y + ctm[4]
    py = ctm[1]*x + ctm[3]*y + ctm[5]
    return (px, PAGE_HEIGHT - py)


ctm = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
stack = []
nums = []
start_line = None
first_point = None
subpath_count = 0

for index, raw in enumerate(lines):
    line = raw.strip()
    if not line:
        continue

    parts = line.split()
    op = parts[-1]
    try:
        values = [float(v) for v in parts[:-1]]
    except ValueError:
        values = []

    if op == "q":
        stack.append(ctm)
    elif op == "Q":
        if stack:
            ctm = stack.pop()
    elif op == "cm" and len(values) == 6:
        ctm = mul(tuple(values), ctm)
    elif op == "m" and len(values) >= 2:
        if start_line is None:
            start_line = index
            first_point = to_model(values[-2], values[-1])
        subpath_count += 1
    elif op == "re" and len(values) >= 4:
        if start_line is None:
            start_line = index
            first_point = to_model(values[-4], values[-3])
        subpath_count += 1
    elif op in ("f", "f*", "F", "B", "B*", "b", "b*"):
        if first_point:
            d = abs(first_point[0] - TARGET[0]) + abs(first_point[1] - TARGET[1])
            if d < 1.0:
                print(f"fill at ({first_point[0]:.2f},{first_point[1]:.2f}) has "
                      f"{subpath_count} subpath(s), lines {start_line}..{index}")
                print("---")
                print("\n".join(lines[start_line:index + 1]))
                print("---")
                sys.exit(0)
        start_line = None
        first_point = None
        subpath_count = 0
    elif op == "n":
        start_line = None
        first_point = None
        subpath_count = 0

print(f"no fill found near {TARGET}")
