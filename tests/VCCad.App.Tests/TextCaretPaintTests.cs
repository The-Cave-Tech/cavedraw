using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Where the caret and the selection highlight are actually drawn.
///
/// Both are worked out from the per-character metrics, and getting the frame wrong does not fail to
/// draw them - it draws them somewhere else, which is what a person sees. The caret is checked by
/// its geometry rather than its pixels because it blinks: half the time there is nothing to find.
/// </summary>
public class TextCaretPaintTests
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

    private static TextItem Label(double x, double y, double rotation, double fontSize, ColorRgb color)
    {
        var text = new TextItem
        {
            Name = "label",
            Origin = new Point2D(x, y),
            RotationRadians = rotation,
            Color = color,
        };
        text.Runs.Add(new TextRun { Text = "ABCDEFGHIJ", FontSize = fontSize });
        return text;
    }

    private static void Add(EditorViewModel viewModel, LayerItem item)
    {
        viewModel.Document.Artboards[0].Layers[0].AddItem(item);
        Settle();
    }

    private static Rect2D Upright(TextItem text)
    {
        var flat = (TextItem)text.Clone();
        flat.RotationRadians = 0;
        return flat.LocalBounds();
    }

    /// <summary>A window pixel turned back into the block's own space, as the canvas does it.</summary>
    private static Point2D ToLocal(CanvasWorkspace workspace, TextItem text, Point window)
    {
        Point2D model = workspace.WindowToModel(window);
        Vector2D offset = text.ArtboardOffset();
        double dx = model.X - (text.Origin.X + offset.X);
        double dy = model.Y - (text.Origin.Y + offset.Y);
        double cos = Math.Cos(-text.RotationRadians);
        double sin = Math.Sin(-text.RotationRadians);
        return new Point2D((dx * cos) - (dy * sin), (dx * sin) + (dy * cos));
    }

    private static byte[] Pixels(WriteableBitmap bitmap)
    {
        int stride = bitmap.PixelSize.Width * 4;
        var buffer = new byte[stride * bitmap.PixelSize.Height];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                handle.AddrOfPinnedObject(),
                buffer.Length,
                stride);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }

    /// <summary>Pixels that differ between two frames, inside <paramref name="region"/>.</summary>
    private static List<(int X, int Y)> Changed(WriteableBitmap first, WriteableBitmap second, PixelRect region)
    {
        byte[] a = Pixels(first);
        byte[] b = Pixels(second);
        int width = first.PixelSize.Width;
        int height = first.PixelSize.Height;
        int stride = width * 4;

        var changed = new List<(int, int)>();
        for (int y = Math.Max(0, region.Y); y < Math.Min(height, region.Bottom); y++)
        {
            for (int x = Math.Max(0, region.X); x < Math.Min(width, region.Right); x++)
            {
                int i = (y * stride) + (x * 4);
                int delta = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                if (delta > 12)
                {
                    changed.Add((x, y));
                }
            }
        }

        return changed;
    }

    /// <summary>A window region big enough for the block however it is turned.</summary>
    private static PixelRect Around(CanvasWorkspace workspace, TextItem text, double reach)
    {
        Point origin = workspace.ModelToWindow(text.Origin + text.ArtboardOffset());
        return new PixelRect(
            (int)Math.Floor(origin.X - reach),
            (int)Math.Floor(origin.Y - reach),
            (int)(reach * 2),
            (int)(reach * 2));
    }

    /// <summary>
    /// The caret marks the line the text is on: both of its ends, turned back into the block's own
    /// space, sit at the caret's character and span the line's height.
    ///
    /// Drawn an ascent above the line - which is what subtracting the ascent from a coordinate that
    /// is already the line's top does - neither end lands in the block. Drawn along the screen's
    /// vertical, a turned block's caret runs across the text instead of down it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(Math.PI / 2)]
    [InlineData(-Math.PI / 2)]
    [InlineData(0.4)]
    public void TheCaretMarksTheLineWhereTheTextIs(double rotation)
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem text = Label(200, 200, rotation, 24, ColorRgb.White);
            Add(viewModel, text);

            Rect2D box = Upright(text);
            Assert.Equal(EditTarget.Text, workspace.EditAt(new Point2D(200, 200)));
            Assert.True(workspace.SetTextSelection(4, 4), "the block should be open");

            (Point top, Point bottom) = workspace.CaretLine()!.Value;

            Point2D localTop = ToLocal(workspace, text, top);
            Point2D localBottom = ToLocal(workspace, text, bottom);

            Assert.InRange(localTop.X, -2, box.Width + 2);
            Assert.InRange(localTop.Y, -2, box.Height + 2);
            Assert.InRange(localTop.X - localBottom.X, -0.5, 0.5); // down the block, not across it

            // And it marks the line box: at least the face's own size, and the leading the block
            // asks for on top of it.
            Assert.InRange(localBottom.Y - localTop.Y, 24 * 0.95, 24 * 1.8);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The highlight is where the selected characters are.
    ///
    /// Two frames - one with a selection, one without - differ by the highlight and the moved caret,
    /// and every changed pixel has to be inside the block. Drawn a line above the text it covers,
    /// most of them are not.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(Math.PI / 2)]
    [InlineData(-Math.PI / 2)]
    public void TheSelectionHighlightCoversTheCharactersItSelects(double rotation)
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem text = Label(200, 200, rotation, 24, ColorRgb.Black);
            Add(viewModel, text);

            Rect2D box = Upright(text);
            Assert.Equal(EditTarget.Text, workspace.EditAt(new Point2D(200, 200)));

            Assert.True(workspace.SetTextSelection(4, 4), "the block should be open");
            WriteableBitmap? without = window.CaptureRenderedFrame();
            Assert.NotNull(without);

            Assert.True(workspace.SetTextSelection(1, 4));
            WriteableBitmap? with = window.CaptureRenderedFrame();
            Assert.NotNull(with);

            List<(int, int)> changed = Changed(without!, with!, Around(workspace, text, 180));
            Assert.True(changed.Count > 40, $"the highlight should be drawn ({changed.Count} pixels)");

            foreach ((int px, int py) in changed)
            {
                Point2D local = ToLocal(workspace, text, new Point(px + 0.5, py + 0.5));
                Assert.InRange(local.X, -3, box.Width + 3);
                Assert.InRange(local.Y, -3, box.Height + 3);
            }
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
