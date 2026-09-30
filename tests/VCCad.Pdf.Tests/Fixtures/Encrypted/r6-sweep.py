#!/usr/bin/env python3
"""Every revision 6 combination that could produce the file key, tested by inflating a stream.

The apparatus is validated (`control-check.py` decrypts and inflates AES-128 and RC4-128 streams), so
this is the first revision 6 sweep whose negative results mean anything.

A stream only inflates if the key is right, which makes zlib the pass signal rather than "looks like
text" - and the fixture's content stream IS compressed, so the signal is available.
"""
import hashlib
import re
import subprocess
import sys
import zlib

sys.path.insert(0, ".")
from control import handler_object, int_value  # noqa: E402


def cbc(key, iv, data, decrypt=True):
    mode = "-aes-128-cbc" if len(key) == 16 else "-aes-256-cbc"
    return subprocess.run(
        ["openssl", "enc", mode, "-d" if decrypt else "-e", "-K", key.hex(), "-iv", iv.hex(), "-nopad"],
        input=data, capture_output=True, check=True).stdout


def ecb(key, data, decrypt=True):
    mode = "-aes-128-ecb" if len(key) == 16 else "-aes-256-ecb"
    return subprocess.run(
        ["openssl", "enc", mode, "-d" if decrypt else "-e", "-K", key.hex(), "-nopad"],
        input=data, capture_output=True, check=True).stdout


def hardened(password, salt, extra):
    k = hashlib.sha256(password + salt + extra).digest()
    for _ in range(64):
        e = cbc(k[0:16], k[16:32], (password + k + extra) * 64, decrypt=False)
        k = [hashlib.sha256, hashlib.sha384, hashlib.sha512][sum(e[0:16]) % 3](e).digest()
    return k[0:32]


raw = open(sys.argv[1], "rb").read()
handler = handler_object(raw)
value = lambda n: (lambda m: bytes.fromhex(m.group(1).decode()) if m else None)(
    re.search(rb"/" + n.encode() + rb"\s*<([0-9A-Fa-f]+)>", handler))

u, o, ue, oe = value("U"), value("O"), value("UE"), value("OE")

streams = []
for m in re.finditer(rb"(\d+)\s+(\d+)\s+obj", raw):
    start = raw.find(b"stream", m.end())
    if start < 0:
        continue
    body = raw[start + len(b"stream"):].lstrip(b"\r\n")
    end = body.find(b"endstream")
    if end >= 0:
        streams.append((m.group(1).decode(), body[:end].rstrip(b"\r\n")))

print(f"{len(streams)} streams; testing combinations")

for password, who in ((b"", "user ''"), (b"owner", "owner 'owner'")):
    for salt_at, salt_label in ((32, "valsalt[32:40]"), (40, "keysalt[40:48]")):
        for extra_label, extra in (("''", b""), ("U", u)):
            for wrapped_label, wrapped in (("UE", ue), ("OE", oe)):
                intermediate = hardened(password, (u if not who.startswith("owner") else o)[salt_at:salt_at + 8], extra)
                for mode in ("cbc", "ecb"):
                    key = cbc(intermediate, bytes(16), wrapped) if mode == "cbc" else ecb(intermediate, wrapped)
                    for stream_label, data in streams:
                        if len(data) < 32:
                            continue
                        for shape, plain in (
                            ("iv+cbc", cbc(key, data[:16], data[16:])),
                            ("whole-cbc", cbc(key, bytes(16), data)),
                            ("ecb", ecb(key, data)),
                        ):
                            # The pass signal is the page's own operator text, not a successful
                            # zlib inflate: only one of the four streams in the known-good AES-128
                            # fixture compresses, so "did it inflate" would miss a correct key on an
                            # uncompressed stream - which is a false negative that looks like proof.
                            if b"0 0 0 rg" in plain or b"re f" in plain:
                                print(f"  *** {who} {salt_label} extra={extra_label} {wrapped_label} "
                                      f"{mode} decrypted-with {shape} -> {plain[:48]!r}")
                                sys.exit(0)

print("  no combination opened a stream")
