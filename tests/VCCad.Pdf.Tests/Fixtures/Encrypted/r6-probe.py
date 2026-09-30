#!/usr/bin/env python3
"""Which revision 5/6 hash slots can be reproduced from a protected PDF?

Independent of the C# implementation on purpose. Uses openssl as the AES oracle so the revision 6
hardened hash (algorithm 2.B) is computed by something that is not our own code, and reads /U, /O,
/UE and /OE straight out of the file rather than from hand-copied hex.

    python3 r6-probe.py <file.pdf> <user-password> <owner-password>

## What it found (2026-09-30), which is why it is kept

  * `Fixtures/Encrypted/aes-256.pdf` - written by qpdf with `--encrypt "" owner 256` - reproduces its
    **owner** slot exactly: `hash(owner, O[32:40], U)`, using the full 48-byte /U as the extra input.
    That is what proves the implementation of algorithm 2.B is correct.
  * The **user** slot of that same file does NOT reproduce from the empty password, by any variant:
    empty extra, /U as extra, the validation salt or the key salt, or the password spelled differently.
    qpdf reports that file's user password as empty, so it should.
  * A second file written with a NON-EMPTY user password reproduces **neither** slot - not even the
    owner one, which is computed from the owner password and /U alone.

The third result is the strange one. The owner hash in the empty-user file matches, and the only
input that differs between the two files is /U, so the formula cannot be `hash(owner, salt, U)` in
general - yet that is what the specification says and what reproduces one of them exactly. Candidates
tried and rejected: /U plus or minus the user password in either order, the file's /ID, the user
password alone, the first 32 bytes of /U, and both salt offsets (32 and 40).

Something is still not understood here, and the next step is a file where the user password is empty
but the owner password is not the same word, to separate "empty user password" from "this particular
pair". See issue #36.
"""
import hashlib
import re
import subprocess
import sys


def aes128_cbc_nopad(key: bytes, iv: bytes, data: bytes) -> bytes:
    return subprocess.run(
        ["openssl", "enc", "-aes-128-cbc", "-K", key.hex(), "-iv", iv.hex(), "-nopad"],
        input=data, capture_output=True, check=True,
    ).stdout


def hash_r6(password: bytes, salt: bytes, extra: bytes) -> bytes:
    """Algorithm 2.B: SHA-256, then 64 rounds of AES-128-CBC and a SHA-2 chosen by the output."""
    k = hashlib.sha256(password + salt + extra).digest()
    for _ in range(64):
        encrypted = aes128_cbc_nopad(k[0:16], k[16:32], (password + k + extra) * 64)
        total = sum(encrypted[0:16])
        k = [hashlib.sha256, hashlib.sha384, hashlib.sha512][total % 3](encrypted).digest()
    return k[0:32]


def hash_r5(password: bytes, salt: bytes, extra: bytes) -> bytes:
    """Revision 5 stops after the first SHA-256: no hardening loop."""
    return hashlib.sha256(password + salt + extra).digest()


def values(raw: bytes) -> dict:
    found = {}
    for name in ("U", "O", "UE", "OE"):
        match = re.search(rb"/" + name.encode() + rb"\s*<([0-9A-Fa-f]+)>", raw)
        if match:
            found[name] = bytes.fromhex(match.group(1).decode())

    return found


def main(path: str, user_password: str, owner_password: str) -> None:
    raw = open(path, "rb").read()
    v = values(raw)
    revision = re.search(rb"/R\s+(\d+)", raw)
    print(f"{path}: /R {revision.group(1).decode() if revision else '?'}, "
          + ", ".join(f"/{n} {len(x)}B" for n, x in v.items()))

    if "U" not in v or "O" not in v:
        print("  not a standard-handler file")
        return

    u, o = v["U"], v["O"]
    user, owner = user_password.encode(), owner_password.encode()

    for hasher, label in ((hash_r6, "R6"), (hash_r5, "R5")):
        for salt_at in (32, 40):
            for extra_label, extra in (("U", u), ("empty", b""), ("user+U", user + u), ("U+user", u + user)):
                if hasher(owner, o[salt_at:salt_at + 8], extra) == o[0:32]:
                    print(f"  OWNER matches {label} salt@{salt_at} extra={extra_label}")
                if hasher(user, u[salt_at:salt_at + 8], extra) == u[0:32]:
                    print(f"  USER  matches {label} salt@{salt_at} extra={extra_label}")

    print("  (nothing listed means no slot reproduced)")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
