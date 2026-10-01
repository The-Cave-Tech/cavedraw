using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The Transform pane, the flip and the node tool edit art in the frame it is stored in (issue #173).
///
/// #165 taught the pointer gestures to carry a world delta into the frame above the item and #172 taught
/// the API translations the same. Three members of that family were left in world space: the pane's scale
/// and rotate pivot, the flip, and the node tool's drags. Each is measured here on **model geometry** -
/// what the document stores - because a wrong frame is a wrong file, not just a wrong animation.
///
/// Every figure is an SVG, so the frame is the file's own grouping rather than a tree the test built to
/// suit the code, and the expected numbers are written out from the transform the file states.
/// </summary>
public class TransformFrameTests
{
    // 200x200 user units at 0.75 pt per unit is a 150x150 pt page: the root group's own transform.
    private const string Header = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">";

    /// <summary>`translate(50,50) scale(2)` on a 10-unit square at the origin: a 15x15 pt box at 37.5,37.5.</summary>
    private const string TranslatedAndScaled = Header +
        "<g transform=\"translate(50,50) scale(2)\"><rect width=\"10\" height=\"10\" fill=\"#000000\"/></g></svg>";

    /// <summary>`translate(40,40) rotate(90)`: the square covers 22.5..30 by 30..37.5 pt, a box no
    /// translation alone could produce - and a frame whose axes are the page's own turned a quarter
    /// turn, which is what tells a pivot-only conversion from a conjugated one.</summary>
    private const string Rotated = Header +
        "<g transform=\"translate(40,40) rotate(90)\"><rect width=\"10\" height=\"10\" fill=\"#000000\"/></g></svg>";

    /// <summary>The same rotated frame around a triangle, so a mirror has a handed shape to change.</summary>
    private const string RotatedTriangle = Header +
        "<g transform=\"translate(40,40) rotate(90)\">" +
        "<path d=\"M0,0 L10,0 L0,10 Z\" fill=\"#000000\"/></g></svg>";

    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel, CadDocument Document)
        Host(string svg)
    {
        var viewModel = new EditorViewModel();
        viewModel.ImportDocument(SvgReader.Read(svg).Document);

        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);
        viewModel.Tool = EditorTool.Select;

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel, viewModel.Document);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Drag through the real pointer path, between two **world** points.</summary>
    private static void Drag(Window window, CanvasWorkspace workspace, Point2D from, Point2D to)
    {
        Point a = workspace.ModelToWindow(from);
        Point b = workspace.ModelToWindow(to);
        InputInjection.Press(window, a.X, a.Y, shift: false);
        InputInjection.Move(window, (a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, leftDown: true);
        InputInjection.Move(window, b.X, b.Y, leftDown: true);
        InputInjection.Release(window, b.X, b.Y);
        Settle();
    }

    /// <summary>The transform every enclosing group states, composed outermost last.</summary>
    private static AffineTransform ToWorld(LayerItem item)
    {
        AffineTransform transform = AffineTransform.Identity;
        for (IItemContainer? container = item.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                transform = group.Transform.Compose(transform);
            }
        }

        Vector2D origin = item.ArtboardOffset();
        return AffineTransform.CreateTranslation(origin.X, origin.Y).Compose(transform);
    }

    /// <summary>An item's box in document coordinates, by the test's own arithmetic.</summary>
    private static Rect2D InWorld(LayerItem item)
    {
        Rect2D box = item switch
        {
            PathItem path => path.BoundingBox(),
            _ => Rect2D.Empty,
        };

        return box.IsEmpty ? box : ToWorld(item).Transform(box);
    }

    /// <summary>A node's position in document coordinates, by the test's own arithmetic.</summary>
    private static Point2D AnchorInWorld(LayerItem item, int index)
        => item is PathItem path
            ? ToWorld(path).Transform(path.SubPaths[0].Nodes[index].Anchor)
            : default;

    private static void AssertBox(Rect2D expected, Rect2D actual, string what)
    {
        Assert.True(
            Math.Abs(actual.Left - expected.Left) <= 1e-6 &&
            Math.Abs(actual.Top - expected.Top) <= 1e-6 &&
            Math.Abs(actual.Right - expected.Right) <= 1e-6 &&
            Math.Abs(actual.Bottom - expected.Bottom) <= 1e-6,
            $"{what} should be {expected.Left},{expected.Top} to {expected.Right},{expected.Bottom} " +
            $"but is {actual.Left},{actual.Top} to {actual.Right},{actual.Bottom}");
    }

    /// <summary>
    /// A pane **rotation** turns an item inside a translated-and-scaled group about the world pivot.
    ///
    /// The square is 37.5..52.5 pt either way. `rotate 90` about its top-right corner (52.5,37.5) carries
    /// it to 37.5..52.5 by 22.5..37.5 pt. The pivot is given in **world** coordinates - that is the frame
    /// the pane's fields are in - and the stored geometry is in the group's, so the rotation has to be
    /// conjugated rather than aimed at `pivot - ArtboardOffset()`.
    /// </summary>
    [AvaloniaFact]
    public void APaneRotationInsideATranslatedAndScaledGroupTurnsAboutTheWorldPivot()
    {
        (Window window, _, EditorViewModel viewModel, CadDocument document) = Host(TranslatedAndScaled);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            Settle();
            AssertBox(new Rect2D(37.5, 37.5, 15, 15), InWorld(rect), "the rect");

            viewModel.ApplyTransform(
                new Point2D(52.5, 37.5), default, 1, 1, 90);

            AssertBox(new Rect2D(37.5, 22.5, 15, 15), InWorld(rect), "the turned rect");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A pane **scale** enlarges an item inside a translated-and-scaled group along the world axes.
    ///
    /// Doubling the width about the world top-left (37.5,37.5) - which is the item's own corner in this
    /// frame - leaves 37.5..67.5 by 37.5..52.5 pt. Aiming the stored geometry at the world pivot instead
    /// scales 0..10 about 37.5 and lands the box at -18.75..11.25, off the page.
    /// </summary>
    [AvaloniaFact]
    public void APaneScaleInsideATranslatedAndScaledGroupScalesAboutTheWorldPivot()
    {
        (Window window, _, EditorViewModel viewModel, CadDocument document) = Host(TranslatedAndScaled);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            Settle();

            viewModel.ApplyTransform(new Point2D(37.5, 37.5), default, 2, 1, 0);

            AssertBox(new Rect2D(37.5, 37.5, 30, 15), InWorld(rect), "the widened rect");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A pane rotation inside a **rotated** group turns about the world pivot as well.
    ///
    /// The group is `translate(40,40) rotate(90)`, so the square is 22.5..30 by 30..37.5 pt. A quarter turn
    /// about its world top-left (22.5,30) carries it to 15..22.5 by 30..37.5 pt. A rotation commutes with
    /// the group's own rotation, so this case is a pivot conversion and nothing more - the scale below is
    /// the one that separates the two.
    /// </summary>
    [AvaloniaFact]
    public void APaneRotationInsideARotatedGroupTurnsAboutTheWorldPivot()
    {
        (Window window, _, EditorViewModel viewModel, CadDocument document) = Host(Rotated);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            Settle();
            AssertBox(new Rect2D(22.5, 30, 7.5, 7.5), InWorld(rect), "the rect");

            viewModel.ApplyTransform(new Point2D(22.5, 30), default, 1, 1, 90);

            AssertBox(new Rect2D(15, 30, 7.5, 7.5), InWorld(rect), "the turned rect");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A pane scale inside a **rotated** group scales along the world axes, which is not the same as
    /// scaling along the group's.
    ///
    /// `translate(40,40) rotate(90)` under the root's 0.75 puts the group's own x axis down the page and
    /// its y axis across it, so the conjugation `A⁻¹ · diag(2,1) · A` is `diag(1,2)`: doubling the world
    /// width is doubling the item's **height**. The box goes from 22.5..30 by 30..37.5 to 22.5..37.5 by
    /// 30..37.5 pt. A fix that only carried the pivot into the frame would scale the local x axis instead
    /// and leave the box at 45..52.5 by 0..15 - a different picture by any measure.
    /// </summary>
    [AvaloniaFact]
    public void APaneScaleInsideARotatedGroupScalesAlongTheWorldAxes()
    {
        (Window window, _, EditorViewModel viewModel, CadDocument document) = Host(Rotated);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            Settle();

            viewModel.ApplyTransform(new Point2D(22.5, 30), default, 2, 1, 0);

            AssertBox(new Rect2D(22.5, 30, 15, 7.5), InWorld(rect), "the widened rect");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A flip inside a group that only **scales and translates** needs the sign change and the world
    /// centre, and nothing more - checked rather than assumed, because the mirror is the one operation in
    /// this family whose frame is not simply conjugated into a different linear map.
    ///
    /// `translate(50,50) scale(2)` under the root's 0.75 is a uniform, axis-aligned frame, so the item's own
    /// axes are the page's: the triangle's world nodes (37.5,37.5), (52.5,37.5) and (37.5,52.5) mirror
    /// horizontally about the selection's world centre (45,45) to (52.5,37.5), (37.5,37.5) and (52.5,52.5)
    /// - the same nodes flipping the stored geometry about its own centre produced. This test passed before
    /// the fix as well, which is why the rotated case below is the one that pins it.
    /// </summary>
    [AvaloniaFact]
    public void AFlipInsideATranslatedAndScaledGroupMirrorsAboutTheWorldCentre()
    {
        const string figure = Header +
            "<g transform=\"translate(50,50) scale(2)\">" +
            "<path d=\"M0,0 L10,0 L0,10 Z\" fill=\"#000000\"/></g></svg>";

        (Window window, _, EditorViewModel viewModel, CadDocument document) = Host(figure);
        try
        {
            PathItem path = Assert.Single(document.AllPaths());
            viewModel.SelectObject(path);
            Settle();

            viewModel.FlipSelection(horizontal: true, vertical: false);

            Point2D[] got = [AnchorInWorld(path, 0), AnchorInWorld(path, 1), AnchorInWorld(path, 2)];
            Array.Sort(got, (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

            Point2D[] want = [new(37.5, 37.5), new(52.5, 37.5), new(52.5, 52.5)];
            for (int i = 0; i < 3; i++)
            {
                Assert.True(
                    Math.Abs(got[i].X - want[i].X) <= 1e-6 && Math.Abs(got[i].Y - want[i].Y) <= 1e-6,
                    $"the mirrored triangle should have a node at {want[i].X},{want[i].Y} but has " +
                    $"{got[0].X},{got[0].Y} {got[1].X},{got[1].Y} {got[2].X},{got[2].Y}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A flip inside a **rotated** group mirrors about the world axis, which in the group's frame is the
    /// other axis - so a mirror is not a sign change, it is the conjugation.
    ///
    /// The triangle's world nodes are (30,30), (30,37.5) and (22.5,30). Mirroring horizontally about the
    /// selection's world centre (26.25,33.75) gives (22.5,30), (22.5,37.5) and (30,30). Flipping the stored
    /// geometry about its own centre instead - which is what `centre - ArtboardOffset()` does - mirrors the
    /// group's x axis, which on the page is the **vertical** axis, and leaves the nodes at (22.5,37.5),
    /// (30,30) and (30,37.5): a differently-handed triangle in the same box.
    /// </summary>
    [AvaloniaFact]
    public void AFlipInsideARotatedGroupMirrorsAboutTheWorldAxis()
    {
        (Window window, _, EditorViewModel viewModel, CadDocument document) = Host(RotatedTriangle);
        try
        {
            PathItem path = Assert.Single(document.AllPaths());
            viewModel.SelectObject(path);
            Settle();

            viewModel.FlipSelection(horizontal: true, vertical: false);

            Point2D[] got = [AnchorInWorld(path, 0), AnchorInWorld(path, 1), AnchorInWorld(path, 2)];
            Array.Sort(got, (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

            Point2D[] want = [new(22.5, 30), new(22.5, 37.5), new(30, 30)];
            for (int i = 0; i < 3; i++)
            {
                Assert.True(
                    Math.Abs(got[i].X - want[i].X) <= 1e-6 && Math.Abs(got[i].Y - want[i].Y) <= 1e-6,
                    $"the mirrored triangle should have a node at {want[i].X},{want[i].Y} but has " +
                    $"{got[0].X},{got[0].Y} {got[1].X},{got[1].Y} {got[2].X},{got[2].Y}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A **node drag** inside a translated-and-scaled group moves the node by the distance the pointer
    /// moved.
    ///
    /// The frame is 1.5 pt per group unit, so a pointer that travels 15 pt across the page moves the node
    /// 10 units of the group's own space and 15 pt of model. Adding the world delta to the stored anchor
    /// moves it 22.5 pt - one and a half times the pointer - and the node is not even grabbable where it
    /// is drawn, because the tool's hit test subtracts the artboard origin and no more.
    /// </summary>
    [AvaloniaFact]
    public void ANodeToolDragInsideATranslatedAndScaledGroupMovesTheNodeByThePointerDelta()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, CadDocument document) =
            Host(TranslatedAndScaled);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            viewModel.Tool = EditorTool.Node;
            Settle();

            Point2D start = AnchorInWorld(rect, 0);
            Assert.Equal(37.5, start.X, 6);
            Assert.Equal(37.5, start.Y, 6);

            Drag(window, workspace, start, new Point2D(22.5, 37.5));

            Point2D after = AnchorInWorld(rect, 0);
            Assert.True(
                Math.Abs(after.X - 22.5) <= 0.5 && Math.Abs(after.Y - 37.5) <= 0.5,
                $"the node should have followed the pointer to 22.5,37.5 but is at {after.X},{after.Y}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A node drag inside a **rotated** group moves in the direction the pointer moved, as well as the
    /// distance.
    ///
    /// The group is `translate(40,40) rotate(90)` under the root's 0.75, so a pointer that moves 15 pt down
    /// the page is 20 units along the group's own x axis. Applying the world delta to the stored anchor
    /// moves the node 15 units of the group's space - 11.25 pt across the page - so the node leaves the
    /// pointer in both components.
    /// </summary>
    [AvaloniaFact]
    public void ANodeToolDragInsideARotatedGroupMovesInTheDirectionThePointerMoved()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, CadDocument document) =
            Host(Rotated);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            viewModel.Tool = EditorTool.Node;
            Settle();

            Point2D start = AnchorInWorld(rect, 0);
            Assert.Equal(30, start.X, 6);
            Assert.Equal(30, start.Y, 6);

            Drag(window, workspace, start, new Point2D(30, 45));

            Point2D after = AnchorInWorld(rect, 0);
            Assert.True(
                Math.Abs(after.X - 30) <= 0.5 && Math.Abs(after.Y - 45) <= 0.5,
                $"the node should have followed the pointer to 30,45 but is at {after.X},{after.Y}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The frame conversions are the ones <see cref="SelectionEngine"/> states once, so the pane and the
    /// pointer cannot disagree: `FromWorld` inverts `ToWorld`, and a delta carried by `DeltaInItem` is the
    /// same displacement the pointer made.
    /// </summary>
    [AvaloniaFact]
    public void ThePaneAndThePointerAgreeAboutTheFrame()
    {
        (Window window, _, _, CadDocument document) = Host(TranslatedAndScaled);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            Assert.NotNull(SelectionEngine.FromWorld(rect));

            // 1.5 pt per group unit: a 15 pt world displacement is 10 units of the path's own space.
            Vector2D carried = SelectionEngine.DeltaInItem(rect, new Vector2D(15, 0));
            Assert.Equal(10.0, carried.X, 6);
            Assert.Equal(0.0, carried.Y, 6);
        }
        finally
        {
            window.Close();
        }
    }
}
