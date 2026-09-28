#!/usr/bin/env python3
"""Report the sfnt flavour of the URW font files (TrueType glyf vs CFF outlines)."""
import pathlib
import struct

FONTS = pathlib.Path(r"C:\Users\submu\AppData\Roaming\VCCad\fonts")

for path in sorted(FONTS.glob("*.otf")):
    data = path.read_bytes()
    tag = data[:4]
    num_tables = struct.unpack(">H", data[4:6])[0]
    tables = []
    for i in range(num_tables):
        offset = 12 + i * 16
        name = data[offset:offset + 4].decode("latin-1")
        tables.append(name)
    flavour = "CFF outlines" if "CFF " in tables else ("glyf outlines" if "glyf" in tables else "?")
    print(f"{path.name:32s} sfnt={tag!r} {flavour:15s} tables={','.join(sorted(tables))[:70]}")
