# AGENTS.md — working in the VCCad repository

This file is the operating manual for anyone (human or AI agent) picking up this
repository in a fresh environment. It aims to make switching environments — local
dev box, Docker host, CI, or an AI harness — uneventful. Read it fully before
running commands.

---

## 1. What this project is

**VCCad** is an Adobe-Illustrator-style vector editor: C# / .NET 8, Avalonia UI
compiled to **WebAssembly**, fully automatable (REST + JSON-RPC over WebSocket),
storing documents **losslessly as PDF**. It imports real-world PDFs into its model
and re-exports them faithfully.

Read these in order for context:

- `README.md` — overview + quickstart.
- `projectplan.md` — vision, architecture, milestones, ADRs.
- `AGENTS.md` (this file) — environment, commands, conventions, gotchas.

---

## 2. Golden rules

1. **Do not commit unless the user explicitly asks.** Then use conventional
   commit messages (`feat:`, `fix:`, `test:`, `docs:`, `build:`, `chore:`).
2. **Never add comments to code unless asked** — but this repo *does* want
   explanatory comments on math/formulas and public APIs; match surrounding style.
3. **Deployment archives `HEAD`** (`git archive HEAD`). Uncommitted work is *not*
   deployed. Commit before running `scripts/deploy-remote.sh`.
4. Keep the working tree clean when handing off (`git status` empty).
5. Prefer the specialized tools (Read/Grep/Glob/Edit) over shell `cat`/`grep`/`sed`.

---

## 3. Prerequisites & bootstrap

Required:

| Tool | Why | Check |
|------|-----|-------|
| .NET 8 SDK | build/test/run | `dotnet --version` (8.0.x) |
| `wasm-tools` workload | building `VCCad.App.Browser` | `dotnet workload list` |
| `qpdf` | PDF structural validation | `qpdf --version` |
| poppler `pdftoppm`/`pdftotext` | render/text verification | `pdftoppm -v` |
| `mutool` (MuPDF) | second reference renderer | `mutool -v` |
| Ghostscript `gs` | third reference renderer | `gs --version` |
| Python 3 + Pillow + numpy | render-diff scripts | `python3 -c "import PIL,numpy"` |
| Python 3 + fontTools | CFF/font inspection (dev only) | `python3 -c "import fontTools"` |
| Java 21 | optional: run veraPDF validator | `java -version` |

Optional / environment-specific (tests skip cleanly when absent):

| Tool | Env var | Default probe path |
|------|---------|--------------------|
| veraPDF validator (PDF/A) | `VCCAD_VERAPDF` | `/tmp/opencode/vpdf/install/verapdf`, `/opt/verapdf/verapdf` |
| veraPDF corpus (2,906 PDFs) | `VCCAD_VERAPDF_CORPUS` | `/tmp/opencode/veraPDF-corpus`, `~/development/veraPDF-corpus` |
| qwen vision endpoint | `QWEN_BASE`, `QWEN_KEY`, `QWEN_MODEL` | `https://your-endpoint.example/v1`, `<your-api-key>`, `qwen3.8-27b` |

One-shot bootstrap:

```bash
./scripts/bootstrap-dev.sh          # installs wasm-tools; prints next steps
```

Manual equivalents:

```bash
# wasm-tools workload (needed once per SDK install)
dotnet workload install wasm-tools

# veraPDF corpus (optional; PDF import-fidelity tests)
git clone --depth 1 https://github.com/veraPDF/veraPDF-corpus.git /tmp/opencode/veraPDF-corpus

# veraPDF validator (optional; PDF/A conformance tests)
mkdir -p /tmp/opencode/vpdf && cd /tmp/opencode/vpdf
curl -O https://software.verapdf.org/releases/verapdf-installer.zip
unzip -q verapdf-installer.zip && cd verapdf-greenfield-*/   # then run the izpack installer
# export VCCAD_VERAPDF=/tmp/opencode/vpdf/install/verapdf
```

---

## 4. Build, test, run

