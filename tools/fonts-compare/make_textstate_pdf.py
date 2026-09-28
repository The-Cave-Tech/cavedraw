"""Build a PDF that uses the text-state operators, so they can be checked.

Tc, Tw, Tz and Ts are not decoration. Character spacing widens every glyph, word spacing
widens the gaps, horizontal scaling stretches or condenses the whole line, and rise lifts
text off the baseline. A run measured without them is not the run the file drew, and all
four are invisible in a file that happens to use their neutral values.

Usage:  python make_textstate_pdf.py  ->  artifacts/text-state.pdf
"""
WIDTH, HEIGHT = 612, 792

# A Helvetica-like font with a known /Widths array, so the advance can be predicted.
WIDTHS = [278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
          556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
          278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
          556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
          1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
          667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
          333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
          556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584]

lines = [
    # Neutral: the plain case, for comparison.
    ("BT /F1 24 Tf 0 0 0 rg 60 700 Td (ABCDE) Tj ET", "neutral"),
    # Character spacing: every glyph gets 10 extra points.
    ("BT /F1 24 Tf 0 0 0 rg 10 Tc 60 650 Td (ABCDE) Tj ET", "Tc 10"),
    # Word spacing: each of the two spaces gets 20 extra points.
    ("BT /F1 24 Tf 0 0 0 rg 20 Tw 60 600 Td (A B C) Tj ET", "Tw 20"),
    # Horizontal scaling: the line at 50%.
    ("BT /F1 24 Tf 0 0 0 rg 50 Tz 60 550 Td (ABCDE) Tj ET", "Tz 50"),
    # Rise: 20 points up off the baseline.
    ("BT /F1 24 Tf 0 0 0 rg 20 Ts 60 500 Td (ABCDE) Tj ET", "Ts 20"),
]

content = "\n".join(line for line, _ in lines)

objects = [
    b"<< /Type /Catalog /Pages 2 0 R >>",
    b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {WIDTH} {HEIGHT}] "
    f"/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>".encode(),
    b"<< /Length %d >>\nstream\n" % len(content) + content.encode() + b"\nendstream",
    ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding "
     "/FirstChar 32 /LastChar 126 /Widths [" + " ".join(str(w) for w in WIDTHS) + "] >>")
    .encode(),
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

open(r"C:\Users\submu\vccad-win\artifacts\text-state.pdf", "wb").write(bytes(out))
print("wrote text-state.pdf")
