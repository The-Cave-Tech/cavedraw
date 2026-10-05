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
/// Every selection handle is painted, on any backdrop (issue #247).
///
/// The eight handles used to be filled with **white**. On a white page a white fill is indistinguishable from no
/// fill, so they read as empty outlines - and the four that crossed something dark read as filled. One kind of
/// handle therefore looked like two different things depending on what was behind it, which is what the person
/// saw on the Lillie header: "of the 8 square control handles, only 4 are filled".
///
/// The test puts a dark shape under the selection on purpose. On a white page a white-fill regression would still
/// pass a "is there ink here" check, and that is exactly the check that was missing.
/// </summary>
public class SelectionHandlePaintTests
{
    private const int Width = 900;
    private const int Height = 700;

    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = Width, Height = Height, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>A black rectangle at the origin, selected - so the handles sit on a dark backdrop.</summary>
    private static PathItem DarkRectangle(EditorViewModel viewModel)
    {
        Artboard board = viewModel.Document.Artboards[0];
        var rect = new PathItem { Name = "dark", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = rect.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 40, board.Y + 40)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 240, board.Y + 40)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 240, board.Y + 180)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 40, board.Y + 180)));
        board.Layers[0].AddItem(rect);
        viewModel.SelectObject(rect);
        Settle();
        return rect;
    }

    private static RenderTargetBitmap Render(Window window)
    {
        var target = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        target.Render(window);
        return target;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(RenderTargetBitmap target, int x, int y)
    {
        int stride = Width * 4;
        var pixels = new byte[stride * Height];
        System.Runtime.InteropServices.GCHandle handle =
            System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        int i = (y * stride) + (x * 4);
        return (pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]);
    }

    [AvaloniaFact]
    public void AllEightSelectionHandlesArePainted()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        DarkRectangle(viewModel);
        Settle();

        IReadOnlyList<Point> centres = workspace.SelectionHandleCentresForTests();
        Assert.Equal(8, centres.Count);

        using RenderTargetBitmap target = Render(window);

        int painted = 0;
        foreach (Point centre in centres)
        {
            int x = (int)Math.Round(centre.X);
            int y = (int)Math.Round(centre.Y);
            Assert.InRange(x, 1, Width - 2);
            Assert.InRange(y, 1, Height - 2);

            (byte r, byte g, byte b, byte a) = Pixel(target, x, y);

            // **The accent, not the page.** A white fill leaves this pixel white - which is what the defect was,
            // and what a check for "some ink is nearby" could never see.
            bool accent = b > 180 && b > r + 40 && a > 200;
            if (accent)
            {
                painted++;
            }
        }

        Assert.Equal(8, painted);
        window.Close();
    }

    /// <summary>
    /// And a handle is smaller than it was: 6x6 of screen, the same as the text edit box's own handles, rather
    /// than the 8x8 the person found a little too large.
    /// </summary>
    [AvaloniaFact]
    public void ASelectionHandleIsSixPixelsAcross()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        DarkRectangle(viewModel);
        Settle();

        IReadOnlyList<Point> centres = workspace.SelectionHandleCentresForTests();

        // **The left-middle handle, not a corner one.** The selection outline is drawn in the same accent through
        // the corners, so walking sideways from a corner measures the handle plus the dashed line - 13 pixels for
        // a 6-pixel handle, which is how this test first failed. At the middle of an edge the outline runs
        // vertically, so a horizontal walk crosses the handle and nothing else.
        // The cells are row-major, so the yielded order is TL, TM, TR, **LM**, RM, BL, BM, BR - the left-middle
        // handle is the fourth. (Picking the last one, believing it was LM, measured a corner: the dashed outline
        // is drawn in the same accent through the corners and turned a 6-pixel handle into 14.)
        Point leftMiddle = centres[3];
        using RenderTargetBitmap target = Render(window);

        // Walk out from the centre while the accent continues, in both directions.
        int left = 0;
        while (left < 10 && IsAccent(Pixel(target, (int)leftMiddle.X - left - 1, (int)leftMiddle.Y)))
        {
            left++;
        }

        int right = 0;
        while (right < 10 && IsAccent(Pixel(target, (int)leftMiddle.X + right + 1, (int)leftMiddle.Y)))
        {
            right++;
        }

        Assert.True(left + right + 1 <= 7, $"the handle is {left + right + 1} pixels across, expected about 6");
        window.Close();

        static bool IsAccent((byte R, byte G, byte B, byte A) p) => p.B > 180 && p.B > p.R + 40 && p.A > 200;
    }
}
