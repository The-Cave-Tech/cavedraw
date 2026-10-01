using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
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
        item.Runs.AddRange(extraRuns);
        return item;
    }

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
                rotationDegrees: 0),
            Block("two", "Nimbus Roman", 20, bold: false, italic: true, colour: ColorRgb.Red,
                alignment: TextAlignment.Right, lineSpacing: 2.0, paragraphSpacing: 6, frameWidth: 100,
                rotationDegrees: 30));

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
                     ("leadingMixed", summary.LineSpacingMixed),
                     ("spaceMixed", summary.ParagraphSpacingMixed),
                     ("turnMixed", summary.RotationMixed),
                     ("frameMixed", summary.FrameWidthMixed),
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
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("leading").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("space").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("turn").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("frame").ValueKind);

        // And the faces the blocks agree on are still reported - here none do, which the flags say.
        Assert.Equal(summary.Runs, reported.GetProperty("runs").GetInt32());
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
                lineSpacing: 1.5, paragraphSpacing: 4, frameWidth: 120, rotationDegrees: 45),
            Block("same", "Nimbus Sans", 18, bold: true, colour: ColorRgb.Green, alignment: TextAlignment.Center,
                lineSpacing: 1.5, paragraphSpacing: 4, frameWidth: 120, rotationDegrees: 45));

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "text.common", Params(new { runIndex = 0 })));

        Assert.False(reported.GetProperty("mixed").GetBoolean());
        Assert.Equal("same", reported.GetProperty("content").GetString());
        Assert.Equal("Nimbus Sans", reported.GetProperty("family").GetString());
        Assert.Equal(18.0, reported.GetProperty("size").GetDouble(), 6);
        Assert.True(reported.GetProperty("bold").GetBoolean());
        Assert.False(reported.GetProperty("italic").GetBoolean());
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
}
