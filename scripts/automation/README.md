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