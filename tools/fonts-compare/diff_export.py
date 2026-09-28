#!/usr/bin/env python3
"""
Diff poppler's reading of the original against poppler's reading of our export.

Both sides are measured by the same extractor, so any difference is ours: text we
dropped, or text we placed somewhere else.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
REF = ROOT / "artifacts" / "ref" / "ref.xml"
OURS = ROOT / "artifacts" / "ref" / "ours.xml"

WORD = re.compile(
    r'<word xMin="([\d.]+)" yMin="([\d.]+)" xMax="([\d.]+)" yMax="([\d.]+)">([^<]*)</word>'
)


def pages(path):
    text = path.read_text(encoding="utf-8")
    out = []
    for page in re.finditer(r"<page width=\"[\d.]+\" height=\"[\d.]+\">(.*?)</page>", text, re.S):
        out.append([
            (float(a), float(b), float(c), float(d), w)
            for a, b, c, d, w in WORD.findall(page.group(1))
        ])
    return out


def main():
    ref = pages(REF)
    ours = pages(OURS)

    print(f"pages: reference {len(ref)}, ours {len(ours)}")
    print(f"words: reference {sum(len(p) for p in ref)}, ours {sum(len(p) for p in ours)}")
    print()

    total_missing = total_moved = 0
    for index in range(min(len(ref), len(ours))):
        r, o = ref[index], ours[index]
        our_by_text = {}
        for x, y, x1, y1, w in o:
            our_by_text.setdefault(w, []).append((x, y))

        missing, moved = [], []
        for x, y, x1, y1, w in r:
            if w not in our_by_text:
                missing.append((x, y, w))
                continue
            # Nearest placement of the same word on this page.
            best = min(our_by_text[w], key=lambda p: abs(p[0] - x) + abs(p[1] - y))
            dx, dy = best[0] - x, best[1] - y
            if abs(dx) > 1.5 or abs(dy) > 1.5:
                moved.append((x, y, w, dx, dy))

        total_missing += len(missing)
        total_moved += len(moved)
        flag = "" if not missing and not moved else "   <-- "
        print(f"page {index + 1:2d}: ref {len(r):3d}  ours {len(o):3d}  "
              f"missing {len(missing):2d}  moved {len(moved):2d}{flag}")
        for x, y, w in missing[:8]:
            print(f"        MISSING ({x:7.1f},{y:7.1f}) {w!r}")
        for x, y, w, dx, dy in moved[:8]:
            print(f"        MOVED   ({x:7.1f},{y:7.1f}) {w!r}  by ({dx:+.1f},{dy:+.1f})")

    print()
    print(f"TOTAL missing {total_missing}, moved {total_moved}")


if __name__ == "__main__":
    sys.exit(main())
