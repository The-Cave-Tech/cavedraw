using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>The shapes a person can draw to paint.</summary>
public enum ShapeKind
{
    Rectangle,
    RoundedRectangle,
    Star,
    Polygon,
    Trapezoid,
    Cloud,
    Callout,
    Heart,
    Arrow,
}

/// <summary>
/// The parameters a shape is made from. One record rather than one per kind, because the shapes share
/// most of their vocabulary - a centre, a size, an angle - and a caller that wants "a star with these
/// numbers" should not have to know which nine fields are the star's.
///
/// Every field has a default that produces a sensible shape on its own, so a caller can say
/// <c>new ShapeParameters { Centre = p, Width = 40, Height = 40 }</c> and get a star.
/// </summary>
public sealed record ShapeParameters
{
    /// <summary>The shape's centre, in model space.</summary>
    public Point2D Centre { get; init; }

    /// <summary>Overall width before rotation.</summary>
    public double Width { get; init; } = 100;

    /// <summary>Overall height before rotation.</summary>
    public double Height { get; init; } = 100;

    /// <summary>Clockwise rotation about the centre, in degrees.</summary>
    public double Rotation { get; init; }

    /// <summary>Corner radius for <see cref="ShapeKind.RoundedRectangle"/> and the callout's box.</summary>
    public double CornerRadius { get; init; } = 12;

    /// <summary>Points on a star, sides on a polygon, lobes on a cloud.</summary>
    public int Points { get; init; } = 5;

    /// <summary>A star's inner radius as a fraction of its outer one.</summary>
    public double InnerRatio { get; init; } = 0.45;

    /// <summary>A trapezoid's top edge as a fraction of its bottom one.</summary>
    public double TopRatio { get; init; } = 0.55;

    /// <summary>An arrow's head length as a fraction of its length.</summary>
    public double HeadLength { get; init; } = 0.35;

    /// <summary>
    /// An arrow's head half-width as a fraction of its half-height. One by default, so an arrow
    /// fills the height it was given; narrower makes a daintier head, which is a deliberate choice
    /// rather than the default a person has to undo.
    /// </summary>
    public double HeadWidth { get; init; } = 1.0;

    /// <summary>An arrow's shaft half-width as a fraction of its half-height.</summary>
    public double ShaftWidth { get; init; } = 0.32;

    /// <summary>Where a callout's tail points, in model space.</summary>
    public Point2D Tail { get; init; }

    /// <summary>Whether a tail has been placed; without one the callout is its box alone.</summary>
    public bool HasTail { get; init; }
}

/// <summary>
/// Builds the nine paint shapes as <see cref="PathItem"/>s of ordinary nodes and handles.
///
/// Nothing here is a new kind of geometry: a star is twenty nodes, a rounded rectangle is eight with
/// bezier handles, a heart is four cubics. That is deliberate - the shapes paint, export and edit as
/// every other path does, and the only thing that makes them shapes is
/// <see cref="ShapeDefinition"/>, which remembers the parameters they came from.
///
/// **Exactness is the whole value.** A star whose points do not reach the radius, or a polygon whose
/// vertices sit slightly inside it, looks right in a thumbnail and is wrong in a drawing. So each
/// shape is built to its stated size, and the tests measure rather than look.
/// </summary>
public static class ShapeLibrary
{
    /// <summary>The names a person sees and an operation accepts.</summary>
    public static string Name(ShapeKind kind) => kind switch
    {
        ShapeKind.Rectangle => "rectangle",
        ShapeKind.RoundedRectangle => "rounded-rectangle",
        ShapeKind.Star => "star",
        ShapeKind.Polygon => "polygon",
        ShapeKind.Trapezoid => "trapezoid",
        ShapeKind.Cloud => "cloud",
        ShapeKind.Callout => "callout",
        ShapeKind.Heart => "heart",
        ShapeKind.Arrow => "arrow",
        _ => "shape",
    };

    /// <summary>Every kind, in the order the picker lists them.</summary>
    public static IReadOnlyList<ShapeKind> All { get; } = Enum.GetValues<ShapeKind>();

