using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Color;

/// <summary>
/// Geometry and colour of the spectrum ring.
///
/// Angles are degrees measured **clockwise** from the +X axis in the screen
/// frame (x right, y down): 0° is to the right, 90° straight down. That is the
/// frame of every renderer this model feeds, and it means the hue can simply be
/// the angle, wrapped into [0, 360) — the ring colour at an angle is the HSV
/// colour at that hue, so there is no accumulated rotation and therefore no
/// drift as the picker is dragged around.
/// </summary>
public static class SpectrumRing
{
    /// <summary>Full turn in degrees.</summary>
    public const double TurnDegrees = 360.0;

    /// <summary>Wraps an angle (or hue) into [0, 360).</summary>
    public static double NormalizeAngle(double angleDegrees) => ColorConversion.NormalizeHue(angleDegrees);

    /// <summary>The hue selected at a ring angle. The angle is the hue, wrapped.</summary>
    public static double HueForAngle(double angleDegrees) => NormalizeAngle(angleDegrees);

    /// <summary>The ring angle at which a hue sits. The hue is the angle, wrapped.</summary>
    public static double AngleForHue(double hueDegrees) => NormalizeAngle(hueDegrees);

    /// <summary>The fully saturated colour of the ring at an angle.</summary>
    public static ColorRgb ColorAtAngle(double angleDegrees) => HsvColor.HueColor(HueForAngle(angleDegrees));

    /// <summary>The point on a ring of the given centre and radius at an angle.</summary>
    public static Point2D PointAtAngle(Point2D center, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        return new Point2D(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }

    /// <summary>
    /// The angle of a point measured from the ring centre, wrapped into [0, 360).
    /// The inverse of <see cref="PointAtAngle"/> for any point away from the centre.
    /// </summary>
    public static double AngleAtPoint(Point2D center, Point2D point)
    {
        double angle = Math.Atan2(point.Y - center.Y, point.X - center.X) * 180.0 / Math.PI;
        return NormalizeAngle(angle);
    }

    /// <summary>The ring colour under a pointer position (the ring is a pure hue wheel).</summary>
    public static ColorRgb ColorAtPoint(Point2D center, Point2D point)
        => ColorAtAngle(AngleAtPoint(center, point));
}
