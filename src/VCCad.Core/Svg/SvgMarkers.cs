using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>
/// The `&lt;marker&gt;` elements a document defines - Inkscape's arrowheads - with the arithmetic that places one at
/// a vertex.
///
/// **Why this is geometry and not a report.** A `&lt;pattern&gt;` is a paint, and the model's paint has no room for
/// a tile, so <see cref="SvgPatterns"/> can only name it. A marker is the opposite kind of thing: the file states
/// where it goes (a vertex), which way it points (the tangent there) and how big it is (the stroke's width), and the
/// model can hold that artwork at that place exactly. Placing it reflects the file instead of approximating it.
///
/// **What the model cannot say, and the reader reports.** In SVG a marker is a *stroke property*: it belongs to the
/// path and follows it when the path is edited. The model's <see cref="StrokeSpec"/> has no marker member, so the
/// arrowhead is an object beside the path rather than a property of it, and the import says so out loud.
///
/// The arithmetic is SVG 1.1 §11.6.4's: the content is carried into a coordinate system rotated by the tangent,
/// scaled by the stroke width when `markerUnits="strokeWidth"`, then translated so that the marker's own
/// `refX`/`refY` sits on the vertex. `refX`/`refY` are read as coordinates **in the view box** - which is what the
/// specification's own worked example does, multiplying them by the view-box scale before subtracting them.
/// </summary>
internal sealed class SvgMarkers
{
    private readonly Dictionary<string, MarkerDefinition> _markers = new(StringComparer.Ordinal);

    /// <summary>Reads every `marker` in the document. A marker with no id cannot be referred to and is skipped.</summary>
    public static SvgMarkers Collect(XElement root)
    {
        var markers = new SvgMarkers();

        foreach (XElement element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName != "marker" ||
                (!string.IsNullOrEmpty(element.Name.NamespaceName) && element.Name.Namespace != SvgReader.Svg))
            {
                continue;
            }

            if (element.Attribute("id")?.Value is not { Length: > 0 } id)
            {
                continue;
            }

            markers._markers[id] = Read(element, id);
        }

