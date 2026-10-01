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
- **Illustrator private data, preserved.** `.ai` files (and Illustrator-saved
  PDFs) carry a private data stream holding the styling, layer and text
  structures PDF content streams flatten away. VCCad decodes it, keeps it
  losslessly, and **re-emits it on export**, so import and export stay in sync
  even where our own model does not yet cover a feature — see
  [`docs/ai-private-data.md`](docs/ai-private-data.md).
- **Web *and* desktop.** The same Avalonia shell runs in the browser
  (WebAssembly) and as a native window on **Windows and Linux**.
- **100% automatable, API-first.** The running desktop app serves a loopback HTTP
  endpoint that exposes *every* editor operation; the built-in assistant drives that
  same registry, so the model can do anything a person can — and the person can run
  any operation from the diagnostics window. Point-and-click automation exists but
  API calls are always preferred.
- **Paginated PDFs import correctly.** Pages are laid out as a grid and the content
  stream's clipping (`W`/`W*`) is honoured, so a pattern tiled across eight A4 sheets
  arrives as eight readable pages with the pieces cut at the sheet edges — not as
  full-size copies spilling across every page.
- **A diary you can learn from.** Every pointer, hover, drag, keystroke, operation and
  model request is timestamped and stored as searchable JSON (`history.search`,
  `history.tail`, `history.sessions`). Finished work can be distilled into a **skill**
  (`history.learn`), which the assistant recalls automatically on later, similar
  requests — so a task only has to be worked out once.
- **The chrome is scriptable too.** `ui.find` / `ui.click` / `ui.setValue` / `ui.keys`
  drive the real menus, toolbar and panes — "open File and click Import" is an API
  call. File dialogs, which cannot be automated, have dialog-free equivalents
  (`document.openFile`, `document.savePdfToFile`).
- **Legible without vision.** `ui.dump` serialises the entire Avalonia visual tree and
  the document tree to text; `ui.describe` asks the vision model to describe the
  window, a named layer, the selection or a region. A tool with no screenshot support
  can still see what the user sees.

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
| `src/VCCad.App.Browser` | WebAssembly host (`net10.0-browser`) |
| `src/VCCad.App.Desktop` | Native desktop host (Windows/Linux) for the same editor shell |
| `tests/*` | xUnit suites (Geometry, Core, Pdf, Api, App headless incl. desktop bootstrap) |
| `tools/qwen-corpus-tracker` | qwen vision-model render-fidelity progress tracker |
| `tools/ai-private-data` | Independent reference decoder + golden manifest for `.ai` private data |
| `docs/` | Format and design notes (start with `ai-private-data.md`) |
| `docker/` | Multi-stage Dockerfile (tests run in-image) |
| `scripts/` | `dev.sh`, `deploy-remote.sh`, `bootstrap-dev.sh`, `publish-desktop.sh`, `fetch-corpora.sh` |
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

# Fetch the optional reference corpora (not vendored; ~650 MB, cached under
# ${XDG_CACHE_HOME:-$HOME/.cache}/vccad-corpora), then enable the externals:
./scripts/fetch-corpora.sh

export VCCAD_GS_CORPUS=$HOME/.cache/vccad-corpora/ghostscript   # Ghostscript/MuPDF + Ghent suite
export VCCAD_AI_CORPUS=$HOME/.cache/vccad-corpora/ai            # 66 real Illustrator files
export VCCAD_VERAPDF_CORPUS=$HOME/.cache/vccad-corpora/verapdf
export VCCAD_VERAPDF=/path/to/verapdf
dotnet test VCCad.sln -c Release
```

Every corpus-backed test **skips cleanly** when its corpus directory is
absent, so the suite is green on a machine with no network and no cache.

### Windows (native, no WSL)

The desktop app builds and runs as an ordinary Windows process:

```powershell
dotnet run --project src/VCCad.App.Desktop     # opens the VCCad window
./scripts/publish-desktop.ps1 -Run             # self-contained win-x64 bundle + launch
./scripts/publish-desktop.ps1 -Rids win-x64,linux-x64 -Tag 0.2.0
```

Only `VCCad.App.Browser` (WebAssembly) needs the `wasm-tools` workload; the
desktop app, libraries and test projects build without it.

Run the editor + API from one process:

```bash
./scripts/dev.sh run        # → http://127.0.0.1:5099
./scripts/dev.sh test       # build + test
./scripts/dev.sh wasm       # publish just the wasm bundle
```

Build native desktop applications:

```bash
./scripts/publish-desktop.sh          # self-contained win-x64 + linux-x64 under artifacts/desktop/
./scripts/publish-desktop.sh 0.1.0    # optional version tag
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

