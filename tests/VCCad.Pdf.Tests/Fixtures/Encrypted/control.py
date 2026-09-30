#!/usr/bin/env python3
"""Positive control: decrypt a stream from a file our C# opens.

  python3 control.py <file.pdf> <password>

Why this exists. Three rounds of revision 6 work rested on a Python probe that read `/U`, `/O`,
`/Length` and the rest by searching the WHOLE FILE with a regex. It derived keys from misread lengths
(a content stream's `/Length` instead of the encryption key length), so every "the key is wrong"
result it produced was worthless - a fact only discovered by running this control, which failed on
AES-128: a file the C# reader opens perfectly well.

So: if this prints the page's operators for `aes-128.pdf` and `rc4-128.pdf`, the apparatus is sound
and revision 6 questions can be asked of it. If it does not, nothing else it says means anything.

Two things it does properly, both of which the earlier probe got wrong:

  * the `/Encrypt` dictionary is isolated by OBJECT (`N 0 obj ... endobj`), not by brace matching -
    the handler has `/CF << /StdCF << ... >> >>` before `/Filter /Standard`, so brace matching finds
    the inner crypt filter and reads `/Length 16` from it as though it were the handler's;
  * the key length comes from where the specification puts it for the handler in use: the crypt
    filter's `/Length` is in BYTES (/V 4), while the handler's own `/Length` is in BITS.
"""
import hashlib
import re
import subprocess
import sys


PADDING = bytes([
    0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
    0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
])


def openssl(*args, data):
    return subprocess.run(["openssl", *args], input=data, capture_output=True, check=True).stdout


def aes_cbc(key, iv, data, decrypt=True):
    mode = "-aes-128-cbc" if len(key) == 16 else "-aes-256-cbc"
    return openssl("enc", mode, "-d" if decrypt else "-e",
                   "-K", key.hex(), "-iv", iv.hex(), "-nopad", data=data)


def rc4(key, data):
    s = list(range(256))
    j = 0
    for i in range(256):
        j = (j + s[i] + key[i % len(key)]) & 0xFF
        s[i], s[j] = s[j], s[i]

    out = bytearray()
    x = y = 0
    for byte in data:
        x = (x + 1) & 0xFF
        y = (y + s[x]) & 0xFF
        s[x], s[y] = s[y], s[x]
        out.append(byte ^ s[(s[x] + s[y]) & 0xFF])
    return bytes(out)


def handler_object(raw: bytes) -> bytes:
    """The object holding /Filter /Standard, whole."""
    marker = raw.find(b"/Filter /Standard")
    if marker < 0:
        marker = raw.find(b"/Filter/Standard")
    if marker < 0:
        raise SystemExit("not a standard security handler")

    start = raw.rfind(b"obj", 0, marker)
    end = raw.find(b"endobj", marker)
    if start < 0 or end < 0:
        raise SystemExit("could not isolate the handler object")
    return raw[start:end]


def hex_value(handler: bytes, name: str) -> bytes:
    m = re.search(rb"/" + name.encode() + rb"\s*<([0-9A-Fa-f]+)>", handler)
    if not m:
        raise SystemExit(f"/{name} not found in the handler")
    return bytes.fromhex(m.group(1).decode())


def int_value(handler: bytes, name: str, default=None):
    m = re.search(rb"/" + name.encode() + rb"\s+(-?\d+)", handler)
    return int(m.group(1)) if m else default


def main(path: str, password: str) -> None:
    raw = open(path, "rb").read()
    handler = handler_object(raw)

    revision = int_value(handler, "R", 2)
    permissions = int_value(handler, "P", 0)
    o = hex_value(handler, "O")

    # The crypt filter decides both the cipher and, for /V 4, the key length - in BYTES.
    filter_match = re.search(rb"/StdCF\s*<<(.*?)>>", handler, re.S)
    filter_body = filter_match.group(1) if filter_match else b""
    use_aes = b"/AESV2" in filter_body or b"/AESV3" in filter_body

    filter_length = re.search(rb"/Length\s+(\d+)", filter_body)
    if filter_length:
        key_bytes = int(filter_length.group(1))
    else:
        key_bytes = int_value(handler, "Length", 40) // 8

    id_match = re.search(rb"/ID\s*\[\s*<([0-9A-Fa-f]+)>", raw)
    id0 = bytes.fromhex(id_match.group(1).decode()) if id_match else b""

    padded = (password.encode() + PADDING)[:32]
    key = hashlib.md5(
        padded + o + permissions.to_bytes(4, "little", signed=True) + id0).digest()
    for _ in range(50):
        key = hashlib.md5(key[:key_bytes]).digest()
    key = key[:key_bytes]

    print(f"/R {revision}  key {len(key)} bytes  {'AES' if use_aes else 'RC4'}  /P {permissions}")
    print(f"file key {key.hex()}")

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
        printable = sum(1 for b in plain if 32 <= b < 127 or b in (10, 13))
        flag = "***" if printable > 18 else "   "
        print(f"  {flag} object {number}: {printable}/{len(plain)} printable  {plain[:38]}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
