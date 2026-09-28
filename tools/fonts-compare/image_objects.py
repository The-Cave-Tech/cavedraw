"""Print every image XObject's dictionary, and the first bytes of its samples.

An image that is imported correctly and exported correctly can still render differently if
its colour space, /Decode array or bit depth are written differently: the samples are the
same numbers and the viewer is told to read them another way.

Usage:  python image_objects.py <file.pdf> [max]
"""
import pathlib
import re
import sys


def main():
    path = sys.argv[1]
    limit = int(sys.argv[2]) if len(sys.argv) > 2 else 4

    raw = pathlib.Path(path).read_bytes()
    shown = 0

    for m in re.finditer(rb"(\d+) 0 obj\s*<<", raw):
        start = m.end() - 2
        end = raw.find(b"endobj", start)
        body = raw[start:end]
        if b"/Subtype /Image" not in body and b"/Subtype/Image" not in body:
            continue

        stream = re.search(rb"stream\r?\n", body)
        head = body[:stream.start()] if stream else body
        dictionary = " ".join(head.decode("latin-1", "replace").split())
        dictionary = dictionary.replace("<", "<").lstrip("<")

        print(f"obj {m.group(1).decode()}: {dictionary[:260]}")

        if stream:
            data = body[stream.end():].rsplit(b"endstream", 1)[0]
            print(f"   raw first 24 bytes: {data[:24].hex(' ')}")

        shown += 1
        if shown >= limit:
            break


if __name__ == "__main__":
    main()
