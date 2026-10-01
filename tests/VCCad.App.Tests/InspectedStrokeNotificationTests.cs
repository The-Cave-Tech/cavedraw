using Avalonia.Headless.XUnit;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The shared inspected-stroke state has to tell its listeners when the **selection** changes, not only when
/// somebody assigns to it.
///
/// The property clamps on read, so its value is never stale - but a panel binding to it only re-reads when
/// something raises a change. Without that, changing the selection leaves both panels showing the stroke from the
/// selection before, which is the disagreement the shared state exists to prevent.
/// </summary>
public class InspectedStrokeNotificationTests
{
    private static PathItem Path(int strokes)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        path.Strokes.Clear();
        for (int i = 0; i < strokes; i++)
        {
            path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 2 + i, StrokeCap.Butt, StrokeJoin.Miter, 4));
        }

        return path;
    }

    [AvaloniaFact]
    public void ClearingTheSelectionTellsListeners()
    {
        var viewModel = new EditorViewModel();
        PathItem path = Path(3);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);
        viewModel.InspectedStroke = 2;

        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.ClearSelection();

        Assert.Contains(nameof(EditorViewModel.InspectedStroke), raised);
        Assert.Contains(nameof(EditorViewModel.InspectedStrokeLabel), raised);
        Assert.Equal(-1, viewModel.InspectedStroke);
        Assert.Equal("none", viewModel.InspectedStrokeLabel);
    }

    /// <summary>And selecting a different path tells them too, because its stack may be a different length.</summary>
    [AvaloniaFact]
    public void SelectingAnotherPathTellsListeners()
    {
        var viewModel = new EditorViewModel();
        PathItem first = Path(4);
        PathItem second = Path(2);
        viewModel.Document.Artboards[0].Layers[0].AddItem(first);
        viewModel.Document.Artboards[0].Layers[0].AddItem(second);

        viewModel.SelectObject(first);
        viewModel.InspectedStroke = 3;

        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.SelectObject(second);

        Assert.Contains(nameof(EditorViewModel.InspectedStroke), raised);

        // The shorter stack clamps what is reported, so the panels cannot describe a stroke that is not there.
        Assert.Equal(1, viewModel.InspectedStroke);
        Assert.Equal("stroke 2 of 2", viewModel.InspectedStrokeLabel);
    }
}
