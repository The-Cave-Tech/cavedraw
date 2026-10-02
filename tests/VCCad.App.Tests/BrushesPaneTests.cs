using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The brush editor (issue #113): one editor whose middle section is the selected engine's parameter set, every
/// control of which calls the operation a driver calls.
///
/// **Every test here is about parity or about a resolved value, never about a control having been set.** The
/// acceptance for this panel is that a brush changed in it and a brush changed by `brush.set` are the same act on
/// the same model state, and that the preview shows the geometry the canvas would draw rather than a picture of its
/// own. A test that asserted only that a combo box had a selection would pass against a panel that changed nothing.
/// </summary>
public class BrushesPaneTests
{
    // ---------------------------------------------------------------- hosting the pane

    private static (BrushesPane Pane, EditorViewModel ViewModel) Host(EditorViewModel? existing = null)
    {
        EditorViewModel viewModel = existing ?? new EditorViewModel();
        var pane = new BrushesPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 420, Height = 900, Content = pane };
        window.Show();
        Settle();

        return (pane, viewModel);
    }

    private static PathItem Line(EditorViewModel viewModel, double width = 4, string name = "line")
    {
        var path = new PathItem { Name = name, Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4.0));
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    /// <summary>A closed square of artwork, which is what a brush's asset has to be: an item of the document.</summary>
    private static PathItem Art(EditorViewModel viewModel, string name)
    {
        var path = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Red) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static AutomationContext Context(EditorViewModel viewModel) => new() { ViewModel = viewModel };

    private static object? Invoke(EditorViewModel viewModel, string operation, object parameters)
        => EditorOperations.Invoke(Context(viewModel), operation, Params(parameters));

    private static string[] RegistryKinds(EditorViewModel viewModel)
        => (string[]?)EditorOperations.Invoke(Context(viewModel), "brush.kinds", default) ?? Array.Empty<string>();

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Click(BrushesPane pane, string name)
        => (pane.FindControl<Button>(name)
            ?? throw new Xunit.Sdk.XunitException($"no button called {name}"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Type(TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
    }

    /// <summary>Points the library at a brush by name, which is the gesture a person makes.</summary>
    private static void Select(BrushesPane pane, string name)
    {
        var library = pane.FindControl<ComboBox>("BrushLibraryBox")
            ?? throw new Xunit.Sdk.XunitException("no library box");
        library.SelectedItem = name;
        Settle();
    }

    private static void SelectTab(BrushesPane pane, string kind)
    {
        var tabs = pane.FindControl<TabControl>("KindTabs")
            ?? throw new Xunit.Sdk.XunitException("no tab control");
        tabs.SelectedIndex = pane.KindNames.ToList().IndexOf(kind);
        Settle();
    }

    private static string Text(BrushesPane pane, string name)
        => (pane.FindControl<TextBlock>(name) ?? throw new Xunit.Sdk.XunitException($"no text called {name}")).Text
            ?? string.Empty;

    private static TextBox Box(BrushesPane pane, string parameter)
        => pane.Field(parameter) as TextBox
            ?? throw new Xunit.Sdk.XunitException($"no text box for '{parameter}'");

    private static ComboBox Combo(BrushesPane pane, string parameter)
        => pane.Field(parameter) as ComboBox
            ?? throw new Xunit.Sdk.XunitException($"no combo box for '{parameter}'");

    /// <summary>The loops as text, so two outlines can be compared point for point rather than by reference.</summary>
    private static string Describe(IReadOnlyList<IReadOnlyList<Point2D>> loops)
        => string.Join(
            "|",
            loops.Select(loop => string.Join(
                ";", loop.Select(point => $"{point.X:0.####},{point.Y:0.####}"))));

    /// <summary>A transform's six numbers, so two placements are compared by what they say rather than by identity.</summary>
    private static string Describe(AffineTransform transform)
        => $"{transform.A:0.####},{transform.B:0.####},{transform.C:0.####},{transform.D:0.####}," +
           $"{transform.E:0.####},{transform.F:0.####}";

    // ---------------------------------------------------------------- the tabs are the registry's kinds

    /// <summary>
    /// **The tabs come from the registry, and every one of them can actually be used.**
    ///
    /// The list is `brush.kinds`'s own answer, so an engine that lands in the model appears here without this file
    /// being touched. The second half is what stops that from being decorative: each tab is driven through
    /// `brush.create`, so a tab over a kind the registry would refuse fails here rather than when a person clicks it.
    /// </summary>
    [AvaloniaFact]
    public void TheTabsAreTheKindsTheRegistryMakesAndEachOneWorks()
    {
        (BrushesPane pane, EditorViewModel viewModel) = Host();

        Assert.Equal(RegistryKinds(viewModel), pane.KindNames);
        Assert.Contains("calligraphic", pane.KindNames);
        Assert.Contains("art", pane.KindNames);
        Assert.Contains("pattern", pane.KindNames);

        foreach (string kind in pane.KindNames)
        {
            Invoke(viewModel, "brush.create", new { name = "probe " + kind, kind });
            Assert.NotNull(viewModel.Document.FindBrush("probe " + kind));
        }
    }

    /// <summary>
    /// **Switching tabs shows that engine's parameters.** The nib has an angle and a roundness; the art brush has a
    /// stretch and a colourisation and neither of the nib's. The controls are asserted as resolved values - what the
    /// box says - not as pseudo-classes, which do not appear on a control at all.
    /// </summary>
    [AvaloniaFact]
    public void SwitchingTabsShowsThatKindsParameters()
    {
        (BrushesPane pane, EditorViewModel viewModel) = Host();

        SelectTab(pane, "calligraphic");
        Assert.NotNull(pane.Field("angle"));
        Assert.NotNull(pane.Field("roundness"));
        Assert.Null(pane.Field("stretch"));

        SelectTab(pane, "art");
        Assert.NotNull(pane.Field("stretch"));
        Assert.NotNull(pane.Field("colourisation"));
        Assert.Null(pane.Field("roundness"));

        SelectTab(pane, "pattern");
        Assert.NotNull(pane.Field("spacing"));
        Assert.NotNull(pane.Field("tile.side.asset"));
        Assert.Null(pane.Field("angle"));
    }

    /// <summary>
    /// A brush created through the operation is offered here **by existing**, and the pane is on it: the library is
    /// the document's, not a list in the panel.
    /// </summary>
    [AvaloniaFact]
    public void TheLibraryIsTheDocumentsOwnBrushes()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        Invoke(viewModel, "brush.create", new { name = "Flat" });

        (BrushesPane pane, _) = Host(viewModel);

        var library = pane.FindControl<ComboBox>("BrushLibraryBox")!;
        Assert.Equal(3, library.ItemCount); // (new brush), Chisel, Flat
        Assert.Equal(BrushesPane.NewBrushLabel, library.SelectedItem);

        Select(pane, "Chisel");
        Assert.Equal("Chisel", library.SelectedItem);
        Assert.Equal("Chisel", pane.FindControl<TextBox>("BrushNameBox")!.Text);
    }

    // ---------------------------------------------------------------- each parameter through its operation

    /// <summary>
    /// **The acceptance for a nib parameter.** Typing an angle and calling `brush.set` leave the same model state,
    /// member for member - the two are run one after the other on one document, undoing between them, so the
    /// comparison is between two model values rather than between two documents built the same way.
    /// </summary>
    [AvaloniaFact]
    public void ANibParameterReachesTheModelThroughBrushSet()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        (BrushesPane pane, _) = Host(viewModel);
        Select(pane, "Chisel");

        Type(Box(pane, "angle"), "30");
        Settle();

        BrushSpec byControl = viewModel.Document.FindBrush("Chisel")!;
        Assert.Equal(30.0, byControl.AngleDegrees, 6);
        Assert.Equal("30", Box(pane, "angle").Text);

        viewModel.Undo();
        Settle();
        Assert.Equal(0.0, viewModel.Document.FindBrush("Chisel")!.AngleDegrees, 6);

        Invoke(viewModel, "brush.set", new { name = "Chisel", angle = 30.0 });
        Assert.Equal(byControl, viewModel.Document.FindBrush("Chisel"));
    }

    /// <summary>The art brush's own members reach the model the same way, and a kind's members do not leak into
    /// another's: the stretch is an art brush's and reading it off a nib is not a question the model answers.</summary>
    [AvaloniaFact]
    public void AnArtParameterReachesTheModelThroughBrushSet()
    {
        var viewModel = new EditorViewModel();
        Art(viewModel, "wave");
        Invoke(viewModel, "brush.create", new { name = "Mapper", kind = "art", asset = Asset(viewModel, "wave") });
        (BrushesPane pane, _) = Host(viewModel);
        Select(pane, "Mapper");

        Combo(pane, "stretch").SelectedItem = nameof(ArtStretch.StretchToFit);
        Settle();

        BrushSpec byControl = viewModel.Document.FindBrush("Mapper")!;
        Assert.Equal(ArtStretch.StretchToFit, byControl.Stretch);

        viewModel.Undo();
        Settle();
        Assert.Equal(ArtStretch.Repeat, viewModel.Document.FindBrush("Mapper")!.Stretch);

        Invoke(viewModel, "brush.set", new { name = "Mapper", stretch = "stretchToFit" });
        Assert.Equal(byControl, viewModel.Document.FindBrush("Mapper"));
    }

    /// <summary>
    /// A pattern brush's slot is filled through <c>brush.setTile</c>, which is the operation that owns a tile: it
    /// carries controls of its own, and one operation taking five slots and five sets of controls would be five
    /// operations wearing a hat.
    /// </summary>
    [AvaloniaFact]
    public void APatternSlotIsFilledThroughBrushSetTile()
    {
        var viewModel = new EditorViewModel();
        PathItem tile = Art(viewModel, "tile");
        Invoke(viewModel, "brush.create", new { name = "Weave", kind = "pattern" });
        (BrushesPane pane, _) = Host(viewModel);
        Select(pane, "Weave");

        Combo(pane, "tile.side.asset").SelectedItem = "tile";
        Settle();

        BrushSpec byControl = viewModel.Document.FindBrush("Weave")!;
        Assert.Equal(tile.Id, byControl.PatternSideTile?.Asset);

        viewModel.Undo();
        Settle();
        Assert.Null(viewModel.Document.FindBrush("Weave")!.PatternSideTile);

        Invoke(viewModel, "brush.setTile", new { name = "Weave", slot = "side", asset = tile.Id });
        Assert.Equal(byControl, viewModel.Document.FindBrush("Weave"));
    }

    /// <summary>The controls that are a tile's own - its turn, its scale - go through the same operation, which is
    /// what makes a corner tile that is drawn turned something a driver can reproduce.</summary>
    [AvaloniaFact]
    public void ATilesOwnControlsReachTheModelThroughBrushSetTile()
    {
        var viewModel = new EditorViewModel();
        PathItem tile = Art(viewModel, "tile");
        Invoke(viewModel, "brush.create",
            new { name = "Weave", kind = "pattern", outerCorner = tile.Id });
        (BrushesPane pane, _) = Host(viewModel);
        Select(pane, "Weave");

        Type(Box(pane, "tile.outerCorner.rotation"), "90");
        Settle();

        BrushSpec brush = viewModel.Document.FindBrush("Weave")!;
        Assert.Equal(90.0, brush.PatternOuterTile!.RotationDegrees, 6);

        // And the same value through the operation, from the state the undo puts back.
        viewModel.Undo();
        Settle();
        Assert.Equal(0.0, viewModel.Document.FindBrush("Weave")!.PatternOuterTile!.RotationDegrees, 6);

        Invoke(viewModel, "brush.setTile",
            new { name = "Weave", slot = "outerCorner", asset = tile.Id, rotation = 90.0 });
        Assert.Equal(90.0, viewModel.Document.FindBrush("Weave")!.PatternOuterTile!.RotationDegrees, 6);
    }

    /// <summary>An edit is one undo step, whichever control made it: a brush parameter can be taken back in one
    /// move, the same as any other style edit.</summary>
    [AvaloniaFact]
    public void EditingAParameterIsOneUndoStep()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        (BrushesPane pane, _) = Host(viewModel);
        Select(pane, "Chisel");

        Type(Box(pane, "diameter"), "24");
        Settle();
        Assert.Equal(24.0, viewModel.Document.FindBrush("Chisel")!.Diameter, 6);

        viewModel.Undo();
        Settle();
        Assert.Equal(1.0, viewModel.Document.FindBrush("Chisel")!.Diameter, 6);
    }

    // ---------------------------------------------------------------- create, rename, delete, apply

    /// <summary>
    /// **Save creates an asset with the values the panel shows.** The brush is created by `brush.create`, and what
    /// the controls then read is what the model holds - so the panel's numbers and the document's are one set, not
    /// two that agree.
    /// </summary>
    [AvaloniaFact]
    public void CreatingABrushCreatesItWithTheValuesShownAndThenEditsItLive()
    {
        (BrushesPane pane, EditorViewModel viewModel) = Host();

        pane.FindControl<TextBox>("BrushNameBox")!.Text = "Quill";
        Click(pane, "NewBrushButton");
        Settle();

        BrushSpec? created = viewModel.Document.FindBrush("Quill");
        Assert.NotNull(created);
        Assert.Equal(BrushKind.Calligraphic, created!.Kind);

        // The panel is now editing it, and shows what the model holds rather than its own idea of the defaults.
        Assert.Equal("Quill", pane.FindControl<ComboBox>("BrushLibraryBox")!.SelectedItem);
        Assert.Equal(created.AngleDegrees.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            Box(pane, "angle").Text);

        Type(Box(pane, "angle"), "15");
        Settle();
        Assert.Equal(15.0, viewModel.Document.FindBrush("Quill")!.AngleDegrees, 6);
    }

    /// <summary>A brush's name has to be free, and the operation's refusal reaches the person rather than the
    /// exception reaching the click handler.</summary>
    [AvaloniaFact]
    public void CreatingABrushUnderANameThatIsTakenIsReported()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        (BrushesPane pane, _) = Host(viewModel);

        SelectTab(pane, "calligraphic");
        pane.FindControl<TextBox>("BrushNameBox")!.Text = "Chisel";
        Click(pane, "NewBrushButton");
        Settle();

        Assert.Equal(1, viewModel.Document.Brushes.Count);
        Assert.Contains("Chisel", Text(pane, "StatusLabel"));
        Assert.True(pane.FindControl<TextBlock>("StatusLabel")!.IsVisible);
    }

    /// <summary>Renaming carries every stroke that named the brush with it, which is what the operation promises and
    /// why the panel does not implement a rename of its own.</summary>
    [AvaloniaFact]
    public void RenamingCarriesTheStrokesThatNamedTheBrush()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        PathItem path = Line(viewModel);
        viewModel.SelectObject(path);
        Invoke(viewModel, "brush.apply", new { name = "Chisel" });

        (BrushesPane pane, _) = Host(viewModel);
        Select(pane, "Chisel");

        pane.FindControl<TextBox>("BrushNameBox")!.Text = "Quill";
        Click(pane, "RenameBrushButton");
        Settle();

        Assert.Null(viewModel.Document.FindBrush("Chisel"));
        Assert.NotNull(viewModel.Document.FindBrush("Quill"));
        Assert.Equal("Quill", path.Strokes[0].Brush?.Name);
    }

    /// <summary>Deleting takes the brush off the strokes rather than leaving them naming nothing, which is the same
    /// thing `brush.delete` does for a driver.</summary>
    [AvaloniaFact]
    public void DeletingClearsTheBrushOffTheStrokes()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        PathItem path = Line(viewModel);
        viewModel.SelectObject(path);
        Invoke(viewModel, "brush.apply", new { name = "Chisel" });

        (BrushesPane pane, _) = Host(viewModel);
        Select(pane, "Chisel");
        Click(pane, "DeleteBrushButton");
        Settle();

        Assert.Empty(viewModel.Document.Brushes);
        Assert.Null(path.Strokes[0].Brush);
    }

    /// <summary>
    /// **Applying sweeps every selected path** - the issue's own acceptance - and the operation's own default is
    /// used when nothing is inspected, so a two-path selection is one undo step.
    /// </summary>
    [AvaloniaFact]
    public void ApplyingSweepsEverySelectedPath()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        PathItem first = Line(viewModel, 4, "first");
        PathItem second = Line(viewModel, 8, "second");
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);

        (BrushesPane pane, _) = Host(viewModel);
        Select(pane, "Chisel");
        Click(pane, "ApplyToSelectionButton");
        Settle();

        Assert.Equal("Chisel", first.Strokes[0].Brush?.Name);
        Assert.Equal("Chisel", second.Strokes[0].Brush?.Name);

        viewModel.Undo();
        Assert.Null(first.Strokes[0].Brush);
        Assert.Null(second.Strokes[0].Brush);
    }

    // ---------------------------------------------------------------- a mixed selection

    /// <summary>
    /// **A mixed selection says mixed**, rather than showing one path's brush as everyone's. A person reading
    /// "Chisel" beside a selection that half carries it would conclude the whole selection does.
    /// </summary>
    [AvaloniaFact]
    public void AMixedSelectionSaysMixed()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        BrushSpec chisel = viewModel.Document.FindBrush("Chisel")!;

        PathItem first = Line(viewModel, 4, "first");
        first.Strokes[0] = first.Strokes[0] with { Brush = chisel };
        PathItem second = Line(viewModel, 4, "second");
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);
        viewModel.InspectedStroke = 0;

        (BrushesPane pane, _) = Host(viewModel);

        Assert.Contains(BrushesPane.MixedWord, Text(pane, "ApplicationLabel"));
    }

    /// <summary>Two paths that agree are not reported as mixed, and the readout names the brush they agree on.</summary>
    [AvaloniaFact]
    public void ASelectionThatAgreesIsNotMixed()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        BrushSpec chisel = viewModel.Document.FindBrush("Chisel")!;

        PathItem first = Line(viewModel, 4, "first");
        first.Strokes[0] = first.Strokes[0] with { Brush = chisel };
        PathItem second = Line(viewModel, 4, "second");
        second.Strokes[0] = second.Strokes[0] with { Brush = chisel };
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);
        viewModel.InspectedStroke = 0;

        (BrushesPane pane, _) = Host(viewModel);

        string label = Text(pane, "ApplicationLabel");
        Assert.DoesNotContain(BrushesPane.MixedWord, label);
        Assert.Contains("Chisel", label);
    }

    // ---------------------------------------------------------------- the preview is the pipeline's

    /// <summary>
    /// **The preview is the pipeline's own geometry, not a picture of its own.**
    ///
    /// The nib half asserts the loops the preview holds are exactly `StrokeOutlineBuilder`'s for the same sample path
    /// and stroke - the call the canvas itself makes when it paints a nib - and the art half asserts the placements
    /// are exactly `PlacedArt.Resolve`'s. Asserting that the preview equals the seam is the point rather than a
    /// tautology: a preview built by any other route fails here, and that is the failure the issue names - a preview
    /// painted by different code is a preview that lies, and the first person to find out applies the brush.
    /// </summary>
    [AvaloniaFact]
    public void ThePreviewShowsThePipelinesOwnGeometry()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel", angle = 35.0, roundness = 0.3, diameter = 18.0 });
        PathItem art = Art(viewModel, "wave");
        Invoke(viewModel, "brush.create", new { name = "Mapper", kind = "art", asset = art.Id, size = 20.0 });

        (BrushesPane pane, _) = Host(viewModel);
        PathItem sample = BrushPreview.SamplePath;

        // A nib: the outline is the builder's, and there is no artwork to place.
        Select(pane, "Chisel");
        BrushSpec chisel = viewModel.Document.FindBrush("Chisel")!;
        StrokeSpec nibStroke = BrushPreview.SampleStroke(pane.Preview.StrokeWidth, chisel);
        string expected = Describe(StrokeOutlineBuilder.Outline(sample, nibStroke));

        Assert.NotEqual(string.Empty, expected);
        Assert.Equal(expected, Describe(pane.Preview.Outline));
        Assert.Empty(pane.Preview.Placed);

        // An art brush: the placements are the resolve seam's, and the outline is the stroke's own, which is the
        // same answer the canvas gets - an art brush contributes nothing to a plan.
        Select(pane, "Mapper");
        BrushSpec mapper = viewModel.Document.FindBrush("Mapper")!;
        IReadOnlyList<PlacedArt> expectedArt = PlacedArt.Resolve(viewModel.Document, sample, mapper);

        Assert.NotEmpty(expectedArt);
        Assert.Equal(expectedArt.Count, pane.Preview.Placed.Count);
        for (int i = 0; i < expectedArt.Count; i++)
        {
            Assert.Equal(expectedArt[i].Asset.Id, pane.Preview.Placed[i].Asset.Id);
            Assert.Equal(Describe(expectedArt[i].Placement.Transform), Describe(pane.Preview.Placed[i].Placement.Transform));
            Assert.Equal(expectedArt[i].Opacity, pane.Preview.Placed[i].Opacity, 9);
        }

        StrokeSpec artStroke = BrushPreview.SampleStroke(pane.Preview.StrokeWidth, mapper);
        Assert.Equal(
            Describe(StrokeOutlineBuilder.Outline(sample, artStroke)),
            Describe(pane.Preview.Outline));
    }

    /// <summary>Editing a parameter moves the preview, which is what makes it a preview of the thing being edited
    /// rather than a picture of whatever was there first.</summary>
    [AvaloniaFact]
    public void EditingABrushMovesThePreview()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel", diameter = 4.0 });
        (BrushesPane pane, _) = Host(viewModel);
        Select(pane, "Chisel");

        string before = Describe(pane.Preview.Outline);

        Type(Box(pane, "diameter"), "40");
        Settle();

        Assert.NotEqual(before, Describe(pane.Preview.Outline));
        Assert.Equal(
            Describe(StrokeOutlineBuilder.Outline(
                BrushPreview.SamplePath,
                BrushPreview.SampleStroke(pane.Preview.StrokeWidth, viewModel.Document.FindBrush("Chisel")))),
            Describe(pane.Preview.Outline));
    }

    // ---------------------------------------------------------------- the thread rule

    /// <summary>
    /// A document change can arrive on any thread - an operation invoked from a driver runs its handler on the
    /// caller's thread rather than marshalling - so the pane posts its UI work rather than writing a control where
    /// the change landed. This is the discipline <see cref="ColorsPane"/> took after #178, stated for this panel.
    /// </summary>
    [AvaloniaFact]
    public void AChangeFromAnotherThreadDoesNotWriteAControlOnThatThread()
    {
        var viewModel = new EditorViewModel();
        Invoke(viewModel, "brush.create", new { name = "Chisel" });
        (BrushesPane pane, _) = Host(viewModel);

        var library = pane.FindControl<ComboBox>("BrushLibraryBox")!;
        Assert.Equal(2, library.ItemCount); // (new brush), Chisel

        Exception? failure = null;

        // A thread of its own, not the thread pool: the change has to be off the UI thread and the assertion has to
        // be about that, not about which worker the pool happened to hand over.
        var thread = new Thread(() =>
        {
            try
            {
                Invoke(viewModel, "brush.create", new { name = "Flat" });
                viewModel.NotifyDocumentChanged();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.Start();
        thread.Join();
        Settle();

        // The posted work ran, and it ran on the UI thread: the new brush is in the list the control shows.
        Assert.True(failure is null, failure?.ToString());
        Assert.Equal(3, library.ItemCount);
        Assert.Contains("Flat", library.ItemsSource!.Cast<string>());
    }

    private static Guid Asset(EditorViewModel viewModel, string name)
        => viewModel.Document.AllItems().First(item => item.Name == name).Id;
}
