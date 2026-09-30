using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>Which kind of gradient paint a fill carries.</summary>
public enum GradientKind
{
    /// <summary>Straight ramp from a start point to an end point.</summary>
    Linear,

    /// <summary>Ramp from a centre outward to a radius (possibly elliptical).</summary>
    Radial,

    /// <summary>Illustrator's freeform: scattered colour points blended by proximity.</summary>
    Freeform,

    /// <summary>
    /// Angular ramp around a centre. Not offered in Illustrator's own gradient tool, so it is
    /// modelled and exported but is a later addition rather than part of the first slice.
    /// </summary>
    Conical,
}

/// <summary>
/// What happens outside the 0..1 ramp. Illustrator calls these "spread" methods; the PDF and
/// SVG shading dictionaries call them extend flags, and they are the same three ideas.
/// </summary>
public enum GradientSpread
{
    /// <summary>The end stops' colours continue outward. Illustrator's default.</summary>
    Pad,

    /// <summary>The ramp is mirrored back on itself, so it alternates.</summary>
    Reflect,

    /// <summary>The ramp repeats.</summary>
    Repeat,
}

/// <summary>
/// One stop on a gradient ramp.
///
/// Position is normalised 0..1 rather than a percentage: every consumer — PDF's function
/// domains, SVG's offsets, the ramp UI — works in 0..1, and percentages are a display concern.
///
/// <see cref="Color"/> is the stop's colour and <see cref="Opacity"/> is carried SEPARATELY, not
/// folded into an alpha channel. Illustrator interpolates opacity independently of colour, and a
/// colour that carries its own alpha cannot express a stop that is 100% opaque in a colour
/// that is itself semi-transparent, or a half-transparent black that must stay black.
/// </summary>
/// <param name="Position">Where on the ramp, 0..1.</param>
/// <param name="Color">The colour at this stop.</param>
/// <param name="Opacity">0..1, interpolated separately from the colour.</param>
/// <param name="Midpoint">0..1 position of this stop's blend midpoint toward the next stop.
/// 0.5 is an even blend; Illustrator exposes this as the diamond between stops.</param>
/// <param name="Name">Optional label for the UI. Never affects rendering.</param>
public sealed record GradientStop(
    double Position,
    ColorRgb Color,
    double Opacity = 1.0,
    double Midpoint = 0.5,
    string? Name = null)
{
    /// <summary>
    /// Clamps into the ramp, so a caller cannot build a stop that cannot be evaluated.
    ///
    /// Non-finite values are REPLACED rather than clamped, because Math.Clamp cannot repair NaN:
    /// Math.Clamp(double.NaN, 0, 1) returns NaN, so a NaN position survived this method and
    /// produced a NaN paint from Sample - a silent wrong colour rather than a refusal. A NaN
    /// position has no sensible place on a ramp, so it becomes the start; a NaN opacity becomes
    /// opaque, which is the value that changes the picture least; a NaN midpoint becomes even.
    /// </summary>
    public GradientStop Clamped() => this with
    {
        Position = double.IsFinite(Position) ? Math.Clamp(Position, 0.0, 1.0) : 0.0,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.0, 1.0) : 1.0,
        Midpoint = double.IsFinite(Midpoint) ? Math.Clamp(Midpoint, 0.0, 1.0) : 0.5,

        // Not Color.Clamped(): that uses Math.Clamp too, so it cannot repair a NaN channel any
        // more than this method could repair a NaN position. A NaN channel has no meaning; black
        // is the value that makes the wrongness visible rather than hiding it in a valid-looking
        // colour.
        Color = new ColorRgb(
            double.IsFinite(Color.R) ? Math.Clamp(Color.R, 0.0, 1.0) : 0.0,
            double.IsFinite(Color.G) ? Math.Clamp(Color.G, 0.0, 1.0) : 0.0,
            double.IsFinite(Color.B) ? Math.Clamp(Color.B, 0.0, 1.0) : 0.0,
            double.IsFinite(Color.A) ? Math.Clamp(Color.A, 0.0, 1.0) : 1.0),
    };
}

/// <summary>
/// One colour point of a freeform gradient, in the ARTBOARD's coordinate space rather than the
/// object's own: Illustrator lets a freeform point sit outside the shape it paints, and tying
/// the points to object-local coordinates would make them move when the object is merely
/// translated.
/// </summary>
public sealed record FreeformPoint(Point2D Position, ColorRgb Color, double Opacity = 1.0);

