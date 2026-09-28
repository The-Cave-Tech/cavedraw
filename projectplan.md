# VCCad — Project Plan

> An Adobe-Illustrator-like vector graphics editor.
> Web-hosted (Avalonia UI compiled to WebAssembly), C# / .NET 8, fully automatable
> through a scriptable API, storing documents losslessly as PDF.
>
> Status: **Accuracy / import-fidelity pass** — tests green, exports validate as
> PDF/A-2b (veraPDF), embedded fonts preserved and rendered without substitution.
> The corpus harness now sweeps the Ghostscript/MuPDF public test files (208 PDFs,
> incl. the Ghent PDF Output Suite), the pdf.js corpus and the veraPDF corpus, and
> Illustrator **private data** is parsed *and* re-emitted (66 real `.ai` fixtures).
> The editor now also ships as a native **Windows/Linux** desktop application.
> Plan revision: 0.3 · **See [`AGENTS.md`](AGENTS.md) for environment + commands.**

---

## 1. Vision

Build a vector graphics editor whose core interactions, object model, and tooling
track Adobe Illustrator as closely as the modern **Illustrator CC** surface area,
with three deliberate differences:

1. **Web-first, not web-only.** The editor UI is one Avalonia application: compiled
   to WebAssembly and served from a container so it runs in an ordinary browser, and
   built natively for Windows and Linux (with macOS nearly free) for users who want
   local rendering performance and native file dialogs.
2. **Headless-first automation.** *Every* user operation is available through a
   documented, deterministic automation API (JSON-RPC 2.0 over WebSocket, plus a
   REST surface for document lifecycle), enabling scripting, tests, and CI-driven
   asset pipelines.
3. **Lossless PDF document model.** Files open/save as PDF; a lossless embedded
   sidecar makes round-trips bit-exact.

### Product non-negotiables (from the brief)

| # | Requirement | Where it lands |
|---|-------------|----------------|
| 1 | Scrollable workspace, at least **one full screen past** the bounds of all objects in every direction (Illustrator "pasteboard" behavior) | `WorkspaceCanvas` panning model |
| 2 | Objects composed of **open and closed paths** | `SubPath` + `PathNode` |
| 3 | Paths composed of **line segments and cubic Bézier curves** | `PathNode` handle model → `CubicBezier` |
| 4 | **Artboard** document structure, default **A4 landscape** | `Artboard` (default `PageSizes.A4Landscape`) |
| 5 | **Layers** beneath artboards | `Artboard.Layers` |
| 6 | Objects may belong to **groups** | `ArtGroup` |
| 7 | Paths have **stroke width + stroke color**; closed paths **filled with fill colors** | `StrokeSpec` / `FillSpec` |
| 8 | Stored **losslessly in PDF** | `VCCad.Pdf` + embedded lossless sidecar |
| 9 | **100% automation** via an API | `VCCad.Api` (REST + WS JSON-RPC) |
| 10 | **Web based** but Avalonia windowing | Avalonia → WebAssembly |
| 11 | Windows-style **menus and toolbars** | `VCCad.App` shell |
| 12 | **Configurable docking panes** around the workspace | Dock integration task |
| 13 | **CI/CD** and **git** from the start | GitHub Actions + this repo |
| 14 | **Thoroughly commented** code, esp. math | Code review gate |
| 15 | **Unit + integration tests** minimum | M1+ gates, coverage budget |
| 16 | **Every human action is an operation**, reachable from the API and the chatbot alike, and runnable by hand from the diagnostics window | `EditorOperations` registry (ADR-20) |
| 17 | **The built-in assistant can do anything the user can, and vice versa** | Shared registry + diagnostics Operations tab |
| 18 | **A driver with no vision can still work**: full UI/document text dump and a model-assisted "what do you see" | `ui.dump`, `ui.describe` (ADR-22) |
| 19 | **API-first automation**, point-and-click as a fallback | ADR-21 |

---

## 2. Scope and non-goals

