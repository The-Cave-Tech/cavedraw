# Queued goal 001 — batched input replay with time deltas

Status: **queued** (not started). Recorded here because only one goal can be active at a
time; the selection goal is currently active. Promote this to the active goal when that one
finishes, or sooner if desired.

## Objective

Let the automation API accept a **batch of user input events with time deltas**, so a whole
human session — mouse, keyboard, stylus, wheel, hover — can be replayed through one call, and
the assistant can carry out a task like "draw a picture of a cat" as the gestures a person
would actually make.

## Why

Today a driver has to make one HTTP call per event and has no way to say how long apart they
were. That is fine for a click and useless for a drawing. A cat is a sequence of hundreds of
strokes with pauses between them — each stroke a press, a run of moves, and a release — and
the timing is part of the input, not decoration:

- a double click is two clicks close together;
- a long press is a different thing from a tap;
- a drag path is sampled over time, and its shape depends on the sampling.

## Requirements

1. **A batch endpoint** takes an ordered list of events, each with a delta in milliseconds
   from the one before it. The whole list is replayed through the **same input path a
   person's events take** — the one the diary records — so a batch and a hand cannot diverge.

2. **Every kind of user event**, not only clicks and keys:
   - pointer press, release, move and wheel, with button and modifier state
   - hover, which the diary already records separately
   - keyboard down and up, with modifiers and text
   - stylus/pen events including pressure and tilt — a drawing is not a mouse drag
   - the window entering and leaving, so a drag interrupted by leaving the canvas is
     reproducible
   - touch, where the platform reports it

3. **Timing is honoured, not ignored.** Events replay against a clock with their deltas, and
   there is a way to say "as fast as possible" for tests, so one format serves both a realistic
   recording and a fast regression run.

4. **Recordable and replayable in both directions.** A session a person performed can be
   exported in this format from the diary and played back; a batch that was played can be
   recorded. One stable file format, producible and consumable with no window open.

5. **Everything a drawing needs is reachable this way**, because a person picks a tool, a
   colour and a pen style with the mouse and the keyboard: tool selection, the colour picker
   and swatches, stroke width, cap, join, dash, fill/stroke toggles, and every panel control.
   Anything reachable only through an operation and not through a control a person can operate
   is a defect; anything reachable only as a control and not as an operation is also a defect.
   The parity rule runs both ways.

6. **Tests.** A batch replays headlessly and produces the same document the same gestures by
   hand would; the corpus is a *recorded session saved as a file* and replayed with no window.
   Timing, modifier state, button state and event ordering are each pinned. Probe every new
   test by reintroducing the defect it claims to catch.

## Method

- Prefer the existing input path over a second one. Synthetic events already go through
  `InputInjection`; a batch is a way of delivering a list to it **with timing**, not a parallel
  implementation of input.
- A malformed batch is rejected with a specific error naming the offending **index**, rather
  than applying part of it and failing.
- Keep `scripts/test-all.ps1` green. After each work item: build `src/VCCad.App.Desktop`, run
  the full suite, sync to WSL, relaunch the desktop app, and report progress concisely.
- Verify by driving the running app through its HTTP automation API, never by reading the code.
  **The proof of this work is a batch that draws something recognisable.**
