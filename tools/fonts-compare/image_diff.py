"""Compare the embedded raster's raw samples between the original and our export."""
import re
import sys
import zlib


def image_streams(path):
    raw = open(path, "rb").read()
    found = []
    for m in re.finditer(rb"(\d+) 0 obj", raw):
        start = m.end()
        end = raw.find(b"endobj", start)
        if end < 0:
            continue
        body = raw[start:end]
        if b"/Image" not in body:
            continue
        head = body[: body.find(b"stream")]
        if b"stream" not in body:
            continue

        def g(key):
            mm = re.search(rb"/" + key + rb"\s*/?([^\s/>\]]+)", head)
            return mm.group(1).decode() if mm else None

        s = body.find(b"stream")
        s = body.find(b"\n", s) + 1
        e = body.find(b"endstream", s)
        try:
            data = zlib.decompress(body[s:e])
        except Exception:
            data = body[s:e]
        found.append((m.group(1).decode(), g(b"Width"), g(b"Height"),
                      g(b"BitsPerComponent"), g(b"ColorSpace"), data))
    return found


for path in sys.argv[1:]:
    print("===", path.split("/")[-1])
    for number, w, h, bpc, cs, data in image_streams(path):
        head = data[:16].hex(" ")
        print("   obj %-5s %sx%s bpc=%s cs=%-12s bytes=%-8d sha=%08x  first: %s"
              % (number, w, h, bpc, cs, len(data), zlib.crc32(data) & 0xFFFFFFFF, head))