### In scope (v1)
- Document object model: document → artboards → layers → groups → path items.
- Node/handle-based path editing (anchor + in/out handles) with lines & cubics.
- Live fill/stroke styling, standard 2D affine transforms on groups and artboards.
- Selection, direct manipulation, hit-testing with hover/tolerance.
- Rendering to the browser canvas (SkiaSharp) and to PDF.
- Full undo/redo command stack, serializable command stream.
- Automation: REST (lifecycle + PDF) and JSON-RPC over WebSocket (everything).
- PDF export with lossless embedded sidecar; sidecar re-import.

### Explicitly deferred (v2+ / non-goals)
- Raster (bitmap) image placement, filters/effects, transparency blend modes
  beyond simple opacity, gradients (stretch), mesh/pattern, symbol libraries.
- Full type/text engine (shaping, typesetting, glyph outlines) — rich-text runs
  exist and PDF text is imported/exported with embedded-font pass-through; full
  shaping and layout are deferred.
- Live trace, image-swatches, perspective, Puppet Warp, and similar AI power tools.
- Concurrent multi-user editing.
- *(Moved into scope — see ADR-06/ADR-15.)* Native desktop packaging for
  Windows/Linux now ships from the same editor code; macOS packaging remains a
  small follow-up.
- Full Adobe private-data coverage: we parse the private data losslessly and
  re-emit it, but only the subset the document model understands is *interpreted*.
  Gradients, brushes, live shapes, symbols and text-on-path stay opaque-but-preserved
  until their model lands.

---

## 3. Product spec snapshot

### 3.1 Document hierarchy (mirrors the brief)
```
CadDocument
└── Artboard []                     default: A4 landscape (841.89 × 595.28 pt)
    ├── Layer []                    (z-order: index 0 = bottom)
    │   ├── ArtGroup  (AffineTransform)
    │   │   └── LayerItem []        (groups or path items, recursive)
    │   └── PathItem  (FillSpec + StrokeSpec)
    │       └── SubPath []          open or closed
    │           └── PathNode []     Anchor + InHandle + OutHandle
    │                               → segments: line (degenerate cubic) or cubic
```

### 3.2 Geometry identity
- All coordinates are stored in **PDF points (1/72 inch)**.
- Model space is **left-handed: origin top-left, +Y down** (natural for screen
  editing). PDF export applies a vertical flip per artboard into PDF user space.
- Handles are **absolute** points, not deltas: straight segments collapse to a
  cubic where P1≡P0 and P2≡P3. This makes every segment a single uniform type.

### 3.3 Lossless PDF
The PDF produced by VCCad is:
- A faithful, standards-compliant PDF 1.7 that renders identically in any viewer
  (each artboard → a MediaBox page; each path → `m/l/c` operators; stroke/fill
  with color + width + cap/join).
- Carries the full VCCad document (sidecar `vccad-document` JSON, FlateDecode
  compressed) as a **plain catalog stream** (`/VCCadDocument`) — not an
  `/EmbeddedFiles` attachment, which PDF/A forbids for non-PDF/A payloads.
  Opening a VCCad PDF in VCCad restores the exact model, including data PDF
  cannot natively express (node handles on "line" segments, styles, text runs,
  embedded font programmes). Opening in a plain viewer still shows the correct
  artwork, and the file validates as PDF/A-2b.

### 3.4 Workspace / panning
The pasteboard behaves like Illustrator: you may scroll until the union of all
object bounds has travelled a **full viewport** beyond the opposite edge of the
canvas in all four directions. Implementation: virtual content size =
`unionBounds ∪ artboards` inflated by one viewport on each side (and never smaller
than the viewport), which the `ScrollViewer` exposes as scroll extents.

### 3.5 Automation model
- **REST** (`/api/v1/...`): document lifecycle, PDF produce/consume, health.
- **JSON-RPC 2.0 over WebSocket** (`/ws/rpc`): everything else, including the
  exact same commands the UI issues. A command stream is just JSON; replaying it
  reproduces a document — the same format used by the integration tests.
- A connection-scoped **undo stack** makes each automation session deterministic.

---

## 4. Architecture overview

