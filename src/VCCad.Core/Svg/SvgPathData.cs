using System.Globalization;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>
/// Parses an SVG path's `d` attribute into subpaths.
///
/// The grammar is deceptively small. Numbers are **not separated by commas**: `10-5` is ten and minus five, and
/// `1.5.5` is 1.5 and 0.5, because the sign and the second decimal point end the previous number. A parser that
/// split on whitespace and commas reads those as one malformed token and loses the rest of the path - which is why
/// the scanner here decides where a number ends rather than trusting the separators.
///
/// Quadratics become cubics and arcs become cubics, because the model has one curve type. Converting rather than
/// carrying a second kind of segment is what keeps every later stage - flattening, offsetting, effects - from
/// having to handle three, and the conversion is exact: a quadratic is a cubic with its control points two thirds
/// of the way out, and an arc is a run of cubics no more than a quarter turn each.
/// </summary>
internal static class SvgPathData
{
    public static IReadOnlyList<SubPath> Parse(string d)
    {
        var subpaths = new List<SubPath>();
        if (string.IsNullOrWhiteSpace(d))
        {
            return subpaths;
        }

        var tokens = Tokenise(d);
        int index = 0;

        double x = 0.0;
        double y = 0.0;
        double startX = 0.0;
        double startY = 0.0;

        // The previous curve's second control point, which is what S and T reflect. Null after anything that is not
        // a cubic or a quadratic, because a smooth command after a line has nothing to be smooth about.
        Point2D? lastControl = null;
        char command = '\0';
        SubPath? current = null;

        while (index < tokens.Count)
        {
            if (tokens[index] is string letter && letter.Length == 1 && char.IsLetter(letter[0]))
            {
                command = letter[0];
                index++;
            }
            else if (command == '\0')
            {
                // Numbers with no command: the file is malformed, and continuing would invent geometry.
                break;
            }
            else if (command == 'M')
            {
                // An implicit repeat of a move is a line, which the specification says explicitly and which files
                // rely on: "M0 0 10 10 20 20" is a move and two lines.
                command = 'L';
            }
            else if (command == 'm')
            {
                command = 'l';
            }

            bool relative = char.IsLower(command);
            char op = char.ToUpperInvariant(command);

            switch (op)
            {
                case 'M':
                {
                    if (!TryNumber(tokens, ref index, out double nx) || !TryNumber(tokens, ref index, out double ny))
                    {
                        return subpaths;
                    }

                    if (relative)
                    {
                        nx += x;
                        ny += y;
                    }

                    x = nx;
                    y = ny;
                    startX = x;
                    startY = y;
                    current = new SubPath { IsClosed = false };
                    current.Nodes.Add(new PathNode(new Point2D(x, y)));
                    subpaths.Add(current);
                    lastControl = null;
                    break;
                }

                case 'L':
                {
                    if (!TryNumber(tokens, ref index, out double nx) || !TryNumber(tokens, ref index, out double ny))
                    {
                        return subpaths;
                    }

                    if (relative)
                    {
                        nx += x;
                        ny += y;
                    }

                    x = nx;
                    y = ny;
                    Ensure(current, subpaths, ref current, x, y).Nodes.Add(new PathNode(new Point2D(x, y)));
                    lastControl = null;
                    break;
                }

                case 'H':
                {
                    if (!TryNumber(tokens, ref index, out double nx))
                    {
                        return subpaths;
                    }

                    x = relative ? x + nx : nx;
                    Ensure(current, subpaths, ref current, x, y).Nodes.Add(new PathNode(new Point2D(x, y)));
                    lastControl = null;
                    break;
                }

                case 'V':
                {
                    if (!TryNumber(tokens, ref index, out double ny))
                    {
                        return subpaths;
                    }

                    y = relative ? y + ny : ny;
                    Ensure(current, subpaths, ref current, x, y).Nodes.Add(new PathNode(new Point2D(x, y)));
                    lastControl = null;
                    break;
                }

                case 'C':
                case 'S':
                {
                    Point2D c1 = op == 'C'
                        ? NextPoint(tokens, ref index, x, y, relative)
                        : lastControl is { } reflected
                            ? new Point2D((2.0 * x) - reflected.X, (2.0 * y) - reflected.Y)
                            : new Point2D(x, y);

                    if (op == 'C' && (c1.X == double.MinValue))
                    {
                        return subpaths;
                    }

                    Point2D c2 = NextPoint(tokens, ref index, x, y, relative);
                    Point2D end = NextPoint(tokens, ref index, x, y, relative);
                    if (c2.X == double.MinValue || end.X == double.MinValue)
                    {
                        return subpaths;
                    }

                    SubPath sub = Ensure(current, subpaths, ref current, x, y);
                    sub.Nodes[^1].OutHandle = c1;
                    sub.Nodes.Add(new PathNode(end, c2, end));

                    lastControl = c2;
                    x = end.X;
                    y = end.Y;
                    break;
                }

                case 'Q':
                case 'T':
                {
                    Point2D control = op == 'Q'
                        ? NextPoint(tokens, ref index, x, y, relative)
                        : lastControl is { } reflected
                            ? new Point2D((2.0 * x) - reflected.X, (2.0 * y) - reflected.Y)
                            : new Point2D(x, y);

                    if (op == 'Q' && control.X == double.MinValue)
                    {
                        return subpaths;
                    }

                    Point2D end = NextPoint(tokens, ref index, x, y, relative);
                    if (end.X == double.MinValue)
                    {
                        return subpaths;
                    }

                    // A quadratic becomes the cubic with the same shape: each control point two thirds of the way
                    // from the endpoints towards the quadratic's control.
                    var c1 = new Point2D(
                        x + (2.0 / 3.0 * (control.X - x)),
                        y + (2.0 / 3.0 * (control.Y - y)));
                    var c2 = new Point2D(
                        end.X + (2.0 / 3.0 * (control.X - end.X)),
                        end.Y + (2.0 / 3.0 * (control.Y - end.Y)));

                    SubPath sub = Ensure(current, subpaths, ref current, x, y);
                    sub.Nodes[^1].OutHandle = c1;
                    sub.Nodes.Add(new PathNode(end, c2, end));

                    lastControl = control;
                    x = end.X;
                    y = end.Y;
                    break;
                }

                case 'A':
                {
                    if (!TryNumber(tokens, ref index, out double rx) ||
                        !TryNumber(tokens, ref index, out double ry) ||
                        !TryNumber(tokens, ref index, out double rotation) ||
                        !TryNumber(tokens, ref index, out double largeArc) ||
                        !TryNumber(tokens, ref index, out double sweep))
                    {
                        return subpaths;
                    }

                    Point2D end = NextPoint(tokens, ref index, x, y, relative);
                    if (end.X == double.MinValue)
                    {
                        return subpaths;
                    }

                    SubPath sub = Ensure(current, subpaths, ref current, x, y);
                    AppendArc(sub, new Point2D(x, y), end, rx, ry, rotation, largeArc != 0, sweep != 0);
                    x = end.X;
                    y = end.Y;
                    lastControl = null;
                    break;
                }

                case 'Z':
                {
                    if (current is not null)
                    {
                        // A closing **curve** is written as a segment back to where the subpath began and then `Z`.
                        // The model keeps that closing segment as the first node's incoming handle, so the last node
                        // is folded back into it - otherwise every round trip grows the path by a node, and the
                        // geometry stays right while the structure slowly drifts.
                        if (current.Nodes.Count > 2 &&
                            current.Nodes[^1].Anchor.NearlyEquals(current.Nodes[0].Anchor, 1e-9))
                        {
                            current.Nodes[0].InHandle = current.Nodes[^1].InHandle;
                            current.Nodes.RemoveAt(current.Nodes.Count - 1);
                        }

                        current.IsClosed = true;
                    }

                    x = startX;
                    y = startY;
                    current = null;
                    lastControl = null;
                    break;
                }

                default:
                    // An unknown command: stop rather than guess, so the path keeps what was read correctly.
                    return subpaths;
            }
        }

        // A subpath with one node is a point, which is not geometry.
        subpaths.RemoveAll(sp => sp.Nodes.Count < 2);
        return subpaths;
    }

