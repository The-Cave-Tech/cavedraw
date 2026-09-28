"""Build a PDF using a stencil image mask, the way logos and stamps are drawn.

An /ImageMask true image is one bit per pixel and has NO /ColorSpace: it is a stencil
that paints the current fill colour wherever the bit is set. A reader that treats it as a
greyscale image paints the wrong thing entirely, and one that guesses a colour space from
the byte count is guessing about a file that is telling it exactly what it is.
"""
import zlib

W, H = 48, 48

# A five-pointed star, as a stencil.
bits = []
for y in range(H):
    row = []
    for x in range(W):
        fx = (x - W / 2) / (W / 2)
        fy = (H / 2 - y) / (H / 2)
        # Simple star: radius modulated by angle.
        import math
        r = math.hypot(fx, fy)
        a = math.atan2(fy, fx)
        edge = 0.55 + 0.30 * math.cos(5 * a)
        row.append(0 if r < edge else 1)
    bits.extend(row)


def pack(values):
    out = bytearray((len(values) + 7) // 8)
    for i, value in enumerate(values):
        if value:
            out[i >> 3] |= 1 << (7 - (i & 7))
    return bytes(out)


stencil = zlib.compress(pack(bits))

# The mask paints in the current colour, so the content stream sets one first.
content = b"q 0 0 0 1 k 200 0 0 200 150 500 cm /Im1 Do Q"

objects = [
    b"<< /Type /Catalog /Pages 2 0 R >>",
    b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
    b"/Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>",
    b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream",
    f"<< /Type /XObject /Subtype /Image /Width {W} /Height {H} "
    f"/ImageMask true /BitsPerComponent 1 /Filter /FlateDecode /Length {len(stencil)} >>"
    .encode() + b"\nstream\n" + stencil + b"\nendstream",
]

out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
offsets = []
for i, obj in enumerate(objects, start=1):
    offsets.append(len(out))
    out += f"{i} 0 obj\n".encode() + obj + b"\nendobj\n"
xref = len(out)
out += f"xref\n0 {len(objects)+1}\n".encode()
out += b"0000000000 65535 f \n"
for off in offsets:
    out += f"{off:010d} 00000 n \n".encode()
out += (f"trailer\n<< /Size {len(objects)+1} /Root 1 0 R >>\n"
        f"startxref\n{xref}\n%%EOF\n").encode()

open(r"C:\Users\submu\vccad-win\artifacts\image-mask.pdf", "wb").write(bytes(out))
print("wrote image-mask.pdf")