## Automation and the built-in assistant

```bash
# start with a task already queued and the diagnostics window open
src/VCCad.App.Desktop/bin/Release/net10.0/VCCad.App.Desktop.exe \
    --chat "draw a red circle centred on the artboard" --diagnostics

curl http://127.0.0.1:5099/api/v1/operations        # everything that can be done
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"ui.describe","params":{"target":"layer","layerName":"UK 14"}}'
```

**F12** toggles the diagnostics overlay — a wide, short, semi-transparent strip across
the bottom of the canvas, showing the assistant conversation, an operations runner, the
full API call log and the endpoint settings without hiding the artwork. The window opens
docked to the right half of the screen (`--no-dock` to disable). The model defaults
to `qwen3.8-27b` at `https://your-endpoint.example/v1`.

See [`AGENTS.md`](AGENTS.md) §1.1 for the non-negotiables (test-first, CI/CD,
user/assistant capability parity, API-first, no-vision introspection).

## Verification tooling

- **PDF/A-2b** via the real veraPDF engine — `PdfAValidationTests` (gated by
  `VCCAD_VERAPDF`). Our exports validate as PDF/A-2b.
- **Import fidelity** — `VeraPdfCorpusTests` runs the vector importer over the
  whole veraPDF corpus (gated by `VCCAD_VERAPDF_CORPUS`); `tools/qwen-corpus-tracker`
  scores our render against poppler with a vision model.
- Structural checks with `qpdf --check` and content-stream assertions.
- **Corpus sweeps.** `scripts/fetch-corpora.sh` populates the Ghostscript/MuPDF
  public test files ([`ArtifexSoftware/tests`](https://github.com/ArtifexSoftware/tests),
  incl. the Ghent PDF Output Suite), real `.ai` fixtures from Inkscape's
  `extension-ai`, the pdf.js corpus and the veraPDF corpus. The PDF feature probe
  and importer sweeps are gated by `VCCAD_GS_CORPUS`; the Illustrator private-data
  sweeps by `VCCAD_AI_CORPUS`.
- **`.ai` private data** — `tools/ai-private-data/decode-ai-private-data.py` is an
  independent reference decoder with a golden manifest of expected format, size and
  SHA-256 per fixture, so the C# decoder is checked against a second implementation
  rather than only against itself.

## Status

- ~3,080 tests green; exports validate as **PDF/A-2b**; 2,906-file corpus sweep.
- Embedded fonts are preserved on import and reused verbatim (simple + Type0),
  and rendered on the canvas by glyph id (no substitution).
- Adobe Illustrator **private data** is decoded (AI8 PostScript, AI9-CS zlib,
  CC-Legacy zlib, AI24 zstd and uncompressed variants), kept losslessly on the
  document model, and re-emitted into exported PDFs — 66 real `.ai` fixtures are
  covered by corpus tests.
- The editor publishes as a **web app and as native Windows/Linux desktop apps**.
- Known gaps toward full import fidelity: Type3 fonts, fonts without an embedded
  programme, text outside page content, and non-text constructs (patterns,
  images, clipping, transparency groups). See [`AGENTS.md`](AGENTS.md) §9 and the
  feature inventory produced by the corpus probe.