/// <summary>
/// Whether a freeform gradient blends every point against its neighbours, or only along the
/// lines the user drew. Illustrator's two modes, and they are genuinely different pictures:
/// Points is a smooth field, Lines is a set of ramps along paths.
/// </summary>
public enum FreeformMode
{
    /// <summary>Distance-weighted blend between all points.</summary>
    Points,

    /// <summary>Blend along the connecting lines only.</summary>
    Lines,
}

/// <summary>
/// A gradient, as a paint that can fill a path.
///
/// Geometry is stored in the coordinate space of the OBJECT being painted (Illustrator's
/// "gradient relative to the object"), NOT in artboard space, so moving an object carries its
/// gradient with it and the gradient keeps its relationship to the shape. The one exception is
/// <see cref="FreeformPoint.Position"/>, which is artboard-relative for the reason given there.
///
/// The type is a property rather than a subclass hierarchy because Illustrator lets the user
/// switch a gradient's type while keeping its stops, and a class hierarchy would make that a
/// conversion between unrelated types instead of a field change.
/// </summary>
public sealed record GradientSpec
{
    /// <summary>Stops, ordered by position. Never empty; see <see cref="Normalised"/>.</summary>
    public IReadOnlyList<GradientStop> Stops { get; init; } = DefaultStops();

    /// <summary>The kind of ramp.</summary>
    public GradientKind Kind { get; init; } = GradientKind.Linear;

    /// <summary>What happens outside 0..1.</summary>
    public GradientSpread Spread { get; init; } = GradientSpread.Pad;

    // ---- Linear geometry -------------------------------------------------
    // Normalised against the object's bounding box, which is how Illustrator stores it: the
    // start is 0,0-ish and the end 1,0-ish for the default left-to-right gradient, and both
    // stay meaningful when the object is resized.

    /// <summary>Linear start, normalised to the object bounds.</summary>
    public Point2D Start { get; init; } = new(0.0, 0.5);

    /// <summary>Linear end, normalised to the object bounds.</summary>
    public Point2D End { get; init; } = new(1.0, 0.5);

    // ---- Radial geometry -------------------------------------------------

    /// <summary>Radial centre, normalised to the object bounds.</summary>
    public Point2D Center { get; init; } = new(0.5, 0.5);

    /// <summary>Horizontal radius as a fraction of the object's width.</summary>
    public double RadiusX { get; init; } = 0.5;

    /// <summary>Vertical radius as a fraction of the object's height. Equal to RadiusX for a circle.</summary>
    public double RadiusY { get; init; } = 0.5;

    /// <summary>Rotation of an elliptical radial, in degrees.</summary>
    public double Rotation { get; init; }

    /// <summary>Conical start angle, in degrees.</summary>
    public double Angle { get; init; }

    // ---- Freeform geometry ----------------------------------------------

    /// <summary>Colour points for a freeform gradient.</summary>
    public IReadOnlyList<FreeformPoint> Points { get; init; } = Array.Empty<FreeformPoint>();

    /// <summary>How a freeform gradient blends.</summary>
    public FreeformMode FreeformMode { get; init; } = FreeformMode.Points;

    /// <summary>Index pairs into <see cref="Points"/> forming the drawn lines, in Lines mode.</summary>
    public IReadOnlyList<(int From, int To)> Lines { get; init; } = Array.Empty<(int, int)>();

    /// <summary>A plain two-stop black-to-white linear gradient, Illustrator's default new gradient.</summary>
    public static GradientSpec Default { get; } = new();

    private static IReadOnlyList<GradientStop> DefaultStops() => new[]
    {
        new GradientStop(0.0, ColorRgb.White),
        new GradientStop(1.0, new ColorRgb(0, 0, 0)),
    };

    /// <summary>
    /// Stops sorted by position and clamped into the ramp, so evaluation has a well-ordered ramp
    /// to work with whatever a caller or an imported file supplied.
    ///
    /// Stops at the SAME position are KEPT, both of them, and that is what makes a hard edge hard.
    /// An earlier version collapsed each duplicate run to its last stop, which quietly turned a
    /// hard edge into a fast ramp - a two-stop pair [0.5 black, 0.5 white] became just [0.5 white]
    /// and the colour approaching it ramped in from the previous stop, so "sample(0.499) is black,
    /// sample(0.501) is white" was not true. Evaluation handles a zero-width span by returning the
    /// later stop, so keeping both stops is all a hard edge needs.
    /// </summary>
    public IReadOnlyList<GradientStop> Normalised()
    {
        if (Stops.Count == 0)
        {
            return DefaultStops();
        }

        return Stops.Select(s => s.Clamped())
            .OrderBy(s => s.Position)
            .ToList();
    }

