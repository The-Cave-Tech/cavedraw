using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The rows above a selected object open too - the layer it lives on, and the page.
///
/// The walk up the tree stops wherever a node does not know its parent. Layer and artboard rows are built
/// separately from object rows, so they were the two links that were missing: selecting anything at the top
/// level of a page opened nothing, because there was no ancestor to open, and the selected row stayed hidden
/// inside a collapsed page. The panel then showed no selection at all - which is exactly what "I don't see
/// the selection happening in the object browser" looks like.
/// </summary>
public class ObjectsPaneAncestorRowsTests
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

    [AvaloniaFact]
    public void SelectingATopLevelObjectOpensItsLayerAndPage()
    {
        var viewModel = new EditorViewModel();
        var pane = new ObjectsPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = pane };
        window.Show();
        Settle();

        Artboard board = viewModel.Document.Artboards[0];
        Layer layer = board.Layers[0];

        PathItem piece = PathFactory.CreateRectangle("piece", new Rect2D(300, 300, 120, 120));
        piece.Fill = FillSpec.Solid(ColorRgb.Black);

        var group = new ArtGroup { Name = "Group" };
        group.AddItem(piece);
        layer.AddItem(group);
        viewModel.NotifyDocumentChanged();
        Settle();

        viewModel.SelectObject(group);
        Settle();

        // The layer row is the one that matters: a page is a root and is on screen whether or not it is
        // expanded, but a layer inside it is not. This is the assertion that failed while the panel's rows
        // were linked only parent-to-child - the walk up ended early and the selection stayed hidden.
        Assert.True(pane.NodeFor(layer)!.IsExpanded, "the layer row should have opened");
        Assert.Same(pane.NodeFor(group), pane.Tree.SelectedItem);

        window.Close();
    }
}
