"""Count how often each PDF content operator appears in a file's streams.

Used to decide what is worth handling: an operator that never appears in real files is a
different kind of gap from one that appears hundreds of times.
"""
import re
import sys
import zlib
from collections import Counter


def streams(path):
    raw = open(path, "rb").read()
    out = []
    for m in re.finditer(rb"(?<!end)stream\r?\n", raw):
        start = m.end()
        end = raw.find(b"endstream", start)
        if end < 0:
            continue
        chunk = raw[start:end]
        try:
            out.append(zlib.decompress(chunk).decode("latin-1"))
        except Exception:
            pass
    return "\n".join(out)


OPS = ["Tc", "Tw", "Tz", "Ts", "Tr", "TL", "'", '"', "BI", "ID", "EI", "sh",
       "W*", "BX", "EX", "ET", "ri", "d0", "d1", "DP", "MP", "i"]

for path in sys.argv[1:]:
    text = streams(path)
    counts = Counter()
    for op in OPS:
        pattern = r"(?<![A-Za-z])" + re.escape(op) + r"(?![A-Za-z])"
        counts[op] = len(re.findall(pattern, text))
    present = {k: v for k, v in counts.items() if v}
    absent = [k for k, v in counts.items() if not v]
    print("%-46s %s" % (path.split("/")[-1], present or "none"))
    if absent:
        print("      absent:", " ".join(absent))
