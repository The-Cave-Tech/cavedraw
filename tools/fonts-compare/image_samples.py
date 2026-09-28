"""Compare the decoded sample streams of the images in two PDFs.

An image can be placed correctly, sized correctly and still differ, because what matters is
the bytes. This prints each image object's dictionary, the length and hash of its decoded
stream, and - where the two files can be lined up by size - whether the bytes match.

Usage:  python image_samples.py <file-a.pdf> <file-b.pdf>
"""
import hashlib
import pathlib
import re
import sys
import zlib


def images(path):
    raw = pathlib.Path(path).read_bytes()
    found = []

    for m in re.finditer(rb"(\d+) 0 obj", raw):
        start = m.end()
        end = raw.find(b"endobj", start)
        body = raw[start:end]
        if b"/Subtype /Image" not in body and b"/Subtype/Image" not in body:
            continue

        stream = re.search(rb"stream\r?\n", body)
        if not stream:
            continue

        head = " ".join(body[:stream.start()].decode("latin-1", "replace").split())
        data = body[stream.end():].rsplit(b"endstream", 1)[0]

        try:
            decoded = zlib.decompress(data)
        except Exception:
            decoded = None

        found.append((m.group(1).decode(), head, data, decoded))

    return found


def main():
    a, b = sys.argv[1], sys.argv[2]

    for name, path in (("A " + pathlib.Path(a).name, a), ("B " + pathlib.Path(b).name, b)):
        print(f"=== {name}")
        for number, head, raw, decoded in images(path):
            size = (head + " ").split("/Width ")[-1].split()[0] if "/Width " in head else "?"
            height = (head + " ").split("/Height ")[-1].split()[0] if "/Height " in head else "?"
            space = (head + " ").split("/ColorSpace ")[-1].split("/")[0].strip() if "/ColorSpace " in head else "?"
            digest = hashlib.sha256(decoded).hexdigest()[:12] if decoded else "n/a"
            print(f"  obj {number}: {size}x{height} {space}  raw {len(raw)}  "
                  f"decoded {len(decoded) if decoded else 0}  sha {digest}")
            if "/DecodeParms" in head:
                parms = head.split("/DecodeParms")[1].split("/Filter")[0]
                print(f"     DecodeParms {parms.strip()[:80]}")
        print()

    left = [i for i in images(a) if i[3] and i[1].count("CMYK")]
    right = [i for i in images(b) if i[3] and i[1].count("CMYK")]

    if left and right:
        same = left[0][3] == right[0][3]
        print(f"CMYK samples identical: {same}")
        if not same:
            differing = sum(1 for x, y in zip(left[0][3], right[0][3]) if x != y)
            print(f"  {differing} of {len(left[0][3])} bytes differ "
                  f"({100.0 * differing / max(1, len(left[0][3])):.1f}%)")


if __name__ == "__main__":
    main()
