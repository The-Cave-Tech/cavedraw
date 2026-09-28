#!/usr/bin/env python3
"""report-corpus-features.py — feature histogram over a PDF corpus.

A small stand-alone companion to the xUnit harness in
``tests/VCCad.Pdf.Tests/Corpus`` (``PdfFeatureProbe``). The C# probe is the
canonical, structural classifier; this script is a cheap approximation for use
outside the test runner (shelling out, dashboarding, picking a sample file for a
bug report) when a ``dotnet test`` round-trip is too heavy.

What it does per file:

* decodes the bytes as Latin-1 (PDF syntax is byte-oriented);
* inflates FlateDecode object streams (``/Type /ObjStm``) so keys hidden inside
  compressed cross-reference streams are still seen;
* matches a table of well-known markers (names and value forms);
* inflates every stream that looks like a content stream and tokenises the
  painting operators (``m``/``l``/``c``/``re``/``f``/``S``/``W``/``W*``/``gs``/
  ``Do``/``sh``/``BI`` ...), reporting total tallies.

Output: a per-feature "files containing it" histogram, an operator tally, and an
optional JSON dump (``--json PATH``).

Usage:
    python3 scripts/report-corpus-features.py [CORPUS_DIR] [--json OUT.json]
                                              [--area NAME] [--limit N]

CORPUS_DIR defaults to $VCCAD_GS_CORPUS, then to the same default probe paths as
the test harness.
"""
from __future__ import annotations

import json
import os
import re
import sys
import zlib
from collections import Counter
from pathlib import Path

DEFAULT_ROOTS = [
    "/home/darren/.cache/vccad-corpora/ghostscript",
    "/tmp/opencode/gs-tests",
    "/home/darren/development/Artifex-tests",
]

# name -> regex over the (inflated) Latin-1 text. Name matches are
# delimiter-terminated so /Type1 does not match inside /Type1C.
NAME = r"/(?:%s)(?=[\s/()<>\[\]{}%%]|$)"


def name_pattern(name: str) -> re.Pattern[str]:
    escaped = re.escape(name)
    return re.compile(r"/" + escaped + r"(?=[\s/()<>\[\]{}%]|$)")


MARKERS = {
    "doc.encrypt": name_pattern("Encrypt"),
    "doc.pdfa": re.compile(r"pdfaid"),
    "doc.pieceInfo": name_pattern("PieceInfo"),
    "doc.aiPrivateData": name_pattern("AIPrivateData"),
    "doc.outlines": name_pattern("Outlines"),
    "doc.annots": name_pattern("Annots") ,
    "doc.optionalContent": name_pattern("OCProperties"),
    "doc.embeddedFiles": name_pattern("EmbeddedFiles"),
    "doc.metadataXmp": name_pattern("Metadata"),
    "doc.objectStreams": name_pattern("ObjStm"),
    "doc.signature": name_pattern("ByteRange"),
    "doc.linearized": name_pattern("Linearized"),
    "font.type1": name_pattern("Type1"),
    "font.trueType": name_pattern("TrueType"),
    "font.type0": name_pattern("Type0"),
    "font.type3": name_pattern("Type3"),
    "font.cidFontType0": name_pattern("CIDFontType0"),
    "font.cidFontType2": name_pattern("CIDFontType2"),
    "font.cff": name_pattern("Type1C"),
    "font.opentype": name_pattern("OpenType"),
    "font.embedded": re.compile(r"/FontFile[23]?(?=[\s/()<>\[\]{}%]|$)"),
    "font.toUnicode": name_pattern("ToUnicode"),
    "font.identityH": name_pattern("Identity-H"),
    "font.encodingDifferences": name_pattern("Differences"),
    "font.cmapName": name_pattern("CMapName"),
    "gs.extGState": name_pattern("ExtGState"),
    "gs.smask": name_pattern("SMask"),
    "gs.blendMode": name_pattern("BM"),
    "gs.fillAlpha": name_pattern("ca"),
    "gs.strokeAlpha": name_pattern("CA"),
    "transparency.group": name_pattern("Group"),
    "pattern.tiling": re.compile(r"/PatternType\s+1(?![0-9])"),
    "pattern.shading": re.compile(r"/PatternType\s+2(?![0-9])"),
    "shading.any": name_pattern("ShadingType"),
    "image.dctDecode": name_pattern("DCTDecode"),
    "image.jpxDecode": name_pattern("JPXDecode"),
    "image.ccittFaxDecode": name_pattern("CCITTFaxDecode"),
    "image.jbig2Decode": name_pattern("JBIG2Decode"),
    "image.any": re.compile(r"/Subtype\s*/Image(?=[\s/()<>\[\]{}%]|$)"),
    "xobject.form": re.compile(r"/Subtype\s*/Form(?=[\s/()<>\[\]{}%]|$)"),
    "res.iccBased": name_pattern("ICCBased"),
    "res.separation": name_pattern("Separation"),
    "res.deviceN": name_pattern("DeviceN"),
    "res.outputIntents": name_pattern("OutputIntents"),
    "res.pattern": name_pattern("Pattern"),
    "res.shading": name_pattern("Shading"),
}

OPERATORS = {
    "m", "l", "c", "v", "y", "re", "h",
    "f", "F", "f*", "S", "s", "B", "B*", "b", "b*", "n",
    "W", "W*", "gs", "Do", "sh", "BI", "Tj", "TJ", "'", '"',
}

STREAM_RE = re.compile(rb"stream\r?\n(.*?)endstream", re.DOTALL)
OBJSTM_RE = re.compile(rb"/Type\s*/ObjStm\b")


