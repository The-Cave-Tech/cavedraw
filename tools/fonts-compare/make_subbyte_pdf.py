"""Build a PDF with 1-bit and 4-bit images, for checking that they render.

Sub-byte images are common in real files - a scan, a stamp, a logo - and the renderer used
to skip anything that was not exactly 8 bits per component, so those images showed as
nothing. This writes a file with a 1-bit smiley and a 4-bit gradient so the canvas can be
checked against something known.
"""
import zlib

W, H = 64, 64

# --- a 1-bit image: a filled circle with a hole, plus a border -------------------
one_bit = []
for y in range(H):
    row = []
    for x in range(W):
        dx, dy = x - W / 2, y - H / 2
        inside = (dx * dx + dy * dy) < (W / 2 - 4) ** 2
        hole = (dx * dx + dy * dy) < (W / 8) ** 2
        border = x < 2 or y < 2 or x >= W - 2 or y >= H - 2
        row.append(0 if (inside and not hole) or border else 1)
    one_bit.extend(row)

# --- a 4-bit image: a horizontal ramp --------------------------------------------
four_bit = []
for y in range(H):
    row = [(x * 15) // (W - 1) for x in range(W)]
    four_bit.extend(row)


def pack(values, bits):
    out = bytearray((len(values) * bits + 7) // 8)
    for i, value in enumerate(values):
        offset = i * bits
        at = offset >> 3
        shift = 8 - bits - (offset & 7)
        out[at] |= (value & ((1 << bits) - 1)) << shift
    return bytes(out)


images = [
    ("Im1", W, H, 1, pack(one_bit, 1)),
    ("Im2", W, H, 4, pack(four_bit, 4)),
]

content = []
for index, (name, width, height, bits, _) in enumerate(images):
    x = 100 + index * 260
    content.append(f"q 180 0 0 180 {x} 480 cm /{name} Do Q")
content.append("q 0 0 0 1 k 100 450 400 4 re f Q")
body = "\n".join(content).encode()

objects = [
    b"<< /Type /Catalog /Pages 2 0 R >>",
    b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
    f"/Resources << /XObject << /Im1 5 0 R /Im2 6 0 R >> >> /Contents 4 0 R >>".encode(),
    b"<< /Length %d >>\nstream\n" % len(body) + body + b"\nendstream",
]

for name, width, height, bits, data in images:
    packed = zlib.compress(data)
    objects.append(
        f"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} "
        f"/BitsPerComponent {bits} /ColorSpace /DeviceGray /Filter /FlateDecode "
        f"/Length {len(packed)} >>\nstream\n".encode() + packed + b"\nendstream")

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

open(r"C:\Users\submu\vccad-win\artifacts\subbyte-images.pdf", "wb").write(bytes(out))
print("wrote subbyte-images.pdf")
