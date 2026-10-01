namespace VCCad.Core.Model;

/// <summary>How the width between two width points is interpolated.</summary>
public enum WidthInterpolation
{
    /// <summary>A straight line between the two widths.</summary>
    Linear,

    /// <summary>
    /// A smooth ease between them - zero slope at both ends - which reads as a drawn taper rather than a cone.
    /// </summary>
    Cubic,
}

/// <summary>
/// One width point on a profile: where along the path it applies, and how wide the stroke is on each side of it.
///
/// **Left and right are separate** because a width profile is not a symmetric bulge. A stroke can swell on one
/// side only, which is what makes a profile read as a drawn line rather than a fattened one.
///
/// Left and right are relative to the direction of travel, as a person reading the path would see it: on a path
/// drawn left to right, <see cref="LeftWidth"/> is the side above it.
/// </summary>
public sealed record WidthPoint(
    double Position,
    double LeftWidth,
    double RightWidth,
    WidthInterpolation Interpolation = WidthInterpolation.Linear)
{
    /// <summary>A point the same width on both sides, which is the common case.</summary>
    public static WidthPoint Even(
        double position, double width, WidthInterpolation interpolation = WidthInterpolation.Linear)
        => new(position, width, width, interpolation);

    /// <summary>Half-widths, which is what the geometry wants: the outline is the centreline moved by these.</summary>
    public (double Left, double Right) Halves => (LeftWidth / 2.0, RightWidth / 2.0);
}

/// <summary>
/// A reusable width profile: width points along a path, from t=0 to t=1.
///
/// A profile is an **asset** - it lives in the document, is referenced by the strokes that use it, and travels
/// with the document. A profile is part of a drawing's style, like a colour, and one that vanished when the file
/// was re-opened would be a style that does not survive its own document.
///
/// The points are **sorted by position in the constructor**, so "the width at t" does not depend on the order a
/// caller happened to list them in. That is the whole reason this is not a bare positional record: the ordering
/// is an invariant of the type rather than something every caller has to remember.
/// </summary>
public sealed record WidthProfileSpec
{
    public WidthProfileSpec(string name, IEnumerable<WidthPoint> points)
    {
        Name = name;
        Points = points.OrderBy(p => p.Position).ToArray();
    }

    /// <summary>
    /// Two profiles are the same when their name and every point are the same.
    ///
    /// Written out rather than left to the record, because the record would compare the <c>Points</c> **array by
    /// reference**: two profiles read from the same file would differ from each other, and every round-trip test
    /// built on equality would fail for a reason that has nothing to do with the round trip. `DashPattern` does
    /// the same thing for the same reason.
    /// </summary>
    public bool Equals(WidthProfileSpec? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Name != other.Name || Points.Count != other.Points.Count)
        {
            return false;
        }

        for (int i = 0; i < Points.Count; i++)
        {
            if (Points[i] != other.Points[i])
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        foreach (WidthPoint point in Points)
        {
            hash.Add(point);
        }

        return hash.ToHashCode();
    }

    /// <summary>The name this asset is known by, which is what a person picks it by.</summary>
    public string Name { get; init; }

    /// <summary>The width points, ordered by position.</summary>
    public IReadOnlyList<WidthPoint> Points { get; init; }

    /// <summary>A profile that is the same width everywhere - the same thing as an ordinary stroke.</summary>
    public static WidthProfileSpec Constant(double width)
        => new("Uniform", new[] { WidthPoint.Even(0, width), WidthPoint.Even(1, width) });

    /// <summary>A taper from fat to thin, the profile most drawings reach for first.</summary>
    public static WidthProfileSpec Taper(double from, double to)
        => new("Taper", new[]
        {
            WidthPoint.Even(0, from),
            WidthPoint.Even(1, to, WidthInterpolation.Cubic),
        });

    /// <summary>Whether there is anything to apply - a profile with no points says nothing about width.</summary>
    public bool IsEmpty => Points.Count == 0;

    /// <summary>
    /// The half-widths at a position along the path, or null when there is nothing to report.
    ///
    /// Clamped outside 0..1 and interpolated between the two points that straddle the position. Null rather than
    /// a number when the profile is empty: an empty profile means "no opinion", and inventing zero would erase
    /// the stroke the caller is applying it to.
    /// </summary>
    public (double Left, double Right)? HalvesAt(double position)
    {
        if (Points.Count == 0)
        {
            return null;
        }

        if (Points.Count == 1)
        {
            return Points[0].Halves;
        }

        double t = Math.Clamp(position, 0.0, 1.0);

        int index = -1;
        for (int i = 0; i < Points.Count; i++)
        {
            if (Points[i].Position <= t)
            {
                index = i;
            }
        }

        if (index < 0)
        {
            return Points[0].Halves;
        }

        if (index >= Points.Count - 1)
        {
            return Points[^1].Halves;
        }

        WidthPoint previous = Points[index];
        WidthPoint next = Points[index + 1];
        double span = next.Position - previous.Position;
        if (span <= 0)
        {
            // Two points at the same position: a hard step, where the later one wins like a paint order.
            return next.Halves;
        }

        double f = Math.Clamp((t - previous.Position) / span, 0.0, 1.0);
        if (next.Interpolation == WidthInterpolation.Cubic)
        {
            f = f * f * (3.0 - (2.0 * f));
        }

        (double leftA, double rightA) = previous.Halves;
        (double leftB, double rightB) = next.Halves;
        return (leftA + ((leftB - leftA) * f), rightA + ((rightB - rightA) * f));
    }
}
