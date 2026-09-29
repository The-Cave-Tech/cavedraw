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

## 1.1 Non-negotiables (read before changing anything)

These are product-defining rules, not preferences. A change that violates one of
them is a bug even if every test passes.

### Test-first, and the tests are the specification

VCCad is a **TDD** project. New behaviour starts as a failing test: a feature, a
parser rule, an operation, a format quirk. Corpus-backed suites
(`tests/*/Corpus/`) test-drive whole feature classes against real documents, and
**failing-on-improvement tests** are how known gaps are recorded — when you fix a
gap, the test that pinned the old behaviour must be turned into a positive
assertion, not deleted.

Every corpus theory **must emit a skip sentinel when its data source is absent**;
a theory that yields no data is a CI error.

### CI/CD is the gate, not an afterthought

`.github/workflows/` builds, tests and publishes on every push; tags publish the
Docker image and the desktop bundles. Tests run inside the Docker build too. If
your change only works on your machine, it is not done.

### The person and the assistant have exactly the same powers — both ways

This is **absolutely critical**. There is exactly one operation registry
(`VCCad.App/Automation/EditorOperations.cs`), and *everything* goes through it:
the canvas UI, the HTTP endpoint, and the built-in chatbot.

- If a person can do it, the assistant must be able to do it — so it must be an
  operation.
- If the assistant can do it, the person must be able to do it — the diagnostics
  overlay's **Operations** tab can run any operation in the registry, with
  parameters, and shows the result.

