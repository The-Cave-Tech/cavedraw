"""Print every form XObject's matrix, bounding box and text matrices.

A licence block or a template element usually lives in a form XObject, and its text
position is the composition of the caller's CTM, the form's /Matrix and the text matrix.
Reading them separately is how the offset can be attributed to one of the three instead of
guessed at.

Usage:  python xobject_matrices.py <file.pdf>
"""
import pathlib
import re
import sys
import zlib


def objects(raw):
    """(number, body) for every indirect object, resolving streams lazily."""
    for m in re.finditer(rb"(\d+) 0 obj", raw):
        start = m.end()
        end = raw.find(b"endobj", start)
        if end < 0:
            continue
        yield int(m.group(1)), raw[start:end]


def main():
    path = sys.argv[1]
    raw = pathlib.Path(path).read_bytes()

    for number, body in objects(raw):
        if b"/Subtype /Form" not in body and b"/Subtype/Form" not in body:
            continue

        head = body.split(b"stream", 1)[0].decode("latin-1", "replace")
        matrix = re.search(r"/Matrix\s*\[([^\]]*)\]", head)
        bbox = re.search(r"/BBox\s*\[([^\]]*)\]", head)

        stream = re.search(rb"stream\r?\n", body)
        text = ""
        if stream:
            data = body[stream.end():]
            data = data.rsplit(b"endstream", 1)[0]
            try:
                text = zlib.decompress(data).decode("latin-1", "replace")
            except Exception:
                text = ""

        tms = re.findall(r"([-\d.]+ [-\d.]+ [-\d.]+ [-\d.]+ [-\d.]+ [-\d.]+) Tm", text)

        print(f"XObject {number}")
        print(f"  Matrix  {matrix.group(1).strip() if matrix else '(none)'}")
        print(f"  BBox    {bbox.group(1).strip() if bbox else '(none)'}")
        print(f"  Tm      {len(tms)}; first: {tms[0] if tms else '(none)'}")
        if tms:
            # The y of the first text matrix, and the widest spread of them, so a block
            # whose lines are stacked can be told from one that is not.
            ys = [float(t.split()[5]) for t in tms]
            print(f"  Tm y    min {min(ys):.3f}  max {max(ys):.3f}")


if __name__ == "__main__":
    main()
