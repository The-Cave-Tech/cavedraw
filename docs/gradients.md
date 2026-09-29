# Gradients

The model and the plan. Written before the implementation so that the layers built on top of it
agree about what a gradient *is*, rather than each inventing its own idea.

## What exists

`VCCad.Core.Model.GradientSpec` — the whole model, in one file, with no dependency on Avalonia,
Skia or PDF. That is deliberate: the same gradient must render on canvas, export to PDF, import
from PDF, round-trip through the sidecar, and be driven by an operation, and a model that knew
about any one of those would be wrong for the other four.

- `GradientKind` — Linear, Radial, Freeform, Conical.
- `GradientStop` — position 0..1, colour, **opacity carried separately**, midpoint, optional name.
- `GradientSpread` — Pad, Reflect, Repeat.
- `FreeformPoint`, `FreeformMode` — Points or Lines.
- `GradientSpec.Sample(t)` and `SampleWithSpread(t)` — the evaluation everyone shares.

`FillSpec` gained a trailing `GradientSpec? Gradient = null`. It is the **last, optional**
parameter so every existing construction still compiles and every document written before
gradients still deserializes to exactly what it meant.

## The decisions that matter, and why

**Opacity is not folded into colour.** Illustrator interpolates opacity independently of colour.
A colour that carries its own alpha cannot express a fully opaque stop in a colour that is itself
semi-transparent, nor a half-transparent black that must stay black. Two channels, interpolated
separately.

**Colour interpolates component-wise in RGB, not in linear light.** This is technically the worse
colour science and it is what Illustrator does: a black-to-white ramp blended in linear light
comes out visibly lighter in the middle than the reference. Matching the reference means matching
this.

**Geometry is normalised to the object's bounding box** — start `(0, 0.5)`, end `(1, 0.5)` for a
default ramp — so resizing an object carries its gradient sensibly, and a document is not tied to
one resolution. The exception is freeform points, which are artboard-relative because Illustrator
lets them sit outside the shape and they must not move when the object is merely translated.

**`Kind` is a field, not a subclass.** Illustrator lets you switch a gradient's type while keeping
its stops. A hierarchy would make that a conversion between unrelated types instead of a field
change.

**`FillSpec.Color` stays meaningful even on a gradient.** It is what to paint with when the
gradient cannot be — a flattened export, a thumbnail, a viewer with no shading support. A fill
never has no colour at all.

**Equal stop positions are allowed and the later one wins.** That is how a hard edge is made, and
it falls out of the collapse rule in `Normalised()` rather than needing a special case.

## Where this is going, staged

The full brief is a large feature — model, rendering, panel, canvas annotators, PDF and AI import
and export, presets. It is staged so that each stage is complete and testable rather than five
half-built layers:

1. **The model** (done) — `GradientSpec`, `FillSpec.Gradient`, evaluation, spread. Tested headless.
2. **Serialization** — sidecar round-trip, `ModelDump` visibility so a driver without eyes can
   read a gradient. Nothing is real until it survives a save and load.
3. **PDF export** — `sh`/`ShadingType 2` (axial) and `3` (radial) with a stitching function
   (`FunctionType 3`) and an exponential interpolation function (`FunctionType 2`) per stop pair.
   Spread becomes the Extend array.
4. **PDF import** — `/Shading` resources and the `sh` operator mapped back to `GradientSpec`;
   unsupported shading types approximated by the closest supported one and said so, never
   silently dropped.
5. **Canvas rendering** — Skia shaders for linear and radial, honouring spread and per-stop alpha.
6. **The Gradient panel** — type selector, ramp with draggable stops, stop colour and opacity,
   angle/scale/reverse, and the freeform point list.
7. **On-canvas annotators** — the linear line with its endpoints, the radial centre and radius
   handles, freeform points; drag, rotate, constrain with Shift, duplicate with Alt.
8. **Operations** — every one of the above reachable through the single `EditorOperations`
   registry, both ways: if a person can do it, the agent can, and vice versa (AGENTS.md §1.1).
9. **Presets**, and freeform `Lines` mode, then conical.

## Non-negotiables carried over

- The registry is the only surface. A gradient capability that exists only in the panel is a
  defect, and so is one that exists only in the API.
- Round-trip fidelity is the test: import a PDF with a shading, export it, and the crop of
  gradient stops, geometry and spread must match.
- A gradient that cannot be represented is **approximated and reported**, never silently dropped.