```
┌──────────── Browser ─────────────┐      ┌─────────── Docker container ───────────┐
│  VCCad.App  (Avalonia → WASM)    │      │  VCCad.Api  (ASP.NET Core / Kestrel)  │
│  · Windows-style menus/toolbars  │ ◄──► │  · REST /api/v1/*                     │
│  · Dockable panes (M5)           │  WS  │  · JSON-RPC /ws/rpc  (command bus)    │
│  · WorkspaceCanvas (pan/zoom)    │ JSON │  · Static files: publish/wwwroot      │
│  · Skia rendering surface        │─RPC─►│     (serves the WASM app)             │
└──────────────────────────────────┘      └───────────────────────────────────────┘
          │ JSON-RPC commands                     │ commands → undo stack
          ▼                                       ▼
    ┌────────────────────────────────  VCCad.Core  ────────────────────────────────┐
    │  CadDocument · Artboard · Layer · ArtGroup · PathItem · SubPath · PathNode   │
    │  FillSpec / StrokeSpec · Commands + IUndoableCommand + CommandStack          │
    └──────┬──────────────────────────────┬────────────────────────────────────────┘
           │                              │
    VCCad.Geometry (pure math)     VCCad.Pdf  (writer/reader, lossless sidecar)
    Point2D/Vector2D/Rect2D        └─ embedded `vccad-document` JSON  ⇄  sidecar
    AffineTransform · CubicBezier         serializer lives in VCCad.Core
    flatten/intersect/bounds
```

Assembly dependency rule: `Geometry ← Core ← {Pdf, Api, App}`. Geometry never
depends on Avalonia, ASP.NET, or the UI; everything testable headless.

---

## 5. Key architectural decisions

| ID | Decision | Rationale | Revisit when |
|----|----------|-----------|--------------|
| ADR-01 | Node/handle path model (absolute handles) | Matches Illustrator editing semantics; uniform cubic segments | Gradient mesh import (v2) |
| ADR-02 | Left-handed model space, flip on PDF export | Screen-editing natural; flip isolated in Pdf layer | Headless render server |
| ADR-03 | Lossless sidecar inside PDF (not pure PDF) | Pure PDF cannot round-trip editing semantics | If a viewer-required strict subset appears |
| ADR-04 | Commands are first-class, serializable, shared by UI + API | Single code path → automation = UI | Live-collab sessions |
| ADR-05 | REST for lifecycle, WS JSON-RPC for interactions | Matches request; JSON-RPC is simple to script | gRPC perf need |
| ADR-06 | Avalonia → WebAssembly for v1, **plus native desktop hosts** (superseded by ADR-15) | Brief demands web + Avalonia windowing; desktop adds local perf and native dialogs | — |
| ADR-07 | Third-party **AvaloniaDock** (Dock) for docking panes | Hand-rolling docking is large; Dock is the OSS standard port | If Dock proves unstable on WASM → in-house `DockHost` |
| ADR-08 | SkiaSharp rendering of the design surface | Avalonia's renderer is Skia; direct `DrawingContext` keeps it fast & testable | GPU filter chains |
| ADR-09 | z-order index at layer/group level; sort stable | Simple, matches Illustrator layering semantics | Multi-select drag reorder perf |
| ADR-10 | Everything in PDF points internally | Lossless PDF math; no unit drift | User-preference unit systems (M7) |
| ADR-11 | Imported embedded fonts are **pass-through**, never substituted | Fidelity with other compliant renderers; honours the source programme | When a font engine is embedded |
| ADR-12 | PDF export targets **PDF/A-2b** | Measurable accuracy via the real veraPDF engine | If a strict PDF 1.7-only mode is needed |
| ADR-13 | Lossless sidecar stored as a **catalog stream** (`/VCCadDocument`) | PDF/A forbids arbitrary `/EmbeddedFiles` payloads | — |
| ADR-14 | Accuracy is measured against the **veraPDF corpus** + qwen render scoring | Objective, regression-guarded import fidelity | — |
| ADR-15 | One editor shell, three hosts: `VCCad.App.Browser` (WASM) and `VCCad.App.Desktop` (Windows/Linux; macOS capable via `Avalonia.Native` but not published) both boot the same `VCCad.App` application | Avoids a second UI; desktop is a host, not a fork | A platform-specific feature forces a split |
| ADR-16 | Adobe **Illustrator private data is parsed losslessly and re-emitted on export**; uninterpreted constructs are preserved verbatim | PDF page content is a flattened projection — styling, layers and text structure only exist in the private stream. Preserving beats losing, and keeps import/export in sync while the model grows | A feature is modelled natively end-to-end |
| ADR-17 | Reference corpora are **fetched, never vendored** (`scripts/fetch-corpora.sh`), and every corpus test skips cleanly when its corpus is absent | The Ghostscript/MuPDF, Inkscape `extension-ai` and veraPDF corpora are AGPL/GPL/CC — incompatible with our MIT repo — and CI must stay green offline | A permissively licensed corpus is adopted |
| ADR-18 | The `.ai` decoder is validated against an **independent reference implementation** + golden manifest (`tools/ai-private-data/`) | A decoder compared only with itself cannot reveal a systematic misreading of an undocumented format | Adobe publishes a specification |
| ADR-19 | **Test-first development with corpus-backed sinks**; failing-on-improvement tests record known gaps until they are closed | The document model must track Adobe's; a test that describes the gap is the cheapest way to keep it visible and to know when it closes | — |
| ADR-20 | **One operation registry is the single source of truth**; UI, HTTP API and chatbot all execute through it, and the person can run any operation from the diagnostics window | Guarantees user/assistant capability parity in both directions, and makes "everything is scriptable" structurally true rather than aspirational | — |
| ADR-21 | **API-first automation**; synthetic pointer/keyboard automation is supported but only used when the gesture itself is the thing under test | Operations are deterministic, typed, logged and replayable; clicks are not | — |
| ADR-22 | **No-vision introspection**: `ui.dump` (full Avalonia + document tree as text) and `ui.describe` (model-assisted description of the window, a named layer, the selection or a region) | Development tools frequently cannot see; the app must be understandable from text and, when needed, describable by the vision model | — |

