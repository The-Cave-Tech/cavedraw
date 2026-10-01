using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The appearance panel publishes the row it is showing, including the first one.
///
/// A click on a row already published the index - the panel's own selection handler does that. What did not was the
/// index it sets while **building the list**: that happens with the update guard raised, so the handler deliberately
/// ignores it, and the shared state stayed at "none" while the list highlighted row 0. The stroke inspector reads
/// that shared state, so a freshly selected path showed empty fields and the label "none" beside a panel plainly
/// describing the bottom stroke.
/// </summary>
public class AppearancePanePublishTests
{
    /// <summary>
    /// Selection is made **before** the pane is attached, because attaching is what subscribes the pane to the
    /// session - attaching first leaves it never having seen the selection, which is a harness mistake, not a bug.
    /// </summary>
    private static (AppearancePane Pane, EditorViewModel ViewModel) Host(params int[] strokes)
    {
        var viewModel = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        foreach (int width in strokes)
        {
            path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4));
        }

        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);

        var pane = new AppearancePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 420, Height = 600, Content = pane };
        window.Show();
        Settle();
        return (pane, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>**Showing a path's strokes publishes which one is being shown**, so the window can agree.</summary>
    [AvaloniaFact]
    public void ShowingAPathPublishesTheHighlightedRow()
    {
        (AppearancePane pane, EditorViewModel viewModel) = Host(2, 8);

        Assert.Equal(0, pane.FindControl<ListBox>("StrokeList")!.SelectedIndex);
        Assert.Equal(0, viewModel.InspectedStroke);
    }

    /// <summary>A path with no strokes publishes none rather than an index into a stack that is not there.</summary>
    [AvaloniaFact]
    public void APathWithNoStrokesPublishesNone()
    {
        (AppearancePane pane, EditorViewModel viewModel) = Host();

        Assert.Equal(-1, pane.FindControl<ListBox>("StrokeList")!.SelectedIndex);
        Assert.Equal(-1, viewModel.InspectedStroke);
    }
}
