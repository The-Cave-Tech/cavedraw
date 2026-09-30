using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>Which parameter of a shape a control point moves.</summary>
public enum ShapeHandle
{
    /// <summary>Where the shape is.</summary>
    Centre,

    /// <summary>How wide the box is.</summary>
    Width,

    /// <summary>How tall the box is.</summary>
    Height,

    /// <summary>Which way the shape faces.</summary>
    Rotation,

    /// <summary>Where a star's or polygon's inner ring sits, as a fraction of the outer.</summary>
    InnerRatio,

    /// <summary>How round a rounded rectangle's or callout's corners are.</summary>
    CornerRadius,

    /// <summary>How wide a trapezoid's top is, as a fraction of its bottom.</summary>
    TopRatio,

    /// <summary>How long an arrow's head is, as a fraction of its length.</summary>
    HeadLength,

    /// <summary>How wide an arrow's head is, as a fraction of the box.</summary>
    HeadWidth,

    /// <summary>How wide an arrow's shaft is, as a fraction of the box.</summary>
    ShaftWidth,

    /// <summary>Where a callout's tail points.</summary>
    Tail,
}

/// <summary>A control point: which parameter it moves, and where it sits in model space.</summary>
public sealed record ShapeHandlePoint(ShapeHandle Handle, Point2D Position);

/// <summary>
/// The control points of a shape: one for each parameter that shape actually has, positioned where that
/// parameter lives, and the arithmetic to move one.
///
/// Two things make this its own piece of work rather than part of the canvas.
///
/// **A shape only offers the handles it has.** A heart has a centre, a width and a height; a star also has
/// its inner ring; an arrow has three more; a callout has a corner radius and a tail. Drawing a full set on
/// every shape would put handles on a heart that change nothing, which teaches a person that handles are
/// unreliable.
///
/// **Every handle is clamped at the degenerate end.** A zero width, a negative radius or an inner ring
/// outside its outer one is not a shape, and letting a drag produce one puts the object in a state no
/// handle can drag it out of.
///
/// The positions are in **model space with rotation applied** - the shape's own parameters are unrotated and
/// turned by <see cref="ShapeParameters.Rotation"/> when the outline is built, so a handle has to be
/// un-rotated to be understood and re-rotated to be drawn.
/// </summary>
public static class ShapeHandles
{
    /// <summary>The smallest a width or height may become, so a shape cannot be dragged flat and stuck.</summary>
    public const double MinExtent = 1.0;

    /// <summary>The handles this shape has, where each one sits, in model space.</summary>
    public static IReadOnlyList<ShapeHandlePoint> For(ShapeDefinition shape)
    {
        ShapeParameters p = shape.Parameters;
        (double hw, double hh) = Half(p);
        var points = new List<ShapeHandlePoint>
        {
            new(ShapeHandle.Centre, p.Centre),

            // Width and height are dragged from the right edge and the bottom edge, so the box reads as a
            // box: a person pulls the side they want to move.
            new(ShapeHandle.Width, At(p, new Point2D(hw, 0))),
            new(ShapeHandle.Height, At(p, new Point2D(0, hh))),
            new(ShapeHandle.Rotation, At(p, new Point2D(0, -hh - RotationOffset(p)))),
        };

        // The inner ring, drawn between the centre and the outer radius where the ratio lives.
        if (shape.Kind is ShapeKind.Star or ShapeKind.Polygon)
        {
            points.Add(new ShapeHandlePoint(
                ShapeHandle.InnerRatio, At(p, new Point2D(0, -hh * Clamp(p.InnerRatio, 0.02, 0.98)))));
        }

        if (shape.Kind is ShapeKind.RoundedRectangle or ShapeKind.Callout)
        {
            double radius = Clamp(p.CornerRadius, 0, Math.Min(hw, hh));
            points.Add(new ShapeHandlePoint(
                ShapeHandle.CornerRadius, At(p, new Point2D(-hw + radius, -hh + radius))));
        }

        if (shape.Kind is ShapeKind.Trapezoid)
        {
            points.Add(new ShapeHandlePoint(
                ShapeHandle.TopRatio, At(p, new Point2D(-hw + (2 * hw * Clamp(p.TopRatio, 0, 1)), -hh))));
        }

        if (shape.Kind is ShapeKind.Arrow)
        {
            points.Add(new ShapeHandlePoint(
                ShapeHandle.HeadLength, At(p, new Point2D(hw - (2 * hw * Clamp(p.HeadLength, 0.05, 0.95)), 0))));
            points.Add(new ShapeHandlePoint(
                ShapeHandle.HeadWidth, At(p, new Point2D(hw - (2 * hw * Clamp(p.HeadLength, 0.05, 0.95)), -hw * Clamp(p.HeadWidth, 0.1, 2)))));
            points.Add(new ShapeHandlePoint(
                ShapeHandle.ShaftWidth, At(p, new Point2D(0, -hh * Clamp(p.ShaftWidth, 0.02, 1.8)))));
        }

        if (p.HasTail)
        {
            points.Add(new ShapeHandlePoint(ShapeHandle.Tail, p.Tail));
        }

        return points;
    }

