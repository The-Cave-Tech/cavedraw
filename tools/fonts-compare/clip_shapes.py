"""Show the clip paths a PDF sets, so their shape can be seen before modelling them.

A clip to a page-sized rectangle is a no-op when the page box already cuts the content.
A clip to a circle or an arbitrary outline is not, and is the case worth handling. Telling
them apart decides whether clipping is worth the machinery.

Usage:  python clip_shapes.py <file.pdf> [max]
"""
import pathlib
import re
import sys
import zlib


def content_streams(path):
    raw = pathlib.Path(path).read_bytes()
    out = []
    for m in re.finditer(rb"(?<!end)stream\r?\n", raw):
        start = m.end()
        end = raw.find(b"endstream", start)
        if end < 0:
            continue
        try:
            data = zlib.decompress(raw[start:end])
        except Exception:
            data = raw[start:end]
        printable = sum(1 for b in data if 9 <= b <= 13 or 32 <= b <= 126)
        if len(data) and printable >= len(data) * 0.9:
            out.append(data.decode("latin-1"))
    return out


def clips(text):
    """Every clip: the path built since the last paint operator, up to W."""
    found = []
    ops = []
    for line in text.splitlines():
        ops.append(line.strip())
        if re.search(r"(?<![A-Za-z])W\*?(?![A-Za-z])", line):
            # Walk back to the last painting or clip operator: that is this path.
            start = len(ops) - 1
            while start > 0 and ops[start - 1] not in ("", "q", "Q"):
                start -= 1
            segment = ops[start:len(ops)]
            found.append(segment)
    return found


def classify(segment):
    text = " ".join(segment)
    rects = re.findall(r"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re", text)
    if rects:
        return "rect " + str(rects[0])
    curves = len(re.findall(r"(?<![A-Za-z])c(?![A-Za-z])", text))
    lines = len(re.findall(r"(?<![A-Za-z])l(?![A-Za-z])", text))
    if curves:
        return f"curved outline ({curves} curves)"
    if lines:
        return f"polygon ({lines} lines)"
    return "unknown: " + text[:60]


for path in sys.argv[1:]:
    limit = 6
    print("===", path.split("/")[-1])
    total = 0
    kinds = {}
    for text in content_streams(path):
        for seg in clips(text):
            total += 1
            kind = classify(seg)
            key = kind.split(" (")[0]
            kinds[key] = kinds.get(key, 0) + 1
            if total <= limit:
                print("   ", kind[:100])
    print(f"    total clips: {total}   kinds: {kinds}")
