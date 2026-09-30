using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Drilling into nested artwork, through the real pointer path.
///
/// A single click takes the **outermost** group, and each double-click descends one level. This is what
/// makes a container selectable with the pointer at all - otherwise a group can only be reached by its name
/// - and it is how a person gets at what is inside one.
///
/// Driven through `InputInjection.Click(..., clickCount: n)` rather than by calling the engine, because the
/// click count *is* the gesture: a test that called `Drill` directly would prove the engine works and say
/// nothing about whether the canvas passes the count through.
/// </summary>
public class CanvasDrillTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>page > group "Group" > group "Layer 1" > the piece, and the piece's own point.</summary>
    private static (ArtGroup Outer, ArtGroup Inner, PathItem Piece, Point2D At) Nested(EditorViewModel viewModel)
    {
        Artboard board = viewModel.Document.Artboards[0];

        PathItem piece = PathFactory.CreateRectangle("piece", new Rect2D(300, 300, 120, 120));
        piece.Fill = FillSpec.Solid(ColorRgb.Black);

        var inner = new ArtGroup { Name = "Layer 1" };
        inner.AddItem(piece);

        var outer = new ArtGroup { Name = "Group" };
        outer.AddItem(inner);

        board.Layers[0].AddItem(outer);
        viewModel.Tool = EditorTool.Select;
        Settle();

        return (outer, inner, piece, new Point2D(board.X + 360, board.Y + 360));
    }

    private static void Click(Window window, CanvasWorkspace workspace, Point2D at, int clickCount)
    {
        Point p = workspace.ModelToWindow(at);
        InputInjection.Click(window, p.X, p.Y, clickCount: clickCount, shift: false);
        Settle();
    }

    private static List<LayerItem> Selected(EditorViewModel viewModel) => viewModel.SelectedObjects.ToList();

    /// <summary>A single click on nested artwork selects the outermost group.</summary>
    [AvaloniaFact]
    public void ASingleClickSelectsTheOutermostGroup()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            (ArtGroup outer, _, _, Point2D at) = Nested(viewModel);
            viewModel.ClearSelection();
            Settle();

            Click(window, workspace, at, 1);

            Assert.Equal(new LayerItem[] { outer }, Selected(viewModel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A double-click descends into the group, and repeated double-clicks reach the object.
    ///
    /// The assertion here is "went inside the group, and stayed inside it" rather than "exactly one level
    /// further". `InputInjection.Click(clickCount: 2)` delivers **two** presses carrying that count, so one
    /// call descends two levels - an artefact of the harness, not of the app, whose real double-click is a
    /// single counted press. The level-by-level rule is pinned precisely in `SelectionDrillTests`, where a
    /// click count is an argument and there is no such ambiguity; what this test adds is that the canvas
    /// passes the count through and the drill reaches the artwork inside the group.
    /// </summary>
    [AvaloniaFact]
    public void ADoubleClickDescendsIntoTheGroup()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            (ArtGroup outer, ArtGroup inner, PathItem piece, Point2D at) = Nested(viewModel);
            viewModel.ClearSelection();
            Settle();

            Click(window, workspace, at, 1);
            Assert.Equal(new LayerItem[] { outer }, Selected(viewModel));

            Click(window, workspace, at, 2);
            LayerItem descended = Assert.Single(Selected(viewModel));
            Assert.NotSame(outer, descended);

            // Inside the group, not beside it.
            Assert.Contains(descended, new LayerItem[] { inner, piece });

            // And another double-click reaches the object itself.
            Click(window, workspace, at, 2);
            Assert.Equal(new LayerItem[] { piece }, Selected(viewModel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>At the bottom the drill stops: it does not wrap, and it does not clear.</summary>
    [AvaloniaFact]
    public void TheDrillStopsAtTheObject()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            (_, _, PathItem piece, Point2D at) = Nested(viewModel);
            viewModel.ClearSelection();
            Settle();

            Click(window, workspace, at, 1);
            Click(window, workspace, at, 2);
            Click(window, workspace, at, 2);
            Click(window, workspace, at, 2);

            Assert.Equal(new LayerItem[] { piece }, Selected(viewModel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A top-level object still selects in one click - there is no group to reach first.</summary>
    [AvaloniaFact]
    public void ATopLevelObjectSelectsInOneClick()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Artboard board = viewModel.Document.Artboards[0];
            PathItem loose = PathFactory.CreateRectangle("loose", new Rect2D(300, 300, 120, 120));
            loose.Fill = FillSpec.Solid(ColorRgb.Black);
            board.Layers[0].AddItem(loose);
            viewModel.Tool = EditorTool.Select;
            Settle();

            viewModel.ClearSelection();
            Settle();
            Click(window, workspace, new Point2D(board.X + 360, board.Y + 360), 1);

            Assert.Equal(new LayerItem[] { loose }, Selected(viewModel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A click on empty space still clears, and a marquee still starts.</summary>
    [AvaloniaFact]
    public void AClickOnEmptySpaceClears()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            (_, _, PathItem piece, _) = Nested(viewModel);
            viewModel.SelectRange(new LayerItem[] { piece }, additive: false);
            Settle();

            Click(window, workspace, new Point2D(60, 60), 1);

            Assert.Empty(Selected(viewModel));
        }
        finally
        {
            window.Close();
        }
    }
}
