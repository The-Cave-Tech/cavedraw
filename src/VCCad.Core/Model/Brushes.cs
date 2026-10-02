using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// The kinds of brush a stroke can carry.
///
/// A brush is what a stroke is **swept with**, as against a width profile, which only says how wide the
/// stroke is at a place. The two are different questions - a nib's width depends on the direction of
/// travel, which a profile cannot express - so a brush is its own member rather than another profile.
/// The family is expected to grow (an art brush, a pattern brush, a scatter brush, a bristle brush), which
/// is why the kind is carried explicitly: the serializer and the operations switch on it rather than on
/// which optional members happen to be set.
/// </summary>
public enum BrushKind
{
    /// <summary>An elliptical nib swept along the path, its width following the direction of travel.</summary>
    Calligraphic,
}

/// <summary>
/// A brush as it travels with a document: its kind, the name it is known by, and the parameters of that kind.
///
/// The calligraphic nib is the first of the family (issue #99) and is what the members below describe. The
/// members of another kind travel here too, for the same reason <see cref="GradientSpec"/> carries the
/// geometry of every gradient kind: a file's value is what it says, and an asset whose parameters were
/// dropped when it went through a build that knew one more kind would be lossy in a way nobody could see.
///
/// **The nib is an ellipse of a stated angle and roundness.** <see cref="Diameter"/> is the length of its
/// long axis, <see cref="Roundness"/> is the short axis as a fraction of the long one (1 is a circular
/// pen), and <see cref="AngleDegrees"/> is the direction the long axis points, measured from the +X axis
/// towards +Y - the same sense as a path direction, which is what lets the two be compared directly.
///
/// A path is stroked with a brush by offsetting it by the **half-width the nib projects across the
/// direction of travel**: a nib edge-on to the path (its long axis along it) draws its short axis, and
/// broadside (across it) draws its long one. That is what makes a calligraphic line look drawn.
/// </summary>
public sealed record BrushSpec(
    string Name,
    double AngleDegrees,
    double Roundness,
    double Diameter,
    BrushKind Kind = BrushKind.Calligraphic,
    DynamicsSpec? Dynamics = null)
{
    /// <summary>An elliptical nib: the calligraphic brush, named for the shape rather than the kind.</summary>
    public static BrushSpec Calligraphic(
        string name, double angleDegrees, double roundness, double diameter, DynamicsSpec? dynamics = null)
        => new(name, angleDegrees, roundness, diameter, BrushKind.Calligraphic, dynamics);

    /// <summary>
    /// Whether the nib is a circular pen, which draws the same width however the path runs.
    ///
    /// Not used to route around the brush: a round nib still becomes an outline, because it is a brush and
    /// its answer has to be the same one the nibs that vary give. It is here for callers that report what a
    /// brush does rather than draw it.
    /// </summary>
    public bool IsRound => Math.Clamp(Roundness, 0.0, 1.0) >= 1.0 - 1e-12;

    /// <summary>
    /// The half-widths the nib lays down where the path is travelling in <paramref name="direction"/> - the
    /// distance from the centreline to the nib's edge, measured across the direction of travel.
    ///
    /// The nib is the ellipse with semi-axes <c>a = Diameter/2</c> along <see cref="AngleDegrees"/> and
    /// <c>b = a * Roundness</c> across it. Its support distance in a direction making an angle theta with
    /// the long axis is <c>sqrt(a^2 sin^2(theta) + b^2 cos^2(theta))</c> - the projection of the ellipse
    /// onto the direction the width is measured in, which is exactly what the pen lays down. At theta = 0
    /// the long axis leads and the short semi-axis answers; at 90 degrees the long axis answers and the line
    /// is at its thickest.
    ///
    /// Left and right are equal because an ellipse is symmetric about its centre: the nib cuts the same
    /// distance on both sides of the line, so a nib never produces the one-sided stroke a width profile can.
    /// </summary>
    public (double Left, double Right) Halves(Vector2D direction)
    {
        double major = Math.Max(0.0, Diameter) / 2.0;
        double minor = major * Math.Clamp(Roundness, 0.0, 1.0);

        // The direction may be the zero vector where a path has nothing to say about it (a doubled-back
        // corner); the +X axis is the same fallback the parameterisation uses.
        double travel = Math.Atan2(direction.Y, direction.X);
        double theta = travel - (AngleDegrees * Math.PI / 180.0);

        double sin = Math.Sin(theta);
        double cos = Math.Cos(theta);
        double half = Math.Sqrt((major * major * sin * sin) + (minor * minor * cos * cos));
        return (half, half);
    }

    /// <summary>A copy whose nib is scaled, which is what a renderer's group transform does to it.</summary>
    public BrushSpec Scaled(double scale) => this with { Diameter = Diameter * scale };

    /// <summary>Whether this brush modulates itself from the pen. False when it records no dynamics.</summary>
    public bool HasDynamics => Dynamics is { IsEmpty: false };
}