---

## 6. Repository, git workflow, CI/CD

- Trunk-based development on `main` with short-lived feature branches
  (`feat/<milestone>-<slug>`); CI must be green before merge.
- Conventional commits: `feat:`, `fix:`, `test:`, `docs:`, `chore:`, `ci:`.
- Release flow: tags `v0.x.y` → release build → docker image
  `cave.registry/vccad:<version>` pushed and deployed to the docker host.
- **CI/CD (GitHub Actions)** — `.github/workflows/`:
  - `ci.yml`: on PR + push to `main`. Jobs: build, unit test, integration test,
    publish WASM, lint/format check. Caches NuGet.
  - `docker.yml`: on tag `v*`. Builds the multi-stage image, runs tests inside
    the container, pushes to the registry, then SSH-deploys to the docker host
    (secret `DOCKER_HOST_USER/KEY`).

### Repository layout
```
/                       .gitignore · Directory.Build.props · LICENSE · README.md · projectplan.md · AGENTS.md
/.github/workflows/    ci.yml · docker.yml
/docker/               Dockerfile · compose.yaml
/scripts/              dev.sh · deploy-remote.sh · bootstrap-dev.sh
/tools/qwen-corpus-tracker/  vision-model render-fidelity tracker
/tools/ai-private-data/      independent .ai private-data decoder + golden manifest
/docs/                       format/design notes (ai-private-data.md)
/samples/              real-world Illustrator PDF fixture
/src/
  VCCad.Geometry/      pure math primitives + Bézier algebra
  VCCad.Core/          document model, commands/undo, sidecar serializer
  VCCad.Pdf/           PDF writer/reader + lossless embedding + font pass-through
  VCCad.Api/           ASP.NET host (REST + WS JSON-RPC + static WASM)
  VCCad.App/           Avalonia WebAssembly editor shell
  VCCad.App.Browser/   net8.0-browser WASM host
  VCCad.App.Desktop/   net8.0 native Windows/Linux host (same shell as the browser)
/tests/
  VCCad.Geometry.Tests/    unit
  VCCad.Core.Tests/        unit
  VCCad.Pdf.Tests/         unit + round-trip + corpus sweep + PDF/A
  VCCad.Api.Integration.Tests/  integration (WebApplicationFactory + real WS)
  VCCad.App.Tests/         Avalonia headless (embedded fonts / glyph ids)
/docs/                 ADRs, tool-spec, API reference (grows over time)
```

