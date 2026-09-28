"""Render known DeviceCMYK patches and read back the RGB a viewer produces.

The importer converts CMYK to RGB with r = (1-c)(1-k). That is not what a viewer does:
the sample's k=0.3 grey renders as 189/255, not the 178 the formula gives. This builds a
patch sheet, renders it with poppler, and prints the measured transform so the conversion
can be matched to reality instead of to a guess.
"""
import subprocess
import sys

PATCHES = []
for k in (0.0, 0.1, 0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.7, 0.75, 0.8, 0.9, 1.0):
    PATCHES.append((0.0, 0.0, 0.0, k))
for c, m, y in ((1, 0, 0), (0, 1, 0), (0, 0, 1), (0.008, 0.141, 0.855),
                (0.616, 0.047, 0.0), (0.871, 0.749, 0.0), (0.5, 0.5, 0.0),
                (0.25, 0.25, 0.25)):
    PATCHES.append((c, m, y, 0.0))

SIZE = 60
COLS = 7
ROWS = (len(PATCHES) + COLS - 1) // COLS
W, H = COLS * SIZE, ROWS * SIZE


def build(path):
    content = []
    for i, (c, m, y, k) in enumerate(PATCHES):
        col, row = i % COLS, i // COLS
        x, yy = col * SIZE, H - (row + 1) * SIZE
        content.append(f"{c} {m} {y} {k} k")
        content.append(f"{x} {yy} {SIZE} {SIZE} re f")
    stream = "\n".join(content).encode()

    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        f"<< /Type /Pages /Kids [3 0 R] /Count 1 >>".encode(),
        f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] "
        f"/Resources << >> /Contents 4 0 R >>".encode(),
        b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream",
    ]

    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += f"{i} 0 obj\n".encode() + body + b"\nendobj\n"
    xref = len(out)
    out += f"xref\n0 {len(objects)+1}\n".encode()
    out += b"0000000000 65535 f \n"
    for off in offsets:
        out += f"{off:010d} 00000 n \n".encode()
    out += (f"trailer\n<< /Size {len(objects)+1} /Root 1 0 R >>\nstartxref\n{xref}\n"
            "%%EOF\n").encode()
    open(path, "wb").write(bytes(out))


def main():
    build("/tmp/cmyk-patches.pdf")
    subprocess.run(["pdftoppm", "-r", "72", "-png", "-singlefile",
                    "/tmp/cmyk-patches.pdf", "/tmp/cmyk-patches"], check=True)

    from PIL import Image
    im = Image.open("/tmp/cmyk-patches.png").convert("RGB")
    print("%-28s %-14s %-8s %s" % ("CMYK", "viewer RGB", "naive", "difference"))
    for i, (c, m, y, k) in enumerate(PATCHES):
        col, row = i % COLS, i // COLS
        px = im.getpixel((col * SIZE + SIZE // 2, row * SIZE + SIZE // 2))
        naive = tuple(round((1 - v) * (1 - k) * 255) for v in (c, m, y))
        diff = tuple(px[j] - naive[j] for j in range(3))
        print("%-28s %-14s %-8s %s" % (
            "%.3f %.3f %.3f %.3f" % (c, m, y, k), str(px), str(naive), str(diff)))


if __name__ == "__main__":
    sys.exit(main())
