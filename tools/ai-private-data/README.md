# tools/ai-private-data

An **independent reference implementation** of the Adobe Illustrator private-data
extraction rules, plus a golden manifest of expected results over the real `.ai`
fixture corpus. It exists so `src/VCCad.Pdf/Ai/` (C#) can be validated against a
second implementation — comparing a decoder only with itself would not catch a
systematic misreading of the format. The format itself is documented in
[`docs/ai-private-data.md`](../../docs/ai-private-data.md).

Development/corpus tooling only; not shipped with the application.

## Requirements

* `qpdf` (normalises PDF containers so `/AIPrivateData` streams are uncompressed)
* a zstd decoder — the `zstandard` Python module, or the `zstd` CLI on `PATH`

## Fetch the fixtures, then build or verify the manifest

```bash
./scripts/fetch-corpora.sh ai

# Decode one file
python3 tools/ai-private-data/decode-ai-private-data.py decode \
    ~/.cache/vccad-corpora/ai/rectangle.ai --head 400

# Regenerate the golden manifest (writes tools/ai-private-data/fixture-manifest.json)
python3 tools/ai-private-data/decode-ai-private-data.py manifest \
    ~/.cache/vccad-corpora/ai -o tools/ai-private-data/fixture-manifest.json

# Verify the manifest against the corpus — the check the Lead runs after a change
python3 tools/ai-private-data/decode-ai-private-data.py check \
    ~/.cache/vccad-corpora/ai tools/ai-private-data/fixture-manifest.json
```

`manifest` exits non-zero if any fixture fails to decode; `check` exits non-zero on
any format, size or SHA-256 mismatch.

## Manifest contents

For each fixture: detected `format`, container size, block count, plain-text prolog
size, decompressed `payload_bytes`, `payload_sha256`, and a printable head of the
payload. Fixture composition at the time of writing:

| Format | Count | Meaning |
|--------|-------|---------|
| `ai24-zstd` | 30 | PDF container, `%AI24_ZStandard_Data` + zstd |
| `postscript-only` | 21 | no PDF layer at all; the file is the payload |
| `plain` | 8 | PDF container whose blocks are uncompressed text |
| `ai12-zlib` | 7 | PDF container, `%AI12_CompressedData` + zlib |