```bash
# Build everything (Release)
dotnet build VCCad.sln -c Release

# Full test suite (~3,080 tests; corpus theory expands when the corpus is present)
dotnet test VCCad.sln -c Release

# With optional external validators enabled:
VCCAD_VERAPDF=/tmp/opencode/vpdf/install/verapdf \
VCCAD_VERAPDF_CORPUS=/tmp/opencode/veraPDF-corpus \
dotnet test VCCad.sln -c Release

# Single project
dotnet test tests/VCCad.Pdf.Tests -c Release
```

Test projects:

| Project | Covers |
|---------|--------|
| `tests/VCCad.Geometry.Tests` | pure math kernel |
| `tests/VCCad.Core.Tests` | document model, commands, serializer |
| `tests/VCCad.Pdf.Tests` | PDF writer/reader, import fidelity, PDF/A, veraPDF corpus sweep |
| `tests/VCCad.Api.Integration.Tests` | REST + JSON-RPC over WebSocket |
| `tests/VCCad.App.Tests` | Avalonia headless: embedded-font registration + glyph ids |

Run the app locally (single Kestrel process serving API + WASM):

```bash
./scripts/dev.sh run        # → http://127.0.0.1:5099
./scripts/dev.sh test       # build + test
./scripts/dev.sh wasm       # publish just the wasm bundle
```

---

## 5. Repository map

```
src/VCCad.Geometry     pure math: Point2D/Vector2D/Rect2D/AffineTransform/CubicBezier
src/VCCad.Core         model (CadDocument/Artboard/Layer/ArtGroup/PathItem/TextItem),
                       styles (FillSpec/StrokeSpec/DashPattern/EmbeddedFont),
                       commands + undo, lossless JSON serializer
src/VCCad.Pdf          PDF 1.7 writer + reader; content importer; font embedding /
                       embedded-font pass-through; CFF charset reader (CffGlyphMap);
                       PDF/A-2b metadata; exporter sidecar
src/VCCad.Api          ASP.NET host: REST /api/v1/* + JSON-RPC /ws/rpc + static WASM
src/VCCad.App          Avalonia editor shell (canvas, panes, docking, font collection)
src/VCCad.App.Browser  net8.0-browser WASM host
tests/*                xUnit suites (see table above)
tools/qwen-corpus-tracker  qwen-based render-fidelity progress tracker
docker/Dockerfile      multi-stage: restore → build → test → publish api + wasm
scripts/               dev.sh, deploy-remote.sh, bootstrap-dev.sh
samples/               A0-Temi-Bow-Bustier-sewing-pattern.pdf (real-world fixture)
```

Assembly dependency rule: **`Geometry ← Core ← {Pdf, Api, App}`**. Geometry never
depends on Avalonia/ASP.NET, so everything is testable headless.

---

## 6. Verification tooling (accuracy work)

This project treats "does it render/parse like other compliant tools?" as a
first-class, measured goal. The harnesses:

### 6.1 PDF/A conformance via veraPDF (real validator)

`tests/VCCad.Pdf.Tests/PdfAValidationTests.cs` — validates our exports as
**PDF/A-2b** with the actual veraPDF engine (gated by `VCCAD_VERAPDF`); also
asserts the exact structures (binary marker, `/Info`, `/ID`, XMP `pdfaid`,
sRGB `OutputIntent`, sidecar as a catalog stream). Our export currently passes.

### 6.2 Corpus sweep (import robustness + fidelity)

`tests/VCCad.Pdf.Tests/VeraPdfCorpusTests.cs` — runs the vector importer over the
whole veraPDF corpus (no-op when `VCCAD_VERAPDF_CORPUS` is absent). An aggregate
test reports the coverage floor. `tools/qwen-corpus-tracker` scores our render vs
poppler.

### 6.3 Render-diff workbench (ad-hoc, in /tmp)

During accuracy work a small workbench is used (not committed; rebuild as needed):

- `/tmp/opencode/rc` — imports every corpus PDF, exports, and compares
  `pdftotext -bbox` word geometry and pixel RMSE against poppler.
- `/tmp/opencode/render` — renders the imported `CadDocument` model directly with
  Skia (no export) for import-only verification.
- `/tmp/opencode/vpdf/install/verapdf` — the validator.
- `/tmp/opencode/qwen_track.py` — vision-model scoring (also committed under
  `tools/qwen-corpus-tracker/track.py`).

