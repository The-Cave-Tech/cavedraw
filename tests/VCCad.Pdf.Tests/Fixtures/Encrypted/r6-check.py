#!/usr/bin/env python3
"""Revision 6, on an apparatus that has passed its positive control.

Superseded `r6-probe.py`, which is deleted rather than kept: it read `/U`, `/O`, `/Length` and the
rest by searching the WHOLE FILE, so it derived keys from a content stream's `/Length` instead of the
encryption key length and produced a 6-byte key. Three rounds of revision 6 conclusions rested on it.
`control.py` + `control-check.py` are the control it should have been measured against - they decrypt
and inflate a stream from AES-128 and RC4-128, files the C# reader demonstrably opens.

What this says about `aes-256.pdf` (qpdf, `--encrypt "" owner 256`), with the apparatus now trusted:

  * the USER validation hash reproduces /U exactly - hash("", U[32:40], "") == U[0:32]
  * every key derivation tried nevertheless fails to decrypt a stream:
      32 combinations of {user, owner} x {validation salt, key salt} x {empty, /U as extra}
      x {CBC, ECB unwrap of /UE or /OE} x {IV-prefixed CBC, whole-stream CBC, ECB}
  * the file has exactly ONE handler object and the trailer points at it, so this is not reading a
    superseded dictionary from an incremental update
  * the content stream is compressed (its ciphertext is the same 48 bytes as the AES-128 fixture's,
    which inflates), so "the key is wrong" is not a false negative from an uncompressed stream

The contradiction is therefore real and narrow: not the hash, not the apparatus, not the handler
object. Something about how the FILE KEY is produced or wrapped is still not understood. See #36.

Usage: python3 r6-check.py <file.pdf> <password>        - one derivation, verbose
       python3 r6-sweep.py <file.pdf>                   - every combination, stops on the first success
"""
import hashlib
import re
import subprocess
import sys

sys.path.insert(0, ".")
from control import handler_object, int_value  # noqa: E402


def cbc(key, iv, data, decrypt=True):
    mode = "-aes-128-cbc" if len(key) == 16 else "-aes-256-cbc"
    return subprocess.run(
        ["openssl", "enc", mode, "-d" if decrypt else "-e", "-K", key.hex(), "-iv", iv.hex(), "-nopad"],
        input=data, capture_output=True, check=True).stdout


from control import hash_r6 as hardened  # one implementation, and it has qpdf's loop termination


def value(handler: bytes, name: str) -> bytes | None:
    m = re.search(rb"/" + name.encode() + rb"\s*<([0-9A-Fa-f]+)>", handler)
    return bytes.fromhex(m.group(1).decode()) if m else None


def compressible_streams(raw: bytes):
    for m in re.finditer(rb"(\d+)\s+(\d+)\s+obj", raw):
        start = raw.find(b"stream", m.end())
        if start < 0:
            continue
        body = raw[start + len(b"stream"):].lstrip(b"\r\n")
        end = body.find(b"endstream")
        if end >= 0:
            yield int(m.group(1)), body[:end].rstrip(b"\r\n")


def main(path: str, password: str) -> None:
    raw = open(path, "rb").read()
    handler = handler_object(raw)
    u, o, ue, oe = value(handler, "U"), value(handler, "O"), value(handler, "UE"), value(handler, "OE")

    print(f"file: {path}  (/R {int_value(handler, 'R', 5)})")
    print(f"  handler objects in file: {len(re.findall(rb'/Filter /Standard', raw))}")

    for who, hashed, wrapped, extra in (
        ("user", u, ue, b""),
        ("owner", o, oe, u),
    ):
        validation = hardened(password.encode(), hashed[32:40], extra)
        print(f"  {who}: validation {'MATCHES' if validation == hashed[0:32] else 'does not match'}")

        file_key = cbc(hardened(password.encode(), hashed[40:48], extra), bytes(16), wrapped)
        opened = 0

        for number, data in compressible_streams(raw):
            if len(data) < 32:
                continue
            plain = cbc(file_key, data[:16], data[16:])
            try:
                content = zlib_decompress(plain)
            except Exception:
                continue
            print(f"    *** object {number} inflates to {content[:40]!r}")
            opened += 1

        if opened == 0:
            print(f"    no stream decrypted with the {who} key {file_key.hex()[:16]}...")


def zlib_decompress(data: bytes) -> bytes:
    import zlib
    return zlib.decompress(data)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
