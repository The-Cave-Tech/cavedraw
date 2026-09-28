#!/usr/bin/env python3
"""
Simulate the graphics state through page 10's /TPL10 form.

Tracks q/Q/cm and reports the CTM in force at every path-painting operator. Poppler
renders this same stream correctly, so anywhere the accumulated scale stops looking like
a normal glyph transform, our parser has lost a state boundary.
"""
import pathlib
import re

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
text = (ROOT / "artifacts" / "ref" / "orig-qdf.pdf").read_text(encoding="latin-1")
objects = dict((int(m.group(1)), m.group(2))
               for m in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S))
form = re.search(r"stream\n(.*?)\nendstream", objects[45], re.S).group(1)

TOKEN = re.compile(r"""
    (?P<num>[-+]?\d*\.?\d+)
  | (?P<name>/[^\s/\[\]()<>{}]+)
  | (?P<op>[A-Za-z*'"]+)
  | (?P<other>\S)
""", re.X)


def mul(m, n):
    """m then n (PDF: CTM' = m x n)."""
    return (
        m[0] * n[0] + m[2] * n[1],
        m[1] * n[0] + m[3] * n[1],
        m[0] * n[2] + m[2] * n[3],
        m[1] * n[2] + m[3] * n[3],
        m[0] * n[4] + m[2] * n[5] + m[4],
        m[1] * n[4] + m[3] * n[5] + m[5],
    )


def scale_of(m):
    return (abs(m[0] * m[3] - m[1] * m[2])) ** 0.5


ctm = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
stack = []
operands = []
paints = []
q_count = q_reset = 0

for match in TOKEN.finditer(form):
    kind = match.lastgroup
    tok = match.group()
    if kind == "num":
        operands.append(float(tok))
    elif kind in ("name", "op"):
        op = tok
        if op == "q":
            stack.append(ctm)
            q_count += 1
        elif op == "Q":
            if stack:
                ctm = stack.pop()
            else:
                q_reset += 1
        elif op == "cm" and len(operands) >= 6:
            n = tuple(operands[-6:])
            ctm = mul(n, ctm)
        elif op in ("f", "f*", "F") and len(operands) == 0:
            paints.append(ctm)
        operands.clear()

print(f"q: {q_count}   Q underflow: {q_reset}   leftover stack: {len(stack)}")
print(f"path-painting ops with no operands: {len(paints)}")

scales = [round(scale_of(m), 3) for m in paints]
hist = {}
for s in scales:
    hist[s] = hist.get(s, 0) + 1
top = sorted(hist.items(), key=lambda kv: -kv[1])[:12]
print("accumulated scale at each paint op (most common first):")
for value, count in top:
    print(f"    scale {value:>10} : {count}")
