"""Build a PDF whose image uses a colour-key mask, the way logos are drawn.

/Decode [1 0 ...] inverts an image's samples, and /Mask [min max ...] makes pixels whose
components fall in those ranges transparent - which is how a logo drawn on white is placed
over coloured artwork. A reader that ignores either paints the wrong picture: an inverted
photograph, or a white rectangle where the logo's background should be.

Usage:  python make_imagemask_pdf.py  ->  artifacts/colour-key-mask.pdf
"""
import zlib

W, H = 64, 64

# A red disc on a white field. The white is the colour key, so only the disc should show.
rgb = []
for y in range(H):
    for x in range(W):
        dx, dy = x - W / 2, y - H / 2
        inside = (dx * dx + dy * dy) < (W / 2 - 6) ** 2
        rgb.extend([220, 30, 30] if inside else [255, 255, 255])

# A ramp with Decode [1 0 1 0 1 0]: the file's bytes are inverted on the way to screen.
ramp = []
for y in range(H):
    for x in range(W):
        v = (x * 255) // (W - 1)
        ramp.extend([v, v, v])

images = [
    ("Im1", zlib.compress(bytes(rgb)), "/ColorSpace /DeviceRGB"),
    ("Im2", zlib.compress(bytes(ramp)), "/ColorSpace /DeviceRGB /Decode [1 0 1 0 1 0]"),
]

# A blue card behind image 1, so whether the white is keyed out is visible rather than
# hidden against a white page.
content = (
    b"q 0 0 1 rg 70 510 180 180 re f Q "
    b"q 160 0 0 160 80 520 cm /Im1 Do Q "
    b"q 160 0 0 160 300 520 cm /Im2 Do Q"
)

objects = [
    b"<< /Type /Catalog /Pages 2 0 R >>",
    b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
    b"/Resources << /XObject << /Im1 5 0 R /Im2 6 0 R >> >> /Contents 4 0 R >>",
    b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream",
]

# Image 1 carries the colour key: white (all three components at the top of their range)
# is transparent.
objects.append(
    f"<< /Type /XObject /Subtype /Image /Width {W} /Height {H} "
    f"/BitsPerComponent 8 /ColorSpace /DeviceRGB "
    f"/Mask [255 255 255 255 255 255] /Filter /FlateDecode /Length {len(images[0][1])} >>"
    .encode() + b"\nstream\n" + images[0][1] + b"\nendstream")

objects.append(
    f"<< /Type /XObject /Subtype /Image /Width {W} /Height {H} "
    f"/BitsPerComponent 8 /ColorSpace /DeviceRGB /Decode [1 0 1 0 1 0] "
    f"/Filter /FlateDecode /Length {len(images[1][1])} >>"
    .encode() + b"\nstream\n" + images[1][1] + b"\nendstream")

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

open(r"C:\Users\submu\vccad-win\artifacts\colour-key-mask.pdf", "wb").write(bytes(out))
print("wrote colour-key-mask.pdf")
