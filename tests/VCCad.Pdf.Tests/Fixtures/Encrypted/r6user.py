#!/usr/bin/env python3
"""What password does qpdf's USER slot actually hash?

The owner slot reproduces from "owner" with /U as the extra input, so the algorithm is right; this
finds what the user slot was built from. Written as a separate file because the first attempt at this
script never reached disk.
"""
import hashlib
import subprocess


def aes128_cbc_nopad(key: bytes, iv: bytes, data: bytes) -> bytes:
    return subprocess.run(
        ["openssl", "enc", "-aes-128-cbc", "-K", key.hex(), "-iv", iv.hex(), "-nopad"],
        input=data, capture_output=True, check=True,
    ).stdout


def hash_r6(password: bytes, salt: bytes, extra: bytes) -> bytes:
    k = hashlib.sha256(password + salt + extra).digest()
    for _ in range(64):
        block = (password + k + extra) * 64
        encrypted = aes128_cbc_nopad(k[0:16], k[16:32], block)
        total = sum(encrypted[0:16])
        k = [hashlib.sha256, hashlib.sha384, hashlib.sha512][total % 3](encrypted).digest()
    return k[0:32]


U = bytes.fromhex("10A961718C7564B02221335E58BDDF59CB69E834FB8855C188C2530DCD6ABF8A7E105A864039C470F10541F1E2B5DCD3")

found = False
for password in (b"", b"owner", b"user", b"password", b"none"):
    for extra in (b"", U):
        for salt_at in (32, 40):
            got = hash_r6(password, U[salt_at:salt_at + 8], extra)
            if got == U[0:32]:
                print(f"MATCH: password={password!r} extra={'U' if extra else 'empty'} salt@{salt_at}")
                found = True

if not found:
    print("no variant matched qpdf's user slot")
