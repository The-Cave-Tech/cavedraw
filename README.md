# VCCad — an Adobe-Illustrator-style vector editor, in the browser

VCCad is a C#/.NET 8 vector graphics editor whose goal is to track Adobe
Illustrator's model and workflow while being **web-native, headless-first, and
PDF-lossless**:

- **Avalonia UI → WebAssembly.** Windows-style menus/toolbars, dockable panes
  and an infinite pasteboard that scrolls a full viewport beyond every object
  in every direction.
- **Illustrator-style document model.** Documents hold **artboards** (A4
  landscape by default) → **layers** → **groups** → **paths**. Paths are open or
  closed, made of line segments and cubic Bézier curves. Paths carry stroke
  widths/colours/dash patterns; closed paths carry fills. Text is rich runs with
  embedded-font fidelity.
- **Faithful, lossless PDF.** Export a **PDF/A-2b** document (validated by
  veraPDF) that renders like other compliant tools and carries the full model as
  a sidecar, so round-trips are bit-exact.
- **100% automatable.** A REST surface for document lifecycle plus JSON-RPC 2.0
  over WebSocket for everything the UI does — through the *same* command stack.

> Roadmap/milestones/ADRs: [`projectplan.md`](projectplan.md).
> Operating manual (setup, commands, gotchas for humans and AI agents): [`AGENTS.md`](AGENTS.md).

## Repository layout

| Path | What it is |
|------|------------|
| `src/VCCad.Geometry` | Pure math kernel: points, vectors, rects, affine transforms, cubic-Bézier algebra |
| `src/VCCad.Core` | Document model, styling, undo/redo command bus, lossless serializer |
| `src/VCCad.Pdf` | PDF 1.7 **writer + reader**, vector importer, font embedding/pass-through, PDF/A-2b metadata, sidecar |
| `src/VCCad.Api` | Automation host: REST + JSON-RPC over WebSocket; serves the published editor |
| `src/VCCad.App` | Avalonia editor shell (menus, toolbar, docking panes, workspace canvas, font collection) |
| `src/VCCad.App.Browser` | WebAssembly host (`net8.0-browser`) |
| `tests/*` | xUnit suites (Geometry, Core, Pdf, Api, App headless) — ~3,080 tests |
| `tools/qwen-corpus-tracker` | qwen vision-model render-fidelity progress tracker |
| `docker/` | Multi-stage Dockerfile (tests run in-image) |
| `scripts/` | `dev.sh` (build/test/run), `deploy-remote.sh`, `bootstrap-dev.sh` |
| `samples/` | Real-world Illustrator PDF fixture (A0 sewing pattern) |

## Prerequisites

Required: **.NET 8 SDK** and the **`wasm-tools` workload** (browser host).
Bootstrap:

```bash
./scripts/bootstrap-dev.sh                 # installs wasm-tools
./scripts/bootstrap-dev.sh --with-corpus   # + veraPDF corpus (optional, ~2 GB)
```

Optional verification tools (tests skip cleanly when absent): `qpdf`,
`pdftoppm`/`pdftotext` (poppler), `mutool` (MuPDF), `gs` (Ghostscript), Java +
**veraPDF** (PDF/A validation), Python 3 + Pillow/numpy/fontTools.
See [`AGENTS.md`](AGENTS.md) §3 for the full matrix and env vars.

## Build, test, run

```bash
dotnet build VCCad.sln -c Release
dotnet test  VCCad.sln -c Release          # all suites

# Enable the optional external checks:
VCCAD_VERAPDF=/tmp/opencode/vpdf/install/verapdf \
VCCAD_VERAPDF_CORPUS=/tmp/opencode/veraPDF-corpus \
dotnet test VCCad.sln -c Release
```

Run the editor + API from one process:

```bash
./scripts/dev.sh run        # → http://127.0.0.1:5099
./scripts/dev.sh test       # build + test
./scripts/dev.sh wasm       # publish just the wasm bundle
```

## Docker (docker host `user@host`)

```bash
# Commit first: the deploy script archives HEAD.
./scripts/deploy-remote.sh            # build image on the host + restart container
# → http://<host>:8080   (health: /api/v1/health)
```

The multi-stage image restores, builds, runs the test suite, publishes the API
and the wasm editor, then serves everything from Kestrel on port 8080.

## Automating VCCad (the API)

REST (document lifecycle):

```
GET    /api/v1/health
POST   /api/v1/documents            {"name":"hello"}
POST   /api/v1/documents/import     (Content-Type: application/pdf, raw body)
GET    /api/v1/documents/{id}       → lossless model JSON
GET    /api/v1/documents/{id}/pdf   → PDF with embedded sidecar
DELETE /api/v1/documents/{id}
```

JSON-RPC 2.0 over WebSocket `/ws/rpc` — the full command surface. A scripted
session (same commands the UI runs, same undo stack):

```json
{"jsonrpc":"2.0","id":1,"method":"documents.create","params":{"name":"scripted"}}
{"jsonrpc":"2.0","id":2,"method":"document.addLayer","params":{"id":"<docId>","name":"Shapes"}}
{"jsonrpc":"2.0","id":3,"method":"document.addRectangle","params":{"id":"<docId>","layerId":"<layerId>","x":20,"y":30,"width":120,"height":80,"fillColor":[220,30,30]}}
{"jsonrpc":"2.0","id":4,"method":"document.undo","params":{"id":"<docId>"}}
```

Discovery: the method catalog is in `EditorApi.ListMethods()`.

## Verification tooling

- **PDF/A-2b** via the real veraPDF engine — `PdfAValidationTests` (gated by
  `VCCAD_VERAPDF`). Our exports validate as PDF/A-2b.
- **Import fidelity** — `VeraPdfCorpusTests` runs the vector importer over the
  whole veraPDF corpus (gated by `VCCAD_VERAPDF_CORPUS`); `tools/qwen-corpus-tracker`
  scores our render against poppler with a vision model.
- Structural checks with `qpdf --check` and content-stream assertions.

## Status

- ~3,080 tests green; exports validate as **PDF/A-2b**; 2,906-file corpus sweep.
- Embedded fonts are preserved on import and reused verbatim (simple + Type0),
  and rendered on the canvas by glyph id (no substitution).
- Known gaps toward full import fidelity: Type3 fonts, fonts without an embedded
  programme, text outside page content, and non-text constructs (patterns,
  images, clipping, transparency groups). See [`AGENTS.md`](AGENTS.md) §9.
