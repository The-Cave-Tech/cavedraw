using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Showing and hiding one stroke of a stack - the last thing #114 asked the panel for.
///
/// Driven through the row's own button, found in the visual tree, because the button lives inside an item template
/// and the gesture is the behaviour under test. Every assertion is on the **model**, including the part that
/// matters most: a hidden stroke keeps its width, caps, joins and dash, because it is a member somebody is about to
/// switch back on.
/// </summary>
public class StrokeVisibilityTests
{
    private static (AppearancePane Pane, EditorViewModel ViewModel, PathItem Path) Host(params StrokeSpec[] strokes)
    {
        var viewModel = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        path.Strokes.AddRange(strokes.Length > 0
            ? strokes
            : new[] { new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4) });

        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);

        var pane = new AppearancePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 400, Height = 400, Content = pane };
        window.Show();
        Settle();

        return (pane, viewModel, path);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>The row's toggle button, found where the template actually put it.</summary>
    private static Button ToggleAt(AppearancePane pane, int row)
    {
        var list = pane.FindControl<ListBox>("StrokeList")
            ?? throw new Xunit.Sdk.XunitException("no stroke list");

        Control container = list.ContainerFromIndex(row)
            ?? throw new Xunit.Sdk.XunitException($"row {row} is not realised");

        Button? button = container.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content is string text && text is "Hide" or "Show");

        return button ?? throw new Xunit.Sdk.XunitException($"row {row} has no toggle");
    }

    private static void Click(AppearancePane pane, int row)
    {
        ToggleAt(pane, row).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Settle();
    }

    /// <summary>**Hiding keeps everything except the visibility** - the settings are what the person is working on.</summary>
    [AvaloniaFact]
    public void HidingAStrokeKeepsItsSettings()
    {
        var stroke = new StrokeSpec(true, ColorRgb.Red, 7, StrokeCap.Round, StrokeJoin.Bevel, 9,
            StrokeAlignment.Center, new DashPattern(new[] { 3.0, 1.5 }, 2.0));

        (AppearancePane pane, _, PathItem path) = Host(stroke);

        Assert.Equal("Hide", ToggleAt(pane, 0).Content as string);

        Click(pane, 0);

        Assert.False(path.Strokes[0].IsVisible);
        Assert.Equal(7.0, path.Strokes[0].Width, 6);
        Assert.Equal(StrokeCap.Round, path.Strokes[0].Cap);
        Assert.Equal(StrokeJoin.Bevel, path.Strokes[0].Join);
        Assert.Equal(9.0, path.Strokes[0].MiterLimit, 6);
        Assert.Equal(new[] { 3.0, 1.5 }, path.Strokes[0].Dash.Segments.ToArray());
        Assert.Equal(2.0, path.Strokes[0].Dash.Offset, 6);
    }

    /// <summary>And the button now offers the opposite, so the row says what it will do.</summary>
    [AvaloniaFact]
    public void TheButtonOffersTheOppositeAfterwards()
    {
        (AppearancePane pane, _, PathItem path) = Host();

        Click(pane, 0);
        Assert.Equal("Show", ToggleAt(pane, 0).Content as string);

        Click(pane, 0);
        Assert.True(path.Strokes[0].IsVisible);
        Assert.Equal("Hide", ToggleAt(pane, 0).Content as string);
    }

    /// <summary>Only the row that was clicked changes, which is the whole point of a per-row toggle.</summary>
    [AvaloniaFact]
    public void OnlyTheClickedRowChanges()
    {
        (AppearancePane pane, _, PathItem path) = Host(
            new StrokeSpec(true, ColorRgb.Red, 4, StrokeCap.Butt, StrokeJoin.Miter, 4),
            new StrokeSpec(true, ColorRgb.Blue, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));

        Click(pane, 0);

        Assert.False(path.Strokes[0].IsVisible);
        Assert.True(path.Strokes[1].IsVisible);
    }

    /// <summary>Undo puts it back, because the toggle went through the same command a driver's would.</summary>
    [AvaloniaFact]
    public void UndoRestoresTheVisibility()
    {
        (AppearancePane pane, EditorViewModel viewModel, PathItem path) = Host();

        Click(pane, 0);
        Assert.False(path.Strokes[0].IsVisible);

        viewModel.ActiveSession.Undo();

        Assert.True(path.Strokes[0].IsVisible);
    }

    // ---------------------------------------------------------------- the operation

    [Fact]
    public void TheOperationHidesTheTopStrokeByDefault()
    {
        var viewModel = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Red, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Blue, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);

        var context = new AutomationContext { ViewModel = viewModel };
        EditorOperations.Invoke(context, "style.setStrokeVisible",
            System.Text.Json.JsonSerializer.SerializeToElement(new { visible = false }));

        Assert.True(path.Strokes[0].IsVisible);
        Assert.False(path.Strokes[1].IsVisible);
        Assert.Equal(8.0, path.Strokes[1].Width, 6);
    }
}