    private static SubPath Ensure(SubPath? current, List<SubPath> subpaths, ref SubPath? slot, double x, double y)
    {
        if (current is not null)
        {
            return current;
        }

        // A path that starts with a line command has an implied move to the origin, which is what the first node
        // then draws from.
        var fresh = new SubPath { IsClosed = false };
        fresh.Nodes.Add(new PathNode(new Point2D(0, 0)));
        subpaths.Add(fresh);
        slot = fresh;
        _ = x;
        _ = y;
        return fresh;
    }

    /// <summary>The next point, or a sentinel when the numbers ran out.</summary>
    private static Point2D NextPoint(List<object> tokens, ref int index, double x, double y, bool relative)
    {
        if (!TryNumber(tokens, ref index, out double px) || !TryNumber(tokens, ref index, out double py))
        {
            return new Point2D(double.MinValue, double.MinValue);
        }

        return relative ? new Point2D(x + px, y + py) : new Point2D(px, py);
    }

    private static bool TryNumber(List<object> tokens, ref int index, out double value)
    {
        if (index < tokens.Count && tokens[index] is double number)
        {
            value = number;
            index++;
            return true;
        }

        value = 0.0;
        return false;
    }

    /// <summary>
    /// Appends an elliptical arc as cubic segments.
    ///
    /// The endpoints and the two radii do not determine an arc: there are two centres that fit, and the flags choose
    /// between them and decide which way round to travel. The standard endpoint-to-centre conversion handles all of
    /// it, including the case where the radii are too small to span the endpoints - where the specification says to
    /// scale them up rather than to fail, which is what drawing programmes rely on.
    /// </summary>
    private static void AppendArc(
        SubPath sub,
        Point2D from,
        Point2D to,
        double rx,
        double ry,
        double rotationDegrees,
        bool largeArc,
        bool sweep)
    {
        if (rx == 0 || ry == 0 || from.Equals(to))
        {
            sub.Nodes.Add(new PathNode(to));
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);

        double rotation = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rotation);
        double sin = Math.Sin(rotation);

