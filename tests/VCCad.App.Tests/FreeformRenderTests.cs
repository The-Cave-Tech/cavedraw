using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A freeform gradient has to reach the screen, not just the model.
///
/// It is sampled into a brush because no shader can express a point field, so the thing worth
/// pinning is that the colours actually land where the colour points are - the field is smooth, and
/// a test that only asked "is it not the flat colour" would pass on a brush that painted one colour
/// everywhere.
/// </summary>
public class FreeformRenderTests
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
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static (byte R, byte G, byte B) PixelAt(WriteableBitmap frame, int x, int y)
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

        int at = (y * stride) + (x * 4);

        // The captured frame's format decides which byte is red: Bgra8888 and Rgba8888 differ
        // only in that, and reading the wrong one turns red into blue - which green, being
        // symmetric, never shows.
        bool bgra = frame.Format == PixelFormat.Bgra8888;
        byte first = buffer[at];
        byte third = buffer[at + 2];
        return bgra
            ? (third, buffer[at + 1], first)
            : (first, buffer[at + 1], third);
    }

    [AvaloniaFact]
    public void AFreeformPointsGradientPaintsEachColourWhereItsPointIs()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            var path = new PathItem { Name = "panel" };
            SubPath sub = path.AddSubPath(closed: true);
            sub.Nodes.Add(new PathNode(new Point2D(100, 100)));
            sub.Nodes.Add(new PathNode(new Point2D(300, 100)));
            sub.Nodes.Add(new PathNode(new Point2D(300, 300)));
            sub.Nodes.Add(new PathNode(new Point2D(100, 300)));

            path.Fill = FillSpec.WithGradient(new GradientSpec
            {
                Kind = GradientKind.Freeform,
                FreeformMode = FreeformMode.Points,
                Points = new[]
                {
                    new FreeformPoint(new Point2D(120, 120), new ColorRgb(1, 0, 0)),
                    new FreeformPoint(new Point2D(280, 280), new ColorRgb(0, 0, 1)),
                },
            });

            viewModel.Document.Artboards[0].Layers[0].AddItem(path);

            // Painting follows the document-changed event the view model raises; a direct model
            // mutation in a test has to ask for the repaint itself.
            workspace.InvalidateVisual();
            Settle();

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            Point nearRed = workspace.ModelToWindow(new Point2D(120, 120));
            Point nearBlue = workspace.ModelToWindow(new Point2D(280, 280));

            (byte r1, byte g1, byte b1) = PixelAt(frame!, (int)nearRed.X, (int)nearRed.Y);
            (byte r2, byte g2, byte b2) = PixelAt(frame!, (int)nearBlue.X, (int)nearBlue.Y);

            Assert.True(r1 > 150 && b1 < 100, $"the red point should be red, was ({r1},{g1},{b1})");
            Assert.True(b2 > 150 && r2 < 100, $"the blue point should be blue, was ({r2},{g2},{b2})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A freeform gradient with nothing in it still paints the flat colour underneath.</summary>
    [AvaloniaFact]
    public void AFreeformGradientWithNoPointsFallsBackToTheFlatColour()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            var path = new PathItem { Name = "panel" };
            SubPath sub = path.AddSubPath(closed: true);
            sub.Nodes.Add(new PathNode(new Point2D(100, 100)));
            sub.Nodes.Add(new PathNode(new Point2D(300, 100)));
            sub.Nodes.Add(new PathNode(new Point2D(300, 300)));
            sub.Nodes.Add(new PathNode(new Point2D(100, 300)));

            path.Fill = FillSpec.WithGradient(new GradientSpec
            {
                Kind = GradientKind.Freeform,
                Points = Array.Empty<FreeformPoint>(),
            }) with { Color = new ColorRgb(0, 1, 0), IsVisible = true };

            viewModel.Document.Artboards[0].Layers[0].AddItem(path);

            // Painting follows the document-changed event the view model raises; a direct model
            // mutation in a test has to ask for the repaint itself.
            workspace.InvalidateVisual();
            Settle();

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            Point centre = workspace.ModelToWindow(new Point2D(200, 200));
            (byte r, byte g, byte b) = PixelAt(frame!, (int)centre.X, (int)centre.Y);

            Assert.True(g > 150 && r < 100 && b < 100, $"expected the flat green fill, was ({r},{g},{b})");
        }
        finally
        {
            window.Close();
        }
    }
}
