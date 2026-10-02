using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The text panel reading and writing the model.
///
/// The panel is a **view**: the content, the face and the block colour go through the session method the operations
/// call, alignment through `text.setAlignment` and the paragraph style through `text.style`. Nothing is assigned to
/// the model from a control's event handler, and every assertion below is on the **model** at the place the panel
/// says the edit will land - which is the run named in the label for face, and the block for content and colour.
///
/// The selection is made **before** `Attach`, because attaching is what subscribes the pane: attaching first leaves a
/// pane that never saw the selection and a test that passes while the panel is blind.
/// </summary>
public class TextPaneTests
{
    // ---------------------------------------------------------------- driving the pane

    private static (TextPane Pane, EditorViewModel ViewModel, TextItem Block) Host(params TextRun[] runs)
    {
        var viewModel = new EditorViewModel();
        TextItem block = Block(viewModel, runs);

        // Built before Attach: attaching subscribes the pane to the session.
        viewModel.SelectObject(block);

        var pane = new TextPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 440, Height = 780, Content = pane };
        window.Show();
        Settle();

        return (pane, viewModel, block);
    }

    /// <summary>Hosts two blocks with the first selected as well, for the edits that reach every member.</summary>
    private static (TextPane Pane, EditorViewModel ViewModel, TextItem First, TextItem Second) HostTwo(
        TextItem first, TextItem second)
    {
        var viewModel = new EditorViewModel();
        viewModel.Document.Artboards[0].Layers[0].AddItem(first);
        viewModel.Document.Artboards[0].Layers[0].AddItem(second);

        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);

        var pane = new TextPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 440, Height = 780, Content = pane };
        window.Show();
        Settle();

        return (pane, viewModel, first, second);
    }

    internal static TextItem Block(EditorViewModel viewModel, params TextRun[] runs)
    {
        var item = new TextItem { Name = "text", Origin = new Point2D(10, 10) };
        foreach (TextRun run in runs)
        {
            item.Runs.Add(run);
        }

        if (runs.Length == 0)
        {
            item.Runs.Add(new TextRun { Text = string.Empty });
        }

        viewModel.Document.Artboards[0].Layers[0].AddItem(item);
        return item;
    }

    internal static TextRun Run(string text, string family = "Nimbus Sans", double size = 12,
        bool bold = false, bool italic = false)
        => new() { Text = text, FontFamily = family, FontSize = size, Bold = bold, Italic = italic };

    internal static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    internal static TextBox Box(TextPane pane, string name)
        => pane.FindControl<TextBox>(name) ?? throw new Xunit.Sdk.XunitException($"no box called {name}");

    internal static ComboBox Combo(TextPane pane, string name)
        => pane.FindControl<ComboBox>(name) ?? throw new Xunit.Sdk.XunitException($"no combo called {name}");

    internal static CheckBox Check(TextPane pane, string name)
        => pane.FindControl<CheckBox>(name) ?? throw new Xunit.Sdk.XunitException($"no check called {name}");

    internal static TextBlock Text(TextPane pane, string name)
        => pane.FindControl<TextBlock>(name) ?? throw new Xunit.Sdk.XunitException($"no text called {name}");

    /// <summary>Types a value and commits it the way the field does - on losing focus.</summary>
    internal static void Commit(TextPane pane, string name, string text)
    {
        TextBox box = Box(pane, name);
        box.Text = text;
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Settle();
    }

    internal static string? Watermark(TextPane pane, string name) => Box(pane, name).Watermark as string;

    // ---------------------------------------------------------------- what the pane shows

    /// <summary>The panel describes the selected block: its words, its face and its block colour.</summary>
    [AvaloniaFact]
    public void ThePaneShowsTheSelectedBlocksContentFaceAndColour()
    {
        (TextPane pane, _, TextItem block) = Host(Run("Hello", "Nimbus Sans", 18, bold: true));

        Assert.Equal("1 text block", Text(pane, "SelectionLabel").Text);
        Assert.Equal("Hello", Box(pane, "ContentBox").Text);
        Assert.Equal("Nimbus Sans", Combo(pane, "FamilyBox").SelectedItem);
        Assert.Equal("18", Box(pane, "SizeBox").Text);
        Assert.True(Check(pane, "BoldBox").IsChecked);
        Assert.False(Check(pane, "ItalicBox").IsChecked);
        Assert.Equal("0,0,0,255", Box(pane, "ColorBox").Text);
        Assert.Equal(0, Combo(pane, "AlignBox").SelectedIndex);
        Assert.Equal("run 1 of 1, the run the face fields describe", Text(pane, "RunLabel").Text);
        Assert.Equal(block.Color, ColorRgb.Black);
    }

    /// <summary>
    /// **The face fields describe the shared inspected run**, not the block's first run: a block can hold a heading
    /// and a caption, and the first run's size is wrong for every run but one.
    ///
    /// The run is view state the view model owns, the way `InspectedStroke` is for the appearance stack - the caret's
    /// run reaches it from `CanvasWorkspace.UpdateCaretInfo` while a person types, and a driver sets it by name with
    /// `text.inspectRun`. The panel reads the shared state rather than the caret, which is what lets a run picker
    /// outlive the caret and what stops the panel and a driver describing different runs.
    /// </summary>
    [AvaloniaFact]
    public void TheFaceFieldsFollowTheInspectedRun()
    {
        var viewModel = new EditorViewModel();
        TextItem block = Block(viewModel, Run("Title", "Nimbus Sans", 24), Run("caption", "Nimbus Roman", 8, italic: true));
        viewModel.SelectObject(block);

        // The inspected run is the second one, and it is in place before Attach.
        viewModel.IsEditingText = true;
        viewModel.InspectedRun = 1;

        var pane = new TextPane();
        pane.Attach(viewModel);
        var window = new Window { Width = 440, Height = 780, Content = pane };
        window.Show();
        Settle();

        Assert.Equal("run 2 of 2, the run the face fields describe", Text(pane, "RunLabel").Text);
        Assert.Equal("8", Box(pane, "SizeBox").Text);
        Assert.Equal("Nimbus Roman", Combo(pane, "FamilyBox").SelectedItem);
        Assert.True(Check(pane, "ItalicBox").IsChecked);
        Assert.False(Check(pane, "BoldBox").IsChecked);

        // The report is the same run's, so the panel cannot describe one run and report another.
        Assert.Contains("Run 2", Text(pane, "FontReport").Text!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The panel says which face is actually drawn, and repeats the name the document asked for when a substitute
    /// stands in for it - the substitution nobody is told about looks deliberate.
    /// </summary>
    [AvaloniaFact]
    public void TheFontReportNamesTheFaceActuallyDrawnAndWhatTheDocumentAskedFor()
    {
        // The file's own name for the font is part of the fixture, so it is set before the pane attaches - a list
        // element assigned afterwards raises no change notification, which is how a test of a stale panel passes.
        TextRun run = Run("DO NOT REPRODUCE");
        run.SourceFont = "Helvetica-Bold";

        (TextPane pane, _, TextItem block) = Host(run);

        string report = Text(pane, "FontReport").Text ?? string.Empty;

        // The resolver's own answer, not a second opinion about what the family name means.
        Assert.Contains(StandardFontResolver.Describe(block.Runs[0]), report, StringComparison.Ordinal);

        // And the name the file used, which is what a person recognises.
        Assert.Contains("Helvetica-Bold", report, StringComparison.Ordinal);
    }

    /// <summary>An embedded programme is named rather than described as a substitution.</summary>
    [AvaloniaFact]
    public void AnEmbeddedProgrammeIsNamedInTheReport()
    {
        TextRun run = Run("words");
        run.EmbeddedFont = new EmbeddedFont
        {
            BaseFont = "ABCDEF+CenturyGothic-Bold",
            FamilyName = "VCCadCenturyGothicTest",
        };

        (TextPane pane, _, _) = Host(run);

        string report = Text(pane, "FontReport").Text ?? string.Empty;

        Assert.Contains("ABCDEF+CenturyGothic-Bold", report, StringComparison.Ordinal);
        Assert.Contains("embedded", report, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- where an edit lands

    /// <summary>
    /// **A size typed in the panel lands on the run the panel names, and nowhere else.** The run that is not being
    /// described keeps its size, and the block's words are not rewritten by a field nobody touched.
    /// </summary>
    [AvaloniaFact]
    public void ASizeEditLandsOnTheInspectedRunAndNowhereElse()
    {
        var viewModel = new EditorViewModel();
        TextItem block = Block(viewModel, Run("Title", size: 24), Run("caption", size: 8));
        viewModel.SelectObject(block);
        viewModel.IsEditingText = true;
        viewModel.InspectedRun = 1;

        var pane = new TextPane();
        pane.Attach(viewModel);
        var window = new Window { Width = 440, Height = 780, Content = pane };
        window.Show();
        Settle();

        Commit(pane, "SizeBox", "20");

        Assert.Equal(20.0, block.Runs[1].FontSize, 6);
        Assert.Equal(24.0, block.Runs[0].FontSize, 6);
        Assert.Equal("Titlecaption", block.PlainText);
        Assert.Equal(ColorRgb.Black, block.Color);
    }

    /// <summary>
    /// **A tracking edit lands on the run the panel names, and nowhere else**, exactly as a size edit does. Letter
    /// spacing is a per-run member the model gained with #147 and the canvas and the exporter already honour, and the
    /// panel's field has to reach it through the same call `text.update` makes rather than keeping a copy.
    /// </summary>
    [AvaloniaFact]
    public void ATrackingEditLandsOnTheInspectedRunAndNowhereElse()
    {
        var viewModel = new EditorViewModel();
        TextItem block = Block(viewModel, Run("Title", size: 24), Run("caption", size: 8));
        viewModel.SelectObject(block);
        viewModel.IsEditingText = true;
        viewModel.InspectedRun = 1;

        var pane = new TextPane();
        pane.Attach(viewModel);
        var window = new Window { Width = 440, Height = 780, Content = pane };
        window.Show();
        Settle();

        Commit(pane, "LetterSpacingBox", "3.5");
        Commit(pane, "WordSpacingBox", "1.25");

        Assert.Equal(3.5, block.Runs[1].LetterSpacing, 6);
        Assert.Equal(1.25, block.Runs[1].WordSpacing, 6);
        Assert.Equal(0.0, block.Runs[0].LetterSpacing, 6);
        Assert.Equal(0.0, block.Runs[0].WordSpacing, 6);
        Assert.Equal("Titlecaption", block.PlainText);
    }

    /// <summary>
    /// **A width and a variant are set and put back through the panel.** The model keeps each as the file's own word
    /// and spells absence as null, so `normal` is how a person clears one - and clearing it must not clear the name
    /// the document asked for, because a stretch does not replace the face.
    /// </summary>
    [AvaloniaFact]
    public void AStretchAndAVariantAreSetAndNormalPutsThemBack()
    {
        (TextPane pane, _, TextItem block) = Host(Run("words"));
        block.Runs[0].SourceFont = "Helvetica-Bold";

        Commit(pane, "FontStretchBox", "condensed");
        Commit(pane, "FontVariantBox", "small-caps");

        Assert.Equal("condensed", block.Runs[0].FontStretch);
        Assert.Equal("small-caps", block.Runs[0].FontVariant);
        Assert.Equal("Helvetica-Bold", block.Runs[0].SourceFont);

        Commit(pane, "FontStretchBox", "normal");

        Assert.Null(block.Runs[0].FontStretch);
        Assert.Equal("small-caps", block.Runs[0].FontVariant);
    }

    /// <summary>A colour typed in the panel lands on every block in the selection, not on the first one.</summary>
    [AvaloniaFact]
    public void AColourEditLandsOnEverySelectedBlock()
    {
        var first = new TextItem { Name = "a", Origin = new Point2D(0, 0) };
        first.Runs.Add(Run("one"));
        var second = new TextItem { Name = "b", Origin = new Point2D(0, 40) };
        second.Runs.Add(Run("two"));

        (TextPane pane, _, _, _) = HostTwo(first, second);

        Commit(pane, "ColorBox", "255,0,0,255");

        Assert.Equal(ColorRgb.FromBytes(255, 0, 0, 255), first.Color);
        Assert.Equal(ColorRgb.FromBytes(255, 0, 0, 255), second.Color);
    }

    /// <summary>The block's words are the block's, and a face field is not allowed to rewrite them.</summary>
    [AvaloniaFact]
    public void AWordEditLandsOnTheBlockAndLeavesTheFaceAlone()
    {
        (TextPane pane, _, TextItem block) = Host(Run("before", "Nimbus Roman", 17, bold: true));

        Commit(pane, "ContentBox", "after");

        Assert.Equal("after", block.PlainText);
        Assert.Equal("Nimbus Roman", block.Runs[0].FontFamily);
        Assert.Equal(17.0, block.Runs[0].FontSize, 6);
        Assert.True(block.Runs[0].Bold);
    }

    /// <summary>Alignment and the paragraph style are the operations' own members, and they reach every block.</summary>
    [AvaloniaFact]
    public void AlignmentAndParagraphStyleReachEveryBlock()
    {
        var first = new TextItem { Name = "a", Origin = new Point2D(0, 0) };
        first.Runs.Add(Run("one"));
        var second = new TextItem { Name = "b", Origin = new Point2D(0, 40) };
        second.Runs.Add(Run("two"));

        (TextPane pane, _, _, _) = HostTwo(first, second);

        Combo(pane, "AlignBox").SelectedIndex = 2;
        Settle();
        Commit(pane, "LeadingBox", "1.6");
        Commit(pane, "FrameWidthBox", "120");

        Assert.Equal(TextAlignment.Right, first.Alignment);
        Assert.Equal(TextAlignment.Right, second.Alignment);
        Assert.Equal(1.6, first.LineSpacing, 6);
        Assert.Equal(1.6, second.LineSpacing, 6);
        Assert.Equal(120.0, first.FrameWidth, 6);
        Assert.Equal(120.0, second.FrameWidth, 6);
    }

    /// <summary>
    /// **A committed field that was not edited is not an edit.** Committing every field without changing one puts no
    /// undo step on the stack, because a step that undoes to exactly where it started reads as "undo did nothing".
    /// </summary>
    [AvaloniaFact]
    public void AFieldThePersonDidNotTouchIsNotAnEdit()
    {
        (TextPane pane, EditorViewModel viewModel, TextItem block) = Host(Run("one"), Run("two"));
        int depth = viewModel.ActiveSession.UndoDepth;

        foreach (string name in new[]
                 {
                     "ContentBox", "SizeBox", "ColorBox", "LeadingBox", "ParagraphBox", "RotationBox",
                     "FrameWidthBox", "LetterSpacingBox", "WordSpacingBox", "FontStretchBox", "FontVariantBox",
                 })
        {
            Box(pane, name).RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        }

        Settle();

        Assert.Equal(depth, viewModel.ActiveSession.UndoDepth);
        Assert.Equal("onetwo", block.PlainText);
    }

    /// <summary>
    /// Picks a family from the box's own list, because which families the machine offers is not this test's subject
    /// and a hard-coded one would pass or fail by whether URW happens to be installed here.
    /// </summary>
    internal static string PickAnotherFamily(TextPane pane, string not)
    {
        ComboBox box = Combo(pane, "FamilyBox");
        var names = (box.ItemsSource as IEnumerable<string> ?? Enumerable.Empty<string>()).ToList();
        string pick = names.First(name => !string.Equals(name, not, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = pick;
        Settle();
        return pick;
    }

    /// <summary>Changing a face is one undo step, and undoing puts the face back.</summary>
    [AvaloniaFact]
    public void AFaceEditIsOneUndoStep()
    {
        (TextPane pane, EditorViewModel viewModel, TextItem block) = Host(Run("words", "Nimbus Sans", 12));
        int depth = viewModel.ActiveSession.UndoDepth;

        string picked = PickAnotherFamily(pane, "Nimbus Sans");

        Assert.Equal(picked, block.Runs[0].FontFamily);
        Assert.Equal(depth + 1, viewModel.ActiveSession.UndoDepth);

        viewModel.Undo();

        Assert.Equal("Nimbus Sans", block.Runs[0].FontFamily);
    }

    /// <summary>
    /// Choosing a face is what makes the document's own font name stop describing what should be drawn, so the panel
    /// clears it - the same judgement the session already makes wherever a person picks a face.
    /// </summary>
    [AvaloniaFact]
    public void ChoosingAFaceClearsTheNameTheDocumentAskedFor()
    {
        (TextPane pane, _, TextItem block) = Host(Run("words"));
        block.Runs[0].SourceFont = "Helvetica-Bold";
        Settle();

        string picked = PickAnotherFamily(pane, "Nimbus Sans");

        Assert.Null(block.Runs[0].SourceFont);
        Assert.Equal(picked, block.Runs[0].FontFamily);
    }

    /// <summary>A colour nobody can read is not an edit, and the panel says so rather than swallowing it.</summary>
    [AvaloniaFact]
    public void AnUnreadableColourIsRefusedAndSaid()
    {
        (TextPane pane, EditorViewModel viewModel, TextItem block) = Host(Run("words"));
        int depth = viewModel.ActiveSession.UndoDepth;

        Commit(pane, "ColorBox", "not a colour");

        Assert.Equal(depth, viewModel.ActiveSession.UndoDepth);
        Assert.Equal(ColorRgb.Black, block.Color);
        Assert.Contains("Text colour", viewModel.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// The operations the panel's controls call are on the registry, which is what makes a person's edit and a
    /// driver's request the same thing rather than two implementations that agree today.
    /// </summary>
    [AvaloniaFact]
    public void TheOperationsThePanelCallsAreOnTheSurface()
    {
        Assert.True(EditorOperations.TryGet("text.update", out _));
        Assert.True(EditorOperations.TryGet("text.setAlignment", out _));
        Assert.True(EditorOperations.TryGet("text.style", out _));
    }

    // ---------------------------------------------------------------- the block's own axes (#127)

    /// <summary>
    /// **The mode and the direction combos write the model through the operation's own method**, one undo step, and
    /// through nothing else - the rule the rest of this panel follows. Before #127 was finished these two members were
    /// on the model, acted on by the layout and written to SVG, and there was no way for a person or a driver to set
    /// either: the control and the operation now land on the same call, so neither can do what the other cannot.
    /// </summary>
    [AvaloniaFact]
    public void TheModeAndDirectionCombosSetTheBlock()
    {
        (TextPane pane, EditorViewModel viewModel, TextItem block) = Host(Run("words"));
        int depth = viewModel.ActiveSession.UndoDepth;

        ComboBox mode = Combo(pane, "WritingModeBox");
        mode.SelectedIndex = 1;
        Settle();

        ComboBox direction = Combo(pane, "DirectionBox");
        direction.SelectedIndex = 1;
        Settle();

        Assert.Equal(TextWritingMode.VerticalRl, block.WritingMode);
        Assert.Equal(TextDirection.RightToLeft, block.Direction);
        Assert.Equal(depth + 2, viewModel.ActiveSession.UndoDepth);

        viewModel.Undo();
        Assert.Equal(TextDirection.LeftToRight, block.Direction);
    }

    /// <summary>
    /// **The panel shows what the selection actually holds, and says mixed when it disagrees.** A combo left on
    /// `horizontal-tb` for a selection holding a vertical block is a control that lies about the document, and an
    /// apply that read it as a value would flatten the vertical block the next time any field was committed.
    /// </summary>
    [AvaloniaFact]
    public void ThePanelShowsAMixedWritingModeAsMixed()
    {
        TextItem first = Block(new EditorViewModel(), Run("one"));
        first.WritingMode = TextWritingMode.VerticalRl;

        var viewModel = new EditorViewModel();
        viewModel.Document.Artboards[0].Layers[0].AddItem(first);
        TextItem second = Block(viewModel, Run("two"));
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);

        var pane = new TextPane();
        pane.Attach(viewModel);
        var window = new Window { Width = 440, Height = 780, Content = pane };
        window.Show();
        Settle();

        ComboBox mode = Combo(pane, "WritingModeBox");
        Assert.Equal(-1, mode.SelectedIndex);
        Assert.Equal("mixed", mode.PlaceholderText);
        Assert.Contains("mode", Text(pane, "MixedLabel").Text ?? string.Empty, StringComparison.Ordinal);

        // And the direction they do agree on is still shown, so one disagreeing member does not blank the others.
        Assert.Equal(0, Combo(pane, "DirectionBox").SelectedIndex);
    }
}
