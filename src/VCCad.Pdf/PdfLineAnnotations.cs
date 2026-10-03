using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf.Parsing;

namespace VCCad.Pdf;

/// <summary>
/// The line endings a PDF's **Line annotations** state, read into the marker model (issue #202).
///
/// SVG gives a path three marker slots; PDF has no equivalent on a path and states an arrowhead as a **line ending**
/// on an annotation instead (`/LE`, ISO 32000-1 §12.5.6.7). So this is the same fact in the file's own spelling, and
/// it is read into the same slots and the same library the SVG reader fills - which means the canvas, the page and
/// the SVG export draw it with the code that already exists rather than with a second implementation here.
///
/// **The shape is named, not drawn, by the file.** A PDF states `/OpenArrow`, `/ClosedArrow`, `/Diamond`, `/Square`,
/// `/Circle` or `/None` - the *style* of an ending - and leaves the geometry to the reader, exactly as it does for
/// the standard fonts. The definitions written here are therefore **this reader's rendering of a named style**, and
/// they are named `pdf-&lt;style&gt;` so that is visible in the library; the file's own word for it is kept in the
/// definition's attributes.
/// </summary>
internal static class PdfLineAnnotations
{
    /// <summary>
    /// Every Line annotation on the page, as a path with the marker slots its endings name. Coordinates arrive in
    /// PDF user space (y up from the page's bottom-left) and are converted to the model's page space, which is the
    /// same conversion the content importer makes for drawn content.
    /// </summary>
    public static IEnumerable<PathItem> Read(PdfFile file, Dictionary<string, object?> page, double height)
    {
        if (file.Resolve(page.GetValueOrDefault("Annots")) is not List<object?> annotations)
        {
            yield break;
        }

        foreach (object? entry in annotations)
        {
            if (file.ResolveDict(entry) is not { } annotation ||
                file.Resolve(annotation.GetValueOrDefault("Subtype")) is not PdfName subtype ||
                subtype.Value != "Line")
            {
                continue;
            }

            if (file.Resolve(annotation.GetValueOrDefault("L")) is not List<object?> ends || ends.Count < 4)
            {
                continue;
            }

            double[] points = new double[4];
            bool complete = true;
            for (int i = 0; i < 4; i++)
            {
                points[i] = file.ResolveNumber(ends[i]) ?? double.NaN;
                complete &= !double.IsNaN(points[i]);
            }

            if (!complete)
            {
                continue;
            }

            object?[] styles = file.Resolve(annotation.GetValueOrDefault("LE")) is List<object?> lineEndings
                ? lineEndings.ToArray()
                : Array.Empty<object?>();

            var path = new PathItem { Name = "annotation", Stroke = StrokeSpec.Hairline(ColorRgb.Black) };
            SubPath line = path.AddSubPath(closed: false);
            line.Nodes.Add(new PathNode(new Point2D(points[0], height - points[1])));
            line.Nodes.Add(new PathNode(new Point2D(points[2], height - points[3])));

            path.MarkerStart = MarkerFor(file, styles, 0);
            path.MarkerEnd = MarkerFor(file, styles, 1);
            yield return path;
        }
    }

    private static string? MarkerFor(PdfFile file, object?[] styles, int index)
        => index < styles.Length && file.Resolve(styles[index]) is PdfName name ? name.Value : null;

    /// <summary>
    /// The library definition for a style name, created once per document. An empty or `/None` style draws nothing,
    /// which is what the specification says an empty line ending is.
    /// </summary>
    public static string? DefinitionFor(CadDocument document, string? style)
    {
        if (style is null || style.Length == 0 || style == "None")
        {
            return null;
        }

        string name = "pdf-" + style.ToLowerInvariant();
        if (document.FindDefinition(name) is { })
        {
            return name;
        }

        var definition = new ArtGroup { Name = name };

        // The tile is the ending's own box, six units of the stroke's width across, with the reference point at the
        // tip - which is where a line ending meets the line it ends.
        definition.ForeignAttributes["markerWidth"] = "6";
        definition.ForeignAttributes["markerHeight"] = "6";
        definition.ForeignAttributes["refX"] = "6";
        definition.ForeignAttributes["refY"] = "3";
        definition.ForeignAttributes["markerUnits"] = "strokeWidth";
        definition.ForeignAttributes["overflow"] = "visible";
        definition.ForeignAttributes["data-pdf-line-ending"] = style;

        bool filled = style is "ClosedArrow" or "Diamond" or "Square" or "Circle";
        var art = new PathItem
        {
            Name = "ending",
            Fill = filled ? FillSpec.Solid(ColorRgb.Black) : FillSpec.None,
            Stroke = filled ? StrokeSpec.None : StrokeSpec.Hairline(ColorRgb.Black),
        };

        SubPath shape = art.AddSubPath(closed: filled);
        foreach (Point2D node in Shape(style))
        {
            shape.Nodes.Add(new PathNode(node));
        }

        definition.AddItem(art);
        document.Definitions.AddItem(definition);
        return name;
    }

    /// <summary>
    /// This reader's rendering of a named ending, in the tile's own six-by-six units with the tip at (6, 3).
    /// </summary>
    private static IEnumerable<Point2D> Shape(string style) => style switch
    {
        "ClosedArrow" => new[]
        {
            new Point2D(0, 0), new Point2D(6, 3), new Point2D(0, 6),
        },
        "Diamond" => new[]
        {
            new Point2D(6, 3), new Point2D(3, 0), new Point2D(0, 3), new Point2D(3, 6),
        },
        "Square" => new[]
        {
            new Point2D(6, 0), new Point2D(6, 6), new Point2D(3, 6), new Point2D(3, 0),
        },
        "Circle" => new[]
        {
            new Point2D(6, 3), new Point2D(4.5, 0.8), new Point2D(1.5, 0.8), new Point2D(3, 3),
            new Point2D(1.5, 5.2), new Point2D(4.5, 5.2),
        },

        // `/OpenArrow`, and anything else the specification adds later: two strokes from the tip, which is the one
        // shape every reader draws for an open ending.
        _ => new[]
        {
            new Point2D(0, 0), new Point2D(6, 3), new Point2D(0, 6),
        },
    };
}