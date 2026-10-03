using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>How a marker's `orient` decides its rotation.</summary>
public enum MarkerOrient
{
    /// <summary>Along the path's tangent - SVG's `auto`.</summary>
    Auto,

    /// <summary>`auto`, except that a marker at a start vertex points the other way.</summary>
    AutoStartReverse,

    /// <summary>A fixed angle in the referencing element's user space.</summary>
    Angle,
}

/// <summary>
/// How a marker is placed: the parameters a `&lt;marker&gt;` states, and the transform they mean (issue #202).
///
/// **Why this is separate from the reader's own definition type.** An arrowhead is a *property of the path* - the
/// path names a marker, a renderer places it at the vertex with the tangent there - so the placement is needed by
/// every renderer, not only by the importer that reads the element. This holds the parameters and the arithmetic
/// once; <see cref="SvgMarkers"/> (the import) uses it, and the canvas and the exporter reach it through
/// <see cref="From"/> on a **library definition's** attributes, which is where a document keeps the marker it read.
///
/// Two implementations of this formula would be two chances to disagree about which way an arrowhead points - the
/// kind of divergence this repository keeps recording - so there is one.
/// </summary>
public sealed record MarkerSpec(
    bool StrokeWidthUnits = true,
    double MarkerWidth = 3.0,
    double MarkerHeight = 3.0,
    double RefX = 0.0,
    double RefY = 0.0,
    MarkerOrient Orient = MarkerOrient.Auto,
    double AngleDegrees = 0.0,
    double[]? ViewBox = null,
    string? Aspect = null,
    bool Clips = true)
{
    /// <summary>
    /// The transform carrying the marker's own content into the user space the vertex is stated in.
    ///
    /// Read it outermost first: put the vertex at the origin, turn the axes along the heading, scale into the
    /// marker's units, move the reference point onto the origin, and finally let the view box map the content
    /// into those units. This is SVG 1.1 §11.6.4's arithmetic, and every renderer of a marker uses exactly it.
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
    public double Unit(double strokeWidth) => StrokeWidthUnits ? strokeWidth : 1.0;

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

    /// <summary>
    /// The spec a library definition carries, read from its own foreign attributes (issue #202).
    ///
    /// The reader stores a marker's placement attributes on the definition it puts in the library, under the names
    /// the file wrote - so this reads the same names with the same defaults and the same units. Anything it cannot
    /// read is **named** through <paramref name="warn"/> rather than silently defaulted: an arrowhead placed by a
    /// number nobody understood is a picture that looks deliberate.
    /// </summary>
    public static MarkerSpec From(IReadOnlyDictionary<string, string> attributes, Action<string>? warn = null)
    {
        string? Read(string name)
            => attributes.TryGetValue(name, out string? value) ? value : null;

        string? units = Read("markerUnits")?.Trim();
        bool strokeWidthUnits = units is null || units.Equals("strokeWidth", StringComparison.OrdinalIgnoreCase);
        if (units is not null &&
            !strokeWidthUnits &&
            !units.Equals("userSpaceOnUse", StringComparison.OrdinalIgnoreCase))
        {
            warn?.Invoke(
                $"a marker states markerUnits=\"{units}\", which is neither 'strokeWidth' nor 'userSpaceOnUse', " +
                "so the size of its content is not established");
        }

        string? orient = Read("orient")?.Trim();
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
                kind = MarkerOrient.Angle;
                angle = degrees;
            }
            else
            {
                warn?.Invoke(
                    $"a marker states orient=\"{orient}\", which is neither 'auto', 'auto-start-reverse' nor an " +
                    "angle");
            }
        }

        // `overflow` is a property and inherits, but a marker's own value decides its viewport; the
        // specification's user-agent sheet makes it `hidden`, so anything but an explicit `visible` cuts.
        string? overflow = Read("overflow")?.Trim();

        return new MarkerSpec(
            strokeWidthUnits,
            Number(Read("markerWidth")) ?? 3.0,
            Number(Read("markerHeight")) ?? 3.0,
            Number(Read("refX")) ?? 0.0,
            Number(Read("refY")) ?? 0.0,
            kind,
            angle,
            SvgReader.Numbers(Read("viewBox")),
            Read("preserveAspectRatio"),
            overflow is null || !overflow.Equals("visible", StringComparison.OrdinalIgnoreCase));
    }

    private static double? Number(string? text)
        => double.TryParse(
            text?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out double value)
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
