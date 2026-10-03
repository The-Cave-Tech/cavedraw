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
        /// How this marker is placed. **The arithmetic lives in <see cref="MarkerSpec"/>, not here** (issue #202):
        /// the canvas and the exporter place markers too, and a second copy of this formula is a second chance to
        /// disagree about which way an arrowhead points.
        /// </summary>
        public MarkerSpec Spec => new(
            StrokeWidthUnits,
            MarkerWidth,
            MarkerHeight,
            RefX,
            RefY,
            Orient,
            AngleDegrees,
            ViewBox,
            Aspect,
            Clips);

        /// <summary>The transform carrying the marker's own content into the user space the vertex is stated in.</summary>
        public AffineTransform Placement(Point2D vertex, double headingRadians, double strokeWidth)
            => Spec.Placement(vertex, headingRadians, strokeWidth);

        /// <summary>The marker's viewport as an outline, for the clip `overflow: hidden` establishes.</summary>
        public ClipSpec? ViewportClip(Point2D vertex, double headingRadians, double strokeWidth)
            => Spec.ViewportClip(vertex, headingRadians, strokeWidth);




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
