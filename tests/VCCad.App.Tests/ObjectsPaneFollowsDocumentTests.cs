using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The object tree follows the document that is actually on screen.
///
/// The panel is attached once, when the shell is built, and the editor starts with a default empty
/// document. Opening a file adds a **second** document and makes it active. If the tree does not follow
/// that switch it keeps showing the first document's rows - which is what "I don't see the selection
/// happening in the object browser" looks like: the canvas selects a piece, the panel is looking at a
/// different document, and there is no row to highlight.
/// </summary>
public class ObjectsPaneFollowsDocumentTests
{
    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static PathItem Box(string name, Rect2D box)
    {
        PathItem path = PathFactory.CreateRectangle(name, box);
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    [AvaloniaFact]
    public void OpeningASecondDocumentAndSelectingInItOpensTheTreeToIt()
    {
        var viewModel = new EditorViewModel();
        var pane = new ObjectsPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = pane };
        window.Show();
        Settle();

        // The editor's own empty document is open first, as it is when the shell is built.
        Assert.NotNull(viewModel.ActiveSession);

        // Opening a file adds a second document and makes it active.
        var imported = new CadDocument { Name = "Imported" };
        Artboard board = imported.AddArtboard(new Size2D(612, 792), "Page 1", new Point2D(0, 0));
        Layer layer = board.AddLayer("Layer 1");

        PathItem piece = Box("piece", new Rect2D(300, 300, 120, 120));
        var inner = new ArtGroup { Name = "Layer 1" };
        inner.AddItem(piece);
        var outer = new ArtGroup { Name = "Group" };
        outer.AddItem(inner);
        layer.AddItem(outer);

        viewModel.AddDocument(imported);
        Settle();

        Assert.NotNull(pane.NodeFor(outer));
        Assert.NotNull(pane.NodeFor(piece));

        viewModel.SelectObject(piece);
        Settle();

        Assert.True(pane.NodeFor(outer)!.IsExpanded, "the outer group should have opened");
        Assert.True(pane.NodeFor(inner)!.IsExpanded, "the inner group should have opened");
        Assert.Same(pane.NodeFor(piece), pane.Tree.SelectedItem);

        window.Close();
    }
}