        double dx = (from.X - to.X) / 2.0;
        double dy = (from.Y - to.Y) / 2.0;
        double x1 = (cos * dx) + (sin * dy);
        double y1 = (-sin * dx) + (cos * dy);

        // Radii too small to reach: grow them, which is what the specification says and what keeps a slightly
        // wrong radius from producing a line where the file drew an arc.
        double lambda = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));
        if (lambda > 1)
        {
            double grow = Math.Sqrt(lambda);
            rx *= grow;
            ry *= grow;
        }

        double sign = largeArc != sweep ? 1.0 : -1.0;
        double numerator = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
        double denominator = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
        double factor = denominator <= 0 ? 0.0 : sign * Math.Sqrt(Math.Max(0.0, numerator / denominator));

        double cx1 = factor * rx * y1 / ry;
        double cy1 = -factor * ry * x1 / rx;
        double cx = (cos * cx1) - (sin * cy1) + ((from.X + to.X) / 2.0);
        double cy = (sin * cx1) + (cos * cy1) + ((from.Y + to.Y) / 2.0);

        double startAngle = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        double sweepAngle = Angle(
            (x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);

        if (!sweep && sweepAngle > 0)
        {
            sweepAngle -= 2.0 * Math.PI;
        }
        else if (sweep && sweepAngle < 0)
        {
            sweepAngle += 2.0 * Math.PI;
        }

        // No more than a quarter turn per segment, which is the coarsest that stays within a fraction of a pixel at
        // any realistic size - a single cubic cannot represent a half turn accurately.
        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepAngle) / (Math.PI / 2.0)));
        double delta = sweepAngle / segments;
        double alpha = 4.0 / 3.0 * Math.Tan(delta / 4.0);

        double angle = startAngle;
        for (int i = 0; i < segments; i++)
        {
            double next = angle + delta;

            Point2D p0 = PointOnArc(cx, cy, rx, ry, cos, sin, angle);
            Point2D p3 = PointOnArc(cx, cy, rx, ry, cos, sin, next);
            Point2D t0 = TangentOnArc(rx, ry, cos, sin, angle);
            Point2D t3 = TangentOnArc(rx, ry, cos, sin, next);

            var c1 = new Point2D(p0.X + (alpha * t0.X), p0.Y + (alpha * t0.Y));
            var c2 = new Point2D(p3.X - (alpha * t3.X), p3.Y - (alpha * t3.Y));

            sub.Nodes[^1].OutHandle = c1;
            sub.Nodes.Add(new PathNode(p3, c2, p3));

            angle = next;
        }
    }

    private static Point2D PointOnArc(
        double cx, double cy, double rx, double ry, double cos, double sin, double angle)
    {
        double x = rx * Math.Cos(angle);
        double y = ry * Math.Sin(angle);
        return new Point2D((cos * x) - (sin * y) + cx, (sin * x) + (cos * y) + cy);
    }

    private static Point2D TangentOnArc(double rx, double ry, double cos, double sin, double angle)
    {
        double dx = -rx * Math.Sin(angle);
        double dy = ry * Math.Cos(angle);
        return new Point2D((cos * dx) - (sin * dy), (sin * dx) + (cos * dy));
    }

    private static double Angle(double ux, double uy, double vx, double vy)
    {
        double dot = (ux * vx) + (uy * vy);
        double length = Math.Sqrt((ux * ux) + (uy * uy)) * Math.Sqrt((vx * vx) + (vy * vy));
        if (length <= 0)
        {
            return 0.0;
        }

        double angle = Math.Acos(Math.Clamp(dot / length, -1.0, 1.0));
        return (ux * vy) - (uy * vx) < 0 ? -angle : angle;
    }

    /// <summary>
    /// Splits `d` into commands and numbers.
    ///
    /// A number ends where the next character cannot continue it: a second sign, or a second decimal point. That
    /// is what makes `10-5` two numbers, and it is why this does not split on separators.
    /// </summary>
    private static List<object> Tokenise(string d)
    {
        var tokens = new List<object>();
        int i = 0;

        while (i < d.Length)
        {
            char c = d[i];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            if (char.IsLetter(c))
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            int start = i;
            bool seenDot = false;
            bool seenExponent = false;

            while (i < d.Length)
            {
                char n = d[i];
                if (char.IsDigit(n))
                {
                    i++;
                    continue;
                }

                if (n == '.')
                {
                    if (seenDot || seenExponent)
                    {
                        break;
                    }

                    seenDot = true;
                    i++;
                    continue;
                }

                if (n is '-' or '+')
                {
                    // Only at the start of a number, unless it follows an exponent marker.
                    bool afterExponent = i > start && (d[i - 1] is 'e' or 'E');
                    if (i != start && !afterExponent)
                    {
                        break;
                    }

                    i++;
                    continue;
                }

                if (n is 'e' or 'E')
                {
                    if (seenExponent)
                    {
                        break;
                    }

                    seenExponent = true;
                    i++;
                    continue;
                }

                break;
            }

            if (i == start)
            {
                // A character that belongs to no token: skip it rather than loop forever on it.
                i++;
                continue;
            }

            if (double.TryParse(d[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                tokens.Add(value);
            }
        }

        return tokens;
    }
}
