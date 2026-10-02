using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **The paint half of the brush editor's live preview** (issue #113's audit, item B).
///
/// The preview's *geometry* was already the pipeline's own - the outline is `StrokeOutlineBuilder.Outline` and the
/// placements are `PlacedArt.Resolve`, both pinned in <see cref="BrushesPaneTests"/>. Its *paint* of a placed asset
/// was not: the canvas's item painter is private to <c>CanvasWorkspace</c>, so the preview filled each asset's own
/// shape in the stroke colour. An art brush carrying a gradient therefore previewed as a flat black silhouette
/// while the canvas drew the ramp - and the first person to find out is the one who applies the brush.
///
/// These tests read pixels, because the gap was in the drawing rather than in the arithmetic: a test that asked
/// the preview for its placements passes either way. And they assert **colours the silhouette cannot produce** - a
/// gradient's two ends and the ramp between them, and an item's own stroke - because "the preview is not blank" is
/// satisfied by exactly the flat black shape this is meant to catch.
/// </summary>
public class BrushPreviewPaintTests
{
    private const int Width = 320;
    private const int Height = 220;

    private static (Window Window, BrushPreviewView Preview, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var preview = new BrushPreviewView();
        var window = new Window { Width = Width, Height = Height, Content = preview };
        window.Show();
        Settle();
        return (window, preview, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Invoke(EditorViewModel viewModel, string operation, object parameters)
    {
        var context = new VCCad.App.Automation.AutomationContext { ViewModel = viewModel };
        VCCad.App.Automation.EditorOperations.Invoke(
            context, operation, System.Text.Json.JsonSerializer.SerializeToElement(parameters));
    }

    /// <summary>A ten by ten square with a red-to-blue ramp across it, which is the asset a brush maps.</summary>
    private static PathItem GradientAsset(EditorViewModel viewModel)
    {
        var path = new PathItem
        {
            Name = "ramp",
            Fill = FillSpec.WithGradient(new GradientSpec
            {
                Kind = GradientKind.Linear,
                Start = new Point2D(0, 0.5),
                End = new Point2D(1, 0.5),
                Stops = new[]
                {
                    new GradientStop(0.0, new ColorRgb(1, 0, 0)),
                    new GradientStop(1.0, new ColorRgb(0, 0, 1)),
                },
            }),
        };

        Square(path);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    /// <summary>A ten by ten square with no fill and its own green outline, which is the other half of the claim.</summary>
    private static PathItem StrokedAsset(EditorViewModel viewModel)
    {
        var path = new PathItem { Name = "ring", Fill = FillSpec.None };
        Square(path);

        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, new ColorRgb(0, 1, 0), 2, StrokeCap.Butt, StrokeJoin.Miter, 4));

        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static void Square(PathItem path)
    {
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
    }

    private static BrushSpec ArtBrush(EditorViewModel viewModel, PathItem asset, string name = "Mapper")
    {
        Invoke(viewModel, "brush.create", new { name, kind = "art", asset = asset.Id, size = 20.0 });
        return viewModel.Document.FindBrush(name)!;
    }

    private static (byte R, byte G, byte B) PixelAt(WriteableBitmap frame, Point at)
    {
        int stride = frame.PixelSize.Width * 4;
        var buffer = new byte[stride * frame.PixelSize.Height];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(
                new PixelRect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height),
                handle.AddrOfPinnedObject(),
                buffer.Length,
                stride);
        }
        finally
        {
            handle.Free();
        }

        // The captured frame's format decides which byte is red: Bgra8888 and Rgba8888 differ only in that,
        // and reading the wrong one turns red into blue - which green, being symmetric, never shows.
        bool bgra = frame.Format == PixelFormat.Bgra8888;
        int at2 = (((int)Math.Round(at.Y) * stride) + ((int)Math.Round(at.X) * 4));
        byte first = buffer[at2];
        byte third = buffer[at2 + 2];
        return bgra
            ? (third, buffer[at2 + 1], first)
            : (first, buffer[at2 + 1], third);
    }

