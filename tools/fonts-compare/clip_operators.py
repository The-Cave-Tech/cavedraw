"""Print the clip path for every `W` operator in a PDF, with its bounding box.

A file's clipping is where "the lines spill past the object" comes from, and it is not
always in the page content: a form XObject may carry its own. This walks every stream,
reports each clip's extent, and names the stream it came from.

Usage:  python clip_operators.py <file.pdf> [max-per-stream]
"""
import pathlib
import re
import sys
import zlib


def main():
    path = sys.argv[1]
    per_stream = int(sys.argv[2]) if len(sys.argv) > 2 else 3

    raw = pathlib.Path(path).read_bytes()
    boxes = {}

    for m in re.finditer(rb"(\d+) 0 obj", raw):
        number = int(m.group(1))
        start = m.end()
        end = raw.find(b"endobj", start)
        body = raw[start:end]

        stream = re.search(rb"stream\r?\n", body)
        if not stream:
            continue

        try:
            text = zlib.decompress(body[stream.end():].rsplit(b"endstream", 1)[0]) \
                .decode("latin-1", "replace")
        except Exception:
            continue

        # A font programme or an image can contain the byte 'W'; a content stream has
        # operators around it.
        if "BT" not in text and " re" not in text and " m" not in text:
            continue

        lines = [l.strip() for l in text.splitlines()]
        shown = 0

        for i, line in enumerate(lines):
            # "W n" on one line and "W" then "n" on two are both legal, and both turn up.
            if not re.search(r"(?<![A-Za-z])W\*?(?![A-Za-z])", line):
                continue

            xs, ys = [], []
            for j in range(i - 1, max(-1, i - 2000), -1):
                for mm in re.finditer(r"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re$", lines[j]):
                    x, y, w, h = (float(g) for g in mm.groups())
                    xs += [x, x + w]
                    ys += [y, y + h]
                for mm in re.finditer(r"([-\d.]+) ([-\d.]+) (?:m|c|l)$", lines[j]):
                    xs.append(float(mm.group(1)))
                    ys.append(float(mm.group(2)))
                if lines[j] == "q" and xs:
                    break

            if not xs:
                continue

            key = (round(min(xs), 2), round(min(ys), 2), round(max(xs), 2), round(max(ys), 2))
            boxes.setdefault(key, []).append(number)

            if shown < per_stream:
                print(f"  obj {number}: clip bbox x {key[0]}..{key[2]}  y {key[1]}..{key[3]}")
                shown += 1

    print(f"\n{len(boxes)} distinct clip boxes")
    for key, streams in sorted(boxes.items(), key=lambda kv: -len(kv[1]))[:20]:
        print(f"  {len(streams):5d} uses  x {key[0]:8.2f}..{key[2]:8.2f}  "
              f"y {key[1]:8.2f}..{key[3]:8.2f}  in streams {streams[:4]}")


if __name__ == "__main__":
    main()