def inflate(data: bytes) -> bytes | None:
    try:
        return zlib.decompress(data)
    except zlib.error:
        pass
    try:
        return zlib.decompressobj().decompress(data)
    except zlib.error:
        return None


def expand(text: str, raw: bytes) -> str:
    """Append inflated object-stream bodies so compressed keys are visible."""
    extra = []
    for match in STREAM_RE.finditer(raw):
        body = match.group(1)
        if b"ObjStm" not in raw[max(0, match.start() - 400):match.start()]:
            continue
        if b"FlateDecode" not in raw[max(0, match.start() - 400):match.start()]:
            continue
        inflated = inflate(body.strip(b"\r\n"))
        if inflated:
            extra.append(inflated.decode("latin-1"))
    return text + "\n" + "\n".join(extra) if extra else text


def tokenize(content: bytes) -> Counter:
    counts: Counter = Counter()
    i, n = 0, len(content)
    token = bytearray()

    def flush() -> None:
        if not token:
            return
        op = token.decode("latin-1")
        token.clear()
        if op in OPERATORS:
            counts[op] += 1

    while i < n:
        c = content[i]
        if c == 0x25:  # %
            flush()
            while i < n and content[i] != 0x0A:
                i += 1
        elif c == 0x28:  # (
            flush()
            depth = 1
            i += 1
            while i < n and depth:
                ch = content[i]
                i += 1
                if ch == 0x5C:
                    i += 1
                elif ch == 0x28:
                    depth += 1
                elif ch == 0x29:
                    depth -= 1
        elif c == 0x3C:  # <
            flush()
            if i + 1 < n and content[i + 1] == 0x3C:
                i += 2
            else:
                while i < n and content[i] != 0x3E:
                    i += 1
                i += 1
        elif c == 0x3E:  # >
            flush()
            i += 2 if i + 1 < n and content[i + 1] == 0x3E else 1
        elif c == 0x2F:  # /
            flush()
            i += 1
            while i < n and content[i] not in b" \t\r\n\f()<>[]{}/%":
                i += 1
        elif c in b" \t\r\n\f\x00[]{}":
            flush()
            i += 1
        else:
            token.append(c)
            i += 1
    flush()
    return counts


def probe(path: Path) -> tuple[set[str], Counter]:
    raw = path.read_bytes()
    text = expand(raw.decode("latin-1"), raw)
    found = {feature for feature, pattern in MARKERS.items() if pattern.search(text)}

    # Derived: a CIDFontType0 descendant is CFF-flavoured, matching the C# probe.
    if "font.cidFontType0" in found:
        found.add("font.cff")

    ops: Counter = Counter()
    for match in STREAM_RE.finditer(raw):
        body = match.group(1)
        data = inflate(body) if b"FlateDecode" in raw[max(0, match.start() - 400):match.start()] else body
        if not data:
            continue
        ops.update(tokenize(data))
    return found, ops


def main(argv: list[str]) -> int:
    args = [a for a in argv[1:] if not a.startswith("--")]
    json_path = None
    area = None
    limit = None
    for index, arg in enumerate(argv[1:]):
        if arg == "--json":
            json_path = argv[index + 2]
        elif arg == "--area":
            area = argv[index + 2]
        elif arg == "--limit":
            limit = int(argv[index + 2])

    root = Path(args[0]) if args else None
    if root is None:
        env = os.environ.get("VCCAD_GS_CORPUS")
        candidates = ([env] if env else []) + DEFAULT_ROOTS
        root = next((Path(c) for c in candidates if c and Path(c).is_dir()), None)
    if root is None or not root.is_dir():
        print("no corpus found; pass a directory or set VCCAD_GS_CORPUS", file=sys.stderr)
        return 1

    files = sorted(root.rglob("*.pdf"))
    if area:
        files = [f for f in files if f.relative_to(root).parts[0] == area]
    if limit:
        files = files[:limit]

    file_counts: Counter = Counter()
    op_totals: Counter = Counter()
    examples: dict[str, list[str]] = {}
    unreadable = []

    for path in files:
        try:
            found, ops = probe(path)
        except Exception as exc:  # noqa: BLE001 - this is a diagnostic tool
            unreadable.append(f"{path.relative_to(root)}: {type(exc).__name__}")
            continue
        for feature in found:
            file_counts[feature] += 1
            examples.setdefault(feature, []).append(str(path.relative_to(root)))
        op_totals.update(ops)

    print(f"corpus: {root}  ({len(files)} PDFs, {len(unreadable)} unreadable)")
    print()
    print(f"{'feature':36} {'files':>6}  example")
    for feature, count in sorted(file_counts.items()):
        example = examples[feature][0] if examples.get(feature) else ""
        print(f"{feature:36} {count:6}  {example}")
    print()
    print("operator tally:")
    for op, count in sorted(op_totals.items(), key=lambda kv: (-kv[1], kv[0])):
        print(f"  {op:6} {count:8}")
    if unreadable:
        print()
        print("unreadable:")
        for entry in unreadable:
            print("  " + entry)

    if json_path:
        Path(json_path).write_text(json.dumps(
            {
                "corpusRoot": str(root),
                "files": len(files),
                "featureFileCounts": dict(sorted(file_counts.items())),
                "featureExamples": examples,
                "operatorTotals": dict(sorted(op_totals.items())),
                "unreadable": unreadable,
            },
            indent=2,
        ), encoding="utf-8")
        print(f"\nwrote {json_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
