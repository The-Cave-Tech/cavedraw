using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// Circular arcs as cubic Bézier nodes.
///
/// A round corner, a round line join and a round cap are the same curve, so the construction lives in one
/// place: a quarter turn per cubic with the usual constant, which is accurate to a few hundredths of a
/// percent of the radius - far below anything that can be rendered or printed.
/// </summary>
internal static class ArcBuilder
{
    /// <summary>The control-point ratio for a quarter circle: 4/3 * tan(pi/8).</summary>
    internal const double Kappa = 0.5522847498307936;

    /// <summary>
    /// Appends an arc about <paramref name="centre"/> to a node list, as cubics of at most a quarter turn
    /// each.
    ///
    /// The first node is the arc's start, unless the list already ends there - which it does when the arc
    /// continues from a segment that has just been appended, and adding it again would leave a
    /// zero-length segment behind.
    /// </summary>
    internal static void Append(
        List<PathNode> nodes, Point2D centre, double radius, double startAngle, double sweep)
    {
        if (radius <= 0 || Math.Abs(sweep) < 1e-12)
        {
            return;
        }

        // The epsilon matters: a sweep that is a quarter turn exactly lands a hair over pi/2 in floating
        // point, and the ceiling then turns one cubic into two - harmless to look at, but it inflates every
        // arc's node count and breaks the arithmetic anyone does on the outline afterwards.
        int steps = Math.Max(1, (int)Math.Ceiling((Math.Abs(sweep) - 1e-9) / (Math.PI / 2)));
        double step = sweep / steps;

        // The circle's own tangent runs counterclockwise, so an arc swept the other way needs it reversed:
        // without that the handles point backwards, the cubic loops through itself, and the arc takes
        // area away instead of putting it there.
        double sense = sweep >= 0 ? 1 : -1;

        for (int i = 0; i < steps; i++)
        {
            double start = startAngle + (step * i);
            double end = start + step;
            // The MAGNITUDE of the control ratio: for a clockwise sweep the step is negative, and taking
            // the tangent of a negative angle makes k negative - which points both handles backwards and
            // turns the arc inside out. The direction is already carried by the tangents below.
            double k = (4.0 / 3.0) * Math.Tan(Math.Abs(step) / 4.0) * radius;

            var startPoint = new Point2D(
                centre.X + (radius * Math.Cos(start)), centre.Y + (radius * Math.Sin(start)));
            var endPoint = new Point2D(
                centre.X + (radius * Math.Cos(end)), centre.Y + (radius * Math.Sin(end)));

            var startTangent = new Vector2D(-Math.Sin(start) * sense, Math.Cos(start) * sense);
            var endTangent = new Vector2D(-Math.Sin(end) * sense, Math.Cos(end) * sense);

            PathNode first = nodes.Count > 0 && nodes[^1].Anchor.NearlyEquals(startPoint, 1e-9)
                ? nodes[^1]
                : new PathNode(startPoint);
            if (!nodes.Contains(first))
            {
                nodes.Add(first);
            }

            first.OutHandle = new Point2D(
                startPoint.X + (startTangent.X * k), startPoint.Y + (startTangent.Y * k));

            nodes.Add(new PathNode(endPoint)
            {
                InHandle = new Point2D(
                    endPoint.X - (endTangent.X * k), endPoint.Y - (endTangent.Y * k)),
            });
        }
    }
}