        return markers;
    }

    /// <summary>The marker called <paramref name="id"/>, or null when the document defines no such marker.</summary>
    public MarkerDefinition? Definition(string id)
        => _markers.TryGetValue(id, out MarkerDefinition? marker) ? marker : null;

    /// <summary>True when the document defines a `marker` under this id, whatever is wrong with it.</summary>
    public bool Defines(string id) => _markers.ContainsKey(id);

    /// <summary>
    /// One `marker` element as the file writes it. Nothing here is resolved against a path: a definition is a
    /// document asset, and the path only supplies the vertex, the heading and the stroke width.
    /// </summary>
    internal sealed class MarkerDefinition
    {
        public required string Id { get; init; }
        public required XElement Element { get; init; }

        /// <summary>`markerUnits="strokeWidth"`, the default: the tile is scaled by the referencing stroke's width.</summary>
        public bool StrokeWidthUnits { get; init; } = true;

        public double MarkerWidth { get; init; } = 3.0;
        public double MarkerHeight { get; init; } = 3.0;
        public double RefX { get; init; }
        public double RefY { get; init; }

        /// <summary>How `orient` decides the rotation.</summary>
        public MarkerOrient Orient { get; init; } = MarkerOrient.Auto;

        /// <summary>The stated angle in degrees, when `orient` is one.</summary>
        public double AngleDegrees { get; init; }

        /// <summary>The `viewBox`, when the file writes one, and the `preserveAspectRatio` to fit it with.</summary>
        public double[]? ViewBox { get; init; }
        public string? Aspect { get; init; }

        /// <summary>Whether the marker's content is cut at its viewport - SVG's default for a marker, which is `hidden`.</summary>
        public bool Clips { get; init; } = true;

        /// <summary>What the file states that this reader does not read, named so the import can report it.</summary>
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The transform carrying the marker's own content into the user space the vertex is stated in.
        ///
        /// Read it outermost first: put the vertex at the origin, turn the axes along the heading, scale into the
        /// marker's units, move the reference point onto the origin, and finally let the view box map the content
        /// into those units.
        /// </summary>
        public AffineTransform Placement(Point2D vertex, double headingRadians, double strokeWidth)
        {
            (AffineTransform viewBox, Point2D reference) = ContentFrame();

            return AffineTransform.CreateTranslation(vertex.X, vertex.Y)
                .Compose(AffineTransform.CreateRotation(headingRadians))
                .Compose(AffineTransform.CreateScale(Unit(strokeWidth), Unit(strokeWidth)))
                .Compose(AffineTransform.CreateTranslation(-reference.X, -reference.Y))
                .Compose(viewBox);
        }

        /// <summary>
        /// The marker's viewport - `markerWidth` by `markerHeight` in the marker's own units - as an outline in the
        /// user space the vertex is stated in, for the implicit clip SVG's default `overflow: hidden` establishes.
        /// </summary>
        public ClipSpec? ViewportClip(Point2D vertex, double headingRadians, double strokeWidth)
        {
            if (!Clips || MarkerWidth <= 0 || MarkerHeight <= 0)
            {
                return null;
            }

            AffineTransform units = AffineTransform.CreateTranslation(vertex.X, vertex.Y)
                .Compose(AffineTransform.CreateRotation(headingRadians))
                .Compose(AffineTransform.CreateScale(Unit(strokeWidth), Unit(strokeWidth)));

            var clip = new ClipSpec { Rule = FillRule.NonZero };
            var outline = new SubPath { IsClosed = true };

            foreach ((double x, double y) in new[]
                     {
                         (0.0, 0.0), (MarkerWidth, 0.0), (MarkerWidth, MarkerHeight), (0.0, MarkerHeight),
                     })
            {
                outline.Nodes.Add(new PathNode(units.Transform(new Point2D(x, y))));
            }

            clip.SubPaths.Add(outline);
            return clip;
        }

        /// <summary>The scale one marker unit is worth: the stroke's width, or one for `userSpaceOnUse`.</summary>
        private double Unit(double strokeWidth) => StrokeWidthUnits ? strokeWidth : 1.0;

        /// <summary>
        /// The transform carrying the marker's content into its viewport, together with where the reference point
        /// ends up in that viewport.
        ///
        /// With no `viewBox` SVG assumes one the size of the viewport at the same origin, which is the identity - so
        /// `refX`/`refY` are the content's own numbers.
        /// </summary>
        private (AffineTransform Frame, Point2D Reference) ContentFrame()
        {
            if (ViewBox is not { Length: 4 } box || box[2] <= 0 || box[3] <= 0 ||
                MarkerWidth <= 0 || MarkerHeight <= 0)
            {
                return (AffineTransform.Identity, new Point2D(RefX, RefY));
            }

            (double scaleX, double scaleY, double offsetX, double offsetY) =
                SvgReader.Fit(MarkerWidth, MarkerHeight, box[2], box[3], Aspect);

            AffineTransform frame = AffineTransform.CreateTranslation(offsetX, offsetY)
                .Compose(AffineTransform.CreateScale(scaleX, scaleY))
                .Compose(AffineTransform.CreateTranslation(-box[0], -box[1]));

            return (frame, frame.Transform(new Point2D(RefX, RefY)));
        }
    }

    /// <summary>How a marker's `orient` decides its rotation.</summary>
    internal enum MarkerOrient
    {
        /// <summary>Along the path's tangent - SVG's `auto`.</summary>
        Auto,

        /// <summary>`auto`, except that a marker at a start vertex points the other way.</summary>
        AutoStartReverse,

        /// <summary>A fixed angle in the referencing element's user space.</summary>
        Angle,
    }

    private static MarkerDefinition Read(XElement marker, string id)
    {
        var problems = new List<string>();

        string? units = marker.Attribute("markerUnits")?.Value?.Trim();
        bool strokeWidthUnits = units is null || units.Equals("strokeWidth", StringComparison.OrdinalIgnoreCase);
        if (units is not null &&
            !strokeWidthUnits &&
            !units.Equals("userSpaceOnUse", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                $"the <marker> '{id}' states markerUnits=\"{units}\", which is neither 'strokeWidth' nor " +
                "'userSpaceOnUse', so the size of its content is not established");
        }

        string? orient = marker.Attribute("orient")?.Value?.Trim();
        MarkerOrient kind = MarkerOrient.Auto;
        double angle = 0.0;
        if (orient is not null)
        {
            if (orient.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                kind = MarkerOrient.Auto;
            }
            else if (orient.Equals("auto-start-reverse", StringComparison.OrdinalIgnoreCase))
            {
                kind = MarkerOrient.AutoStartReverse;
            }
            else if (Angle(orient) is { } degrees)
            {
                // An angle, with or without the unit - the specification allows `45` and `45deg` alike.
                kind = MarkerOrient.Angle;
                angle = degrees;
            }
            else
            {
                problems.Add(
                    $"the <marker> '{id}' states orient=\"{orient}\", which is neither 'auto', " +
                    "'auto-start-reverse' nor an angle");
            }
        }

        // `overflow` is a property and inherits, but a marker's own value is what decides its viewport; the
        // specification's user-agent sheet makes it `hidden`, so anything but an explicit `visible` cuts.
        string? overflow = marker.Attribute("overflow")?.Value?.Trim();
        bool clips = overflow is null || !overflow.Equals("visible", StringComparison.OrdinalIgnoreCase);

        string? width = marker.Attribute("markerWidth")?.Value;
        string? height = marker.Attribute("markerHeight")?.Value;

        return new MarkerDefinition
        {
            Id = id,
            Element = marker,
            StrokeWidthUnits = strokeWidthUnits,
            MarkerWidth = Number(width) ?? 3.0,
            MarkerHeight = Number(height) ?? 3.0,
            RefX = Number(marker.Attribute("refX")?.Value) ?? 0.0,
            RefY = Number(marker.Attribute("refY")?.Value) ?? 0.0,
            Orient = kind,
            AngleDegrees = angle,
            ViewBox = SvgReader.Numbers(marker.Attribute("viewBox")?.Value),
            Aspect = marker.Attribute("preserveAspectRatio")?.Value,
            Clips = clips,
            Problems = problems,
        };
    }

    private static double? Number(string? text)
        => double.TryParse(
            text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    /// <summary>
    /// An `orient` angle in degrees, accepting the unit the specification allows (`45deg`) as well as a bare
    /// number. Null when the value is not an angle at all, which the caller reports.
    /// </summary>
    private static double? Angle(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.EndsWith("deg", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].Trim();
        }

        return Number(trimmed);
    }
}