    /// <summary>A closed path for <paramref name="kind"/> at these parameters. Never null.</summary>
    public static PathItem Create(ShapeKind kind, ShapeParameters parameters)
    {
        PathItem path = Build(kind, parameters);
        path.Name = Name(kind);
        return path;
    }

    private static PathItem Build(ShapeKind kind, ShapeParameters p) => kind switch
    {
        ShapeKind.Rectangle => Rectangle(p),
        ShapeKind.RoundedRectangle => RoundedRectangle(p),
        ShapeKind.Star => Star(p),
        ShapeKind.Polygon => Polygon(p),
        ShapeKind.Trapezoid => Trapezoid(p),
        ShapeKind.Cloud => Cloud(p),
        ShapeKind.Callout => Callout(p),
        ShapeKind.Heart => Heart(p),
        ShapeKind.Arrow => Arrow(p),
        _ => Rectangle(p),
    };

    /// <summary>
    /// Places a point expressed in the shape's own frame - x to the right, y down, centred on the
    /// origin - into model space, applying the rotation. Every builder below works this way, so
    /// rotation is applied once and identically instead of nine times differently.
    /// </summary>
    private static Point2D Place(ShapeParameters p, double x, double y)
    {
        double radians = p.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new Point2D(
            p.Centre.X + (x * cos) - (y * sin),
            p.Centre.Y + (x * sin) + (y * cos));
    }

    /// <summary>Half the size, with a floor so a zero-sized shape is still a shape.</summary>
    private static (double HalfWidth, double HalfHeight) Half(ShapeParameters p)
        => (Math.Max(Math.Abs(p.Width) / 2, 0.01), Math.Max(Math.Abs(p.Height) / 2, 0.01));

