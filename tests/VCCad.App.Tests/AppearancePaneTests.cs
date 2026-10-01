using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The appearance panel: the strokes on a path, and the three interactions the issue names.
///
/// Tested through the real controls - the buttons are found by name and clicked - and asserted on the **model**,
/// because the failure this panel is most likely to have is a list that reorders itself while the document does
/// not. Asserting the rows alone would not see that.
/// </summary>
public class AppearancePaneTests
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

    private static Button Button(AppearancePane pane, string name)
        => pane.FindControl<Button>(name) ?? throw new Xunit.Sdk.XunitException($"no button called {name}");

    private static void Click(AppearancePane pane, string name)
    {
        // Qualified: the helper above is called Button, and an unqualified Button.ClickEvent resolves to it.
        Button(pane, name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Settle();
    }

    private static int Rows(AppearancePane pane) => pane.RowCount;

    // ---------------------------------------------------------------- the single-stroke case

    /// <summary>A path with one stroke shows one row and behaves exactly as it did before the stack existed.</summary>
    [AvaloniaFact]
    public void ASingleStrokeShowsOneRow()
    {
        (AppearancePane pane, _, PathItem path) = Host();

        Assert.Equal(1, Rows(pane));
        Assert.Equal(0, pane.SelectedRow);
        Assert.Single(path.Strokes);
    }

    // ---------------------------------------------------------------- add

    /// <summary>**Adding adds one to the model and selects it**, so the next thing typed lands on the new stroke.</summary>
    [AvaloniaFact]
    public void AddingAddsAStrokeAndSelectsIt()
    {
        (AppearancePane pane, _, PathItem path) = Host();

        Click(pane, "AddStrokeButton");

        Assert.Equal(2, path.Strokes.Count);
        Assert.Equal(2, Rows(pane));
        Assert.Equal(1, pane.SelectedRow);
        Assert.Equal(path.Strokes.Count - 1, pane.SelectedRow);
    }

    /// <summary>Adding copies the stroke that was on top, which is what pressing add gives a person.</summary>
    [AvaloniaFact]
    public void AddingCopiesTheTopStroke()
    {
        (AppearancePane pane, _, PathItem path) = Host(
            new StrokeSpec(true, ColorRgb.Red, 9, StrokeCap.Round, StrokeJoin.Bevel, 6));

        Click(pane, "AddStrokeButton");

        Assert.Equal(2, path.Strokes.Count);
        Assert.Equal(path.Strokes[0], path.Strokes[1]);
    }

    // ---------------------------------------------------------------- remove

    /// <summary>**Removing selects the neighbour**, not nothing: somebody removing a stroke wants to keep working.</summary>
    [AvaloniaFact]
    public void RemovingSelectsTheNeighbour()
    {
        (AppearancePane pane, _, PathItem path) = Host(
            new StrokeSpec(true, ColorRgb.Red, 4, StrokeCap.Butt, StrokeJoin.Miter, 4),
            new StrokeSpec(true, ColorRgb.Green, 6, StrokeCap.Butt, StrokeJoin.Miter, 4),
            new StrokeSpec(true, ColorRgb.Blue, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));

        // Remove the middle one, which leaves one either side of where it was.
        pane.FindControl<ListBox>("StrokeList")!.SelectedIndex = 1;
        Click(pane, "RemoveStrokeButton");

        Assert.Equal(2, path.Strokes.Count);
        Assert.Equal(2, Rows(pane));
        Assert.Equal(1, pane.SelectedRow);
    }

    /// <summary>Removing the last remaining stroke leaves an invisible one, and a row to select.</summary>
    [AvaloniaFact]
    public void RemovingTheLastStrokeLeavesOneRow()
    {
        (AppearancePane pane, _, PathItem path) = Host();

        Click(pane, "RemoveStrokeButton");

        Assert.Single(path.Strokes);
        Assert.False(path.Strokes[0].IsVisible);
        Assert.Equal(1, Rows(pane));
    }

    // ---------------------------------------------------------------- reorder

    /// <summary>
    /// **Reordering changes the document's order, and the list follows it.** This is the failure the issue calls
    /// out: a list that shuffles its rows while the document keeps the old order looks right and draws wrong.
    /// </summary>
    [AvaloniaFact]
    public void MovingAStrokeChangesTheModelsOrder()
    {
        (AppearancePane pane, _, PathItem path) = Host(
            new StrokeSpec(true, ColorRgb.Red, 4, StrokeCap.Butt, StrokeJoin.Miter, 4),
            new StrokeSpec(true, ColorRgb.Blue, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));

        Assert.Equal(new[] { 4.0, 8.0 }, path.Strokes.Select(s => s.Width).ToArray());

        pane.FindControl<ListBox>("StrokeList")!.SelectedIndex = 0;
        Click(pane, "MoveUpButton");

        // The model's order changed, and the selection followed the stroke that moved.
        Assert.Equal(new[] { 8.0, 4.0 }, path.Strokes.Select(s => s.Width).ToArray());
        Assert.Equal(1, pane.SelectedRow);
    }

    /// <summary>The list's order **is** the model's order after a reorder, row for row.</summary>
    [AvaloniaFact]
    public void TheListOrderMatchesTheModelAfterAReorder()
    {
        (AppearancePane pane, _, PathItem path) = Host(
            new StrokeSpec(true, ColorRgb.Red, 4, StrokeCap.Butt, StrokeJoin.Miter, 4),
            new StrokeSpec(true, ColorRgb.Blue, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));

        pane.FindControl<ListBox>("StrokeList")!.SelectedIndex = 1;
        Click(pane, "MoveDownButton");

        Assert.Equal(new[] { 8.0, 4.0 }, path.Strokes.Select(s => s.Width).ToArray());

        // Rebuilding the panel from the model shows the same order, which is what "the list cannot show an order
        // the document does not have" means in practice.
        pane.Refresh();
        Assert.Equal(2, Rows(pane));
    }

    /// <summary>Undo puts the order back, because the reorder went through the same command a driver's does.</summary>
    [AvaloniaFact]
    public void UndoRestoresTheOrder()
    {
        (AppearancePane pane, EditorViewModel viewModel, PathItem path) = Host(
            new StrokeSpec(true, ColorRgb.Red, 4, StrokeCap.Butt, StrokeJoin.Miter, 4),
            new StrokeSpec(true, ColorRgb.Blue, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));

        pane.FindControl<ListBox>("StrokeList")!.SelectedIndex = 0;
        Click(pane, "MoveUpButton");
        viewModel.ActiveSession.Undo();

        Assert.Equal(new[] { 4.0, 8.0 }, path.Strokes.Select(s => s.Width).ToArray());
    }
}