    /// <summary>
    /// The colour at a point of the artboard, for a freeform gradient, or null when there is
    /// nothing to blend.
    ///
    /// **Points** mode is a smooth field: every colour point pulls on the answer with a weight that
    /// falls off with the square of the distance, so each one dominates near itself and the space
    /// between them is a blend. A point sitting exactly on a colour point takes that colour
    /// outright - without that, two points close together would divide by a distance near zero and
    /// average into a colour that is neither of them.
    ///
    /// **Lines** mode is a set of ramps: the point is projected onto each drawn line, the ramp
    /// between that line's two endpoint colours is sampled at the projection, and those results are
    /// blended the same way. A line whose ends coincide contributes nothing rather than dividing by
    /// zero.
    ///
    /// The point is in the same artboard space as <see cref="FreeformPoint.Position"/>, which is the
    /// one piece of gradient geometry that is not relative to the object.
    /// </summary>
    public (ColorRgb Color, double Opacity)? SampleAt(Point2D point)
    {
        if (Kind != GradientKind.Freeform || Points.Count == 0)
        {
            return null;
        }

        if (FreeformMode == FreeformMode.Lines && Lines.Count > 0)
        {
            return SampleLines(point);
        }

        double weightSum = 0;
        double r = 0;
        double g = 0;
        double b = 0;
        double a = 0;
        double opacity = 0;

        foreach (FreeformPoint colourPoint in Points)
        {
            double dx = point.X - colourPoint.Position.X;
            double dy = point.Y - colourPoint.Position.Y;
            double squared = (dx * dx) + (dy * dy);

            if (squared <= 1e-12)
            {
                return (colourPoint.Color, colourPoint.Opacity);
            }

            double weight = 1.0 / squared;
            weightSum += weight;
            r += colourPoint.Color.R * weight;
            g += colourPoint.Color.G * weight;
            b += colourPoint.Color.B * weight;
            a += colourPoint.Color.A * weight;
            opacity += colourPoint.Opacity * weight;
        }

        if (weightSum <= 0)
        {
            return null;
        }

        return (new ColorRgb(r / weightSum, g / weightSum, b / weightSum, a / weightSum), opacity / weightSum);
    }

    /// <summary>
    /// The Lines mode field: each drawn line is a ramp between the colours of the two points it
    /// joins, and a point takes a blend of the nearest part of each ramp.
    /// </summary>
    private (ColorRgb Color, double Opacity)? SampleLines(Point2D point)
    {
        double weightSum = 0;
        double r = 0;
        double g = 0;
        double b = 0;
        double a = 0;
        double opacity = 0;

        foreach ((int from, int to) in Lines)
        {
            if (from < 0 || from >= Points.Count || to < 0 || to >= Points.Count)
            {
                continue;
            }

            FreeformPoint start = Points[from];
            FreeformPoint end = Points[to];
            double ax = end.Position.X - start.Position.X;
            double ay = end.Position.Y - start.Position.Y;
            double lengthSquared = (ax * ax) + (ay * ay);
            if (lengthSquared <= 1e-12)
            {
                continue;
            }

            // Where the point falls along the line, clamped to it: past either end the line's own
            // end colour is what it means, which is the same clamping a ramp's spread does.
            double t = Math.Clamp(
                (((point.X - start.Position.X) * ax) + ((point.Y - start.Position.Y) * ay)) / lengthSquared,
                0.0,
                1.0);

            double projectedX = start.Position.X + (ax * t);
            double projectedY = start.Position.Y + (ay * t);
            double dx = point.X - projectedX;
            double dy = point.Y - projectedY;
            double squared = (dx * dx) + (dy * dy);

            // A point ON a line takes that line outright rather than dividing by zero.
            if (squared <= 1e-12)
            {
                return (Blend(start.Color, end.Color, t), start.Opacity + ((end.Opacity - start.Opacity) * t));
            }

            double weight = 1.0 / squared;
            ColorRgb colour = Blend(start.Color, end.Color, t);
            weightSum += weight;
            r += colour.R * weight;
            g += colour.G * weight;
            b += colour.B * weight;
            a += colour.A * weight;
            opacity += (start.Opacity + ((end.Opacity - start.Opacity) * t)) * weight;
        }

        if (weightSum <= 0)
        {
            return null;
        }

        return (new ColorRgb(r / weightSum, g / weightSum, b / weightSum, a / weightSum), opacity / weightSum);
    }

