"""Build a PDF that paints with a Separation (spot) colour space.

A Separation is defined by its alternate space and a tint transform:
[/Separation /name /AlternateSpace <function>]. The transform turns a tint from 0 to 1
into a colour in the alternate space, and the usual form is a type 2 exponential function
whose C0 and C1 are the zero-tint and full-tint colours.

Read wrong, a spot colour does not come out slightly off — it comes out black, which is
what a full-tint cyan-and-magenta spot should never look like.

Usage:  python make_separation_pdf.py  ->  artifacts/separation.pdf
"""
SPOT = ("[/Separation /Spot /DeviceCMYK "
        "<< /FunctionType 2 /Domain [0 1] "
        "/C0 [0.0 0.0 0.0 0.0] /C1 [0.9 0.9 0.0 0.0] /N 1 >>]")

# Three tints of the same spot, plus a plain CMYK swatch for comparison.
content = "\n".join([
    "q /CS1 cs 0.2 scn 60 600 120 120 re f Q",
    "q /CS1 cs 0.6 scn 200 600 120 120 re f Q",
    "q /CS1 cs 1.0 scn 340 600 120 120 re f Q",
    "q 0.0 0.6 0.6 0.0 k 480 600 120 120 re f Q",
])

# A colour space is referenced by name, so the Separation goes in the page resources.
resources = "/ColorSpace << /CS1 " + SPOT + " >>"

objects = [
    b"<< /Type /Catalog /Pages 2 0 R >>",
    b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
    f"/Resources << {resources} >> /Contents 4 0 R >>".encode(),
    b"<< /Length %d >>\nstream\n" % len(content) + content.encode() + b"\nendstream",
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

open(r"C:\Users\submu\vccad-win\artifacts\separation.pdf", "wb").write(bytes(out))
print("wrote separation.pdf")
