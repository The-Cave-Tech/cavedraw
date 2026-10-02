using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The stroke inspector describing a **selection**, which is not obliged to agree with itself.
///
/// The pane used to read the first selected path's stroke and present it as everyone's: a person selected two
/// paths whose widths differed, saw one of the two numbers, and believed it described what they had selected.
/// `StrokeSummary` already knows which members the selection agrees on and which are **mixed**, so the pane has to
/// say so rather than pick a member and hope.
///
/// Everything is asserted on the **model** and cross-checked against `StrokeSummary.Of`, because the failure this
/// guards against is a field that shows one path's value while the edit reaches every path's.
/// </summary>
public class StrokePaneMixedSelectionTests
{
    // ---------------------------------------------------------------- driving the pane

    private static (StrokePane Pane, EditorViewModel ViewModel, PathItem First, PathItem Second) Host(
        StrokeSpec first, StrokeSpec second, int inspected = 0)
    {
        var viewModel = new EditorViewModel();
        PathItem firstPath = Line(viewModel, first);
        PathItem secondPath = Line(viewModel, second);

        // Built **before** Attach: attaching is what subscribes the pane to the session, and attaching first would
        // leave it never having seen the selection at all.
        viewModel.SelectObject(firstPath);
        viewModel.ToggleObjectSelection(secondPath);
        viewModel.InspectedStroke = inspected;

        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 460, Height = 760, Content = pane };
        window.Show();
        Settle();

