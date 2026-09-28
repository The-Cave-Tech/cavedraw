#!/usr/bin/env python3
"""Reference decoder + golden-manifest generator for Adobe Illustrator private data.

This is an *independent* implementation of the `.ai` private-data extraction rules
(see docs/ai-private-data.md). It exists so the C# implementation in
`src/VCCad.Pdf/Ai/` can be validated against a second implementation over the
whole fixture corpus — a test that only compares the C# decoder with itself would
not catch a systematic misreading of the format.

It is a development/corpus tool, not part of the shipping application. It needs
`qpdf` (to normalise PDF containers) and a zstd decoder (`zstandard` module or the
`zstd` CLI).

Usage
-----
    # Decode one file and print a summary (optionally dump the payload):
    decode-ai-private-data.py decode FILE.ai [--out payload.bin] [--head 400]

    # Build a golden manifest of expected results over a fixture directory:
    decode-ai-private-data.py manifest CORPUS_DIR [-o manifest.json]

    # Verify a manifest against the corpus (used by the Lead's final check):
    decode-ai-private-data.py check CORPUS_DIR manifest.json

Formats reported
----------------
    postscript-only   `.ai` saved as plain PostScript (no PDF container): the whole
                      file is the payload.
    ai24-zstd         PDF container; payload after `%AI24_ZStandard_Data`, zstd.
    ai12-zlib         PDF container; payload after `%AI12_CompressedData`, zlib.
    ai9-zlib          PDF container; payload from the `H\\x89` zlib marker.
    plain             PDF container whose private-data blocks are uncompressed.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zlib

MARKER_AI24 = b"%AI24_ZStandard_Data"
MARKER_AI12 = b"%AI12_CompressedData"
MARKER_AI9 = b"H\x89"
ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"

try:  # optional pure-python zstd
    import zstandard as _zstd  # type: ignore

    def zstd_decompress(data: bytes) -> bytes:
        dctx = _zstd.ZstdDecompressor()
        out = bytearray()
        # Tolerate trailing garbage: some Adobe files append bytes after the frame.
        src = data
        while src:
            try:
                out += dctx.decompressobj().decompress(src)
                break
            except Exception:
                break
        return bytes(out)

    ZSTD_BACKEND = "python-zstandard"
except Exception:  # fall back to the zstd CLI
    def zstd_decompress(data: bytes) -> bytes:
        if shutil.which("zstd") is None:
            raise RuntimeError("no zstd decoder available (install `zstandard` or the `zstd` CLI)")
        proc = subprocess.run(["zstd", "-d", "-c", "-q"], input=data, capture_output=True)
        if not proc.stdout:
            raise RuntimeError(f"zstd failed: {proc.stderr.decode(errors='replace')[:160]}")
        return proc.stdout

    ZSTD_BACKEND = "zstd-cli"


# ----------------------------------------------------------------------------
# PDF container: pull out the /AIPrivateData<N> streams
# ----------------------------------------------------------------------------

def _normalise(path: str) -> bytes | None:
    """qpdf --qdf: uncompressed streams, object streams disabled. None if not a PDF."""
    proc = subprocess.run(
        ["qpdf", "--qdf", "--object-streams=disable", "--decode-level=none",
         path, "/tmp/_vccad_ref_qdf.pdf"],
        capture_output=True,
    )
    if proc.returncode != 0:
        return None
    with open("/tmp/_vccad_ref_qdf.pdf", "rb") as fh:
        return fh.read()


def _all_streams(data: bytes) -> dict[int, bytes]:
    out: dict[int, bytes] = {}
    for match in re.finditer(rb"(\d+)\s+0\s+obj\b", data):
        number = int(match.group(1))
        end = data.find(b"endobj", match.end())
        body = data[match.end():end if end > 0 else len(data)]
        sm = re.search(rb"stream\r?\n", body)
        if not sm:
            continue
        start = sm.end()
        stop = body.find(b"endstream", start)
        out[number] = body[start:stop].rstrip(b"\r\n")
    return out


def _private_blocks(data: bytes) -> list[bytes]:
    streams = _all_streams(data)
    refs = sorted(
        ((int(m.group(1)), int(m.group(2)))
         for m in re.finditer(rb"/AIPrivateData(\d+)\s+(\d+)\s+0\s+R", data)),
    )
    return [streams.get(objnum, b"") for _, objnum in refs]


def is_pdf_container(raw: bytes) -> bool:
    """A modern `.ai` is a PDF container; Illustrator <= 8 wrote plain PostScript."""
    return b"%PDF" in raw[:2048]


# ----------------------------------------------------------------------------
# The decode rules
# ----------------------------------------------------------------------------

def decode_joined(joined: bytes) -> tuple[str, bytes, bytes]:
    """Return (format, payload, prolog) for the concatenated private-data blocks."""
    idx = joined.find(MARKER_AI24)
    if idx >= 0:
        rest = joined[idx + len(MARKER_AI24):]
        # The marker is followed immediately by the zstd frame magic; the '(' the
        # files show *is* the 0x28 first magic byte, so take `rest` verbatim.
        if not rest.startswith(ZSTD_MAGIC):
            shifted = rest.find(ZSTD_MAGIC)
            if shifted < 0:
                raise RuntimeError("AI24 marker found but no zstd frame follows")
            rest = rest[shifted:]
        return "ai24-zstd", zstd_decompress(rest), joined[:idx]

    idx = joined.find(MARKER_AI12)
    if idx >= 0:
        rest = joined[idx + len(MARKER_AI12):]
        return "ai12-zlib", _inflate_all(rest), joined[:idx]

    idx = joined.find(MARKER_AI9)
    if idx >= 0:
        return "ai9-zlib", _inflate_all(joined[idx:]), joined[:idx]

    return "plain", joined, b""


def _inflate_all(data: bytes) -> bytes:
    """Inflate a run of one or more concatenated zlib streams."""
    out = bytearray()
    rest = data
    while rest:
        obj = zlib.decompressobj()
        try:
            out += obj.decompress(rest)
            out += obj.flush()
        except zlib.error:
            if not out:
                raise
            break
        if not obj.unused_data:
            break
        rest = obj.unused_data
    return bytes(out)


def decode_file(path: str) -> dict:
    """Decode one `.ai` file into a result record."""
    with open(path, "rb") as fh:
        raw = fh.read()

    result: dict = {"file": os.path.basename(path), "bytes": len(raw)}
    if not is_pdf_container(raw):
        result.update(format="postscript-only", payload_bytes=len(raw),
                      payload_sha256=hashlib.sha256(raw).hexdigest(),
                      blocks=0, prolog_bytes=0)
        return result

    normalised = _normalise(path)
    if normalised is None:
        raise RuntimeError("qpdf could not normalise the PDF container")
    blocks = _private_blocks(normalised)
    joined = b"".join(blocks)
    fmt, payload, prolog = decode_joined(joined)
    result.update(
        format=fmt,
        payload_bytes=len(payload),
        payload_sha256=hashlib.sha256(payload).hexdigest(),
        blocks=len(blocks),
        prolog_bytes=len(prolog),
        head=payload[:96].decode("latin-1"),
    )
    return result


# ----------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------

def _corpus_files(root: str) -> list[str]:
    files: list[str] = []
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in sorted(filenames):
            if name.endswith(".ai") and os.path.getsize(os.path.join(dirpath, name)) > 2000:
                files.append(os.path.join(dirpath, name))
    return sorted(files)


def _cmd_decode(argv: list[str]) -> int:
    path = argv[0]
    out = None
    head = 0
    if "--out" in argv:
        out = argv[argv.index("--out") + 1]
    if "--head" in argv:
        head = int(argv[argv.index("--head") + 1])

    if not is_pdf_container(open(path, "rb").read()):
        payload = open(path, "rb").read()
        record = decode_file(path)
    else:
        blocks = _private_blocks(_normalise(path) or b"")
        fmt, payload, _ = decode_joined(b"".join(blocks))
        record = decode_file(path)

    print(json.dumps(record, indent=2))
    if out:
        with open(out, "wb") as fh:
            fh.write(payload)
        print(f"wrote {len(payload)} bytes to {out}")
    if head:
        sys.stdout.write(payload[:head].decode("latin-1"))
    return 0


def _cmd_manifest(argv: list[str]) -> int:
    root = argv[0]
    out = argv[argv.index("-o") + 1] if "-o" in argv else "manifest.json"
    records, failures = [], []
    for path in _corpus_files(root):
        try:
            records.append(decode_file(path))
        except Exception as exc:  # keep going; record the failure
            failures.append({"file": os.path.relpath(path, root), "error": str(exc)[:200]})
    manifest = {
        "generator": "tools/ai-private-data/decode-ai-private-data.py",
        "zstd_backend": ZSTD_BACKEND,
                "corpus_root": os.path.basename(os.path.normpath(root)),
        "count": len(records),
        "formats": _histogram(records),
        "fixtures": records,
        "failures": failures,
    }
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=1, sort_keys=True)
        fh.write("\n")
    print(f"wrote {out}: {len(records)} fixtures, formats={manifest['formats']}, "
          f"failures={len(failures)}")
    return 0 if not failures else 1


def _cmd_check(argv: list[str]) -> int:
    root, manifest_path = argv[0], argv[1]
    expected = json.load(open(manifest_path, encoding="utf-8"))
    by_file = {f["file"]: f for f in expected["fixtures"]}
    bad = 0
    for path in _corpus_files(root):
        name = os.path.basename(path)
        if name not in by_file:
            continue
        want = by_file[name]
        try:
            got = decode_file(path)
        except Exception as exc:
            print(f"FAIL {name}: {exc}")
            bad += 1
            continue
        for key in ("format", "payload_bytes", "payload_sha256"):
            if got.get(key) != want.get(key):
                print(f"FAIL {name}: {key} expected {want.get(key)!r} got {got.get(key)!r}")
                bad += 1
                break
    print(f"checked {len(by_file)} fixtures: {bad} mismatches")
    return 1 if bad else 0


def _histogram(records: list[dict]) -> dict[str, int]:
    hist: dict[str, int] = {}
    for r in records:
        hist[r["format"]] = hist.get(r["format"], 0) + 1
    return dict(sorted(hist.items()))


def main(argv: list[str]) -> int:
    if len(argv) < 3:
        print(__doc__)
        return 2
    command, rest = argv[1], argv[2:]
    if command == "decode":
        return _cmd_decode(rest)
    if command == "manifest":
        return _cmd_manifest(rest)
    if command == "check":
        return _cmd_check(rest)
    print(__doc__)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
