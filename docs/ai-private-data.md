# AI private data — format notes and VCCad's approach

> Status: reference notes captured during the corpus/private-data work. These are the
> *verified* rules — every statement below was checked against the 66 real `.ai`
> fixtures in the Inkscape `extension-ai` corpus (see `tools/ai-private-data/`).

## 1. Why this matters

An Adobe Illustrator document must round-trip its **styling and structure**, not just
its rendered artwork. Illustrator stores that information in a private data stream
that PDF viewers ignore. If VCCad wants Illustrator's feature set — live gradients,
appearance/opacity stacks, text-on-path data, layer hierarchy, brush and symbol
definitions, artboard metadata — it has to read and re-emit that stream, because the
PDF page content alone is a flattened projection of the design.

So: **import parses the private data; export re-emits it.** Anything we cannot model
yet is still preserved verbatim, which is what keeps import and export in sync.

## 2. Where the private data lives

### 2.1 Illustrator 9 and later — `.ai` as a PDF container

Illustrator saves `.ai` files as *PDF with a private payload* (the "Create PDF
Compatible File" option, on by default since version 9). Steps:

1. Byte-signature check, not the extension: `%PDF` anywhere in the first ~2 KB means a
   PDF container; `%!PS-Adobe-3.0` means a legacy PostScript `.ai` (see 2.2).
2. The payload hangs off the **page** dictionary:

   ```
   /PieceInfo << /Illustrator <<
       /Subtype /Artwork
       /CreatorInfo << /Creator (Adobe Illustrator 30.1) /Subtype /Artwork >>
       /Private << /NumBlock 6
                   /AIPrivateData1 40 0 R
                   /AIPrivateData2 41 0 R
                   ... >> >> >>
   ```

   The blocks are `/AIPrivateData<digits>` streams, ordered by the numeric suffix, and
   `/NumBlock` *usually* matches the count — but not always (`cmyk_rectangle.ai` and
   `rgb_rectangle.ai` have a single block and no `/NumBlock` at all). Never require it.
3. The block streams are normally `FlateDecode` **at the PDF level**. Apply the PDF
   filters first; the Illustrator-level compression below sits inside them.

### 2.2 Illustrator 8 and earlier — plain PostScript

The file *is* the payload: `%!PS-Adobe-3.0` with `%%AI8_CreatorVersion` and the
Illustrator procset. There is no PDF layer. 21 of the 66 corpus fixtures are of this
kind, including some AI-2020-era files saved in legacy mode.

> Consequence for the importer: if there is no `%PDF` header, hand the whole file to
> the AI parser instead of the PDF reader.

## 3. Decompressing the payload

Concatenate **all** blocks in numeric order, then apply the *first* matching rule. The
order matters: `H\x89` occurs by chance inside zstd and zlib data, so the version
markers must be tested first.

| # | Detect | Payload | Notes |
|---|--------|---------|-------|
| 1 | `%AI24_ZStandard_Data` (20 bytes) | everything after the marker, **zstd**-decompressed | The next four bytes are the zstd frame magic `28 B5 2F FD`. The `(` visible in the files *is* the `0x28`. Take the bytes verbatim; do not skip a parenthesis or a newline. |
| 2 | `%AI12_CompressedData` (20 bytes) | everything after the marker, **zlib**-inflated | The zlib header (`78 9C`) follows immediately, with no newline. The result can be several concatenated zlib streams — loop until the input is consumed. |
| 3 | `H\x89` (`0x48 0x89`, another legal zlib header) | from that offset, zlib-inflated | The Illustrator 9–CS rule. Per the published amendment, blocks that do not contain the marker are discarded. |
| 4 | none of the above | the concatenation is already plain text | Illustrator frequently writes the payload uncompressed. |

Two practical traps found in the corpus:

* **A plain-text prolog precedes the marker.** In `simple_cs6.ai` block 1 is 6,643
  bytes of `%%BoundingBox:` / `%%HiResBoundingBox:` / `%AI7_Thumbnail:` text and the
  `%AI12_CompressedData` marker only appears at the start of block 2. So always search
  the *whole concatenation* for the marker rather than assuming an offset.
* **Trailing garbage.** `circle.ai` and `gradient_radial_random_test.ai` have bytes
  after the zstd frame. A streaming decompressor must read to EOF and tolerate the
  tail instead of reporting failure.

## 4. The decoded payload

The result is DSC-style PostScript text. Directives seen in real files include
`%!PS-Adobe-3.0`, `%%Creator`, `%%AI8_CreatorVersion`, `%AI5_FileFormat`,
`%AI3_ColorUsage`, `%AI3_Cropmarks`, `%AI5_BeginLayer`/`%AI5_EndLayer` (nestable),
`%AI9_OpenToView`, `%AI10_OpenToVie`, `%AI12_BuildNumber`, `%AI12_CMSettings`,
`%AI17_Begin_Content_if_version_gt`/`%AI17_End_Versioned_Content`,
`%AI24_LargeCanvasScale`, `%AI24_ZStandard_Data`, `%%BeginData`/`%%EndData`.

VCCad takes a **lossless** view: the parser keeps the exact text (so unmodified
documents round-trip byte-for-byte) *and* builds a structured view (header directives,
hex/ASCII data blocks, nested layers, unrecognised lines preserved in order). The
structured view is what future styling work consumes; the text is what export
re-emits.

## 5. What VCCad does

* `src/VCCad.Pdf/Ai/` — codec (locate + decompress), lossless tokenizer/parser, and
  writer.
* `CadDocument` carries the decoded payload, so the lossless sidecar JSON preserves it
  and the PDF exporter re-emits it as page `/PieceInfo`.
* Exporting a document **without** an AI payload is byte-for-byte unchanged.
* Emitting AI8-style plain text is acceptable and is what our own exports use —
  Illustrator opens uncompressed private data directly, and it avoids us having to
  reproduce Adobe's exact zstd framing.

## 6. Corpora

None of these are vendored (their licences are not ours) — `scripts/fetch-corpora.sh`
downloads them into `${XDG_CACHE_HOME:-$HOME/.cache}/vccad-corpora`:

| Corpus | Source | Why |
|--------|--------|-----|
| `ghostscript` | [ArtifexSoftware/tests](https://github.com/ArtifexSoftware/tests) (AGPL-3.0) | The public Ghostscript/MuPDF test files: 208 PDFs incl. the Ghent PDF Output Suite (overprint, DeviceN, shading, font substitution, output intents) and `pdf/safedocs`. |
| `ai` | [inkscape/extras/extension-ai](https://gitlab.com/inkscape/extras/extension-ai) (GPL-2.0-or-later) | 66 genuine `.ai` files spanning AI8 PostScript, AI9-CS, CC-Legacy and AI24 zstd. |
| `pdfjs` | [mozilla/pdf.js](https://github.com/mozilla/pdf.js) `test/pdfs` (Apache-2.0) | Broad real-world PDF feature coverage. |
| `verapdf` | [veraPDF-corpus](https://github.com/veraPDF/veraPDF-corpus) (CC-BY-4.0) | ISO 32000 / PDF 2.0 conformance. |

Test suites read `VCCAD_AI_CORPUS`, `VCCAD_GS_CORPUS`, `VCCAD_PDFJS_CORPUS` and
`VCCAD_VERAPDF_CORPUS`, and **skip cleanly** when the corresponding directory is
absent — a theory never yields zero data.

## 7. Independent oracle

`tools/ai-private-data/decode-ai-private-data.py` is a second, independent
implementation of section 3 (with `qpdf` doing the PDF normalisation) plus a golden
manifest, `tools/ai-private-data/fixture-manifest.json`, recording the expected
format, decompressed size and SHA-256 for each of the 66 fixtures. It is used to
validate the C# decoder against something other than itself:

```bash
python3 tools/ai-private-data/decode-ai-private-data.py check \
    ~/.cache/vccad-corpora/ai tools/ai-private-data/fixture-manifest.json
# checked 66 fixtures: 0 mismatches
```

Measured fixture composition at the time of writing:
`ai24-zstd` 30, `postscript-only` 21, `plain` 8, `ai12-zlib` 7.
