# Automation scenes

Twenty-four scenes that draw with **mouse and keyboard only** - `input.pointer`, `input.batch`, `input.type`
and `ui.keys` - and assert the document model after every gesture. They exist because the features a person
uses are the ones a unit test cannot reach: selection, text editing, the toolbars, the panes, the flyouts.

```
pwsh -NoProfile -File scripts/automation/scenes.ps1                    # all 24
pwsh -NoProfile -File scripts/automation/scenes.ps1 -Only '1;7;8'      # a few, by number
```

Results land in `artifacts/auto/report.json`, grouped by scene and symptom, with what was expected and what
the model actually held. The application must be running or launchable on port 5099; the scripts start it
themselves if it is not.

## Two rules the harness learned the hard way

**Both injection paths take canvas-relative coordinates, `view.toScreen` returns window pixels.** An aimed
gesture therefore subtracts the canvas origin - and that origin is a property of the current *view*, not of
the window, so it is measured per document with the correction zeroed first. Measuring it with a correction
already applied makes the probe land where the correction says it will and the measurement returns zero; that
mistake cost a round and produced two dozen "selection is broken" reports that were arithmetic.

**A click on the canvas with a shape tool armed draws a shape.** Every selection helper arms `ToolSelect`
first, and a scene that visits the Gradient or Stroke pane puts the colour tab back, because `HexBox` only
exists while that tab is showing.

## What the scenes found

They are how #213 was found - the editor **crashed** during scene 15 and the event log carried the stack
(`ArgumentNullException` from `FontFamily`'s constructor, on the render path). They are also a machine
reproduction of the text defects in #211 and #212: scene 7's eight checks fail with the stored string
unchanged, and a fix for those issues flips them.

## The probes

`probe-clicks.ps1` draws one rectangle and clicks its edge, printing where the click landed and what got
selected - the smallest thing that tells "the editor cannot select" from "my coordinates are wrong".
`probe-text.ps1` walks the three ways of re-entering an existing text. `probe-family.ps1` walks the text
toolbar and prints the runs' font families after each control, which is how the family-destroying field edit
behind #213 was found.
## Screenshots, and the scale that costs a round to find

Two operations answer for what a person sees: `ui.describe` asks a vision model, and `GET
/api/v1/screenshot` returns the pixels. The payload is JSON - `{ ok, result: { available, pngBase64 } }` - and
the image is **1600x1000 for a 1920x1200 window**, so a window coordinate has to be scaled by **0.8333**
before it is sampled.

Sampling at unscaled coordinates returns white everywhere, which reads exactly like "nothing was drawn". Three
attempts at the text-ink question in issue #221 were lost to that before the payload was decoded properly, so
the number is written down here rather than rediscovered.

Two other traps in the same vein: the with-text/without-text image diff is pure noise unless the object was
actually deleted - an image diff over the whole window catches chrome churn, and the 16x16 tile map is what
makes a cluster visible - and a click that is supposed to select something has to be **verified by id** before
the frames around it are compared.