using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Controls;

/// <summary>
/// Shows a document's pattern **tiling**, which is what #135 asks a pattern editor to do and what nothing in the
/// application could show before: a pattern is a paint, so a person could not see one without a shape to fill.
///
/// **It reaches the canvas's own painter rather than reimplementing it.** The tile, the grid, the pattern's own
/// `patternTransform` and the clip are all the same code the canvas uses to fill a shape
/// (<see cref="CanvasWorkspace.PaintItemStandalone"/> into the same <c>PaintFill</c>), so the preview cannot drift
/// from what the canvas would draw - the gap the brush editor's audit recorded when a preview was written twice.
///
/// The shape it fills is a rectangle covering the control, because that is the plainest thing a pattern can be shown
/// on; a pattern whose tile has no box draws nothing, which is the same answer the canvas gives.
/// </summary>
public class PatternPreview : Control
{
    /// <summary>The document holding the pattern, set by the panel.</summary>
    public CadDocument? Document { get; set; }

    /// <summary>The pattern definition to show, by name, or null for nothing.</summary>
    public string? Pattern { get; set; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Document?.FindDefinition(Pattern ?? string.Empty) is not ArtGroup definition)
        {
            return;
        }

        PatternSpec? spec = PatternSpec.From(definition.Name, definition.ForeignAttributes);
        if (spec is null)
        {
            return;
        }

        // A rectangle the size of the control, filled with the pattern: the tile is repeated across it by the same
        // code path that fills a path on the canvas, clip included.
        var rect = new PathItem
        {
            Name = "preview",
            Fill = FillSpec.Solid(ColorRgb.White) with { Pattern = spec },
        };

        SubPath shape = rect.AddSubPath(closed: true);
        shape.Nodes.Add(new PathNode(new Point2D(0, 0)));
        shape.Nodes.Add(new PathNode(new Point2D(Bounds.Width, 0)));
        shape.Nodes.Add(new PathNode(new Point2D(Bounds.Width, Bounds.Height)));
        shape.Nodes.Add(new PathNode(new Point2D(0, Bounds.Height)));

        CanvasWorkspace.PaintItemStandalone(context, Document, rect, 1.0, AffineTransform.Identity);
    }

    /// <summary>Redraws when the pattern or the document changes.</summary>
    public void Show(CadDocument? document, string? pattern)
    {
        Document = document;
        Pattern = pattern;
        InvalidateVisual();
    }
}
