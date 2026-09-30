#!/usr/bin/env python3
"""Which of the revision 6 hashes can be reproduced from what the file holds?

If the owner validation hash reproduces and the user one does not, the algorithm is right and the
empty-password case is what differs. If neither does, the loop itself is wrong.
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
O = bytes.fromhex("F6C3D6C8BADAAF3A0C3D9584532151D5F089C315BB8ED8AB3217011474B2BA9FA8287A9099DA2087ADC6692B27CC024F")

for label, password, hash_bytes, extra in (
    ("user '' with udata=''", b"", U, b""),
    ("user '' with udata=U", b"", U, U),
    ("user '' with udata=O", b"", U, O),
):
    got = hash_r6(password, hash_bytes[32:40], extra)
    print(f"{label:24s} -> {'MATCH' if got == hash_bytes[0:32] else 'no'}")

for label, password, hash_bytes, extra in (
    ("owner 'owner' udata=U", b"owner", O, U),
    ("owner 'owner' udata=''", b"owner", O, b""),
    ("owner 'owner' udata=O", b"owner", O, O),
):
    got = hash_r6(password, hash_bytes[32:40], extra)
    print(f"{label:24s} -> {'MATCH' if got == hash_bytes[0:32] else 'no'}")