---

## 7. Milestones, sprints, tasks

Sprint = 2 weeks. `[x]` marks items already scaffolded at kickoff.
Effort in relative story points (S/M/L/XL ≈ 1/2/4/8). **MoSCoW** = Must/Should/Could.

### M0 — Foundations & pipeline  `(done at kickoff)`
Ensure the skeleton is trustworthy before feature work.

| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | `[x]` Repo init: git, .gitignore, LICENSE (MIT), README | 1 | M |
| 2 | `[x]` Solution skeleton: Geometry/Core/Pdf/Api/App/test projects all compile | 3 | M |
| 3 | `[x]` projectplan.md (this document) | 2 | M |
| 4 | `[x]` CI `ci.yml` (build+test+publish WASM) | 3 | M |
| 5 | `[x]` Multi-stage `Dockerfile` + compose | 2 | M |
| 6 | `[x]` Docker build + run verified on docker host `user@host` | 1 | M |
| 7 | `[ ]` CI badge + local `./scripts/dev.sh` (build/test/publish/run) | 2 | S |
| 8 | `[ ]` Branch protection + conventional-commit lint on `main` | 1 | S |

**Accept:** CI green on first push; `docker run` serves the app; README quickstart works.

### M1 — Geometry kernel  `(foundation scaffolded)`
Pure math, 100% commented, exhaustively unit-tested. Everything above it is dumb
geometry.

| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | `[x]` `Point2D`/`Vector2D`/`Rect2D`/`Size2D` (operators, predicates, epsilon) | 3 | M |
| 2 | `[x]` `AffineTransform`: translate/scale/rotate/skew, compose, invert, rect/image mapping | 3 | M |
| 3 | `[x]` `CubicBezier`: point/tangent/curvature, split, adaptive flatten, arc-length approx | 5 | M |
| 4 | `[x]` Tight bounds via derivative extrema (`CubicRoots`) | 3 | M |
| 5 | `[ ]` Intersections: line-line, line-cubic, cubic-cubic (Bézier clipping) | 5 | M |
| 6 | `[ ]` Point-on-path nearest point (Newton refine), distance-to-segment | 3 | M |
| 7 | `[ ]` Polygon/`ShapeMath`: area, winding, even-odd test, sweep-line boolean ops | 8 | S (M6 enabler) |
| 8 | `[ ]` Fat-line / curve offset & outline generation (parallel curves) | 5 | C (M8) |
| 9 | `[ ]` Property tests (FsCheck) + randomized oracle vs brute force | 3 | S |

**Accept:** >95% line coverage on Geometry; every public method commented with
formulae; randomized tests stable.

### M2 — Document model  `(foundation scaffolded)`
Object graph + styling + bounds/invariants.

| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | `[x]` CadDocument/Artboard/Layer/ArtGroup/PathItem/SubPath/PathNode | 5 | M |
| 2 | `[x]` FillSpec/StrokeSpec + stroke caps/joins/dash (dash as stretch item) | 2 | M |
| 3 | `[x]` Node→segment model: line = degenerate cubic; segment iteration; open/closed | 3 | M |
| 4 | `[x]` Bounds: item/group(with transform)/layer/artboard/document union | 3 | M |
| 5 | `[x]` Change-notification bus (edit events, minimal) | 2 | M |
| 6 | `[ ]` Deep-clone + structural equality for model (test/compare support) | 3 | M |
| 7 | `[ ]` Invariant/validation rules: duplicate ids, orphan layers, empty geometry | 2 | M |
| 8 | `[ ]` Group transform composition & world↔local point mapping | 3 | M |
| 9 | `[ ]` Document templates & defaults (A4 landscape doc factory, artboard presets) | 2 | S |
| 10 | `[ ]` Z-order operations (bring-forward/send-backward APIs) | 2 | S |

