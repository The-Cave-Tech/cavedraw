"""Find where the embedded raster actually lands in each page raster.

The image data in our export is byte-identical to the original's and the placement box is
the same size in the same place, yet the region still differs by about 21 of 255. So one
of those beliefs is wrong. This decodes the image, converts it the way a viewer would for
a thumbnail comparison, and locates it by template matching in each rendered page.
"""
import re
import sys
import zlib

import numpy as np
from PIL import Image


def extract(path):
    """The largest CMYK image in the file, as a numpy array."""
    raw = open(path, "rb").read()
    best = None
    for m in re.finditer(rb"(\d+) 0 obj", raw):
        start = m.end()
        end = raw.find(b"endobj", start)
        body = raw[start:end]
        if b"/Image" not in body or b"stream" not in body:
            continue
        head = body[: body.find(b"stream")]

        def g(key):
            mm = re.search(rb"/" + key + rb"\s*/?([^\s/>\]]+)", head)
            return mm.group(1).decode() if mm else None

        if g(b"ColorSpace") != "DeviceCMYK":
            continue
        width, height = int(g(b"Width")), int(g(b"Height"))
        s = body.find(b"stream")
        s = body.find(b"\n", s) + 1
        data = zlib.decompress(body[s:body.find(b"endstream", s)])
        best = (width, height, np.frombuffer(data, dtype=np.uint8).reshape(height, width, 4))
    return best


def to_rgb(cmyk):
    c = cmyk[:, :, 0].astype(float) / 255.0
    m = cmyk[:, :, 1].astype(float) / 255.0
    y = cmyk[:, :, 2].astype(float) / 255.0
    k = cmyk[:, :, 3].astype(float) / 255.0
    return np.stack([(1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k)], axis=2) * 255


def main():
    width, height, cmyk = extract(sys.argv[1])
    image = to_rgb(cmyk)
    print(f"image {width}x{height}")

    # a high-contrast piece of the image: its top-left ninth
    patch = image[0:height // 4, 0:width // 3]
    patch_gray = patch.mean(axis=2)

    for path in sys.argv[2:]:
        page = np.array(Image.open(path).convert("L"), dtype=float)
        best = None
        # the placement is 136.7 x 241.5 pt; at 150 dpi that is ~285 x 503 px
        for scale in np.arange(0.85, 1.55, 0.01):
            ph = int(patch_gray.shape[0] * scale)
            pw = int(patch_gray.shape[1] * scale)
            if ph < 8 or pw < 8 or ph > page.shape[0] or pw > page.shape[1]:
                continue
            template = np.array(Image.fromarray(patch_gray.astype(np.uint8)).resize(
                (pw, ph), Image.LANCZOS), dtype=float)
            t = template - template.mean()
            tn = np.sqrt((t * t).sum())
            stride = 4
            for y in range(60, page.shape[0] - ph, stride):
                for x in range(60, page.shape[1] - pw, stride):
                    window = page[y:y + ph, x:x + pw]
                    w = window - window.mean()
                    score = (t * w).sum() / (tn * np.sqrt((w * w).sum()) + 1e-9)
                    if best is None or score > best[0]:
                        best = (score, x, y, scale)
        print("%-14s best match score=%.3f at (%d,%d) scale=%.2f"
              % (path.split("/")[-1], best[0], best[1], best[2], best[3]))


if __name__ == "__main__":
    main()