    /// <summary>
    /// Where a point inside a placement lands in the preview, asked of the preview itself: the test looks at the
    /// pixel a placement names rather than at the whole picture, and the fit that puts it there is the preview's own.
    /// </summary>
    private static Point Placed(BrushPreviewView preview, PlacedArt piece, Point2D assetLocal)
        => preview.ControlPoint(piece.Placement.Transform.Transform(assetLocal));

    /// <summary>
    /// The first piece the brush places. A sample path this long takes several - the count is not the claim here,
    /// and the first one sits wholly inside the control at every window size these tests use.
    /// </summary>
    private static PlacedArt First(BrushPreviewView preview)
    {
        Assert.NotEmpty(preview.Placed);
        return preview.Placed[0];
    }

    /// <summary>
    /// **A gradient asset previews as its gradient, not as a silhouette.**
    ///
    /// The asset is one square with a red-to-blue ramp across it, so a painter that draws *the asset* puts red at
    /// one end and blue at the other. The old painter drew the asset's own shape filled black, which is a colour
    /// neither probe can read, and the midpoint probe pins the ramp rather than two flat halves: a painter that
    /// filled half the square red and half blue would have to split it exactly where the probes fall, and the
    /// canvas's fill is one brush over the whole shape.
    /// </summary>
    [AvaloniaFact]
    public void APlacedAssetsGradientReachesThePreviewPixels()
    {
        (Window window, BrushPreviewView preview, EditorViewModel viewModel) = Host();
        try
        {
            PathItem asset = GradientAsset(viewModel);
            preview.Show(viewModel.Document, ArtBrush(viewModel, asset));
            Settle();

            PlacedArt piece = First(preview);
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            (byte r0, byte g0, byte b0) = PixelAt(frame!, Placed(preview, piece, new Point2D(1.5, 5)));
            Assert.True(r0 > 150 && b0 < 110 && g0 < 110,
                $"the red end of the ramp should be red, was ({r0},{g0},{b0}) - a black silhouette is (0,0,0)");

            (byte r1, byte g1, byte b1) = PixelAt(frame!, Placed(preview, piece, new Point2D(5, 5)));
            Assert.True(r1 > 60 && b1 > 60 && Math.Abs(r1 - b1) < 70,
                $"the middle of the ramp should be half way between red and blue, was ({r1},{g1},{b1})");

            (byte r2, byte g2, byte b2) = PixelAt(frame!, Placed(preview, piece, new Point2D(8.5, 5)));
            Assert.True(b2 > 150 && r2 < 110 && g2 < 110,
                $"the blue end of the ramp should be blue, was ({r2},{g2},{b2}) - a black silhouette is (0,0,0)");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **An asset's own stroke is what the preview draws, and its absent fill stays absent.**
    ///
    /// The asset is an unfilled square whose outline is green: the canvas strokes it green, and the silhouette
    /// painter filled it black instead. Both halves are asserted - the edge carries green, and the middle does not,
    /// because the item states no fill and a painter that filled the shape would put ink where the document says
    /// there is none.
    /// </summary>
    [AvaloniaFact]
    public void APlacedAssetsOwnStrokeReachesThePreviewPixels()
    {
        (Window window, BrushPreviewView preview, EditorViewModel viewModel) = Host();
        try
        {
            PathItem asset = StrokedAsset(viewModel);
            preview.Show(viewModel.Document, ArtBrush(viewModel, asset, "Outline"));
            Settle();

            PlacedArt piece = First(preview);
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            (byte r, byte g, byte b) = PixelAt(frame!, Placed(preview, piece, new Point2D(5, 0)));
            Assert.True(g > 150 && r < 110 && b < 110,
                $"the asset's own green outline should be drawn, was ({r},{g},{b}) - a black silhouette is (0,0,0)");

            (byte ir, byte ig, byte ib) = PixelAt(frame!, Placed(preview, piece, new Point2D(5, 5)));
            Assert.False(ig > 150 && ir < 110 && ib < 110,
                $"the asset states no fill, so its middle should not be green, was ({ir},{ig},{ib})");
        }
        finally
        {
            window.Close();
        }
    }
}
