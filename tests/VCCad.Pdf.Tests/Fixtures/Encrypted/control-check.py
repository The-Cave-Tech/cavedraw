#!/usr/bin/env python3
"""Does object 4 inflate to the page's operators? The control's final assertion."""
import hashlib
import re
import subprocess
import sys
import zlib

sys.path.insert(0, ".")
from control import PADDING, aes_cbc, handler_object, hex_value, int_value, rc4  # noqa: E402


def main(path: str, password: str) -> None:
    raw = open(path, "rb").read()
    handler = handler_object(raw)
    revision = int_value(handler, "R", 2)
    permissions = int_value(handler, "P", 0)
    o = hex_value(handler, "O")

    filter_match = re.search(rb"/StdCF\s*<<(.*?)>>", handler, re.S)
    filter_body = filter_match.group(1) if filter_match else b""
    use_aes = b"/AESV2" in filter_body or b"/AESV3" in filter_body
    length = re.search(rb"/Length\s+(\d+)", filter_body)
    key_bytes = int(length.group(1)) if length else int_value(handler, "Length", 40) // 8

    id_match = re.search(rb"/ID\s*\[\s*<([0-9A-Fa-f]+)>", raw)
    id0 = bytes.fromhex(id_match.group(1).decode()) if id_match else b""

    padded = (password.encode() + PADDING)[:32]
    key = hashlib.md5(padded + o + permissions.to_bytes(4, "little", signed=True) + id0).digest()
    for _ in range(50):
        key = hashlib.md5(key[:key_bytes]).digest()
    key = key[:key_bytes]

    found = 0
    for m in re.finditer(rb"(\d+)\s+(\d+)\s+obj", raw):
        start = raw.find(b"stream", m.end())
        if start < 0:
            continue
        body = raw[start + len(b"stream"):].lstrip(b"\r\n")
        end = body.find(b"endstream")
        if end < 0:
            continue

        data = body[:end].rstrip(b"\r\n")
        number, generation = int(m.group(1)), int(m.group(2))
        material = key + number.to_bytes(3, "little") + generation.to_bytes(2, "little")
        if use_aes:
            material += b"sAlT"
        object_key = hashlib.md5(material).digest()[:min(key_bytes + 5, 16)]
        plain = aes_cbc(object_key, data[:16], data[16:]) if use_aes else rc4(object_key, data)

        try:
            content = zlib.decompress(plain)
        except zlib.error:
            continue

        print(f"  object {number} inflates to: {content[:48]!r}")
        found += 1

    print(f"{path}: {found} stream(s) decrypted and inflated")


main(sys.argv[1], sys.argv[2])
