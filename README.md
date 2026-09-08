# VCCad — an Adobe-Illustrator-style vector editor, in the browser

VCCad is a C#/.NET 8 vector graphics editor whose goal is to track Adobe
Illustrator's model and workflow while being **web-native, headless-first, and
PDF-lossless**:

- **Avalonia UI → WebAssembly.** Windows-style menus/toolbars, dockable panes
  and an infinite pasteboard that scrolls a full viewport beyond every object
  in every direction.
- **Illustrator-style document model.** Documents hold **artboards** (A4
  landscape by default) → **layers** → **groups** → **paths**. Paths are open or
  closed, made of line segments and cubic Bézier curves (stored as nodes with
  in/out handles). Paths carry stroke widths/colours; closed paths carry fills.
- **Lossless PDF.** Save/export a faithful PDF (one page per artboard, native
  `m/l/c` operators) that carries the full model as an embedded sidecar, so
  round-trips are bit-exact.
- **100% automatable.** A REST surface for document lifecycle plus JSON-RPC 2.0
  over WebSocket for everything the UI does — through the *same* command stack.

> Full roadmap, milestones/sprints/tasks and architecture decisions:
> see [`projectplan.md`](projectplan.md).

## Repository layout

| Path | What it is |
|------|------------|
| `src/VCCad.Geometry` | Pure math kernel: points, vectors, rects, affine transforms, cubic-Bézier algebra (split/flatten/length/tight bounds), root solvers |
| `src/VCCad.Core` | Document model, styling, undo/redo command bus, lossless serializer, pasteboard panning math |
| `src/VCCad.Pdf` | PDF 1.7 exporter (page per artboard, native curves) + embedded lossless sidecar, and the reader for round-trips |
| `src/VCCad.Api` | Automation host: REST + JSON-RPC over WebSocket; serves the published editor |
| `src/VCCad.App` | Avalonia editor shell (menus, toolbar, docking panes, workspace canvas) |
| `src/VCCad.App.Browser` | WebAssembly host (`net8.0-browser`) |
| `tests/*` | Unit + integration tests (xUnit). Geometry ≥95% coverage target |
| `docker/` | Multi-stage Dockerfile (tests run in-image) + compose |
| `.github/workflows/` | CI (`ci.yml`) and CD / deployment (`docker.yml`) |

## Quickstart (local)

Prerequisites: .NET 8 SDK.

```bash
dotnet build VCCad.sln -c Release
dotnet test tests/VCCad.Geometry.Tests   -c Release --no-build
dotnet test tests/VCCad.Core.Tests       -c Release --no-build
dotnet test tests/VCCad.Pdf.Tests        -c Release --no-build
dotnet test tests/VCCad.Api.Integration.Tests -c Release --no-build

# Serve the editor + API from one process:
dotnet publish src/VCCad.Api -c Release -o /tmp/vccad-run/api
dotnet publish src/VCCad.App.Browser -c Release -o /tmp/vccad-wasm
cp -r /tmp/vccad-wasm/wwwroot /tmp/vccad-run/api/wwwroot
ASPNETCORE_URLS=http://127.0.0.1:5099 dotnet /tmp/vccad-run/api/VCCad.Api.dll
# → open http://127.0.0.1:5099
```

`scripts/dev.sh` wraps the same flow.

## Docker (docker host `user@host`)

The image builds, runs the full test suite, publishes the API and the wasm
editor, then serves everything from Kestrel on port 8080:

```bash
git archive --format=tar.gz HEAD -o /tmp/vccad-src.tgz
scp /tmp/vccad-src.tgz user@host:/tmp/
ssh user@host 'rm -rf ~/vccad-build && mkdir ~/vccad-build && \
  tar -xzf /tmp/vccad-src.tgz -C ~/vccad-build && \
  cd ~/vccad-build && docker build -f docker/Dockerfile -t vccad:0.1.0 .'
ssh user@host 'docker rm -f vccad || true; \
  docker run -d --name vccad -p 8080:8080 vccad:0.1.0'
# → http://<host>:8080
```

## Automating VCCad (the API)

REST (document lifecycle):

```
GET    /api/v1/health
POST   /api/v1/documents            {"name":"hello"}
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

## Design notes

- **All code, especially math, is commented** with formulas and rationale.
- **Determinism is load-bearing**: identical documents serialize to identical
  bytes; PDF output is byte-stable; command replay reproduces documents.
- Coordinates are stored in **PDF points**, model space is top-left origin with
  +Y down; the PDF exporter applies the y-flip per artboard.
- Straight segments are degenerate cubics (handles collapsed), so geometry code
  has a single primitive.

## Status

Foundation milestone (M0–M4 seeds, M5 shell seed): 109 tests green, exports
validated by `qpdf`/Ghostscript. Interactive tooling, full path editing and
docking drag-out are scheduled — see `projectplan.md`.
