#!/usr/bin/env python3
"""
Track the fill colour through /TPL10 and compare it with our model.

If the file paints a glyph's counter white (to punch the hole) and we recorded it black,
the counter renders solid — the "6 with no hole".
"""
import json
import pathlib
import re

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
text = (ROOT / "artifacts" / "ref" / "orig-qdf.pdf").read_text(encoding="latin-1")
objects = dict((int(m.group(1)), m.group(2))
               for m in re.finditer(r"(?m)^(\d+) 0 obj\n(.*?)\nendobj", text, re.S))
form = re.search(r"stream\n(.*?)\nendstream", objects[45], re.S).group(1)

PAGE_HEIGHT = 792.0
TOKEN = re.compile(r"""
    (?P<num>[-+]?\d*\.?\d+)
  | (?P<name>/[^\s/\[\]()<>{}]*)
  | (?P<op>[A-Za-z*'"]+)
  | (?P<other>\S)
""", re.X)


def mul(m, n):
    return (m[0]*n[0]+m[2]*n[1], m[1]*n[0]+m[3]*n[1],
            m[0]*n[2]+m[2]*n[3], m[1]*n[2]+m[3]*n[3],
            m[0]*n[4]+m[2]*n[5]+m[4], m[1]*n[4]+m[3]*n[5]+m[5])


ctm = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
stack = []
ops = []
pending = None
painted = []            # (colour, model_x, model_y)
grey = 0.0              # current grey fill (g)
rgb = None              # current rgb fill (rg)
cmyk = None             # current cmyk fill (k)

for match in TOKEN.finditer(form):
    kind, tok = match.lastgroup, match.group()
    if kind == "num":
        ops.append(float(tok))
    elif kind in ("name", "op"):
        if tok == "q":
            stack.append((ctm, grey, rgb, cmyk))
        elif tok == "Q":
            if stack:
                ctm, grey, rgb, cmyk = stack.pop()
        elif tok == "cm" and len(ops) >= 6:
            ctm = mul(tuple(ops[-6:]), ctm)
        elif tok == "g" and len(ops) >= 1:
            grey, rgb, cmyk = ops[-1], None, None
        elif tok == "rg" and len(ops) >= 3:
            rgb, grey, cmyk = tuple(ops[-3:]), None, None
        elif tok == "k" and len(ops) >= 4:
            cmyk, grey, rgb = tuple(ops[-4:]), None, None
        elif tok == "m" and len(ops) >= 2:
            x, y = ops[-2], ops[-1]
            pending = (ctm[0]*x + ctm[2]*y + ctm[4], PAGE_HEIGHT - (ctm[1]*x + ctm[3]*y + ctm[5]))
        elif tok in ("f", "f*", "F") and pending:
            colour = rgb if rgb is not None else ((grey, grey, grey) if grey is not None else
                                                  (f"cmyk{cmyk}" if cmyk else "?"))
            painted.append((colour, pending[0], pending[1]))
            pending = None
        elif tok == "n":
            pending = None
        ops.clear()

white = [p for p in painted if p[0] == (1.0, 1.0, 1.0)]
print(f"paths painted in /TPL10: {len(painted)}")
print(f"  painted pure white (1,1,1): {len(white)}")
if white:
    print("  their positions:")
    for c, x, y in sorted(white, key=lambda p: (p[2], p[1]))[:20]:
        print(f"     ({x:8.2f}, {y:8.2f})")

others = {}
for c, _, _ in painted:
    others[str(c)] = others.get(str(c), 0) + 1
print("\nfill colour histogram in the form:")
for key, count in sorted(others.items(), key=lambda kv: -kv[1])[:8]:
    print(f"   {key} : {count}")