    private static PathItem Rectangle(ShapeParameters p)
    {
        (double hw, double hh) = Half(p);
        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);
        sub.AppendNode(Place(p, -hw, -hh));
        sub.AppendNode(Place(p, hw, -hh));
        sub.AppendNode(Place(p, hw, hh));
        sub.AppendNode(Place(p, -hw, hh));
        return path;
    }

    /// <summary>
    /// A rectangle with its corners turned by quarter-circle cubics.
    ///
    /// Two nodes per corner - the arc's two ends - each carrying the tangent handle the circle wants,
    /// which is <see cref="PathFactory.Kappa"/> times the radius. The radius is clamped to half the
    /// shorter side: a radius larger than the side it is turning is not a shape, and inventing one
    /// would produce overlapping arcs that look like a mistake because they are one.
    /// </summary>
    private static PathItem RoundedRectangle(ShapeParameters p)
    {
        (double hw, double hh) = Half(p);
        double radius = Math.Clamp(p.CornerRadius, 0, Math.Min(hw, hh));
        double k = radius * PathFactory.Kappa;

        if (radius <= 0.01)
        {
            return Rectangle(p);
        }

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);

        void Corner(double x, double y, double towardX, double towardY)
        {
            // The tangent runs along the side being left and the side being joined; the two ends of
            // the arc are the corner offset by the radius along each.
            double ax = x - (towardX * radius);
            double ay = y;
            double bx = x;
            double by = y - (towardY * radius);
            PathNode a = sub.AppendNode(Place(p, ax, ay));
            a.InHandle = Place(p, ax - (towardX * k), ay);
            PathNode b = sub.AppendNode(Place(p, bx, by));
            b.OutHandle = Place(p, bx, by + (towardY * k));
        }

        // Top-left, top-right, bottom-right, bottom-left; `toward` says which way each corner turns.
        Corner(-hw, -hh, -1, -1);
        Corner(hw, -hh, 1, -1);
        Corner(hw, hh, 1, 1);
        Corner(-hw, hh, -1, 1);
        return path;
    }

    /// <summary>
    /// A star of <paramref name="p"/>.Points points: 2N nodes alternating outer and inner radius, so a
    /// five-pointed star is ten straight segments - which is the ten that symmetric editing works on.
    /// </summary>
    private static PathItem Star(ShapeParameters p)
    {
        (double hw, double hh) = Half(p);
        int points = Math.Max(3, p.Points);
        double outerX = hw;
        double outerY = hh;

        // An inner radius above the outer one would turn the star inside out; the geometry stops at
        // the outer radius rather than drawing something the caller cannot have meant.
        double inner = Math.Clamp(p.InnerRatio, 0.02, 1.0);

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);

        for (int i = 0; i < points * 2; i++)
        {
            double angle = (Math.PI * i / points) - (Math.PI / 2);
            bool isOuter = i % 2 == 0;
            double rx = isOuter ? outerX : outerX * inner;
            double ry = isOuter ? outerY : outerY * inner;
            sub.AppendNode(Place(p, rx * Math.Cos(angle), ry * Math.Sin(angle)));
        }

        return path;
    }

    /// <summary>A regular polygon: N vertices on the radius, so N straight segments.</summary>
    private static PathItem Polygon(ShapeParameters p)
    {
        (double hw, double hh) = Half(p);
        int sides = Math.Max(3, p.Points);

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);

        for (int i = 0; i < sides; i++)
        {
            double angle = (2 * Math.PI * i / sides) - (Math.PI / 2);
            sub.AppendNode(Place(p, hw * Math.Cos(angle), hh * Math.Sin(angle)));
        }

        return path;
    }

    /// <summary>A trapezoid: a full-width bottom edge and a narrower top one, centred.</summary>
    private static PathItem Trapezoid(ShapeParameters p)
    {
        (double hw, double hh) = Half(p);
        double top = hw * Math.Clamp(p.TopRatio, 0, 1);

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);
        sub.AppendNode(Place(p, -top, -hh));  // top-left
        sub.AppendNode(Place(p, top, -hh));   // top-right
        sub.AppendNode(Place(p, hw, hh));     // bottom-right
        sub.AppendNode(Place(p, -hw, hh));    // bottom-left
        return path;
    }

    /// <summary>
    /// A cloud: a ring of outward arcs whose radius varies a little lobe to lobe, so it reads as
    /// billows rather than as a circle with scallops. The bottom is flattened slightly, which is what
    /// stops it looking like a spiky ball.
    /// </summary>
    private static PathItem Cloud(ShapeParameters p)
    {
        (double hw, double hh) = Half(p);
        int lobes = Math.Max(3, p.Points);

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);

        for (int i = 0; i < lobes * 2; i++)
        {
            double angle = (Math.PI * i / lobes) - (Math.PI / 2);
            // Alternating long and short radii give the billow; the pair is the lobe.
            double scale = i % 2 == 0 ? 1.0 : 0.72;
            double x = hw * scale * Math.Cos(angle);
            double y = hh * scale * Math.Sin(angle) * (Math.Sin(angle) > 0 ? 1.0 : 0.82);

            // The handle runs along the circle through the point, so consecutive lobes meet in a
            // smooth billow rather than a crease.
            double tangent = Math.PI * (i + 0.5) / lobes;
            double k = 0.55 * (i % 2 == 0 ? hw : hw * 0.72);
            Point2D inHandle = Place(p, x - (k * Math.Cos(tangent)), y - (k * Math.Sin(tangent)));
            Point2D outHandle = Place(p, x + (k * Math.Cos(tangent)), y + (k * Math.Sin(tangent)));
            PathNode node = sub.AppendNode(Place(p, x, y));
            node.InHandle = inHandle;
            node.OutHandle = outHandle;
        }

        return path;
    }

    /// <summary>
    /// A callout: a box with rounded corners and a tail running to a tip. The tail is part of the same
    /// closed outline - it joins the box's bottom edge and comes back to it - so the shape fills and
    /// strokes as one outline rather than as a box with a line near it.
    /// </summary>
    private static PathItem Callout(ShapeParameters p)
    {
        (double hw, double hh) = Half(p);
        double radius = Math.Clamp(p.CornerRadius, 0, Math.Min(hw, hh) * 0.9);

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);

        // The box, with square corners for now: the tail's join needs the plain edge, and the corners
        // are arced below by the same construction the rounded rectangle uses.
        double tailBase = Math.Clamp(hw * 0.25, 0, hw);

        if (p.HasTail)
        {
            // Clockwise from the top-left, with the tail hanging off the bottom edge.
            sub.AppendNode(Place(p, -hw + radius, -hh));
            sub.AppendNode(Place(p, hw - radius, -hh));
            sub.AppendNode(Place(p, hw, -hh + radius));
            sub.AppendNode(Place(p, hw, hh - radius));
            sub.AppendNode(Place(p, hw - radius, hh));
            sub.AppendNode(Place(p, tailBase, hh));
            sub.AppendNode(p.Tail);
            sub.AppendNode(Place(p, -tailBase, hh));
            sub.AppendNode(Place(p, -hw + radius, hh));
            sub.AppendNode(Place(p, -hw, hh - radius));
            sub.AppendNode(Place(p, -hw, -hh + radius));

            // The two corners that want a curve get one, by pulling the handle toward the corner.
            double k = radius * PathFactory.Kappa;
            sub.Nodes[0].InHandle = Place(p, -hw + radius - k, -hh);
            sub.Nodes[1].OutHandle = Place(p, hw - radius + k, -hh);
            return path;
        }

        PathItem box = RoundedRectangle(p);
        box.Name = Name(ShapeKind.Callout);
        return box;
    }

    /// <summary>
    /// A heart: two cubics for the upper lobes and two for the point, which is the fewest curves that
    /// give a heart its characteristic shoulders.
    /// </summary>
    private static PathItem Heart(ShapeParameters p)
    {
        (double hw, double hh) = Half(p);
        double k = PathFactory.Kappa;

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);

        // The bottom point, then up the left lobe to the cleft, then the right lobe and back.
        PathNode bottom = sub.AppendNode(Place(p, 0, hh));
        bottom.InHandle = Place(p, -hw * 0.62, hh * 0.42);
        bottom.OutHandle = Place(p, hw * 0.62, hh * 0.42);

        PathNode left = sub.AppendNode(Place(p, -hw, -hh * 0.28));
        left.InHandle = Place(p, -hw * 1.08, hh * 0.1);
        left.OutHandle = Place(p, -hw * 1.08, -hh * 0.62);

        PathNode cleft = sub.AppendNode(Place(p, 0, -hh * 0.42));
        cleft.InHandle = Place(p, -hw * 0.52, -hh * 1.1);
        cleft.OutHandle = Place(p, hw * 0.52, -hh * 1.1);

        PathNode right = sub.AppendNode(Place(p, hw, -hh * 0.28));
        right.InHandle = Place(p, hw * 1.08, -hh * 0.62);
        right.OutHandle = Place(p, hw * 1.08, hh * 0.1);

        return path;
    }

    /// <summary>A block arrow: shaft, head, and the two barbs - seven corners, all straight.</summary>
    private static PathItem Arrow(ShapeParameters p)
    {
        (double hw, double hh) = Half(p);
        double shaft = hh * Math.Clamp(p.ShaftWidth, 0.02, 1.0);
        double head = hw * Math.Clamp(p.HeadLength, 0.05, 0.95);
        double headHalf = hh * Math.Clamp(p.HeadWidth, 0.05, 1.0);
        double headStart = hw - head;

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);
        sub.AppendNode(Place(p, -hw, -shaft));        // shaft top-left
        sub.AppendNode(Place(p, headStart, -shaft));  // shaft top-right
        sub.AppendNode(Place(p, headStart, -headHalf)); // barb top
        sub.AppendNode(Place(p, hw, 0));              // tip
        sub.AppendNode(Place(p, headStart, headHalf));  // barb bottom
        sub.AppendNode(Place(p, headStart, shaft));   // shaft bottom-right
        sub.AppendNode(Place(p, -hw, shaft));         // shaft bottom-left
        return path;
    }
}
