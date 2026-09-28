using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// What an object is called before anybody names it.
///
/// A document imports as "Path", "Path", "Path", because the file says what to draw and not
/// what to call it. Every editor of this kind shows the shape instead — a line reads as a
/// line, a rectangle as a rectangle — and the way to know is the geometry itself: how many
/// corners, whether any segment bends, whether the corners meet at right angles.
///
/// These are defaults. A name the person typed is theirs and is never recomputed.
/// </summary>
public static class ObjectNaming
{
    /// <summary>
    /// A quarter circle's control handle, as a fraction of the radius. An ellipse drawn with
    /// four cubic segments uses it; anything else with four curves is not an ellipse.
    /// </summary>
    private const double Kappa = 0.5522847498307936;

    /// <summary>Tolerance for calling a handle "on the corner" or a corner square.</summary>
    private const double Epsilon = 0.01;

    /// <summary>What to show for an item nobody has renamed.</summary>
    public static string LabelFor(LayerItem item) => item switch
    {
        ImageItem => "Image",
        TextItem text => TextLabel(text),
        ArtGroup group => GroupLabel(group),
        PathItem path => PathLabel(path),
        _ => "Object",
    };

    /// <summary>
    /// The name to show: the person's, or what the thing is.
    ///
    /// The distinction matters because a name typed once must survive every later edit, and
    /// a name never typed must keep up with the geometry — a path that started as a line and
    /// was dragged into a curve should say curve.
    /// </summary>
    public static string DisplayName(LayerItem item)
        => item.NameIsUserSet && !string.IsNullOrWhiteSpace(item.Name)
            ? item.Name
            : LabelFor(item);

    private static string TextLabel(TextItem text)
    {
        string content = text.PlainText.Trim();

        if (content.Length == 0)
        {
            return "Text";
        }

        // Whitespace is worth preserving in the document but not in a label: a line that
        // begins with a newline would otherwise show as blank.
        content = string.Join(' ', content.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return content.Length <= 40 ? content : content[..39] + "…";
    }

    private static string GroupLabel(ArtGroup group)
        => group.Clips.Count > 0 ? "Clipping Mask" : "Group";

    private static string PathLabel(PathItem path)
    {
        if (path.SubPaths.Count == 0 || path.SubPaths.Any(sp => sp.IsEmpty))
        {
            return "Path";
        }

        // One subpath of two corners, joined by something straight, is a line. More than one
        // subpath is a shape made of parts, and each part being straight does not make the
        // whole a line.
        if (path.SubPaths.Count == 1)
        {
            SubPath only = path.SubPaths[0];

            if (IsRectangle(only))
            {
                return "Rectangle";
            }

            if (IsEllipse(only))
            {
                return "Ellipse";
            }

            if (!only.IsClosed && only.Nodes.Count == 2 && IsStraight(only, 0))
            {
                return "Line";
            }
        }

        return AnyCurve(path) ? "Curve" : "Path";
    }

    /// <summary>Whether any segment in the path bends, rather than being a straight run.</summary>
    private static bool AnyCurve(PathItem path)
    {
        foreach (SubPath sub in path.SubPaths)
        {
            int segments = sub.IsClosed ? sub.Nodes.Count : sub.Nodes.Count - 1;

            for (int i = 0; i < segments; i++)
            {
                if (!IsStraight(sub, i))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a segment is a straight line: both of its handles sit on the corners they
    /// belong to, which is how a corner-style node is built.
    /// </summary>
    private static bool IsStraight(SubPath sub, int index)
    {
        (int start, int end) = sub.SegmentEndNodes(index);
        PathNode from = sub.Nodes[start];
        PathNode to = sub.Nodes[end];

        return Near(from.OutHandle, from.Anchor) && Near(to.InHandle, to.Anchor);
    }

    /// <summary>
    /// Whether a closed four-corner subpath is an axis-aligned rectangle.
    ///
    /// Four straight corners at right angles, with the sides running along the axes. A
    /// rotated rectangle is still a rectangle to a person, so the sides only have to be
    /// perpendicular to each other, not to the page.
    /// </summary>
    private static bool IsRectangle(SubPath sub)
    {
        if (!sub.IsClosed || sub.Nodes.Count != 4)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (!IsStraight(sub, i))
            {
                return false;
            }
        }

        var corners = sub.Nodes.Select(n => n.Anchor).ToList();

        for (int i = 0; i < 4; i++)
        {
            Vector2D a = corners[(i + 1) % 4] - corners[i];
            Vector2D b = corners[(i + 2) % 4] - corners[(i + 1) % 4];

            if (a.Length < Epsilon || b.Length < Epsilon)
            {
                return false;
            }

            // Right angles: the dot product is zero, and opposite sides are the same length.
            double dot = (a.X * b.X) + (a.Y * b.Y);
            if (Math.Abs(dot) > Epsilon * a.Length * b.Length)
            {
                return false;
            }

            Vector2D opposite = corners[(i + 3) % 4] - corners[(i + 2) % 4];
            if (Math.Abs(opposite.Length - a.Length) > Epsilon)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a closed four-corner subpath is an ellipse: every segment a curve, and every
    /// handle the quarter-circle length for the radius it spans.
    /// </summary>
    private static bool IsEllipse(SubPath sub)
    {
        if (!sub.IsClosed || sub.Nodes.Count != 4)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (IsStraight(sub, i))
            {
                return false;
            }
        }

        Rect2D box = sub.BoundingBox();
        if (box.Width < Epsilon || box.Height < Epsilon)
        {
            return false;
        }

        double rx = box.Width / 2.0;
        double ry = box.Height / 2.0;

        for (int i = 0; i < 4; i++)
        {
            (int start, int end) = sub.SegmentEndNodes(i);
            PathNode from = sub.Nodes[start];
            PathNode to = sub.Nodes[end];

            // The handle leaving a corner is tangent: it may only move along one axis, and
            // only as far as kappa times the radius on that axis.
            Vector2D leaving = from.OutHandle - from.Anchor;
            Vector2D arriving = to.InHandle - to.Anchor;

            if (!IsTangentHandle(leaving, rx, ry) || !IsTangentHandle(arriving, rx, ry))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A handle along one axis, of about the quarter-circle length.</summary>
    private static bool IsTangentHandle(Vector2D handle, double rx, double ry)
    {
        if (Math.Abs(handle.X) < Epsilon)
        {
            return Math.Abs(Math.Abs(handle.Y) - (Kappa * ry)) < 0.05 * ry + Epsilon;
        }

        if (Math.Abs(handle.Y) < Epsilon)
        {
            return Math.Abs(Math.Abs(handle.X) - (Kappa * rx)) < 0.05 * rx + Epsilon;
        }

        return false;
    }

    private static bool Near(Point2D a, Point2D b)
        => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;
}
