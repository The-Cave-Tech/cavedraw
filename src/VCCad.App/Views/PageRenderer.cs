using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Views;

/// <summary>
/// Renders a single page of the document to a PNG at a requested resolution.
///
/// This exists so a comparison against another PDF engine can be made without screen
/// shots: a screen shot depends on the window size, the panels and whatever the user
/// last did, while this always produces exactly <c>page points x dpi / 72</c> pixels of
/// that page and nothing else. A diff against poppler, MuPDF or Ghostscript is then
/// apples to apples, repeated identically at any scale.
///
/// It uses the same <see cref="CanvasWorkspace"/> the person sees, so what it exports is
/// what the editor draws — not a second, parallel renderer that could drift from it.
/// </summary>
public static class PageRenderer
{
    /// <summary>The live workspace, set once by the host when the window is created.</summary>
    public static CanvasWorkspace? Workspace { get; set; }

    /// <summary>
    /// Renders one page. <paramref name="pageIndex"/> is zero-based; <paramref name="dpi"/>
    /// is the output resolution (72 dpi = one pixel per point).
    /// </summary>
    public static byte[]? Render(CadDocument document, int pageIndex, double dpi)
    {
        CanvasWorkspace? workspace = Workspace;
        if (workspace is null || pageIndex < 0 || pageIndex >= document.Artboards.Count)
        {
            return null;
        }

        return Dispatcher.UIThread.CheckAccess()
            ? RenderCore(workspace, document.Artboards[pageIndex], dpi)
            : Dispatcher.UIThread.Invoke(() => RenderCore(workspace, document.Artboards[pageIndex], dpi));
    }

    private static Point2D PageCentre(Artboard artboard) => new(
        artboard.X + (artboard.Width / 2.0),
        artboard.Y + (artboard.Height / 2.0));

    private static byte[]? RenderCore(CanvasWorkspace workspace, Artboard artboard, double dpi)
    {
        double zoom = Math.Max(dpi / 72.0, 0.01);
        double previousWidth = workspace.Width;
        double previousHeight = workspace.Height;

        try
        {
            // Size the control to the page at this resolution, so the whole sheet lands
            // in the bitmap; then point the view at the page's centre at the matching zoom.
            workspace.Width = Math.Max(1, artboard.Width * zoom);
            workspace.Height = Math.Max(1, artboard.Height * zoom);
            workspace.Measure(new Size(workspace.Width, workspace.Height));
            workspace.Arrange(new Rect(0, 0, workspace.Width, workspace.Height));

            workspace.SetZoomForExport(zoom);
            workspace.CenterOn(PageCentre(artboard));

            // Lay out again after the zoom and scroll: the scroll must be computed
            // against the final viewport, or the page lands off-centre in the bitmap.
            workspace.Measure(new Size(workspace.Width, workspace.Height));
            workspace.Arrange(new Rect(0, 0, workspace.Width, workspace.Height));
            workspace.CenterOn(PageCentre(artboard));

            var pixelSize = new PixelSize(
                Math.Max(1, (int)Math.Round(artboard.Width * zoom)),
                Math.Max(1, (int)Math.Round(artboard.Height * zoom)));

            // 96 dpi keeps one bitmap pixel per logical pixel, so the sheet fills it exactly.
            using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
            bitmap.Render(workspace);

            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            // An export must never take the application down; the caller reports null.
            return null;
        }
        finally
        {
            workspace.Width = previousWidth;
            workspace.Height = previousHeight;
        }
    }
}
