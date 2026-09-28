"""Print the graphics-state objects a PDF defines.

A file's /ExtGState dictionaries hold the alpha, blend mode and overprint settings. If a
file sets them and its exporter does not, the difference is invisible in operator counts and
obvious on the page: opaque where the original was translucent.

Usage:  python graphics_states.py <file.pdf>
"""
import pathlib
import re
import sys


def main():
    path = sys.argv[1]
    raw = pathlib.Path(path).read_bytes()

    names = set()
    for m in re.finditer(rb"/ExtGState\s*<<([^>]*)>>", raw):
        for ref in re.finditer(rb"/(\w+)\s+(\d+) 0 R", m.group(1)):
            names.add((ref.group(1).decode("latin-1"), int(ref.group(2))))

    print(f"{len(names)} named graphics states")

    for name, number in sorted(names, key=lambda p: p[1]):
        m = re.search((r"(?<![0-9])%d 0 obj(.{0,300}?)endobj" % number).encode(), raw, re.S)
        if not m:
            print(f"  /{name} -> {number} 0 R (not found)")
            continue

        body = " ".join(m.group(1).decode("latin-1").split())
        body = body.replace("stream", "stream[...]")
        print(f"  /{name} -> {number} 0 R  {body[:150]}")


if __name__ == "__main__":
    main()