**Accept:** model passes property invariants; clone==original by equality; every
subpath yields correct segment count & reversal symmetry.

### M3 — Command bus, undo, automation API  `(Api scaffolded)`
Make the document programmable. UI and scripts converge on one command language.

| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | `[x]` `IUndoableCommand` + `CommandStack` (limit, merge policy, dirty flag) | 3 | M |
| 2 | `[x]` Seed commands: add/remove artboard/layer/item, set fill/stroke, transform | 3 | M |
| 3 | `[ ]` Full command catalog (M2-adjacent ops) w/ serialization + validation | 8 | M |
| 4 | `[ ]` REST lifecycle endpoints hardened (ids, ETags, 4xx contracts) | 3 | M |
| 5 | `[x]` JSON-RPC 2.0 dispatcher (methods, error objects, batching later) | 3 | M |
| 6 | `[x]` WS endpoint wired to dispatcher | 2 | M |
| 7 | `[ ]` Change-events broadcast (notifications) to subscribers + UI | 3 | S |
| 8 | `[ ]` API OpenAPI/Swagger + typed client generation + API tests green | 3 | S |
| 9 | `[ ]` AuthN/Z story: static API key / bearer for headless use | 2 | S |
| 10 | `[ ]` Idempotency tokens for replay-safe automation | 2 | C |

**Accept:** a JSON-RPC session can build the M2 reference document end-to-end and
`undo` it deterministically; integration tests pass under `dotnet test`.

### M4 — Lossless PDF  `(writer scaffolded)`
| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | `[x]` PDF writer core: objects, xref, trailer, FlateDecode streams | 3 | M |
| 2 | `[x]` Page-per-artboard export; y-flip into PDF user space | 2 | M |
| 3 | `[x]` Path→content stream (`m/l/c`), fill/stroke, width, cap/join, color spaces | 3 | M |
| 4 | `[x]` Embedded sidecar (Filespec + EmbeddedFiles + Names tree) | 2 | M |
| 5 | `[x]` Minimal reader: xref parse + sidecar extraction (round-trip test) | 3 | M |
| 6 | `[x]` Reader: full vector content → model (paths, styles, text, embedded fonts) | 8 | S |
| 7 | `[ ]` Reader: preserve unknown constructs in sidecar cache (import-in-place) | 3 | S |
| 8 | `[x]` PDF validation harness: veraPDF (PDF/A-2b) + poppler/mupdf/ghostscript render diff | 3 | M |
| 9 | `[x]` Fonts/text export **and import**: embedded-font pass-through (simple + Type0) | 5 | M |
| 10 | `[ ]` XObject groups & dash pattern, transparency group nesting | 3 | S |
| 11 | `[x]` **Illustrator private-data codec**: locate `/PieceInfo` blocks, AI9-CS zlib / CS2-CC zlib / AI24 zstd / plain / legacy-PostScript decode | 5 | M |
| 12 | `[x]` **Private-data parser + writer**: lossless tokenizer, structured header/layer view, byte-exact re-emission | 5 | M |
| 13 | `[x]` **Private-data round-trip in the model**: captured on import, carried by the sidecar, re-emitted into page `/PieceInfo` on export | 3 | M |
| 14 | `[ ]` Interpret private data into native model objects (gradients, appearance stacks, live shapes, text-on-path) | 13 | S |

**Accept:** round-trip `doc → pdf → sidecar → doc` is structurally equal for the
M2 reference document; exported PDF renders identically in reference viewers.

### M4b — Automation, assistant and diagnostics  `(done)`

| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | `[x]` Operation registry covering the human surface (selection, objects, transforms, styles, text, layers, artboards, paths, z-order) | 8 | M |
| 2 | `[x]` Loopback HTTP automation endpoint (`invoke`, `chat`, `diagnostics`, `operations`, `screenshot`) | 5 | M |
| 3 | `[x]` In-app assistant: tool-calling loop over the registry, vision on request | 8 | M |
| 4 | `[x]` Diagnostics **overlay** (semi-transparent strip across the bottom of the canvas): assistant, operations runner, API call log, endpoint info; window docks to the right half of the screen | 5 | M |
| 5 | `[x]` No-vision introspection: `ui.dump` (Avalonia + document tree as text) and `ui.describe` (model-assisted, per layer/selection/region) | 5 | M |
| 6 | `[ ]` Point-and-click automation surface (synthetic pointer/keyboard) for gesture-level testing | 5 | S |
| 7 | `[ ]` Operation catalog published for the WASM/web build and the JSON-RPC transport | 3 | S |

**Accept:** every human action has an operation; the assistant completes a multi-step
task through the same registry; a blind driver can dump and describe the screen.

### M5 — Application shell & workspace  `(shell scaffolded)`
Avalonia WASM editor chrome around the workspace.

| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | `[x]` Avalonia browser project boots from API container | 2 | M |
| 2 | `[x]` MainWindow: MenuBar + ToolBar + StatusBar (Windows style) | 3 | M |
| 3 | `[x]` `WorkspaceCanvas`: virtual pasteboard sized bounds+viewport all sides | 3 | M |
| 4 | `[x]` Pan/scroll + zoom (ctrl+wheel, %, fit) | 3 | M |
| 5 | `[x]` Placeholder dockable panes: Layers/Color/Properties/Appearance | 2 | M |
| 6 | `[ ]` AvaloniaDock integration → drag/resize/persist pane layouts | 5 | M |
| 7 | `[ ]` Docking persistence (JSON layout) + View menu presets | 2 | S |
| 8 | `[ ]` Document open/save dialogs + PDF save, template New | 2 | M |
| 9 | `[ ]` UI ↔ Core bridge: dispatcher client, change-event → panel refresh | 4 | M |
| 10 | `[ ]` Keyboard/shortcut map (AI-like: V/A/P/B/E/G/…) | 3 | S |

**Accept:** full chrome renders in browser; panning reaches one full viewport
beyond all objects in every direction; layout survives reload.

### M6 — Selection, tools, direct manipulation
The "feels like Illustrator" milestone.

| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | Hit-testing service (path stroke + fill, tolerance, groups, topmost-first) | 5 | M |
| 2 | Marquee + shift toggle selection; selection transforms (bounding-box handles) | 5 | M |
| 3 | Direct-select: node + handle drag (corner/smooth toggling), live curve editing | 8 | M |
| 4 | Pen tool: add/move/convert anchor & handles; closed/open toggling | 8 | M |
| 5 | Shape tools: rect/ellipse/polygon/star/line → path converters | 3 | M |
| 6 | Free transform tool (scale/rotate/shear/reflect UI) | 4 | S |
| 7 | Geometry snapping: grid, guides, smart (node/center/tangent), rulers | 5 | S |
| 8 | Appearance panel (fill/stroke editing live) + Eyedropper-ish copying | 4 | S |
| 9 | Layers panel: create/reorder/rename/lock/hide/group/select | 5 | M |
| 10 | Undo/redo UI + shortcuts driving the M3 bus | 2 | M |

**Accept:** golden interaction tests (headless RPC replay) can re-create a
multi-node logo from raw mouse-stream scripts; see section 9.

### M7 — Professional toolkit round #1
| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | Align & distribute, Artboard tool (multi artboard, reorder, preset sizes) | 4 | S |
| 2 | Pathfinder: unite/intersect/subtract/exclude (M1 booleans) | 5 | S |
| 3 | Path operations: join, average, simplify, offset, outline stroke | 5 | S |
| 4 | Stroke width tool + arrowheads/dashes/fidelity UI | 3 | S |
| 5 | Units/rulers/guides settings; document & app preferences persistence | 3 | S |
| 6 | View: outline/preview, hide artboard, pasteboard rulers | 2 | S |
| 7 | Group isolation mode, lock/hide object-level | 3 | C |

