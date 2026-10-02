using System.Text.Json;
using System.Text.RegularExpressions;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using VCCad.App.Views.Panes;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The parity half of the Text panel: **everything the panel can do, a driver can do through an
/// <see cref="EditorOperations"/> invoke, and everything the panel reads, an operation reports.**
///
/// The panel's write is member-by-member - <see cref="DocumentSession.ApplyTextFieldsAt"/> is handed only the members
/// the person changed, and a null member means "leave it alone" - so a mixed selection can have its agreeing members
/// edited without a field nobody touched being invented and written over the others. Its read is the mixed reading
/// <see cref="TextSummary"/> makes, so a person can see that a selection disagrees rather than being shown one
/// block's value as though it were everyone's.
///
/// These tests go through the **operation** path and assert on the **model**, because that is the only way to tell
/// whether the driver's request and the person's gesture are the same edit rather than two implementations that
/// happen to agree today. `EditorOperations.Invoke` is the registry entry point the HTTP endpoint, the chatbot and
/// the diagnostics Operations tab all reach, so a test here covers every driver at once.
/// </summary>
public class TextOperationParityTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static TextRun Run(string text, string family = "Nimbus Sans", double size = 12,
        bool bold = false, bool italic = false, ColorRgb? colour = null)
        => new()
        {
            Text = text,
            FontFamily = family,
            FontSize = size,
            Bold = bold,
            Italic = italic,
            Color = colour,
        };

    private static TextItem Block(string words, string family = "Nimbus Sans", double size = 12,
        bool bold = false, bool italic = false, ColorRgb? colour = null, TextAlignment alignment = TextAlignment.Left,
        double lineSpacing = 1.2, double paragraphSpacing = 0, double frameWidth = 0, double rotationDegrees = 0,
        double letterSpacing = 0, double wordSpacing = 0, string? fontStretch = null, string? fontVariant = null,
        GlyphOrientation orientation = GlyphOrientation.Auto,
        params TextRun[] extraRuns)
    {
        var item = new TextItem
        {
            Name = words,
            Origin = new Point2D(0, 0),
            Color = colour ?? ColorRgb.Black,
            Alignment = alignment,
            LineSpacing = lineSpacing,
            ParagraphSpacing = paragraphSpacing,
            FrameWidth = frameWidth,
            RotationRadians = rotationDegrees * Math.PI / 180.0,
        };

        item.Runs.Add(Run(words, family, size, bold, italic));
        item.Runs[0].LetterSpacing = letterSpacing;
        item.Runs[0].WordSpacing = wordSpacing;
        item.Runs[0].FontStretch = fontStretch;
        item.Runs[0].FontVariant = fontVariant;
        item.Runs[0].FontOrientation = orientation;
        item.Runs.AddRange(extraRuns);
        return item;
    }

    /// <summary>
    /// The quarter turn the layout gives one character in the block's own space - the turn the canvas draws and the
    /// SVG writer has to write, asked of the one layout both read rather than of the member it came from.
    /// </summary>
    private static double GlyphTurn(TextItem text, int index)
        => TextLayoutEngine.Compute(text).Glyphs.Single(glyph => glyph.Index == index).Rotation;

    /// <summary>Two blocks on one layer, both selected, and a context that can be invoked through the registry.</summary>
    private static (AutomationContext Context, EditorViewModel ViewModel, TextItem First, TextItem Second) With(
        TextItem first, TextItem second)
    {
        var viewModel = new EditorViewModel();
        viewModel.Document.Artboards[0].Layers[0].AddItem(first);
        viewModel.Document.Artboards[0].Layers[0].AddItem(second);
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);

        return (new AutomationContext { ViewModel = viewModel }, viewModel, first, second);
    }

    // ---------------------------------------------------------------- text.update is member-by-member

    /// <summary>
    /// **A size edit leaves the mixed members exactly as they were**, which is the defect the panel's mixed readout
    /// exists to remove: `text.update` used to pass the first block's words, colour and face to
    /// `UpdateSelectedText` for the whole selection, so setting a size wrote one block's words over the other's from
    /// a field nobody touched. The panel never did that - it hands over only the members that changed - so the two
    /// were not the same edit.
    /// </summary>
    [Fact]
    public void ASizeEditDoesNotWriteTheMixedMembers()
    {
        (AutomationContext context, _, TextItem first, TextItem second) = With(
            Block("one", size: 10, colour: ColorRgb.Red, alignment: TextAlignment.Center),
            Block("two", size: 30, colour: ColorRgb.Blue, alignment: TextAlignment.Right));

        EditorOperations.Invoke(context, "text.update", Params(new { fontSize = 20.0 }));

        Assert.Equal(20.0, first.Runs[0].FontSize, 6);
        Assert.Equal(20.0, second.Runs[0].FontSize, 6);

        // Nobody touched the words, the colours or the alignment, so neither block may have moved.
        Assert.Equal("one", first.PlainText);
        Assert.Equal("two", second.PlainText);
        Assert.Equal(ColorRgb.Red, first.Color);
        Assert.Equal(ColorRgb.Blue, second.Color);
        Assert.Equal(TextAlignment.Center, first.Alignment);
        Assert.Equal(TextAlignment.Right, second.Alignment);
    }

    /// <summary>
    /// **A plain `text.update` with no parameters is not an edit at all.** Every member is omitted, and an omitted
    /// member means "leave it alone" in the shape the panel uses. The old form had no such case: it always passed the
    /// first block's text, colour and face for the whole selection, so a request that named nothing rewrote
    /// everything it touched - which is what a driver reaching for a read-only reply got.
    /// </summary>
    [Fact]
    public void AnUpdateNamingNothingChangesNothing()
    {
        (AutomationContext context, EditorViewModel viewModel, TextItem first, TextItem second) = With(
            Block("one", size: 10, colour: ColorRgb.Red), Block("two", size: 30, colour: ColorRgb.Blue));
        int depth = viewModel.ActiveSession.UndoDepth;

        EditorOperations.Invoke(context, "text.update", default);

        Assert.Equal("one", first.PlainText);
        Assert.Equal("two", second.PlainText);
        Assert.Equal(ColorRgb.Red, first.Color);
        Assert.Equal(ColorRgb.Blue, second.Color);
        Assert.Equal(10.0, first.Runs[0].FontSize, 6);
        Assert.Equal(30.0, second.Runs[0].FontSize, 6);

        // And no undo step was added: an undo that returns to exactly where it started reads as "undo did nothing".
        Assert.Equal(depth, viewModel.ActiveSession.UndoDepth);
    }

    /// <summary>
    /// **An edit to a multi-run block does not collapse its runs.** The panel writes a face at one named run, or
    /// every run when none is named, and never through `PlainText` - whose setter rewrites the first run and drops
    /// the rest, taking each run's own face and colour with it. A driver's request has to land the same way.
    /// </summary>
    [Fact]
    public void AFaceEditKeepsTheOtherRuns()
    {
        TextItem first = Block("one", size: 10,
            extraRuns: new[] { Run("tail", "Nimbus Roman", 14, colour: ColorRgb.Green) });
        (AutomationContext context, _, TextItem _, TextItem second) = With(first, Block("two", size: 10));

        EditorOperations.Invoke(context, "text.update", Params(new { fontSize = 20.0 }));

        Assert.Equal(2, first.Runs.Count);
        Assert.Equal("one", first.Runs[0].Text);
        Assert.Equal("tail", first.Runs[1].Text);
        Assert.Equal(20.0, first.Runs[0].FontSize, 6);
        Assert.Equal(20.0, first.Runs[1].FontSize, 6);

        // The second run's own colour and face are its own and were not touched.
        Assert.Equal(ColorRgb.Green, first.Runs[1].Color);
        Assert.Equal("Nimbus Roman", first.Runs[1].FontFamily);
    }

    /// <summary>
    /// **A named run takes the face and the others keep theirs**, which is what `text.styleSelection` does for a
    /// character range and what the panel does for the run it names. A block whose run list is shorter than the index
    /// is a **gap** and is skipped rather than counting as a disagreement - the same judgement `TextSummary` makes.
    /// </summary>
    [Fact]
    public void ARunIndexedFaceEditLandsOnThatRunAlone()
    {
        TextItem first = Block("one", size: 10,
            extraRuns: new[] { Run("tail", "Nimbus Roman", 14) });
        (AutomationContext context, _, TextItem _, TextItem second) = With(first, Block("two", size: 10));

        EditorOperations.Invoke(context, "text.update", Params(new { fontSize = 20.0, runIndex = 1 }));

        Assert.Equal(10.0, first.Runs[0].FontSize, 6);
        Assert.Equal(20.0, first.Runs[1].FontSize, 6);

        // The second block has no run at index 1, so it is a gap: nothing was clamped onto the run it does have.
        Assert.Single(second.Runs);
        Assert.Equal(10.0, second.Runs[0].FontSize, 6);
    }

    /// <summary>
    /// **A face edit is one undo step, and each block is restored whole.** The panel's face commit is one
    /// `ReplaceTextCommand` per block under a composite; a driver's is the same edit, so it undoes the same way.
    /// </summary>
    [Fact]
    public void AFaceEditIsOneUndoStepAndRestoresEveryBlock()
    {
        (AutomationContext context, EditorViewModel viewModel, TextItem first, TextItem second) = With(
            Block("one", size: 10, colour: ColorRgb.Red), Block("two", size: 30, colour: ColorRgb.Blue));
        int depth = viewModel.ActiveSession.UndoDepth;

        EditorOperations.Invoke(context, "text.update", Params(new { fontSize = 20.0, bold = true }));

        Assert.Equal(depth + 1, viewModel.ActiveSession.UndoDepth);
        Assert.Equal(20.0, first.Runs[0].FontSize, 6);
        Assert.True(first.Runs[0].Bold);

        viewModel.Undo();

        Assert.Equal(10.0, first.Runs[0].FontSize, 6);
        Assert.False(first.Runs[0].Bold);
        Assert.Equal(30.0, second.Runs[0].FontSize, 6);
        Assert.Equal("one", first.PlainText);
        Assert.Equal("two", second.PlainText);
    }

    /// <summary>
    /// **A colour is a block member the panel can set on its own**, and `text.update` has to reach it without
    /// inventing the others. Setting only the colour leaves the words and the faces exactly as they were.
    /// </summary>
    [Fact]
    public void AColourOnlyEditLeavesTheWordsAndFaces()
    {
        (AutomationContext context, _, TextItem first, TextItem second) = With(
            Block("one", size: 10), Block("two", size: 30));

        EditorOperations.Invoke(context, "text.update", Params(new { color = new[] { 0, 255, 0 } }));

        Assert.Equal(ColorRgb.FromBytes(0, 255, 0, 255), first.Color);
        Assert.Equal(ColorRgb.FromBytes(0, 255, 0, 255), second.Color);
        Assert.Equal("one", first.PlainText);
        Assert.Equal("two", second.PlainText);
        Assert.Equal(10.0, first.Runs[0].FontSize, 6);
        Assert.Equal(30.0, second.Runs[0].FontSize, 6);
    }

    /// <summary>
    /// **The words can be replaced on their own**, which is the one member the old operation always wrote. It has to
    /// stay reachable, and it has to leave the faces alone: content and face are different members.
    /// </summary>
    [Fact]
    public void AContentOnlyEditLeavesTheFaceAlone()
    {
        (AutomationContext context, _, TextItem first, TextItem second) = With(
            Block("one", "Nimbus Roman", 10), Block("two", "Nimbus Roman", 30));

        EditorOperations.Invoke(context, "text.update", Params(new { text = "same words" }));

        Assert.Equal("same words", first.PlainText);
        Assert.Equal("same words", second.PlainText);
        Assert.Equal("Nimbus Roman", first.Runs[0].FontFamily);
        Assert.Equal(10.0, first.Runs[0].FontSize, 6);
        Assert.Equal(30.0, second.Runs[0].FontSize, 6);
    }

    /// <summary>
    /// **The reply says how many blocks changed**, so a driver can tell a request that landed from one that found
    /// nothing to do. The panel's own apply reports the same thing.
    /// </summary>
    [Fact]
    public void TheReplyCountsTheBlocksThatChanged()
    {
        (AutomationContext context, _, _, _) = With(
            Block("one", size: 10, colour: ColorRgb.Red), Block("two", size: 30, colour: ColorRgb.Blue));

        JsonElement changed = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.update", Params(new { fontSize = 20.0 })));

        Assert.Equal(2, changed.GetProperty("changed").GetInt32());
        Assert.True(changed.GetProperty("mixed").GetBoolean());

        // A request naming a value both blocks already hold changes nothing, and says so.
        JsonElement nothing = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.update", Params(new { fontSize = 20.0 })));

        Assert.Equal(0, nothing.GetProperty("changed").GetInt32());
    }

    // ---------------------------------------------------------------- the per-run members #147 added

    /// <summary>
    /// **Tracking, word spacing, stretch and variant reach the model through `text.update`.** This is the gap the
    /// panel's own comment recorded as impossible: #147 put all four on <see cref="TextRun"/>, the canvas, the layout
    /// and the exporter read them, and until now neither a person nor a driver could set one. They are per-run, so
    /// they land on the named run and a block with no run at that index is skipped like any other face member.
    /// </summary>
    [Fact]
    public void SpacingAndFaceRequestsReachTheNamedRunAlone()
    {
        TextItem first = Block("one", size: 10,
            extraRuns: new[] { Run("tail", "Nimbus Roman", 14) });
        (AutomationContext context, _, TextItem _, TextItem second) = With(first, Block("two", size: 10));

        EditorOperations.Invoke(context, "text.update", Params(new
        {
            letterSpacing = 1.5,
            wordSpacing = 2.5,
            fontStretch = "condensed",
            fontVariant = "small-caps",
            runIndex = 1,
        }));

        Assert.Equal(1.5, first.Runs[1].LetterSpacing, 6);
        Assert.Equal(2.5, first.Runs[1].WordSpacing, 6);
        Assert.Equal("condensed", first.Runs[1].FontStretch);
        Assert.Equal("small-caps", first.Runs[1].FontVariant);

        // The run nobody named keeps its own, and the block with no run at index 1 is a gap.
        Assert.Equal(0.0, first.Runs[0].LetterSpacing, 6);
        Assert.Null(first.Runs[0].FontStretch);
        Assert.Single(second.Runs);
        Assert.Equal(0.0, second.Runs[0].LetterSpacing, 6);
        Assert.Null(second.Runs[0].FontStretch);
    }

    /// <summary>
    /// **Naming one of the four leaves the others exactly as they were**, which is the member-by-member judgement
    /// `text.update` makes everywhere else: a request that wants a stretch put back must not clear the tracking or
    /// the variant nobody mentioned.
    /// </summary>
    [Fact]
    public void AFaceRequestEditLeavesTheUntouchedMembersAlone()
    {
        TextItem first = Block("one", size: 10,
            letterSpacing: 4.0, wordSpacing: 3.0, fontStretch: "condensed", fontVariant: "small-caps");
        (AutomationContext context, _, _, _) = With(first, Block("two", size: 10));

        EditorOperations.Invoke(context, "text.update", Params(new { fontStretch = "normal" }));

        Assert.Null(first.Runs[0].FontStretch);
        Assert.Equal(4.0, first.Runs[0].LetterSpacing, 6);
        Assert.Equal(3.0, first.Runs[0].WordSpacing, 6);
        Assert.Equal("small-caps", first.Runs[0].FontVariant);
    }

    /// <summary>
    /// **A width or a variant does not replace the face.** The model chooses a face by family, weight and slant, and
    /// the name the document asked for and the programme it carried belong to the family that is still in force - so
    /// setting a stretch must not clear either, the way choosing a family does.
    /// </summary>
    [Fact]
    public void AFaceRequestEditDoesNotClearTheNameTheDocumentAskedFor()
    {
        TextItem first = Block("one", size: 10);
        first.Runs[0].SourceFont = "Helvetica-Bold";
        first.Runs[0].EmbeddedFont = new EmbeddedFont { BaseFont = "ABCDEF+Test", FamilyName = "VCCadTest" };
        (AutomationContext context, _, _, _) = With(first, Block("two", size: 10));

        EditorOperations.Invoke(context, "text.update",
            Params(new { letterSpacing = 1.0, fontStretch = "condensed", fontVariant = "small-caps" }));

        Assert.Equal("condensed", first.Runs[0].FontStretch);
        Assert.Equal("Helvetica-Bold", first.Runs[0].SourceFont);
        Assert.Equal("ABCDEF+Test", first.Runs[0].EmbeddedFont!.BaseFont);
    }

    // ---------------------------------------------------------------- text.common is the mixed report

    /// <summary>
    /// **`text.common` reports what the panel reads, member by member.** A disagreeing member is explicitly mixed
    /// and carries no value; the members the selection agrees on are still reported, so one disagreement does not
    /// hide the rest. This is the reading the panel already shows and that no operation could ask for.
    /// </summary>
    [Fact]
    public void TheCommonReportIsMixedWhereTheSelectionDisagrees()
    {
        (AutomationContext context, EditorViewModel viewModel, _, _) = With(
            Block("one", "Nimbus Sans", 12, bold: true, italic: false, colour: ColorRgb.Black,
                alignment: TextAlignment.Left, lineSpacing: 1.2, paragraphSpacing: 0, frameWidth: 0,
                rotationDegrees: 0, letterSpacing: 1.0, wordSpacing: 1.0, fontStretch: "condensed",
                fontVariant: "small-caps"),
            Block("two", "Nimbus Roman", 20, bold: false, italic: true, colour: ColorRgb.Red,
                alignment: TextAlignment.Right, lineSpacing: 2.0, paragraphSpacing: 6, frameWidth: 100,
                rotationDegrees: 30, letterSpacing: 5.0, wordSpacing: 3.0, fontStretch: "expanded",
                fontVariant: null, orientation: GlyphOrientation.Upright));

        // The panel's own authority on agreement, so the operation and the panel cannot report different things.
        TextSummary summary = TextSummary.Of(viewModel.ActiveSession.SelectedTextItems(), 0);

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", Params(new { runIndex = 0 })));

        Assert.Equal(summary.Blocks, reported.GetProperty("blocks").GetInt32());
        Assert.Equal(summary.IsMixed, reported.GetProperty("mixed").GetBoolean());

        foreach ((string name, bool mixed) in new[]
                 {
                     ("contentMixed", summary.ContentMixed),
                     ("colourMixed", summary.ColorMixed),
                     ("alignmentMixed", summary.AlignmentMixed),
                     ("familyMixed", summary.FamilyMixed),
                     ("sizeMixed", summary.FontSizeMixed),
                     ("boldMixed", summary.BoldMixed),
                     ("italicMixed", summary.ItalicMixed),
                     ("letterSpacingMixed", summary.LetterSpacingMixed),
                     ("wordSpacingMixed", summary.WordSpacingMixed),
                     ("fontStretchMixed", summary.FontStretchMixed),
                     ("fontVariantMixed", summary.FontVariantMixed),
                     ("orientationMixed", summary.OrientationMixed),
                     ("lineSpacingMixed", summary.LineSpacingMixed),
                     ("paragraphSpacingMixed", summary.ParagraphSpacingMixed),
                     ("rotationMixed", summary.RotationMixed),
                     ("frameWidthMixed", summary.FrameWidthMixed),
                 })
        {
            Assert.Equal(mixed, reported.GetProperty(name).GetBoolean());
        }

        // Mixed means no common value, not the first block's.
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("colour").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("family").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("size").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("alignment").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("bold").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("italic").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("letterSpacing").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("wordSpacing").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("fontStretch").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("fontVariant").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("orientation").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("leading").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("space").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("turn").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("frame").ValueKind);

        // And the faces the blocks agree on are still reported - here none do, which the flags say.
        Assert.Equal(0, reported.GetProperty("runs").GetInt32());
    }

    /// <summary>
    /// **An agreeing selection reports the common value and no mix**, so a driver can read a value rather than only
    /// being told "mixed". The panel shows exactly these values in exactly these fields.
    /// </summary>
    [Fact]
    public void TheCommonReportIsTheValueWhereTheSelectionAgrees()
    {
        (AutomationContext context, _, _, _) = With(
            Block("same", "Nimbus Sans", 18, bold: true, colour: ColorRgb.Green, alignment: TextAlignment.Center,
                lineSpacing: 1.5, paragraphSpacing: 4, frameWidth: 120, rotationDegrees: 45,
                letterSpacing: 2.0, wordSpacing: 1.0, fontStretch: "condensed", fontVariant: "small-caps"),
            Block("same", "Nimbus Sans", 18, bold: true, colour: ColorRgb.Green, alignment: TextAlignment.Center,
                lineSpacing: 1.5, paragraphSpacing: 4, frameWidth: 120, rotationDegrees: 45,
                letterSpacing: 2.0, wordSpacing: 1.0, fontStretch: "condensed", fontVariant: "small-caps"));

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", Params(new { runIndex = 0 })));

        Assert.False(reported.GetProperty("mixed").GetBoolean());
        Assert.Equal("same", reported.GetProperty("content").GetString());
        Assert.Equal("Nimbus Sans", reported.GetProperty("family").GetString());
        Assert.Equal(18.0, reported.GetProperty("size").GetDouble(), 6);
        Assert.True(reported.GetProperty("bold").GetBoolean());
        Assert.False(reported.GetProperty("italic").GetBoolean());
        Assert.Equal(2.0, reported.GetProperty("letterSpacing").GetDouble(), 6);
        Assert.Equal(1.0, reported.GetProperty("wordSpacing").GetDouble(), 6);
        Assert.Equal("condensed", reported.GetProperty("fontStretch").GetString());
        Assert.Equal("small-caps", reported.GetProperty("fontVariant").GetString());

        // The initial orientation is CSS's `mixed`, not an absent member: a driver reads a value it can write.
        Assert.Equal("mixed", reported.GetProperty("orientation").GetString());
        Assert.False(reported.GetProperty("orientationMixed").GetBoolean());
        Assert.Equal(TextAlignment.Center.ToString(), reported.GetProperty("alignment").GetString());
        Assert.Equal(1.5, reported.GetProperty("leading").GetDouble(), 6);
        Assert.Equal(4.0, reported.GetProperty("space").GetDouble(), 6);
        Assert.Equal(120.0, reported.GetProperty("frame").GetDouble(), 6);
        Assert.Equal(45.0, reported.GetProperty("turn").GetDouble(), 6);
        Assert.Equal(2, reported.GetProperty("blocks").GetInt32());
    }

    /// <summary>
    /// **A block with no run at the inspected index is a gap, not a disagreement.** Blocks carry different numbers
    /// of runs - that is what a run list is - and counting a shorter block as "different" would make every selection
    /// of unequal blocks report every face member as mixed, which makes the report useless. This is the judgement
    /// `TextSummary.Of` already makes, so the operation has to make it too.
    /// </summary>
    [Fact]
    public void ABlockWithNoRunAtIndexIsAGapAndNotADisagreement()
    {
        (AutomationContext context, _, _, _) = With(
            Block("longer", size: 12, extraRuns: new[] { Run("second", size: 14) }),
            Block("shorter", size: 12));

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", Params(new { runIndex = 1 })));

        Assert.False(reported.GetProperty("sizeMixed").GetBoolean());
        Assert.Equal(14.0, reported.GetProperty("size").GetDouble(), 6);
        Assert.Equal(2, reported.GetProperty("blocks").GetInt32());
        Assert.Equal(1, reported.GetProperty("runs").GetInt32());
    }

    /// <summary>An empty selection says so rather than reporting the first thing it can find.</summary>
    [Fact]
    public void AnEmptySelectionSaysSo()
    {
        var viewModel = new EditorViewModel();
        var context = new AutomationContext { ViewModel = viewModel };

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", default));

        Assert.True(reported.GetProperty("empty").GetBoolean());
        Assert.Equal(0, reported.GetProperty("blocks").GetInt32());
    }

    // ---------------------------------------------------------------- the inspected run is shared state

    /// <summary>
    /// **Which run the face fields describe is shared state, settable and readable through the registry**, the way
    /// `style.inspectStroke` shares which stroke is being inspected. Without it a driver has no way to say which run
    /// a face edit means except by placing a caret first, and the panel has no way to offer a run picker that
    /// outlives the caret - which is the third gap the issue records.
    /// </summary>
    [Fact]
    public void TheInspectedRunIsSharedStateOnTheRegistry()
    {
        (AutomationContext context, EditorViewModel viewModel, _, _) = With(
            Block("one", extraRuns: new[] { Run("tail") }), Block("two"));

        EditorOperations.Invoke(context, "text.inspectRun", Params(new { index = 1 }));
        Assert.Equal(1, viewModel.InspectedRun);

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.inspectRun", default));

        Assert.Equal(1, reported.GetProperty("index").GetInt32());
        Assert.Equal(2, reported.GetProperty("runs").GetInt32());
        Assert.Equal("run 2 of 2", reported.GetProperty("label").GetString());
    }

    /// <summary>
    /// **The index is clamped to the selection's own run list**, so a selection change cannot leave it pointing at a
    /// run that is not there - which is how the panel and the driver would end up describing different runs.
    /// </summary>
    [Fact]
    public void TheInspectedRunIsClampedToTheRunList()
    {
        (AutomationContext context, EditorViewModel viewModel, _, TextItem second) = With(
            Block("one", extraRuns: new[] { Run("tail") }), Block("two"));

        EditorOperations.Invoke(context, "text.inspectRun", Params(new { index = 99 }));
        Assert.Equal(1, viewModel.InspectedRun);

        // Selecting a block with one run clamps it further; the index cannot outlive the list it pointed into.
        viewModel.SelectObject(second);
        Assert.Equal(0, viewModel.InspectedRun);

        viewModel.ClearSelection();
        Assert.Equal(-1, viewModel.InspectedRun);
    }

    /// <summary>
    /// **The shared inspected run is what the face fields describe**, and `text.common` reads it when no index is
    /// named. A caret is not a run picker - outside editing it holds whatever the last edit left, which may belong
    /// to a block that is no longer selected - so this is the state both views read.
    /// </summary>
    [Fact]
    public void TheSharedInspectedRunReachesTheCommonReport()
    {
        (AutomationContext context, EditorViewModel viewModel, _, _) = With(
            Block("one", size: 10, extraRuns: new[] { Run("tail", "Nimbus Roman", 14) }),
            Block("two", size: 10, extraRuns: new[] { Run("tail", "Nimbus Roman", 14) }));

        EditorOperations.Invoke(context, "text.inspectRun", Params(new { index = 1 }));
        Assert.Equal(1, viewModel.InspectedRun);

        JsonElement common = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", default));

        Assert.Equal(1, common.GetProperty("runIndex").GetInt32());
        Assert.Equal(14.0, common.GetProperty("size").GetDouble(), 6);
    }

    /// <summary>
    /// **A face edit at the shared run leaves the other run alone**, which is the end the shared state exists for: a
    /// driver can name a run without first placing a caret, and a run picker can outlive one.
    /// </summary>
    [Fact]
    public void AFaceEditAtTheSharedRunLeavesTheOtherRunAlone()
    {
        TextItem first = Block("one", size: 10,
            extraRuns: new[] { Run("tail", "Nimbus Roman", 14) });
        (AutomationContext context, _, TextItem _, TextItem second) = With(first, Block("two", size: 10));

        EditorOperations.Invoke(context, "text.inspectRun", Params(new { index = 1 }));
        EditorOperations.Invoke(context, "text.update", Params(new { runIndex = 1, fontSize = 20.0 }));

        Assert.Equal(10.0, first.Runs[0].FontSize, 6);
        Assert.Equal(20.0, first.Runs[1].FontSize, 6);

        // The second block has no run at index 1, so it is a gap: nothing was clamped onto the run it does have.
        Assert.Single(second.Runs);
        Assert.Equal(10.0, second.Runs[0].FontSize, 6);
    }

    // ---------------------------------------------------------------- the block's own axes (#127)

    /// <summary>
    /// **`text.style` carries the writing mode and the base direction, and `text.common` reads them back in the same
    /// words.** Before this the model held both, the layout acted on both and the SVG export wrote both - and no
    /// operation and no control could set either, so a driver could import a vertical block and then never make one.
    /// A driver reads back exactly what it can write, which is why the report speaks the property's own spelling and
    /// not the enum's.
    /// </summary>
    [Fact]
    public void TheWritingModeAndDirectionAreSettableAndReported()
    {
        (AutomationContext context, EditorViewModel viewModel, TextItem first, TextItem second) = With(
            Block("one"), Block("two"));
        int depth = viewModel.ActiveSession.UndoDepth;

        EditorOperations.Invoke(context, "text.style",
            Params(new { writingMode = "vertical-rl", direction = "rtl" }));

        Assert.Equal(TextWritingMode.VerticalRl, first.WritingMode);
        Assert.Equal(TextDirection.RightToLeft, first.Direction);
        Assert.Equal(TextWritingMode.VerticalRl, second.WritingMode);
        Assert.Equal(TextDirection.RightToLeft, second.Direction);
        Assert.Equal(depth + 1, viewModel.ActiveSession.UndoDepth);

        JsonElement common = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", default));

        Assert.Equal("vertical-rl", common.GetProperty("writingMode").GetString());
        Assert.False(common.GetProperty("writingModeMixed").GetBoolean());
        Assert.Equal("rtl", common.GetProperty("direction").GetString());
        Assert.False(common.GetProperty("directionMixed").GetBoolean());
    }

    /// <summary>
    /// **A member that is not named is left alone.** The whole reason the members are optional is that a selection is
    /// not obliged to agree with itself: asking for `rtl` must not reset every block's writing mode to the initial
    /// value, which is what an all-or-nothing operation would do to a selection holding one vertical block.
    /// </summary>
    [Fact]
    public void AStyleEditLeavesTheMembersItDidNotName()
    {
        TextItem first = Block("one");
        first.WritingMode = TextWritingMode.VerticalLr;
        first.Direction = TextDirection.RightToLeft;

        (AutomationContext context, _, TextItem _, TextItem second) = With(first, Block("two"));

        EditorOperations.Invoke(context, "text.style", Params(new { lineSpacing = 1.5 }));

        Assert.Equal(TextWritingMode.VerticalLr, first.WritingMode);
        Assert.Equal(TextDirection.RightToLeft, first.Direction);
        Assert.Equal(1.5, first.LineSpacing, 6);

        // The second block kept the initial values it had, because nothing named them.
        Assert.Equal(TextWritingMode.HorizontalTb, second.WritingMode);
        Assert.Equal(TextDirection.LeftToRight, second.Direction);
    }

    /// <summary>
    /// **A selection that disagrees about the mode says so**, rather than reporting the first block's. The mixed
    /// reading is the whole point of the summary: a panel or a driver shown `vertical-rl` for a selection holding one
    /// horizontal block believes something untrue about what it selected.
    /// </summary>
    [Fact]
    public void AMixedWritingModeIsReportedAsMixed()
    {
        TextItem first = Block("one");
        first.WritingMode = TextWritingMode.VerticalRl;
        TextItem second = Block("two");

        (AutomationContext context, _, TextItem _, TextItem _) = With(first, second);

        JsonElement common = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", default));

        Assert.True(common.GetProperty("writingModeMixed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, common.GetProperty("writingMode").ValueKind);
        Assert.False(common.GetProperty("directionMixed").GetBoolean());
        Assert.Equal("ltr", common.GetProperty("direction").GetString());
    }

    /// <summary>
    /// **A value that is not one of the property's own is refused by name**, not silently read as the initial value:
    /// a driver that asked for a vertical block and got a horizontal one has no way to notice.
    /// </summary>
    [Fact]
    public void AnUnknownWritingModeIsRefused()
    {
        (AutomationContext context, EditorViewModel viewModel, TextItem first, TextItem _) = With(
            Block("one"), Block("two"));
        int depth = viewModel.ActiveSession.UndoDepth;

        Assert.ThrowsAny<Exception>(() => EditorOperations.Invoke(
            context, "text.style", Params(new { writingMode = "sideways" })));

        Assert.Equal(TextWritingMode.HorizontalTb, first.WritingMode);
        Assert.Equal(depth, viewModel.ActiveSession.UndoDepth);
    }

    // ---------------------------------------------------------------- the glyph orientation (#127)

    /// <summary>
    /// **The orientation reaches the geometry, not only the member.** This is the whole point of the gap: the model
    /// has carried <see cref="TextRun.FontOrientation"/> since #127 and `TextLayout` turns every glyph by it, but no
    /// operation could set one - so a driver could import a column the file set upright and never make one. The
    /// assertion is on the quarter turn the layout gives each character, which is what the canvas draws and what
    /// the writer writes: `o` is Latin, so the initial value turns it a quarter turn clockwise in a vertical column,
    /// and `upright` has to leave it alone.
    /// </summary>
    [Fact]
    public void TheOrientationReachesTheGlyphTurnTheLayoutDraws()
    {
        TextItem first = Block("one");
        first.WritingMode = TextWritingMode.VerticalRl;
        TextItem second = Block("two");
        second.WritingMode = TextWritingMode.VerticalRl;
        (AutomationContext context, _, TextItem _, TextItem _) = With(first, second);

        Assert.Equal(-Math.PI / 2.0, GlyphTurn(first, 0), 9);

        EditorOperations.Invoke(context, "text.update", Params(new { orientation = "upright" }));

        Assert.Equal(0.0, GlyphTurn(first, 0), 9);
        Assert.Equal(0.0, GlyphTurn(second, 0), 9);

        // And `sideways` is the other turn, so the request is not read as "upright or nothing".
        EditorOperations.Invoke(context, "text.update", Params(new { orientation = "sideways" }));

        Assert.Equal(-Math.PI / 2.0, GlyphTurn(first, 0), 9);
    }

    /// <summary>
    /// **The orientation reaches the bytes the writer writes.** The SVG writer writes
    /// `glyph-orientation-vertical` in SVG 1.1's own spelling only when the run asks for something other than the
    /// initial value, so an ordinary vertical block keeps the bytes it had - and a run set through the operation has
    /// to appear in the file, because the geometry alone cannot say whether a column was set upright or turned.
    /// </summary>
    [Fact]
    public void TheOrientationReachesTheSvgTheWriterWrites()
    {
        TextItem first = Block("one");
        first.WritingMode = TextWritingMode.VerticalRl;
        TextItem second = Block("two");
        second.WritingMode = TextWritingMode.VerticalRl;
        (AutomationContext context, EditorViewModel viewModel, TextItem _, TextItem _) = With(first, second);

        string plain = SvgWriter.Write(viewModel.Document);
        Assert.DoesNotContain("glyph-orientation-vertical", plain, StringComparison.Ordinal);

        // Naming the value a run already holds is not an edit: the initial value is absent from the file, so the
        // bytes have to be exactly what they were. This is the absent-at-default rule, through the operation.
        EditorOperations.Invoke(context, "text.update", Params(new { orientation = "mixed" }));
        Assert.Equal(plain, SvgWriter.Write(viewModel.Document));

        EditorOperations.Invoke(context, "text.update", Params(new { orientation = "sideways" }));

        Assert.Contains(
            "glyph-orientation-vertical=\"90\"", SvgWriter.Write(viewModel.Document), StringComparison.Ordinal);

        // The initial value is absent from the file, so putting it back writes nothing again.
        EditorOperations.Invoke(context, "text.update", Params(new { orientation = "auto" }));

        Assert.Equal(plain, SvgWriter.Write(viewModel.Document));
    }

    /// <summary>
    /// **The same value is read back in the words the operation accepts.** A driver reads what it can write: the
    /// report speaks CSS's `text-orientation` spelling, and the request accepted SVG 1.1's `90` for the same value,
    /// because the reader reads both and a caller should not have to know which one the file used.
    /// </summary>
    [Fact]
    public void TheOrientationIsSettableAndReportedInThePropertiesOwnWords()
    {
        (AutomationContext context, _, TextItem first, TextItem second) = With(Block("one"), Block("two"));

        EditorOperations.Invoke(context, "text.update", Params(new { orientation = "90" }));

        Assert.Equal(GlyphOrientation.Rotate, first.Runs[0].FontOrientation);
        Assert.Equal(GlyphOrientation.Rotate, second.Runs[0].FontOrientation);

        JsonElement common = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", default));

        Assert.Equal("sideways", common.GetProperty("orientation").GetString());
        Assert.False(common.GetProperty("orientationMixed").GetBoolean());
    }

    /// <summary>
    /// **A selection that disagrees about the orientation says so**, rather than reporting the first block's, and
    /// says that no run agrees on a value - the same judgement every other face member makes.
    /// </summary>
    [Fact]
    public void AMixedOrientationIsReportedAsMixed()
    {
        TextItem first = Block("one", orientation: GlyphOrientation.Upright);
        (AutomationContext context, _, TextItem _, TextItem _) = With(first, Block("two"));

        JsonElement common = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", default));

        Assert.True(common.GetProperty("orientationMixed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, common.GetProperty("orientation").ValueKind);
        Assert.Equal(0, common.GetProperty("runs").GetInt32());
    }

    /// <summary>
    /// **The orientation is per run like the face**, so a named run takes it and the others keep theirs - and a block
    /// with no run at the index is a gap rather than a disagreement, exactly as for every other run member.
    /// </summary>
    [Fact]
    public void TheOrientationLandsOnTheNamedRunAlone()
    {
        TextItem first = Block("one", extraRuns: new[] { Run("tail") });
        (AutomationContext context, _, TextItem _, TextItem second) = With(first, Block("two"));

        EditorOperations.Invoke(context, "text.update", Params(new { orientation = "upright", runIndex = 1 }));

        Assert.Equal(GlyphOrientation.Auto, first.Runs[0].FontOrientation);
        Assert.Equal(GlyphOrientation.Upright, first.Runs[1].FontOrientation);

        // The second block has no run at index 1, so it is a gap: nothing was clamped onto the run it does have.
        Assert.Single(second.Runs);
        Assert.Equal(GlyphOrientation.Auto, second.Runs[0].FontOrientation);
    }

    /// <summary>
    /// **Setting the orientation does not replace the face.** The run's family, its own colour and the name the
    /// document asked for all belong to the run and to the family that is not being replaced, so a request that
    /// names only the orientation must leave every one of them exactly as it was.
    /// </summary>
    [Fact]
    public void AnOrientationEditLeavesTheFaceAndTheNameTheDocumentAskedFor()
    {
        TextItem first = Block("one", "Nimbus Roman", 18, colour: ColorRgb.Red);
        first.Runs[0].Color = ColorRgb.Red;
        first.Runs[0].SourceFont = "Helvetica-Bold";
        first.Runs[0].LetterSpacing = 4.0;
        (AutomationContext context, _, TextItem _, TextItem _) = With(first, Block("two"));

        EditorOperations.Invoke(context, "text.update", Params(new { orientation = "upright" }));

        Assert.Equal(GlyphOrientation.Upright, first.Runs[0].FontOrientation);
        Assert.Equal("Nimbus Roman", first.Runs[0].FontFamily);
        Assert.Equal(18.0, first.Runs[0].FontSize, 6);
        Assert.Equal(ColorRgb.Red, first.Runs[0].Color);
        Assert.Equal("Helvetica-Bold", first.Runs[0].SourceFont);
        Assert.Equal(4.0, first.Runs[0].LetterSpacing, 6);
    }

    /// <summary>
    /// **A value that is not one of the property's own is refused by name**, not silently read as the initial value:
    /// a driver that asked for an upright column and got a turned one has no way to notice. And a refused request
    /// leaves no undo step behind.
    /// </summary>
    [Fact]
    public void AnUnknownOrientationIsRefused()
    {
        (AutomationContext context, EditorViewModel viewModel, TextItem first, TextItem _) = With(
            Block("one"), Block("two"));
        int depth = viewModel.ActiveSession.UndoDepth;

        Assert.ThrowsAny<Exception>(() => EditorOperations.Invoke(
            context, "text.update", Params(new { orientation = "upside-down" })));

        Assert.Equal(GlyphOrientation.Auto, first.Runs[0].FontOrientation);
        Assert.Equal(depth, viewModel.ActiveSession.UndoDepth);
    }

    // ---------------------------------------------------------------- the run's own colour (#190)

    /// <summary>
    /// **The run colour reaches the bytes the writer writes.** <see cref="TextRun.Color"/> has been on the model and
    /// honoured by the canvas, the SVG writer and the PDF exporter since #161, and written only by the importers -
    /// no operation could set one, so a run a person coloured had no way to reach a file. The SVG writer writes a
    /// `fill` on a run's `tspan` only when the run's colour differs from the block's, so the second run's own blue
    /// has to appear there, and the block's own fill has to be untouched.
    /// </summary>
    [Fact]
    public void TheRunColourReachesTheSvgTheWriterWrites()
    {
        TextItem first = Block("one", extraRuns: new[] { Run("tail") });
        (AutomationContext context, EditorViewModel viewModel, TextItem _, TextItem _) = With(
            first, Block("two", extraRuns: new[] { Run("tail") }));

        string plain = SvgWriter.Write(viewModel.Document);

        // Nothing states a fill of its own yet, so nothing is written for a run.
        Assert.DoesNotContain("fill=\"#0000ff\"", plain, StringComparison.Ordinal);

        EditorOperations.Invoke(context, "text.update", Params(new { runColor = new[] { 0, 0, 255 }, runIndex = 1 }));

        string edited = SvgWriter.Write(viewModel.Document);
        Assert.Contains("fill=\"#0000ff\"", edited, StringComparison.Ordinal);

        // The run the request **named**, in both selected blocks - each has a run at index 1 - and never run 0,
        // whose piece is the same `one` it always was: the pieces that state the fill are the two `tail`s.
        string[] painted = Regex.Matches(edited, "fill=\"#0000ff\"[^>]*>([^<]*)<")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(new[] { "tail", "tail" }, painted);
        Assert.Contains($"fill=\"{Hex(first.Color)}\"", edited, StringComparison.Ordinal);

        // Clearing it is not an edit that leaves a colour behind: the run states none again and the bytes are
        // exactly what they were. This is the absent-at-default rule, through the operation.
        EditorOperations.Invoke(context, "text.update", Params(new { runColor = (int[]?)null, runIndex = 1 }));
        Assert.Equal(plain, SvgWriter.Write(viewModel.Document));

        // Naming the colour the block is already painted in is the same statement as no colour of its own, so it
        // stores none and the file is unchanged - the model has one spelling for one paint.
        EditorOperations.Invoke(context, "text.update", Params(new { runColor = new[] { 0, 0, 0 }, runIndex = 1 }));
        Assert.Equal(plain, SvgWriter.Write(viewModel.Document));
    }

    /// <summary>
    /// **The run colour is set at the inspected run and read back in the words the operation takes.** A driver reads
    /// what it can write: the report names the colour the inspected run holds, and a selection whose runs disagree
    /// says mixed rather than showing one run's paint as though every run were drawn in it.
    /// </summary>
    [Fact]
    public void TheRunColourIsSettableAndReportedInThePropertiesOwnWords()
    {
        TextItem first = Block("one", extraRuns: new[] { Run("tail") });
        (AutomationContext context, _, TextItem _, TextItem _) = With(
            first, Block("two", extraRuns: new[] { Run("tail") }));

        EditorOperations.Invoke(context, "text.update", Params(new { runColor = new[] { 0, 0, 255 }, runIndex = 1 }));

        JsonElement atOne = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", Params(new { runIndex = 1 })));

        Assert.Equal("0,0,255,255", atOne.GetProperty("runColour").GetString());
        Assert.False(atOne.GetProperty("runColourMixed").GetBoolean());

        // Run 0 was not named, so it still states no colour of its own and is drawn in the block's.
        JsonElement atZero = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", Params(new { runIndex = 0 })));

        Assert.Equal(JsonValueKind.Null, atZero.GetProperty("runColour").ValueKind);
        Assert.False(atZero.GetProperty("runColourMixed").GetBoolean());

        // The block's own colour is a different member and was not touched.
        Assert.Equal(ColorRgb.Black, first.Color);
        Assert.Equal("0,0,0,255", atOne.GetProperty("colour").GetString());

        // A colour equal to the block's is the same paint, so the run holds none of its own: the report says so
        // rather than claiming a colour the block already gives it, and the bytes stay what they were.
        EditorOperations.Invoke(context, "text.update", Params(new { runColor = new[] { 0, 0, 0 }, runIndex = 1 }));

        JsonElement inherited = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", Params(new { runIndex = 1 })));

        Assert.Equal(JsonValueKind.Null, inherited.GetProperty("runColour").ValueKind);
        Assert.False(inherited.GetProperty("runColourMixed").GetBoolean());
    }

    /// <summary>
    /// **A selection that disagrees about a run's own colour says so**, rather than reporting the first run's paint,
    /// and says that no run agrees on a value - the same judgement every other per-run member makes.
    /// </summary>
    [Fact]
    public void AMixedRunColourIsReportedAsMixed()
    {
        TextItem first = Block("one", extraRuns: new[] { Run("tail", colour: ColorRgb.Green) });
        (AutomationContext context, _, TextItem _, TextItem _) = With(
            first, Block("two", extraRuns: new[] { Run("tail") }));

        JsonElement common = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", Params(new { runIndex = 1 })));

        Assert.True(common.GetProperty("runColourMixed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, common.GetProperty("runColour").ValueKind);
        Assert.Equal(0, common.GetProperty("runs").GetInt32());
    }

    /// <summary>
    /// **Giving a run a colour does not disturb the block's colour, the run's face or the name the document asked
    /// for.** A colour is a value the run holds rather than a choice of face, so it is deliberately not routed
    /// through the face path: `SourceFont` and the embedded programme belong to the family that is not being
    /// replaced, and the block's own colour still paints every run that states none.
    /// </summary>
    [Fact]
    public void ARunColourEditLeavesTheBlockColourAndTheFaceAlone()
    {
        TextItem first = Block("one", "Nimbus Roman", 18, colour: ColorRgb.Red,
            extraRuns: new[] { Run("tail", "Nimbus Roman", 18) });
        first.Runs[1].SourceFont = "Helvetica-Bold";
        first.Runs[1].LetterSpacing = 4.0;
        (AutomationContext context, _, TextItem _, TextItem _) = With(
            first, Block("two", "Nimbus Roman", 18, colour: ColorRgb.Red));

        EditorOperations.Invoke(context, "text.update", Params(new { runColor = new[] { 0, 0, 255 }, runIndex = 1 }));

        Assert.Equal(ColorRgb.Red, first.Color);
        Assert.Equal("Nimbus Roman", first.Runs[1].FontFamily);
        Assert.Equal(18.0, first.Runs[1].FontSize, 6);
        Assert.Equal("Helvetica-Bold", first.Runs[1].SourceFont);
        Assert.Equal(4.0, first.Runs[1].LetterSpacing, 6);

        // The run the request named is the one that changed, and it is drawn in the colour it now holds.
        Assert.Equal(ColorRgb.Blue, first.ColourOf(first.Runs[1]));
        Assert.Equal(ColorRgb.Red, first.ColourOf(first.Runs[0]));
    }

    /// <summary>
    /// **A value that is not a colour is refused by name**, not read as "no colour of its own": a driver that asked
    /// for blue and got the block's red has no way to notice. And a refused request leaves no undo step behind.
    /// </summary>
    [Fact]
    public void AnUnknownRunColourIsRefused()
    {
        (AutomationContext context, EditorViewModel viewModel, TextItem first, TextItem _) = With(
            Block("one"), Block("two"));
        int depth = viewModel.ActiveSession.UndoDepth;

        Assert.ThrowsAny<Exception>(() => EditorOperations.Invoke(
            context, "text.update", Params(new { runColor = "blue", runIndex = 0 })));
        Assert.ThrowsAny<Exception>(() => EditorOperations.Invoke(
            context, "text.update", Params(new { runColor = new[] { 1, 2 }, runIndex = 0 })));

        Assert.All(first.Runs, run => Assert.Null(run.Color));
        Assert.Equal(depth, viewModel.ActiveSession.UndoDepth);
    }

    /// <summary>A colour as the SVG writer spells it, so the byte assertion names the file's own word.</summary>
    private static string Hex(ColorRgb colour)
    {
        static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

        return $"#{Channel(colour.R):x2}{Channel(colour.G):x2}{Channel(colour.B):x2}";
    }
}