    /// <summary>
    /// The parameters that result from dragging a handle to a point, **clamped** so the result is still a
    /// shape. Returns the same parameters when the handle does not belong to this shape.
    /// </summary>
    public static ShapeParameters Move(ShapeDefinition shape, ShapeHandle handle, Point2D to)
    {
        ShapeParameters p = shape.Parameters;
        (double hw, double hh) = Half(p);

        // Into the shape's own frame: the parameters are unrotated, so a pointer has to be turned back
        // before it means anything to them.
        Point2D local = Unrotate(p, to);

        return handle switch
        {
            ShapeHandle.Centre => p with
            {
                Centre = p.Centre + (to - p.Centre),
                Tail = p.HasTail ? p.Tail + (to - p.Centre) : p.Tail,
            },

            ShapeHandle.Width => p with
            {
                Width = Math.Max(MinExtent, Math.Abs(local.X - p.Centre.X) * 2),
            },

            ShapeHandle.Height => p with
            {
                Height = Math.Max(MinExtent, Math.Abs(local.Y - p.Centre.Y) * 2),
            },

            ShapeHandle.Rotation => p with
            {
                Rotation = Bearing(p.Centre, to),
            },

            ShapeHandle.InnerRatio => p with
            {
                InnerRatio = Clamp(Distance(p.Centre, to) / Math.Max(Math.Min(hw, hh), 1e-6), 0.02, 0.98),
            },

            ShapeHandle.CornerRadius => p with
            {
                CornerRadius = Clamp(Distance(new Point2D(p.Centre.X - hw, p.Centre.Y - hh), to), 0, Math.Min(hw, hh)),
            },

            ShapeHandle.TopRatio => p with
            {
                TopRatio = Clamp((local.X - (p.Centre.X - hw)) / Math.Max(2 * hw, 1e-6), 0, 1),
            },

            ShapeHandle.HeadLength => p with
            {
                HeadLength = Clamp(((p.Centre.X + hw) - local.X) / Math.Max(2 * hw, 1e-6), 0.05, 0.95),
            },

            ShapeHandle.HeadWidth => p with
            {
                HeadWidth = Clamp(Math.Abs(local.Y - p.Centre.Y) / Math.Max(hw, 1e-6), 0.1, 2.0),
            },

            ShapeHandle.ShaftWidth => p with
            {
                ShaftWidth = Clamp(Math.Abs(local.Y - p.Centre.Y) / Math.Max(hh, 1e-6), 0.02, 1.8),
            },

            ShapeHandle.Tail => p with { HasTail = true, Tail = to },

            _ => p,
        };
    }

    /// <summary>The handle nearest a point, or null when none is within reach.</summary>
    public static ShapeHandlePoint? Nearest(ShapeDefinition shape, Point2D point, double within)
    {
        ShapeHandlePoint? best = null;
        double bestDistance = within;

        foreach (ShapeHandlePoint handle in For(shape))
        {
            double distance = Distance(handle.Position, point);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = handle;
            }
        }

        return best;
    }

    /// <summary>How far above the box the rotation handle sits, so it is clear of the corner handles.</summary>
    private static double RotationOffset(ShapeParameters p) => Math.Max(12, p.Height * 0.15);

    private static (double HalfWidth, double HalfHeight) Half(ShapeParameters p)
        => (Math.Max(p.Width, MinExtent) / 2, Math.Max(p.Height, MinExtent) / 2);

    /// <summary>A point in the shape's own unrotated frame, moved to where it belongs on the page.</summary>
    private static Point2D At(ShapeParameters p, Point2D local)
    {
        double cos = Math.Cos(p.Rotation);
        double sin = Math.Sin(p.Rotation);
        return new Point2D(
            p.Centre.X + (local.X * cos) - (local.Y * sin),
            p.Centre.Y + (local.X * sin) + (local.Y * cos));
    }

    /// <summary>A model point turned back into the shape's own unrotated frame.</summary>
    private static Point2D Unrotate(ShapeParameters p, Point2D point)
    {
        double dx = point.X - p.Centre.X;
        double dy = point.Y - p.Centre.Y;
        double cos = Math.Cos(-p.Rotation);
        double sin = Math.Sin(-p.Rotation);
        return new Point2D(p.Centre.X + (dx * cos) - (dy * sin), p.Centre.Y + (dx * sin) + (dy * cos));
    }

    private static double Bearing(Point2D from, Point2D to)
        => Math.Atan2(to.Y - from.Y, to.X - from.X) + (Math.PI / 2);

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static double Clamp(double value, double min, double max)
        => Math.Clamp(value, min, max);
}