### M8 — Professional toolkit round #2 (post-v1 candidates)
Color spaces & swatches, gradients, opacity/blend isolation, patterns,
symbols, text engine, transform each, envelope/effects. Each carries its own
spike task before planning. *(planning-level only)*

### M9 — Release readiness
| # | Task | SP | MoSCoW |
|---|------|----|--------|
| 1 | E2E browser suite (Playwright) for gold flows in real Chrome | 5 | M |
| 2 | Performance: >10k-node doc still 30fps pan/zoom; memory budget check | 5 | M |
| 3 | Docker hardening: non-root, read-only fs, healthchecks, size budget (<200 MB) | 2 | M |
| 4 | Docs: user guide, API reference, auto-commands cheat-sheet | 3 | M |
| 5 | v1.0 tag, image promotion, runbook for the docker host | 2 | M |

**Accept (release gate):** CI green, integration + e2e green, docs shipped, image
deployed and healthchecked on `user@host`.

### Sequencing
```
M0 ──► M1 ──► M2 ──► M3 ──► M4 ──► M5 ──► M6 ──► M7 ──► M9 (M8 slips post-v1)
        ▲      │      │      │      │      ▲
        └──────┴──────┴──────┴──────┘      │
   Geometry must land before everything; shell (M5) can start in parallel
   once M2 model is usable for rendering.
```

---

## 8. Definition of Done & quality gates

A task is **Done** when all apply:
1. Code merged to `main` through a green CI run (build + tests + publish WASM).
2. All new public APIs carry XML-style docs/comments; math has formula comments.
3. Unit tests for new logic + integration tests where seams cross assemblies.
4. No warnings introduced (warnings promoted over time).
5. Docker image builds; app boots; healthcheck passes.
6. `git log` readable: one concern per conventional commit.

Quality budget: Geometry **≥ 95%** coverage; Core/Pdf **≥ 85%**; Api **≥ 75%**;
regressions gated in `ci.yml`.

## 9. Testing strategy
- **Unit** (fast, headless): geometry (oracle comparisons, property tests),
  model invariants, command stack, PDF writer micro-tests.
- **Integration**: `WebApplicationFactory` REST tests; real WebSocket JSON-RPC
  sessions driving the full command catalog (the "golden script" suite).
- **Round-trip**: `model → PDF → sidecar → model` equality; PDF opened by external
  renderer snapshot tests once a render oracle is chosen. Illustrator private data
  round-trips `payload → parse → write → payload` byte-exactly.
- **Corpus-driven**: a byte/object-level **feature probe** classifies every PDF in
  the Ghostscript/MuPDF, pdf.js and veraPDF corpora (fonts, clips, ExtGState,
  soft masks, blend modes, patterns, shadings, image filters, OCG layers, XMP,
  encryption, PDF/A markers, Illustrator `/PieceInfo`), and aggregate coverage
  floors turn capability drift into a single summarised failure. Corpora are
  fetched, never vendored (ADR-17).
- **E2E (M9)**: Playwright driving the WASM build in real Chrome for gold flows.
- Mouse-stream replay tests: hand-recorded pointer scripts assert the same command
  sequence the UI emits (proves UI == automation == tests).

## 10. Risks & mitigations
| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| AvaloniaDock unstable on WASM | Med | Spike early in M5; fallback to in-house DockHost (ADR-07) |
| WASM memory/perf for large docs | Med | Skia direct render, culling, page-in geometry; perf budget in M9 |
| PDF 3rd-party validator drift | Low | Golden renderer suite pinned in CI |
| Scope creep vs Illustrator parity | High | MoSCoW gates per milestone; parity measured on defined task list only |
| Offline/registry downtime on docker host | Low | Image cached locally; compose healthcheck + rollback tag |

## 11. Glossary
- **Anchor / node** — an editable point on a path.
- **In/out handle** — absolute control points either side of an anchor; a straight
  segment has both handles collapsed onto the neighbouring anchors.
- **Pasteboard** — the infinite canvas area surrounding artboards.
- **Sidecar** — private lossless payload embedded in the PDF.
- **Command stream** — serialized undoable operations; the automation lingua franca.
