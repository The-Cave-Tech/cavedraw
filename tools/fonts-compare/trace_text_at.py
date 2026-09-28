"""Show the text operators whose matrix lands near a point, so a fragment can be traced.

Given a model-space origin (top-left, +Y down) and the page height, this prints the
content-stream operators around the matching PDF-space baseline. Use it to answer "what
did the file actually draw here?" without reading the whole stream.

Usage:  python trace_text_at.py <file.pdf> <x> <y_model> [page_height]
"""
import pathlib
import re
import sys
import zlib


def main():
    path = sys.argv[1]
    x = float(sys.argv[2])
    y_model = float(sys.argv[3])
    height = float(sys.argv[4]) if len(sys.argv) > 4 else 792.0

    # The model stores a block's top-left; the baseline sits one ascent below it, so the
    # window is generous rather than exact.
    want = height - y_model
    print(f"looking for text near x={x} pdf-y={want}\n")

    raw = pathlib.Path(path).read_bytes()
    for m in re.finditer(rb"(?<!end)stream\r?\n", raw):
        start = m.end()
        end = raw.find(b"endstream", start)
        if end < 0:
            continue
        try:
            text = zlib.decompress(raw[start:end]).decode("latin-1", "replace")
        except Exception:
            continue

        if not re.search(r"(?<![A-Za-z])BT(?![A-Za-z])", text):
            continue

        lines = [l.strip() for l in text.splitlines()]
        for i, line in enumerate(lines):
            tm = re.match(
                r"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) Tm$", line)
            if not tm:
                continue

            tx, ty = float(tm.group(5)), float(tm.group(6))
            if abs(tx - x) > 40 or abs(ty - want) > 30:
                continue

            print(f"--- Tm {line}")
            for inner in lines[i + 1:i + 4]:
                print("   ", inner[:150])


if __name__ == "__main__":
    main()