    /// <summary>
    /// The colour and opacity at a ramp position, interpolated between the surrounding stops.
    ///
    /// A midpoint other than 0.5 biases the blend, which is Illustrator's diamond control, so the
    /// interpolation is a power curve rather than a straight line when a midpoint is set.
    /// </summary>
    public (ColorRgb Color, double Opacity) Sample(double t)
    {
        IReadOnlyList<GradientStop> stops = Normalised();

        if (stops.Count == 1)
        {
            return (stops[0].Color, stops[0].Opacity);
        }

        if (t <= stops[0].Position)
        {
            return (stops[0].Color, stops[0].Opacity);
        }

        if (t >= stops[^1].Position)
        {
            return (stops[^1].Color, stops[^1].Opacity);
        }

        // Walk the pairs and keep the LAST one that contains t. That matters at a hard edge, where
        // two stops share a position: the pair ending at the edge and the zero-width pair starting
        // at it both "contain" t, and the later stop is the one a person sees on the right-hand
        // side of the edge. Taking the first match instead would resolve the boundary to the
        // colour before it.
        int found = -1;
        for (int i = 0; i < stops.Count - 1; i++)
        {
            if (t >= stops[i].Position && t <= stops[i + 1].Position)
            {
                found = i;
            }
        }

        if (found < 0)
        {
            return (stops[^1].Color, stops[^1].Opacity);
        }

        GradientStop a = stops[found];
        GradientStop b = stops[found + 1];
        double span = b.Position - a.Position;
        if (span <= 0)
        {
            return (b.Color, b.Opacity);
        }

        double u = (t - a.Position) / span;

        // The midpoint control is a power curve: 0.5 linear, below it biases toward the first
        // stop, above it toward the second.
        double m = Math.Clamp(a.Midpoint, 1e-6, 1.0 - 1e-6);
        if (Math.Abs(m - 0.5) > 1e-9)
        {
            double exponent = Math.Log(0.5) / Math.Log(m);
            u = Math.Pow(u, exponent);
        }

        return (Blend(a.Color, b.Color, u), a.Opacity + ((b.Opacity - a.Opacity) * u));
    }

    /// <summary>
    /// Applies the spread method to a ramp position before sampling, so a caller can ask for
    /// any t and get the extended behaviour rather than having to clamp first.
    /// </summary>
    public (ColorRgb Color, double Opacity) SampleWithSpread(double t)
    {
        double mapped = Spread switch
        {
            GradientSpread.Pad => Math.Clamp(t, 0.0, 1.0),
            GradientSpread.Reflect => Reflect(t),
            GradientSpread.Repeat => Repeat(t),
            _ => Math.Clamp(t, 0.0, 1.0),
        };

        return Sample(mapped);
    }

    /// <summary>Mirrors t back and forth so the ramp alternates indefinitely.</summary>
    private static double Reflect(double t)
    {
        double period = t % 2.0;
        if (period < 0)
        {
            period += 2.0;
        }

        return period <= 1.0 ? period : 2.0 - period;
    }

    /// <summary>Wraps t into 0..1.</summary>
    private static double Repeat(double t)
    {
        double wrapped = t % 1.0;
        return wrapped < 0 ? wrapped + 1.0 : wrapped;
    }

    /// <summary>
    /// Straight component-wise interpolation in RGB.
    ///
    /// This is deliberate rather than an sRGB-linear blend. Illustrator interpolates RGB stops
    /// component-wise in the document's RGB space, and blending in linear light produces visibly
    /// different midpoints - a black-to-white ramp would come out lighter in the middle - so
    /// matching the reference means matching this, however imperfect the colour science.
    ///
    /// Channels are 0..1 in this model (see ColorRgb.FromBytes). An earlier version of this
    /// method rounded to a byte 0..255 and handed that to the constructor, which silently
    /// collapsed every blend to 0 or 1 - a ramp came out almost black. The renderer noticed
    /// because it reads the stops directly rather than trusting Sample.
    /// </summary>
    public static ColorRgb Blend(ColorRgb a, ColorRgb b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        static double Mix(double x, double y, double u) => Math.Clamp(x + ((y - x) * u), 0.0, 1.0);

        return new ColorRgb(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t), Mix(a.A, b.A, t));
    }
}