        return (pane, viewModel, firstPath, secondPath);
    }

    private static PathItem Line(EditorViewModel viewModel, params StrokeSpec[] strokes)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        path.Strokes.AddRange(strokes);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static StrokeSpec Stroke(double width, StrokeCap cap = StrokeCap.Butt,
        StrokeJoin join = StrokeJoin.Miter, double miter = 4, StrokeAlignment alignment = StrokeAlignment.Center,
        DashPattern? dash = null)
        => new(true, ColorRgb.Black, width, cap, join, miter, alignment, dash ?? DashPattern.None);

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static TextBox Box(StrokePane pane, string name)
        => pane.FindControl<TextBox>(name) ?? throw new Xunit.Sdk.XunitException($"no box called {name}");

    private static ComboBox Combo(StrokePane pane, string name)
        => pane.FindControl<ComboBox>(name) ?? throw new Xunit.Sdk.XunitException($"no combo called {name}");

    private static TextBlock Mixed(StrokePane pane)
        => pane.FindControl<TextBlock>("MixedLabel") ?? throw new Xunit.Sdk.XunitException("no mixed label");

    /// <summary>Types a value and commits it the way the field does - on losing focus.</summary>
    private static void Commit(StrokePane pane, string name, string text)
    {
        TextBox box = Box(pane, name);
        box.Text = text;
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Settle();
    }

    private static int CapIndex(StrokeCap cap) => cap switch { StrokeCap.Round => 1, StrokeCap.Square => 2, _ => 0 };

    private static int JoinIndex(StrokeJoin join) => join switch { StrokeJoin.Round => 1, StrokeJoin.Bevel => 2, _ => 0 };

    private static int AlignIndex(StrokeAlignment alignment)
        => alignment switch { StrokeAlignment.Inside => 1, StrokeAlignment.Outside => 2, _ => 0 };

    // ---------------------------------------------------------------- what a mixed selection reads as

    /// <summary>
    /// One disagreeing member does not hide the others: the width differs and is **mixed**, and the cap, join,
    /// miter limit, alignment and dash the two paths agree on are still shown. Blanking the whole panel because one
    /// member differs would throw away most of what a panel is for.
    /// </summary>
    [AvaloniaFact]
    public void ADisagreeingMemberIsMixedAndTheAgreeingOnesKeepTheirValue()
    {
        (StrokePane pane, _, _, _) = Host(
            Stroke(4, cap: StrokeCap.Round, join: StrokeJoin.Bevel, miter: 6, alignment: StrokeAlignment.Inside,
                dash: new DashPattern(new double[] { 4, 3 })),
            Stroke(8, cap: StrokeCap.Round, join: StrokeJoin.Bevel, miter: 6, alignment: StrokeAlignment.Inside,
                dash: new DashPattern(new double[] { 4, 3 })));

        Assert.Equal("mixed", Box(pane, "StrokeWidthBox").Text);
        Assert.Equal("6", Box(pane, "MiterBox").Text);
        Assert.Equal(1, Combo(pane, "StrokeCapBox").SelectedIndex);
        Assert.Equal(2, Combo(pane, "StrokeJoinBox").SelectedIndex);
        Assert.Equal(1, Combo(pane, "StrokeAlignBox").SelectedIndex);
        Assert.Equal(1, Combo(pane, "StrokeDashBox").SelectedIndex);
    }

    /// <summary>**Every member that disagrees says mixed rather than one path's value**, and the pane names them,
    /// so a person cannot read a number here and believe it describes the selection.</summary>
    [AvaloniaFact]
    public void EveryDisagreeingMemberSaysMixed()
    {
        (StrokePane pane, _, _, _) = Host(
            Stroke(4, cap: StrokeCap.Butt, join: StrokeJoin.Miter, miter: 4, alignment: StrokeAlignment.Center,
                dash: DashPattern.None),
            Stroke(8, cap: StrokeCap.Round, join: StrokeJoin.Bevel, miter: 6, alignment: StrokeAlignment.Inside,
                dash: new DashPattern(new double[] { 4, 3 })));

        Assert.Equal("mixed", Box(pane, "StrokeWidthBox").Text);
        Assert.Equal("mixed", Box(pane, "MiterBox").Text);

        foreach (string name in new[] { "StrokeCapBox", "StrokeJoinBox", "StrokeAlignBox", "StrokeDashBox" })
        {
            Assert.Equal(-1, Combo(pane, name).SelectedIndex);
            Assert.Equal("mixed", Combo(pane, name).PlaceholderText);
        }

        string named = Mixed(pane).Text ?? string.Empty;
        Assert.True(Mixed(pane).IsVisible);
        foreach (string member in new[] { "width", "cap", "join", "miter", "dash", "align" })
        {
            Assert.True(named.Contains(member, StringComparison.OrdinalIgnoreCase),
                $"the mixed readout should name '{member}' but says '{named}'");
        }
    }

    /// <summary>
    /// **Nothing is shown as common when `StrokeSummary` says it is mixed.** The summary is the authority on
    /// agreement, so the test asks it directly for each of a set of agreement patterns and requires the pane to
    /// agree with it member by member - in both directions, because a pane that says "mixed" over an agreeing
    /// selection is as unusable as one that shows a number over a disagreeing one.
    /// </summary>
    [AvaloniaFact]
    public void NothingIsShownAsCommonWhenTheSummarySaysItIsMixed()
    {
        var patterns = new (StrokeSpec First, StrokeSpec Second)[]
        {
            (Stroke(4), Stroke(8)),
            (Stroke(4), Stroke(4)),
            (Stroke(4, cap: StrokeCap.Round), Stroke(4, cap: StrokeCap.Butt)),
            (Stroke(4, join: StrokeJoin.Round), Stroke(4, join: StrokeJoin.Round)),
            (Stroke(4, miter: 6), Stroke(4, miter: 9)),
            (Stroke(4, alignment: StrokeAlignment.Inside), Stroke(4, alignment: StrokeAlignment.Outside)),
            (Stroke(4, dash: new DashPattern(new double[] { 4, 3 })), Stroke(4, dash: DashPattern.None)),
            (Stroke(4, dash: new DashPattern(new double[] { 4, 3 })),
                Stroke(4, dash: new DashPattern(new double[] { 4, 3 }))),
            (Stroke(4, cap: StrokeCap.Round, miter: 6), Stroke(8, cap: StrokeCap.Round, miter: 9)),
        };

        foreach ((StrokeSpec first, StrokeSpec second) in patterns)
        {
            (StrokePane pane, EditorViewModel viewModel, _, _) = Host(first, second);
            StrokeSummary summary = StrokeSummary.Of(
                viewModel.ActiveSession.SelectedPaths(), 0);

            string context = $"widths {first.Width}/{second.Width}";

            if (summary.WidthMixed)
            {
                Assert.True(Box(pane, "StrokeWidthBox").Text == "mixed",
                    $"{context}: the summary says the width is mixed but the pane shows "
                    + $"'{Box(pane, "StrokeWidthBox").Text}'");
            }
            else
            {
                Assert.Equal(summary.Width!.Value.ToString("0.##"), Box(pane, "StrokeWidthBox").Text);
            }

            if (summary.MiterMixed)
            {
                Assert.Equal("mixed", Box(pane, "MiterBox").Text);
            }
            else
            {
                Assert.Equal(summary.MiterLimit!.Value.ToString("0.##"), Box(pane, "MiterBox").Text);
            }

            Assert.Equal(summary.CapMixed ? -1 : CapIndex(summary.Cap!.Value), Combo(pane, "StrokeCapBox").SelectedIndex);
            Assert.Equal(summary.JoinMixed ? -1 : JoinIndex(summary.Join!.Value), Combo(pane, "StrokeJoinBox").SelectedIndex);
            Assert.Equal(
                summary.AlignmentMixed ? -1 : AlignIndex(summary.Alignment!.Value),
                Combo(pane, "StrokeAlignBox").SelectedIndex);

            // Dash is a summary member like the rest, so the pane must agree with the summary about it in both
            // directions: an explicit "mixed" where the summary disagrees, and a shown value where it does not.
            if (summary.DashMixed)
            {
                Assert.Equal(-1, Combo(pane, "StrokeDashBox").SelectedIndex);
                Assert.Equal("mixed", Combo(pane, "StrokeDashBox").PlaceholderText);
            }
            else
            {
                Assert.True(Combo(pane, "StrokeDashBox").SelectedIndex >= 0,
                    $"{context}: the summary agrees on the dash but the pane shows no value");
                Assert.True(string.IsNullOrEmpty(Combo(pane, "StrokeDashBox").PlaceholderText),
                    $"{context}: the summary agrees on the dash but the pane says "
                    + $"'{Combo(pane, "StrokeDashBox").PlaceholderText}'");
            }
        }
    }

    // ---------------------------------------------------------------- where an edit lands

    /// <summary>
    /// **An edit to an agreeing member does not write the mixed one.** This is the defect the mixed readout exists
    /// to prevent, and it is not a display bug: the old pane read the first path's width (4), so choosing a cap
    /// wrote 4 over the second path's 8 - one path's value applied to everybody, from a field the person never
    /// touched.
    /// </summary>
    [AvaloniaFact]
    public void AnEditToAnAgreeingMemberLeavesTheMixedMemberAlone()
    {
        (StrokePane pane, _, PathItem first, PathItem second) = Host(Stroke(4), Stroke(8));

        Combo(pane, "StrokeCapBox").SelectedIndex = 1; // Round
        Settle();

        Assert.Equal(StrokeCap.Round, first.Strokes[0].Cap);
        Assert.Equal(StrokeCap.Round, second.Strokes[0].Cap);
        Assert.Equal(4.0, first.Strokes[0].Width, 6);
        Assert.Equal(8.0, second.Strokes[0].Width, 6);
    }

    /// <summary>Typing into a mixed member is how the disagreement is resolved, and it lands on every path the
    /// inspector is describing.</summary>
    [AvaloniaFact]
    public void AnEditToAMixedMemberResolvesIt()
    {
        (StrokePane pane, _, PathItem first, PathItem second) = Host(Stroke(4), Stroke(8));

        Commit(pane, "StrokeWidthBox", "10");

        Assert.Equal(10.0, first.Strokes[0].Width, 6);
        Assert.Equal(10.0, second.Strokes[0].Width, 6);
    }

    /// <summary>The other five members land the same way, and one gesture is still one undo step.</summary>
    [AvaloniaFact]
    public void AnEditFromThePaneLandsWhereTheInspectorSaysItWill()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem first, PathItem second) = Host(Stroke(4), Stroke(8));

        Combo(pane, "StrokeJoinBox").SelectedIndex = 2; // Bevel
        Settle();
        Commit(pane, "MiterBox", "7");

        Assert.Equal(StrokeJoin.Bevel, first.Strokes[0].Join);
        Assert.Equal(StrokeJoin.Bevel, second.Strokes[0].Join);
        Assert.Equal(7.0, first.Strokes[0].MiterLimit, 6);
        Assert.Equal(7.0, second.Strokes[0].MiterLimit, 6);

        viewModel.Undo();
        viewModel.Undo();

        Assert.Equal(StrokeJoin.Miter, first.Strokes[0].Join);
        Assert.Equal(4.0, first.Strokes[0].MiterLimit, 6);
    }

    /// <summary>A selection with nothing mixed says nothing about mixing - the readout is not always-on chrome.</summary>
    [AvaloniaFact]
    public void AnAgreeingSelectionShowsNoMixedReadout()
    {
        (StrokePane pane, _, _, _) = Host(Stroke(4), Stroke(4));

        Assert.False(Mixed(pane).IsVisible);
        Assert.Equal("4", Box(pane, "StrokeWidthBox").Text);
    }

    /// <summary>One path is a selection of one, and a selection of one cannot disagree with itself.</summary>
    [AvaloniaFact]
    public void ASingleSelectedPathIsNeverMixed()
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, Stroke(4));
        viewModel.SelectObject(path);
        viewModel.InspectedStroke = 0;

        var pane = new StrokePane();
        pane.Attach(viewModel);
        var window = new Window { Width = 460, Height = 760, Content = pane };
        window.Show();
        Settle();

        Assert.False(Mixed(pane).IsVisible);
        Assert.Equal("4", Box(pane, "StrokeWidthBox").Text);
    }
}