A capability that exists only in the UI (handled inside a control's event handler)
or only in the chatbot is a design defect. Add it to the registry.

This includes the *chrome*. "Open the File menu" and "click Import" are things a
person can do, so they are automatable too:

- `ui.find` — locate controls by type, name or displayed text (menu entries included,
  even though Avalonia renders them in a popup with its own visual tree).
- `ui.click` — open a menu, invoke a menu item or button, flip a toggle.
- `ui.setValue` — set a TextBox/ComboBox/CheckBox/Slider value.
- `ui.keys` — send a keyboard shortcut such as `F12` or `Ctrl+S`.
- `tool.set` / `tool.get`, `view.zoomIn` / `view.zoomOut`, `pane.list` / `pane.set`,
  `document.list` / `document.select` / `document.close`, `document.saveToServer` /
  `document.openFromServer`, `app.exit` — the toolbar, Windows menu, document tabs and
  File menu, so the shell is covered too.

The diagnostics overlay is toggled from **Windows → Diagnostics overlay** as well as F12.

The native file picker is the one thing a headless driver genuinely cannot operate, so
its *effect* is exposed directly as well: `document.openFile` and
`document.savePdfToFile`. Prefer those over clicking File → Import; clicking the menu
item still works and opens the real dialog for a person.

### Everything is recorded in the diary

The command queue says *how to redo work*. The **diary** (`InteractionLog`) says *what
actually happened*, and it is part of the product, not a debugging aid:

- Every pointer press, release, wheel, **hover**, drag, drop and keystroke a person
  makes — via `UiEventRecorder`, which taps the window's tunneling phase.
- Every operation from **any** source (UI, HTTP, assistant) — the audit trail is
  mirrored into it automatically, so one search covers all three.
- Every model request, session boundary and free-form note.

Entries are timestamped, session-tagged and written as newline-delimited JSON under
`%APPDATA%\VCCad\history` (`--history-dir`, `VCCAD_HISTORY_DIR`; `--no-recording`
disables UI capture). Retention is 90 days. The store is dependency-free on purpose:
it must also work in the browser build, where there is no SQLite.

**Skills.** `history.learn` distils a finished task — its description plus the
operations that achieved it — into a retrievable record. At the start of every turn
the assistant is handed the skills and past steps relevant to the request
(`EditorAgent.RecallFor`), so "we drafted a bodice block last week, learn this as a
skill" pays off: the next similar request starts from the recorded approach instead
of rediscovering it. When the user says "learn this as a skill", call it.

Volume is handled by sampling, not by capping what is captured: hover is recorded
when the control under the pointer changes, drag positions every 60 ms, and keys are
de-bounced. A minute of mouse movement is a few hundred entries, not tens of
thousands.

### API first, point-and-click second

The automation API is the primary way to drive the application. Point-and-click
automation (synthetic pointer/keyboard events) is supported and sometimes the only
option — for testing a gesture, a drag, a hover — but **always prefer an API
call**. An operation is deterministic, typed, logged and replayable; a click is
none of those. Reach for clicks only when the behaviour under test *is* the
gesture itself.

### Assume the driver cannot see

Development tools often have no vision. Two operations exist so an LLM can
understand the screen and the document without pixels:

- **`ui.dump`** — a massive text dump: the entire Avalonia visual tree with each
  control's type, name, text, geometry and state, plus the document tree with
  artboards, layers, objects, ids, bounds and text. Use it to answer "what is on
  screen right now?" in text.
- **`ui.describe`** — asks the vision model to describe what is visible, focused
  on the window, the document, a **named layer**, or the current selection, and
  optionally on a region of the window. Example:
  `{"op":"ui.describe","params":{"target":"layer","layerName":"UK 14"}}`
  answers "describe what is seen on the document object layer 'UK 14'".

A driver that cannot see should still be able to work confidently: dump first,
describe when a visual judgement is needed.

---

---

## 2. Golden rules

1. **Commit constantly — a standing instruction, not a per-task one.** The project
   wants a readable history, so each self-contained piece of work is its own commit
   with a conventional message (`feat:`, `fix:`, `test:`, `docs:`, `build:`,
   `chore:`). Commit *before* starting the next item, not at the end of a session: an
   uncommitted tree is invisible history, and a change that is not in `git log` cannot
   be reviewed, bisected or reverted. Keep the tree clean when handing off.
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
| veraPDF corpus (2,906 PDFs) | `VCCAD_VERAPDF_CORPUS` | `/tmp/opencode/veraPDF-corpus`, `~/development/veraPDF-corpus`, `$HOME/.cache/vccad-corpora/verapdf` |
| Ghostscript/MuPDF PDF test corpus (208 PDFs, incl. the Ghent Output Suite) | `VCCAD_GS_CORPUS` | `$HOME/.cache/vccad-corpora/ghostscript` |
| Illustrator `.ai` fixture corpus (66 files) | `VCCAD_AI_CORPUS` | `$HOME/.cache/vccad-corpora/ai` |
| pdf.js test corpus | `VCCAD_PDFJS_CORPUS` | `$HOME/.cache/vccad-corpora/pdfjs/test/pdfs` |
| qwen vision endpoint | `QWEN_BASE`, `QWEN_KEY`, `QWEN_MODEL` | `https://your-endpoint.example/v1`, `<your-api-key>`, `qwen3.8-27b` |
| URW base-35 fonts (standard PDF faces) | `VCCAD_URW_FONTS` | `/usr/share/fonts/opentype/urw-base35` (Debian/Ubuntu), Ghostscript's `Resource/Font`, or the WSL copy |

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

# All reference corpora at once (Ghostscript/MuPDF test files, real .ai
# fixtures, pdf.js, veraPDF). Cached under $XDG_CACHE_HOME/vccad-corpora, never
# vendored — their licences are not ours. `fetch-corpora.sh ai|ghostscript|...`
# fetches one at a time (~650 MB for everything).
./scripts/fetch-corpora.sh

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

# With the reference corpora enabled (corpus sweeps + feature inventory):
VCCAD_VERAPDF=/tmp/opencode/vpdf/install/verapdf \
VCCAD_VERAPDF_CORPUS=$HOME/.cache/vccad-corpora/verapdf \
VCCAD_GS_CORPUS=$HOME/.cache/vccad-corpora/ghostscript \
VCCAD_AI_CORPUS=$HOME/.cache/vccad-corpora/ai \
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
| `tests/VCCad.App.Tests` | Avalonia headless: embedded-font registration, glyph ids, desktop bootstrap |

`tests/VCCad.Pdf.Tests/Corpus/` holds the corpus harness and the PDF feature probe;
every corpus theory emits a skip sentinel when its corpus is absent.

Run the app locally (single Kestrel process serving API + WASM):

```bash
./scripts/dev.sh run        # → http://127.0.0.1:5099
./scripts/dev.sh test       # build + test
./scripts/dev.sh wasm       # publish just the wasm bundle
```

Native desktop builds (same Avalonia shell, no browser):

```bash
dotnet run --project src/VCCad.App.Desktop        # run on this machine
./scripts/publish-desktop.sh [version]            # self-contained win-x64 + linux-x64
#   → artifacts/desktop/{win-x64,linux-x64}/VCCad.App.Desktop[.exe]
```

### 4.1 Windows-native workflow (recommended on this machine)

When the checkout is reached from Windows, build and run **natively** — it is much
faster than the WSL path and the desktop app is a normal Windows process.

```powershell
./scripts/test-all.ps1                              # build + every test project, corpora auto-detected
dotnet run --project src/VCCad.App.Desktop          # opens the VCCad window

# Self-contained bundles (no .NET install needed to run them):
./scripts/publish-desktop.ps1                       # win-x64 (default)
./scripts/publish-desktop.ps1 -Rids win-x64,linux-x64 -Tag 0.2.0
./scripts/publish-desktop.ps1 -Run                  # publish, then launch it
#   → artifacts/desktop/<rid>/VCCad.App.Desktop[.exe]

# One executable per platform (self-extracting; slower first launch)
VCCAD_DESKTOP_SINGLE_FILE=1 ./scripts/publish-desktop.sh
# Publish another RID (win-arm64, linux-arm64, osx-arm64, ...)
VCCAD_DESKTOP_RIDS=linux-arm64 ./scripts/publish-desktop.sh
```

Bundles are self-contained and **untrimmed** — the sidecar serializer still uses
reflection-based `System.Text.Json`, so trimming breaks document loading at runtime.
`RuntimeIdentifiers` is `win-x64;linux-x64`: declaring more pulls their runtime packs
into every cold restore although nothing publishes them, but any RID still publishes
with an explicit `-r` (above).

Only the desktop project (and the libraries it pulls in) builds without extra
setup. `VCCad.App.Browser` needs the **`wasm-tools` workload**, so
`dotnet build VCCad.sln` / `dotnet test VCCad.sln` **fail on Windows without it**.
`scripts/test-all.ps1` builds the desktop host and runs all five test projects
instead, exporting the corpus paths it finds so the sweeps expand (~3,650 tests
with corpora versus ~270 without):

```powershell
./scripts/test-all.ps1                 # Release, every corpus it can find
./scripts/test-all.ps1 -NoCorpus       # fast run; corpus tests skip cleanly
./scripts/test-all.ps1 -Filter "FullyQualifiedName~AiPrivateData"
```

Corpus-backed tests probe their default cache path under `$HOME/.cache/vccad-corpora`
inside the *host* OS; run `scripts/fetch-corpora.sh` in WSL (or set
`VCCAD_GS_CORPUS` / `VCCAD_AI_CORPUS` to a Windows-visible path) when you want the
sweeps to expand. The WSL cache is reachable from Windows as
`\\wsl.localhost\Ubuntu\home\darren\.cache\vccad-corpora`, which is what the script
falls back to.

### 4.2 Driving the running application (no mouse required)

The desktop app starts a loopback HTTP automation endpoint on boot (default port
5099, `--port N`, `--no-server` to disable). Every operation in the registry is
reachable, and every call is written to the audit trail.

```bash
# What is it, and what can it do?
curl http://127.0.0.1:5099/api/v1/health
curl http://127.0.0.1:5099/api/v1/operations        # JSON catalog
curl http://127.0.0.1:5099/api/v1/operations.txt    # text catalog (for a model)

# Do something (this is preferred over synthetic clicks — see §1.1)
curl -X POST http://127.0.0.1:5099/api/v1/invoke      -H 'Content-Type: application/json'      -d '{"op":"object.create","params":{"type":"ellipse","cx":420,"cy":300,"rx":60,"ry":60,"fillColor":[0,128,0]}}'

# Drive the chrome exactly as a person would
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"ui.find","params":{"type":"MenuItem","text":"Import"}}'
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"ui.click","params":{"type":"MenuItem","text":"_File"}}'
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"ui.keys","params":{"keys":"F12"}}'

# Files without a dialog (the picker cannot be automated)
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"document.openFile","params":{"path":"C:/work/pattern.pdf"}}'

# Viewport: fit / zoom are operations, like the toolbar buttons
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"view.fit","params":{}}'

# Understand the screen with no vision
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"ui.dump","params":{"scope":"all"}}'
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"ui.describe","params":{"target":"layer","layerName":"UK 14"}}'

# Ask the built-in assistant to do the work
curl -X POST http://127.0.0.1:5099/api/v1/chat      -H 'Content-Type: application/json'      -d '{"prompt":"Create a green circle in the middle and check it looks right","withScreenshot":true}'

# The audit trail: who did what, UI, HTTP or assistant
curl 'http://127.0.0.1:5099/api/v1/diagnostics?since=0'

# The diary: search everything that has ever happened, and teach it a skill
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"history.search","params":{"query":"bodice block drafting"}}'
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"history.learn","params":{"title":"US size 10 bodice block","description":"How to lay one out"}}'
curl -X POST http://127.0.0.1:5099/api/v1/invoke -H 'Content-Type: application/json' \
     -d '{"op":"history.sessions","params":{}}'

# Abort the in-flight operation (returns 409 on the request being cancelled)
curl -X POST http://127.0.0.1:5099/api/v1/cancel
```

Start the editor with work already queued, and with the diagnostics window open:

```powershell
src/VCCad.App.Desktop/bin/Release/net8.0/VCCad.App.Desktop.exe `
    --chat "draw a red circle centred on the artboard" `
    --port 5099 --diagnostics
```

**F12** (or F9) toggles the **diagnostics overlay**: a wide, short, semi-transparent
strip across the bottom of the canvas area, so the artwork stays visible while you
watch the model work. Its tabs are **Assistant** (the conversation, including the
screenshots the model was shown), **History** (the live diary — every UI event,
operation and model request, with a search box over all recorded sessions and a
**Learn as skill** button), **Operations** (run any operation by hand — the person's
half of the capability-parity rule), **API calls** (every operation and model request,
with parameters, results and timings) and **Endpoint** (routes and model settings).
`×` hides it.

The window opens **docked to the right half of the primary screen**, because
development happens with the agent harness on the left; `--no-dock` disables that
and `--no-server` disables the HTTP endpoint.

### 4.2b Ports, naming, and crash reporting

**An explicit port is a promise.** `--port N` means port `N` or a refusal — never a
different port. A taken port exits **2** with a message naming it, on both stdout and
stderr. This used to fall back to an ephemeral port silently, which meant a driver that
launched the app and called `5099` reached a *different* instance and could not tell; a
`WinExe` has no console, so even a warning would have gone nowhere (issue #8).

```powershell
# arbitrary port, named so a caller can find it without guessing
VCCad.App.Desktop.exe --name transform-panel
#   → [vccad] automation endpoint listening on http://127.0.0.1:57398
#     (instance 'transform-panel', %APPDATA%\VCCad\instances\transform-panel.json)

# read the file for {name,pid,port,started,args,state}, then talk to that port.
# poll for state:"listening" - the name is claimed before the endpoint exists.
```

A name is **exclusive**: a second instance with the same name exits 2 naming the holder.
A file whose pid is dead is stale and reclaimable; a clean exit removes it. `--port 0` and
`--port-any` also accept any port, and are the only ways to do so. `GET /api/v1/health`
reports the receiver's own port, so a caller can confirm whom it is talking to.

**Crash reporting.** Uncaught exceptions are recorded — never swallowed. The app behaves
exactly as it would have; the difference is that there is now evidence.

| | |
|---|---|
| Hooks | `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `Dispatcher.UIThread.UnhandledException` |
| Files | `%APPDATA%\VCCad\crashes\crash-<stamp>-<id>.json`, one per crash, `Flush(true)`d inside the handler |
| Contents | type/message/stack/inner chain, session id, last 20 operations plus the failing one, document summary, app/runtime/OS/arch/pid/argv |
| Switches | `--dev` / `--no-dev`, `--crash-test`; `VCCAD_DEV`, `VCCAD_CRASH_TEST`, `VCCAD_CRASH_DIR` |

With `--dev` (or `VCCAD_DEV=1`) a crash is also **filed as a GitHub issue** against
`darrenstarr/cavedraw`, via native `gh` or through WSL. Filing is **off by default** — it
is the only path that sends anything off the machine. Before an issue is written the body
is **redacted**: absolute paths reduced to file names, the document name dropped,
`--api-key` and `Bearer` tokens stripped. Repeats are deduplicated on a crash id (SHA-256
of exception type plus top stack frame), which comments on the existing open issue instead
of opening another. Any filing failure keeps the local file.

`--crash-test` triggers the pipeline deliberately, so it can be exercised without waiting
for a real fault.

Two honest limits: filing is synchronous inside the handler, so a crash can take up to 60 s
to die in dev mode; and dedup reads the first 100 open issues rather than searching, which
would need GitHub search if the backlog ever passed that.

The view **auto-fits** the artboard on every resize until the person zooms manually,
on every new/imported/activated document, and the fit reserves the diagnostics
overlay's height — so a fitted artboard is never hidden behind the panel and an
imported page is never shown at the previous document's scale. Screenshots taken by
automation therefore show a sensibly-sized artboard from the first frame.

The assistant talks to `https://your-endpoint.example/v1` with model
`qwen3.8-27b` by default; override with `--model/--llm-url/--api-key` or
`VCCAD_LLM_MODEL/VCCAD_LLM_BASE/VCCAD_LLM_KEY`.

**Model work stays inside its context window.** Observations handed back to the model are
truncated (3 kB), `object.list` is paged (200 rows by default, with `count`/`truncated` and a
hint to use `object.find`), and the conversation is trimmed at user-turn boundaries once it
passes ~48 kB — never losing the system prompt, the original task or a tool result's parent.
Without this a corpus-sized pattern file (2,768 objects) overflowed the endpoint's 94 k-token
context on the first listing.

**Model work never blocks the interface.** Slow operations (`ui.describe`, and
every model call the assistant makes) run off the UI thread; only short,
synchronous document operations are executed there. While a turn is running the
canvas is disabled — the model and the person must not edit at once — but the
diagnostics overlay stays live: the transcript updates per step, the status line
names the operation in flight, and **Cancel** aborts the turn and re-enables the
editor. Cancellation is reported as cancellation (HTTP 409, `"cancelled": true`),
never as an endpoint error. Automated drivers can do the same through
`POST /api/v1/cancel`.

### 4.3 Agent tooling for this checkout

See §10 for the file-staging helpers used when the repository is edited through a
Windows/WSL boundary.


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
src/VCCad.App.Desktop  net8.0 native Windows/Linux host for the same editor shell
tests/*                xUnit suites (see table above)
tools/qwen-corpus-tracker  qwen-based render-fidelity progress tracker
tools/ai-private-data      independent reference decoder + golden manifest for .ai private data
docs/                      format/design notes (ai-private-data.md)
docker/Dockerfile      multi-stage: restore → build → test → publish api + wasm
scripts/               dev.sh, deploy-remote.sh, bootstrap-dev.sh, fetch-corpora.sh,
                       publish-desktop.sh, publish-desktop.ps1 (Windows-native),
                       test-all.ps1 (Windows-native), stage-apply.sh, replace-text.py
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

### 6.2b Corpus-driven feature testing (Ghostscript / pdf.js / .ai)

`tests/VCCad.Pdf.Tests/Corpus/` classifies every PDF in a corpus with a
byte/object-level **feature probe** (font kinds, path/clip operators, ExtGState,
soft masks and blend modes, patterns and shadings, image filters, optional-content
layers, annotations, XMP, encryption, PDF/A markers, Illustrator `/PieceInfo`), then
guards today's capability with aggregate coverage floors so drift shows up as one
failure with a summary. The Ghostscript/MuPDF public test files
([`ArtifexSoftware/tests`](https://github.com/ArtifexSoftware/tests), 208 PDFs
including the Ghent PDF Output Suite) are the primary corpus: they are all valid,
well-formed documents and therefore the strongest "must import" set.

The Illustrator private-data work has its own corpus (66 real `.ai` files from
Inkscape's `extension-ai`) and an **independent oracle**:
`tools/ai-private-data/decode-ai-private-data.py` plus a golden manifest of the
expected format, decompressed size and SHA-256 for every fixture. Use it to check
the C# decoder against a second implementation, not only against itself:

```bash
python3 tools/ai-private-data/decode-ai-private-data.py check \
    $HOME/.cache/vccad-corpora/ai tools/ai-private-data/fixture-manifest.json
# checked 66 fixtures: 0 mismatches
```

Format notes: [`docs/ai-private-data.md`](docs/ai-private-data.md).

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

- **Automation**: the desktop app serves a loopback HTTP endpoint exposing every
  operation (`/api/v1/health|operations|invoke|chat|diagnostics|screenshot`), and the
  in-app assistant drives the same registry by tool calling.
- **Capability parity**: `ui.dump` and `ui.describe` make the application legible to a
  driver with no vision; the diagnostics overlay's Operations tab lets a person run any
  operation the assistant can.

- **3,700 tests green** over the veraPDF + Ghostscript/MuPDF corpora (424 of them
  PDF feature-probe corpus tests); PDF/A-2b validated by veraPDF.
- **Fonts are never bundled.** A document's own embedded programmes are used verbatim;
  anything it does not embed is supplied from the machine. A PDF is entitled to name
  `Helvetica` and embed nothing at all, expecting the viewer to provide it, and there
  are exactly **fourteen** such names (ISO 32000-1 §9.6.2.2): Helvetica, Times and
  Courier in four styles each, plus Symbol and ZapfDingbats. `StandardFonts` classifies
  a name by its family word, which covers the whole alias space (`ArialMT`,
  `Helvetica-BoldOblique`, `TimesNewRomanPSMT`, `CourierStd`…).

  The supply, in the order every viewer uses:
  1. **URW Core 35** from the machine (`StandardFontFiles`) — the faces Ghostscript and
     Inkscape use, with the original metrics. AGPL-3, so they are located and used, never
     redistributed with this MIT project. Their font exception explicitly permits
     embedding them in an exported PDF, which is why export embeds these.
  2. **A metric-compatible platform clone**: Arial, Times New Roman, Courier New — the
     same substitution Chrome's PDF engine makes, and why a Windows machine with no
     Ghostscript still renders such a document correctly.
  3. If neither exists, the run is reported as **missing** (`fonts.list`, and the status
     bar), and `fonts.installStandard` downloads the URW faces into the user's own font
     directory — installing for the user, which is permitted, rather than shipping them.

  `scripts/test-all.ps1` locates a URW directory and exports `VCCAD_URW_FONTS`; export
  tests that need a programme to embed skip cleanly when none is installed.
- **Illustrator private data** is decoded (AI8 PostScript, AI9-CS zlib, CC-Legacy
  zlib, AI24 zstd and uncompressed containers), preserved losslessly on the
  document model, and re-emitted into exported PDFs. 66 real `.ai` fixtures are
  swept by `AiPrivateDataCorpusTests`; the decoder is cross-checked against
  `tools/ai-private-data/` and its golden manifest.
- **Desktop targets**: `VCCad.App.Desktop` hosts the same shell on Windows and
  Linux; `scripts/publish-desktop.sh` (Linux/WSL, cross-publish) and
  `scripts/publish-desktop.ps1` (Windows-native) produce self-contained bundles.
- Corpus sweeps: `scripts/fetch-corpora.sh` + the PDF **feature probe** in
  `tests/VCCad.Pdf.Tests/Corpus/` report a measured feature inventory and fail if
  any feature class stops being covered.
- **Text is decoded through the font's `/Encoding`.** A content stream holds codes,
  not characters. When a font has no `/ToUnicode` CMap, the codes must be read
  through its encoding (`PdfTextEncoding`: WinAnsi, MacRoman, plus `/Differences`
  glyph names). Reading them as Latin-1 is right for ASCII and wrong for everything
  else: WinAnsi 0x94 is a right double quotation mark — the inches mark on a pattern —
  and Latin-1 makes it U+0094, a control character with no glyph in any font. That is
  what turned inches into empty boxes and dropped other characters entirely.
- **A `TJ` array's numeric adjustments are position, not noise.** They are thousandths
  of an em between the strings around them, and they carry real layout: the page-number
  table on a sewing pattern is `[(1)-1130(2)-1118(3)]TJ`, where `-1130` means "move
  1.13 em before the next digit". Concatenating the strings and dropping the numbers
  collapses that to `123` bunched at the origin — the digits land in the wrong cell and
  an extractor reads one word where the file has three. The importer therefore splits a
  `TJ` array into separately-positioned pieces, advancing the pen by each piece's glyph
  advances *and* its adjustment, and composing the running offset into the text matrix.
  Adjustments below a quarter of an em are kerning and are deliberately folded away:
  splitting on those would turn a letter-spaced line into one object per glyph.
  `tests/VCCad.Pdf.Tests/TjPositioningTests.cs` pins both halves of that.
- **Illustrator writes `1 Tf` and carries the real size in `Tm`.** Offsets inside a
  `TJ` array are in text space, so they are measured with the declared `Tf` size — using
  the matrix scale as well multiplies the layout by the font size twice.
- **The standard-font programme embedded on export must be TrueType (`glyf`).** An
  exported PDF puts a programme in `/FontFile2` with a `/CIDFontType2` descendant, which
  requires glyf outlines. The URW project ships each face as both `.otf` (CFF) and
  `.ttf` (glyf); embedding the CFF build there is a font/type mismatch that strict
  readers report as *"Mismatch between font type and embedded font file"*, so the
  `.ttf` build is preferred and is what `fonts.installStandard` fetches.
- **Every run records the font the file asked for** (`TextRun.SourceFont`), separately
  from the family used to render it. Without it a substitution cannot be reported in
  terms the person recognises.
- **A font that cannot be supplied is reported, never silent.** `fonts.list` says how
  every font in a document is actually drawn — "embedded programme", "URW Nimbus Sans
  (installed on this machine)", "metric-compatible Arial (installed on this machine)", or
  "unavailable". Only a genuine gap reaches the status bar, because a PDF that merely
  declines to embed Helvetica is not a problem. A font that *is* embedded but fails to
  resolve is reported as a **defect**: the file carries what we need, and being told is
  the point.
- **Clipping is a rendering concern, not an import-time rewrite.** A tiled PDF — a
  pattern paginated across A4 sheets — draws each piece at full size on every sheet it
  touches and lets the page edge do the cutting. The importer therefore stores that
  geometry **exactly as authored**, overflow included, and the view clips each artboard
  to its page box (`CanvasWorkspace.ClipToArtboard`, on by default, toggleable with the
  `view.clipToArtboard` operation). A PDF viewer behaves the same way at the MediaBox,
  so an exported page is visually identical.

  This was learned the hard way. Cutting the geometry and trimming text runs to fit was
  tried and it butchered the labels: removing characters invalidates the embedded-font
  glyph mapping, and a per-character advance estimate cuts in the wrong place. Do not
  rewrite content to make it fit. `tests/VCCad.Pdf.Tests/SamplePatternTests.cs` now pins
  the opposite invariant — every run keeps its full string, its raw codes and a glyph
  id per character, and path curves stay curved.

  Fit and the pasteboard extent follow the same rule: with clipping on, the extent is
  the union of the page boxes, so a tiled document still fits the window readably.
- **Multi-page documents are laid out as a grid**, not one row: page columns/rows are
  chosen so the sheet's shape is close to a working area. A row of eight A4 pages is a
  4837x814 pt strip that fits at ~13% zoom and is unreadable; the 4x2 grid fits at ~37%.
- Known import gaps toward "100%": **Type3 fonts**, fonts with **no embedded
  programme**, and text outside page content (~54 corpus files lose some words).
  Non-text gaps: tiling/shading **patterns**, **images**, **transparency groups /
  soft masks / blend modes**.
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
| Headless XAML load fails with "No precompiled XAML found for VCCad.App.App" after a crashed build (`MSB4166`) | a killed MSBuild child left a *corrupted incremental* `VCCad.App.dll` missing its compiled-XAML IL. Rebuild that project: `dotnet build src/VCCad.App/VCCad.App.csproj -c Release -t:Rebuild` |
| Editing the checkout from Windows fails with `ENOTSUP`/`EIO` | the repo lives on the WSL ext4 filesystem over 9p, which has no hardlink+rename — VS Code / VS over `\\wsl.localhost\...` are fine, but simple atomic-replace writers are not. `scripts/stage-apply.sh <relpath>` installs a file staged under the Windows temp dir, and `scripts/replace-text.py` performs exact-substring patches |