Reference numbers from the last accuracy pass (vs poppler, 2,906 files): text
`median |dx| 0.000pt`, `|dy| 0.003pt`, missing words 502/18,371; pixel RMSE
mean 0.045.

---

## 7. Deployment (Docker host `user@host`)

```bash
# Commit first — the script archives HEAD, not the working tree.
./scripts/deploy-remote.sh            # build image on host + restart container
VCCAD_DOCKER_HOST=user@host ./scripts/deploy-remote.sh 0.1.0
# → http://<host>:8080   (health: /api/v1/health)
```

What it does: `git archive HEAD` → `scp` → on the host `docker build -f
docker/Dockerfile -t vccad:<tag>` → `docker rm -f vccad; docker run -d -p 8080:8080`
→ healthcheck. The container **must be recreated** (not just reloaded) to pick up
a new image.

Dockerfile notes:

- The restore layer `COPY`s each `.csproj` explicitly. **If you add a project,
  add its csproj to the Dockerfile restore block** or CI/docker builds fail with
  `MSB3202` (project file not found).
- The build stage runs the test suite before producing images.
- Static assets are served `no-cache`, so a browser reload picks up new builds.

---

## 8. Conventions & invariants

- **Coordinates** are PDF points; model space is top-left origin, +Y down; the PDF
  exporter applies the per-artboard y-flip.
- **Determinism is load-bearing**: identical documents serialize to identical
  bytes; PDF output is byte-stable.
- **PDF import fidelity**: when a font is embedded we **must not substitute** it.
  The importer captures the programme + original glyph codes (`EmbeddedFont`,
  `TextRun.RawCodes`), maps glyph ids (`TextRun.GlyphIds`) for canvas rendering,
  and the exporter re-emits the programme verbatim (simple and Type0).
- **Text decoding**: PDF literal/hex strings are byte strings; escapes (octal) and
  `/ToUnicode` CMaps must be honoured (see `Parsing/PdfReader.cs`,
  `PdfContentImporter.cs`).
- **PDF/A**: the exporter targets PDF/A-2b — metadata (`/Info`, `/ID`, XMP
  `pdfaid`), sRGB `OutputIntent`, embedded fonts, and the lossless sidecar stored
  as a **catalog stream** (`/VCCadDocument`), not `/EmbeddedFiles`.
- xUnit **theories must never yield no data** (CI errors "No data found"); emit a
  skip sentinel when an optional data source is absent (see `VeraPdfCorpusTests`).

---

## 9. Current status & known gaps

- ~3,080 tests green; PDF/A-2b validated by veraPDF; corpus coverage 2,906 files.
- Embedded fonts: simple + composite pass-through implemented; canvas renders via
  an Avalonia `IFontCollection` and glyph ids.
- Known import gaps toward "100%": **Type3 fonts**, fonts with **no embedded
  programme**, and text outside page content (~54 corpus files lose some words).
  Non-text gaps: tiling/shading **patterns**, **images**, **clipping** (`W`/`Wn`),
  **transparency groups / soft masks / blend modes**.
- Next target: `https://github.com/SteveTheKiller/KillerPDF-Corpus` — note it is a
  *harness* repo (adapters/baselines/benchmarks/manifests, no PDFs), so read its
  manifests/scripts to learn what it expects.

---

## 10. Troubleshooting

| Symptom | Fix |
|---------|-----|
| Docker build `MSB3202` project file not found | add the project's `.csproj` to the restore `COPY` block in `docker/Dockerfile` |
| Tests pass locally, fail in container "No data found" | a theory has no data — add a skip sentinel |
| Reload shows the old build | the container must be **recreated**; run `deploy-remote.sh` |
| veraPDF tests do nothing | set `VCCAD_VERAPDF` to the launcher path |
| Corpus tests show 34 total only | set `VCCAD_VERAPDF_CORPUS` to the corpus checkout |
| `wasm-tools` missing / browser publish fails | `dotnet workload install wasm-tools` (needs python3 on PATH in containers) |
| Font renders as a substitute on canvas | the programme isn't registered: check `EmbeddedFontManager.Register` ran and `TextRun.GlyphIds` is non-null |
