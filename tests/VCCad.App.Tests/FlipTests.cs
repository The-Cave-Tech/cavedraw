using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Flipping objects, and editing a block that has been flipped.
///
/// A mirror is state, not baked geometry - a run has to keep its string and its glyph ids, and an
/// image has to keep the file's samples - so the interesting questions are whether the state
/// round-trips, whether flipping twice is the identity, and whether the text editor still aims at
/// the character under the pointer when the block runs the other way.
/// </summary>
public class FlipTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        workspace.Focus();
        Settle();
        return (window, workspace, viewModel);
    }

    private static TextItem Label(double x, double y, string content = "ABCDEFGHIJ")
    {
        var text = new TextItem { Name = "label", Origin = new Point2D(x, y) };
        text.Runs.Add(new TextRun { Text = content, FontSize = 24 });
        return text;
    }

    private static PathItem Box(string name, double x, double y, double w, double h)
    {
        var path = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + w, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + w, y + h)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + h)));
        return path;
    }

    private static void Add(EditorViewModel viewModel, LayerItem item)
    {
        viewModel.Document.Artboards[0].Layers[0].AddItem(item);
        Settle();
    }

    [AvaloniaFact]
    public void FlippingABlockTwiceLeavesItExactlyWhereItWas()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        try
        {
            TextItem text = Label(200, 200);
            Add(viewModel, text);
            viewModel.SelectObject(text);

            Point2D origin = text.Origin;
            Rect2D before = text.WorldBounds();

            viewModel.FlipSelection(horizontal: true, vertical: false);
            Assert.True(text.MirrorX);

            // The block hangs on the other side of its origin, so the origin moves - and the box it
            // occupies does not. A flip is in place.
            Assert.Equal((2 * (origin.X + (before.Width / 2))) - origin.X, text.Origin.X, 6);
            Assert.Equal(before.X, text.WorldBounds().X, 6);
            Assert.Equal(before.Width, text.WorldBounds().Width, 6);

            viewModel.FlipSelection(horizontal: true, vertical: false);
            Assert.False(text.MirrorX);
            Assert.Equal(origin.X, text.Origin.X, 6);
            Assert.Equal(before.X, text.WorldBounds().X, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FlippingBothAxesAtOnceIsStillTheIdentity()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        try
        {
            TextItem text = Label(200, 200);
            Add(viewModel, text);
            viewModel.SelectObject(text);

            Point2D origin = text.Origin;

            viewModel.FlipSelection(horizontal: true, vertical: true);
            Assert.True(text.MirrorX);
            Assert.True(text.MirrorY);

            viewModel.FlipSelection(horizontal: true, vertical: true);
            Assert.False(text.MirrorX);
            Assert.False(text.MirrorY);
            Assert.Equal(origin.X, text.Origin.X, 6);
            Assert.Equal(origin.Y, text.Origin.Y, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void APathFlipsAsGeometry()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        try
        {
            PathItem path = Box("box", 100, 100, 80, 40);
            Add(viewModel, path);
            viewModel.SelectObject(path);

            viewModel.FlipSelection(horizontal: true, vertical: false);

            // Mirrored about its own centre, so the box is unchanged and still on one page; what
            // moved is the geometry inside it. A second flip puts the nodes back.
            Point2D firstNode = path.SubPaths[0].Nodes[0].Anchor;
            Assert.Equal(180, firstNode.X, 6);

            viewModel.FlipSelection(horizontal: true, vertical: false);
            Assert.Equal(100, path.SubPaths[0].Nodes[0].Anchor.X, 6);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The acceptance that matters most: on a flipped block the caret goes to the character under
    /// the pointer, not to the one the unflipped block would have had there.
    /// </summary>
    [AvaloniaFact]
    public void AFlippedBlockPutsTheCaretOnTheCharacterUnderThePointer()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem text = Label(300, 200);
            Add(viewModel, text);
            viewModel.SelectObject(text);

            int length = TextEditing.Length(text);
            Rect2D upright = text.LocalBounds();

            viewModel.FlipSelection(horizontal: true, vertical: false);

            // Mirrored in place: the box has not moved, but the block now runs the other way, so
            // the first character is at the visual LEFT of it.
            Rect2D box = text.BoundingBox();
            Assert.Equal(300, box.X, 6);
            Assert.Equal(300 + upright.Width, box.Right, 6);

            // Clicking the visual left end opens the block with the caret at the end of the text.
            Assert.Equal(EditTarget.Text, workspace.EditAt(new Point2D(box.X + 2, box.Y + 4)));
            Assert.True(viewModel.TextSelectionStart >= length - 2,
                $"the visual left of a mirrored block is the end of the text (was {viewModel.TextSelectionStart})");

            // And the visual right end is the beginning.
            Assert.Equal(EditTarget.Text, workspace.EditAt(new Point2D(box.Right - 2, box.Y + 4)));
            Assert.True(viewModel.TextSelectionStart <= 2,
                $"the visual right of a mirrored block is the start of the text (was {viewModel.TextSelectionStart})");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AFlippedBlockStillLaysOutAndDrawsItsCaretInItsOwnSpace()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem text = Label(300, 200);
            Add(viewModel, text);
            viewModel.SelectObject(text);
            viewModel.FlipSelection(horizontal: true, vertical: false);

            Assert.Equal(EditTarget.Text, workspace.EditAt(new Point2D(302, 204)));
            Assert.True(workspace.SetTextSelection(2, 2), "the block should be open");

            (Point top, Point bottom) = workspace.CaretLine()!.Value;

            // The caret is a line the height of the line box, and it is drawn inside the block -
            // which, flipped, is on the other side of the origin.
            Rect2D box = text.BoundingBox();
            Assert.InRange(top.X, box.X - 1, box.Right + 1);
            Assert.InRange(bottom.X, box.X - 1, box.Right + 1);
            Assert.True(Math.Abs(bottom.Y - top.Y) > 1, "the caret still marks a line");
        }
        finally
        {
            window.Close();
        }
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
