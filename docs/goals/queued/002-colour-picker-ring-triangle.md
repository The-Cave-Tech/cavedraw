# Queued goal 002 — a proper colour picker (ring + triangle)

Status: **queued** (not started). Recorded here because only one goal can be active at a
time; the selection goal is currently active. Promote this when that finishes, or sooner if
desired.

Reference image: the user supplied a screenshot of the target picker (a spectrum ring with an
inscribed triangle, HSL readout, hex field, opacity bar and a swatch pad). Keep it with this
note when promoting — it is the specification of the layout, and "like Inkscape/Affinity" is
not precise enough on its own.

## Objective

Replace the current colour picker with a spectrum-ring picker of the kind Inkscape and Affinity
use. The present one "sucks": it is a square saturation/brightness area beside a hue strip.

## The ring and the triangle

- A **ring** showing the spectrum, wrapped around a circle.
- An **equilateral triangle inscribed inside the ring**, sized so its three points touch the
  ring.
- The triangle **always has one point on the ring**, at the currently selected position in the
  spectrum. That point **adopts the colour of the ring where it touches**.
- **Clockwise from that point**, the next corner is **white** and the third is **black**.
- The triangle's **fill is a three-way gradient of those three colours**, so the inside shows
  the full range between them.

## Interaction

- **Pressing on the ring** orients the triangle's first point to the angle the pointer is at.
- **Dragging** moves it: the triangle follows the pointer **at any angle**, all the way round
  the screen, and comes to rest where the pointer is released.
- A **small circle**, large enough to click but not much larger, **white**, marks the
  **currently selected colour**. It sits at that colour's position in the triangle.
- **Clicking inside the triangle** selects the colour at that point and the circle **moves** to
  the clicked position.

## The rest of the panel

- **South-east of the circle**: a small **hex RGB text entry**.
- **South-west**: the **HSL** values (H, S, L).
- **Bottom of the panel**, left to right:
  - an **opacity preview circle**;
  - an **opacity slider as a bar with a gradient from light to dark**;
  - a **percentage** value on the right.
- **Right of the ring**: the **recently used colours**, as a **two-column swatch pad**.

## Method

- The colour maths belongs in `VCCad.Core` and must be testable headless: ring angle to hue,
  the three triangle corners, the barycentric mapping from a point inside the triangle to the
  three weights, and the round trip colour -> position -> colour.
- The drawing belongs in an Avalonia control, but **every value the picker shows must also be
  reachable as an operation** — the parity rule runs both ways, so a person can set the colour
  from the picker and a driver can set exactly the same value through the registry. Anything
  the picker can do that the registry cannot is a defect, and vice versa.
- Tests first. Probe each new test by reintroducing the defect it claims to catch. Round trips
  must be exact enough that clicking a colour and reading it back gives the same value, and the
  ring must not drift as the triangle is re-oriented.
- Verify by driving the running app through its HTTP automation API, and by taking a screenshot
  and comparing it with the reference — this is a visual feature, and "it builds" is not
  evidence that it looks right.
- Keep `scripts/test-all.ps1` green. After each work item: build `src/VCCad.App.Desktop`, run
  the full suite, sync to WSL, relaunch the desktop app for the person, and report progress
  concisely.
