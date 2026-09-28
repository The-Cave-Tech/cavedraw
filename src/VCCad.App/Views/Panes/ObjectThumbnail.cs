using Avalonia;
using Avalonia.Media;
using VCCad.Core.Model;
using VCCad.Geometry;
using MediaGeometry = Avalonia.Media.Geometry;

namespace VCCad.App.Views.Panes;

/// <summary>
/// The little sample of an object that sits at the left of its row.
///
/// It is the object's own shape, scaled down and fitted to the box, rather than an icon per
/// kind: a rectangle looks like a rectangle, a diagonal line looks like a diagonal line, and
/// two objects of the same kind do not look identical. That is the point of showing it.
///
/// Returned as geometry rather than as a bitmap because the panel is virtualised over
/// thousands of rows — a picture per row would be thousands of allocations for rows nobody
/// is looking at, while a geometry is a few numbers.
/// </summary>
public static class ObjectThumbnail
{
    /// <summary>
    /// The item's shape, fitted into a <paramref name="size"/> square and centred in it, or
    /// null when there is nothing to draw.
    /// </summary>
    public static MediaGeometry? For(LayerItem item, double size)
    {
        if (size <= 0)
        {
            return null;
        }

        return item switch
        {
            PathItem path => FromSubPaths(path.SubPaths, path.BoundingBox(), size),
            TextItem text => FromText(text, size),
            ImageItem image => Frame(image.Placement, SizeOf(image), size),
            ArtGroup group => FromSubPaths(
                group.Children.OfType<PathItem>().SelectMany(p => p.SubPaths).ToList(),
                group.Transform.Transform(group.BoundingBox()),
                size),
            _ => null,
        };
    }

    private static Rect2D SizeOf(LayerItem item) => item switch
    {
        ImageItem image => image.Placement,
        _ => Rect2D.Empty,
    };

    /// <summary>
    /// A path's own outline. The fit is uniform so a long thin line stays long and thin
    /// rather than being stretched to fill the box, which would make it look like something
    /// it is not.
    /// </summary>
    private static MediaGeometry? FromSubPaths(IReadOnlyList<SubPath> subPaths, Rect2D bounds, double size)
    {
        // A line is a legitimate object with no thickness, so "empty" is not the test: only a
        // point has nothing to draw. Rejecting a zero-height box outright would give every
        // horizontal line an empty square.
        if (subPaths.Count == 0 || (bounds.Width <= 0 && bounds.Height <= 0))
        {
            return null;
        }

        double scale = FitScale(bounds, size);
        var geometry = new StreamGeometry();

        using (StreamGeometryContext g = geometry.Open())
        {
            g.SetFillRule(Avalonia.Media.FillRule.NonZero);

            foreach (SubPath sub in subPaths)
            {
                if (sub.Nodes.Count == 0)
                {
                    continue;
                }

                Point Map(Point2D p) => new(
                    ((p.X - bounds.Left) * scale) + ((size - (bounds.Width * scale)) / 2),
                    ((p.Y - bounds.Top) * scale) + ((size - (bounds.Height * scale)) / 2));

                g.BeginFigure(Map(sub.Nodes[0].Anchor), sub.IsClosed);

                int last = sub.IsClosed ? sub.Nodes.Count : sub.Nodes.Count - 1;
                for (int i = 0; i < last; i++)
                {
                    PathNode from = sub.Nodes[i];
                    PathNode to = sub.Nodes[(i + 1) % sub.Nodes.Count];
                    g.CubicBezierTo(Map(from.OutHandle), Map(to.InHandle), Map(to.Anchor));
                }

                g.EndFigure(sub.IsClosed);
            }
        }

        return geometry;
    }

    /// <summary>
    /// The first few characters of a text run, as outlines.
    ///
    /// Outlines rather than a text control because the row has to show the run in its own
    /// face at a size nobody chose; taking the glyph shapes lets the sample be scaled to the
    /// box like every other thumbnail.
    /// </summary>
    private static MediaGeometry? FromText(TextItem text, double size)
    {
        string content = text.PlainText.Trim();
        if (content.Length == 0)
        {
            return null;
        }

        TextRun? run = text.Runs.FirstOrDefault(r => r.Text.Trim().Length > 0);
        string sample = new string(content.Take(3).ToArray());

        var typeface = new Typeface(
            new FontFamily(run?.FontFamily ?? TextItem.DefaultFontFamily),
            run?.Italic == true ? FontStyle.Italic : FontStyle.Normal,
            run?.Bold == true ? FontWeight.Bold : FontWeight.Normal);

        var built = new FormattedText(
            sample, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 100, Brushes.Black);

        MediaGeometry? outlines = built.BuildGeometry(new Point(0, 0));
        if (outlines is null)
        {
            return null;
        }

        Rect2D box = new(
            outlines.Bounds.X, outlines.Bounds.Y,
            outlines.Bounds.Width, outlines.Bounds.Height);

        if (box.IsEmpty)
        {
            return null;
        }

        double scale = FitScale(box, size);
        var fitted = new GeometryGroup
        {
            Transform = new MatrixTransform(
                Matrix.CreateScale(scale, scale) *
                Matrix.CreateTranslation(
                    (-box.Left * scale) + ((size - (box.Width * scale)) / 2),
                    (-box.Top * scale) + ((size - (box.Height * scale)) / 2))),
        };

        fitted.Children.Add(outlines);
        return fitted;
    }

    /// <summary>
    /// An image's extent, drawn as its frame.
    ///
    /// A picture's pixels are not available to draw: a JPEG in a file is passed through
    /// untouched because this reader cannot open it, so there is nothing here to shrink. The
    /// frame still says "an image, this shape, this proportion", which is what the row is for.
    /// </summary>
    private static MediaGeometry? Frame(Rect2D bounds, Rect2D _, double size)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        double scale = FitScale(bounds, size);
        double w = bounds.Width * scale;
        double h = bounds.Height * scale;
        double x = (size - w) / 2;
        double y = (size - h) / 2;

        var geometry = new StreamGeometry();

        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(x, y), true);
            g.LineTo(new Point(x + w, y));
            g.LineTo(new Point(x + w, y + h));
            g.LineTo(new Point(x, y + h));
            g.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>Uniform scale that fits a box inside the square, never enlarging past 1:1.</summary>
    private static double FitScale(Rect2D bounds, double size)
    {
        double longest = Math.Max(bounds.Width, bounds.Height);
        if (longest <= 0)
        {
            return 1;
        }

        return size / longest;
    }
}
