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
/// Selecting an item opens the object tree to it.
///
/// Seeing the selection in the tree is how a person learns what they just picked, which is exactly what is
/// needed when the artwork is nested several levels deep - the LILLIE page is `Group > Group > Layer 1 >
/// piece`, and with the tree collapsed there is nothing on screen that says which of those is selected.
///
/// **Adding to a multi-selection must not do it.** Re-opening the tree on every additive click moves the
/// panel around underneath the person while they are building a selection, and loses whatever they had
/// expanded deliberately.
/// </summary>
public class ObjectsPaneRevealTests
{
    private static (Window Window, ObjectsPane Pane, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var pane = new ObjectsPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = pane };
        window.Show();
        Settle();
        return (window, pane, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (ArtGroup Outer, ArtGroup Inner, PathItem Piece) Nested(EditorViewModel viewModel)
    {
        Artboard board = viewModel.Document.Artboards[0];

        PathItem piece = PathFactory.CreateRectangle("piece", new Rect2D(300, 300, 120, 120));
        piece.Fill = FillSpec.Solid(ColorRgb.Black);

        var inner = new ArtGroup { Name = "Layer 1" };
        inner.AddItem(piece);

        var outer = new ArtGroup { Name = "Group" };
        outer.AddItem(inner);

        board.Layers[0].AddItem(outer);
        viewModel.NotifyDocumentChanged();
        Settle();

        return (outer, inner, piece);
    }

    /// <summary>A fresh single selection opens the tree down to it.</summary>
    [AvaloniaFact]
    public void SelectingAnItemOpensTheTreeToIt()
    {
        (Window window, ObjectsPane pane, EditorViewModel viewModel) = Host();
        try
        {
            (ArtGroup outer, ArtGroup inner, PathItem piece) = Nested(viewModel);

            viewModel.ClearSelection();
            Settle();
            Assert.False(pane.NodeFor(outer)!.IsExpanded);
            Assert.False(pane.NodeFor(inner)!.IsExpanded);

            viewModel.SelectObject(piece);
            Settle();

            Assert.True(pane.NodeFor(outer)!.IsExpanded, "the outer group should have opened");
            Assert.True(pane.NodeFor(inner)!.IsExpanded, "the inner group should have opened");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A selection that is not a single item leaves the tree alone - the additive case, and any multi-item
    /// selection.
    /// </summary>
    [AvaloniaFact]
    public void AddingToTheSelectionLeavesTheTreeAlone()
    {
        (Window window, ObjectsPane pane, EditorViewModel viewModel) = Host();
        try
        {
            (ArtGroup outer, ArtGroup inner, PathItem piece) = Nested(viewModel);

            PathItem other = PathFactory.CreateRectangle("other", new Rect2D(500, 500, 60, 60));
            other.Fill = FillSpec.Solid(ColorRgb.Black);
            viewModel.Document.Artboards[0].Layers[0].AddItem(other);
            viewModel.NotifyDocumentChanged();
            Settle();

            viewModel.ClearSelection();
            Settle();

            viewModel.SelectRange(new LayerItem[] { piece, other }, additive: false);
            Settle();

            Assert.False(pane.NodeFor(outer)!.IsExpanded);
            Assert.False(pane.NodeFor(inner)!.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Selecting the same item again is idempotent: nothing collapses behind it.</summary>
    [AvaloniaFact]
    public void RevealingTwiceChangesNothing()
    {
        (Window window, ObjectsPane pane, EditorViewModel viewModel) = Host();
        try
        {
            (ArtGroup outer, ArtGroup inner, PathItem piece) = Nested(viewModel);

            viewModel.SelectObject(piece);
            Settle();
            viewModel.SelectObject(piece);
            Settle();

            Assert.True(pane.NodeFor(outer)!.IsExpanded);
            Assert.True(pane.NodeFor(inner)!.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }
}
