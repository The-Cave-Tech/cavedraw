#!/bin/bash
# Builds the encryption fixtures for the security-handler tests.
#
# The plaintext is a one-page PDF with a single filled rectangle at a known place, so a test can say
# what "the file opened" means: the rectangle is there, where it should be. qpdf then writes a copy
# at each level of the standard security handler, which is the independent implementation these tests
# check ours against.
cd /mnt/c/Development/vccad/main/tests/VCCad.Pdf.Tests/Fixtures/Encrypted

CONTENT=$'0 0 0 rg\n10 10 50 50 re f\n'
LENGTH=$(printf '%s' "$CONTENT" | wc -c)

{
  printf '%%PDF-1.7\n'
  printf '1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n'
  printf '2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n'
  printf '3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>\nendobj\n'
  printf '4 0 obj\n<< /Length %s >>\nstream\n' "$LENGTH"
  printf '%s' "$CONTENT"
  printf 'endstream\nendobj\n'
  printf 'trailer\n<< /Root 1 0 R /Size 5 >>\n'
  printf '%%%%EOF\n'
} > plain.pdf

# qpdf normalises it first: the hand-written file has no xref, which qpdf repairs - with a warning,
# hence the tolerated exit - and the encrypted copies are then made from a valid source.
qpdf --object-streams=disable plain.pdf plain-normalised.pdf || true

qpdf --object-streams=disable --allow-weak-crypto --encrypt "" owner 40  -- plain-normalised.pdf rc4-40.pdf
qpdf --object-streams=disable --allow-weak-crypto --encrypt "" owner 128 -- plain-normalised.pdf rc4-128.pdf
qpdf --object-streams=disable --allow-weak-crypto --encrypt "" owner 128 --use-aes=y -- plain-normalised.pdf aes-128.pdf
qpdf --object-streams=disable --encrypt "" owner 256 -- plain-normalised.pdf aes-256.pdf

# The same handler levels again, but with a password a person has to TYPE. Without one of these there
# is no way to tell "this file opened because the handler works" from "this file never needed a
# password", which is the whole point of the password-entry tests.
qpdf --object-streams=disable --allow-weak-crypto --encrypt secret owner 128 -- plain-normalised.pdf rc4-128-password.pdf
qpdf --object-streams=disable --allow-weak-crypto --encrypt secret owner 128 --use-aes=y -- plain-normalised.pdf aes-128-password.pdf

rm -f plain.pdf
ls -la
echo "--- what qpdf says each one is ---"
for f in plain-normalised rc4-40 rc4-128 aes-128 aes-256; do
  printf '%s: ' "$f"
  qpdf --show-encryption "$f.pdf" | grep -E '^R =|^P =|encrypted' | tr '\n' ' '
  echo
done
