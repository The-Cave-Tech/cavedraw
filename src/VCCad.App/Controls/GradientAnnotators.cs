using Avalonia;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Controls;

/// <summary>Which part of a gradient's on-canvas annotation a pointer grabbed.</summary>
public enum GradientHandle
{
    None,

    /// <summary>The linear ramp's start.</summary>
    LinearStart,

    /// <summary>The linear ramp's end. Dragging it turns and stretches the ramp about the start.</summary>
    LinearEnd,

    /// <summary>The radial centre.</summary>
    RadialCentre,

    /// <summary>Where the highlight sits, when the gradient has a focus away from its centre.</summary>
    RadialFocus,

    /// <summary>The radial ellipse's horizontal radius handle.</summary>
    RadialRadiusX,

    /// <summary>The radial ellipse's vertical radius handle.</summary>
    RadialRadiusY,
}

/// <summary>
/// A gradient's on-canvas annotators: where the handles are, which one a pointer is on, and what
/// dragging it does to the spec.
///
/// The model stores gradient geometry normalised to the object's bounding box because that is what
/// survives a resize; the canvas paints in world space, and so do these. Everything here is in
/// world coordinates - the control converts to screen - so the arithmetic can be tested without a
/// window, and the drawing, the hit test and the drag all agree about one set of points.
///
/// Freeform and conical gradients have no annotators because they have no renderer yet: they fall
/// back to the fill's flat colour (see <see cref="GradientPaint.CreateShader"/>), and handles
/// around a picture that is not painted would be pointing at nothing.
/// </summary>
public static class GradientAnnotators
{
    /// <summary>How near a handle counts as grabbing it, in world units at the current zoom.</summary>
    public const double DefaultTolerance = 6.0;

    /// <summary>The annotator handles for a spec, in world space, in a stable order.</summary>
    public static IReadOnlyList<(GradientHandle Handle, Point2D Point)> Handles(GradientSpec spec, Rect box)
    {
        GradientGeometry geometry = GradientGeometry.For(spec, box);
        var handles = new List<(GradientHandle, Point2D)>();

        switch (spec.Kind)
        {
            case GradientKind.Linear:
                handles.Add((GradientHandle.LinearStart, new Point2D(geometry.Start.X, geometry.Start.Y)));
                handles.Add((GradientHandle.LinearEnd, new Point2D(geometry.End.X, geometry.End.Y)));
                break;

            case GradientKind.Radial:
                // The focus is offered before the centre, and only when it is a point of its own.
                // A gradient with no focus paints what one naming its centre paints, so a second
                // handle on the centre would be a handle for a state the model does not have - and
                // it would sit exactly under the centre's, where it could not be picked.
                if (geometry.Focus is { } focus)
                {
                    handles.Add((GradientHandle.RadialFocus, new Point2D(focus.X, focus.Y)));
                }

                handles.Add((GradientHandle.RadialCentre, new Point2D(geometry.Centre.X, geometry.Centre.Y)));
                handles.Add((GradientHandle.RadialRadiusX,
                    RadialHandle(geometry, geometry.RadiusX, 0.0)));

                // The vertical handle only exists once the ellipse is not a circle; on a circle it
                // would sit on top of the horizontal one and no pointer could pick it.
                if (Math.Abs(geometry.RadiusY - geometry.RadiusX) > 1e-9)
                {
                    handles.Add((GradientHandle.RadialRadiusY,
                        RadialHandle(geometry, geometry.RadiusY, 90.0)));
                }

                break;
        }

        return handles;
    }

    /// <summary>The nearest handle within <paramref name="tolerance"/>, or None.</summary>
    public static GradientHandle HitTest(
        GradientSpec spec, Rect box, Point2D world, double tolerance = DefaultTolerance)
    {
        GradientHandle best = GradientHandle.None;
        double nearest = tolerance;

        foreach ((GradientHandle handle, Point2D point) in Handles(spec, box))
        {
            double distance = (world - point).Length;
            if (distance <= nearest)
            {
                nearest = distance;
                best = handle;
            }
        }

        return best;
    }

    /// <summary>
    /// Moves a handle to <paramref name="world"/> and returns the gradient that results.
    ///
    /// <paramref name="shift"/> constrains: a linear end snaps to 45-degree steps about the start,
    /// and a radius handle keeps the ellipse's aspect. Both are what the gesture means in every
    /// drawing program, and both are applied in world space so "45 degrees" looks like 45 degrees
    /// on a box that is not square.
    /// </summary>
    public static GradientSpec Drag(GradientSpec spec, Rect box, GradientHandle handle, Point2D world, bool shift)
    {
        if (box.Width <= 1e-9 || box.Height <= 1e-9)
        {
            return spec;
        }

        Point2D Normal(Point2D point) => new(
            (point.X - box.X) / box.Width,
            (point.Y - box.Y) / box.Height);

        switch (handle)
        {
            case GradientHandle.LinearStart:
                return spec with { Start = Normal(world) };

            case GradientHandle.LinearEnd:
            {
                GradientGeometry geometry = GradientGeometry.For(spec, box);
                var start = new Point2D(geometry.Start.X, geometry.Start.Y);
                Point2D end = shift ? ConstrainToStep(start, world, Math.PI / 4.0) : world;
                return spec with { End = Normal(end) };
            }

            case GradientHandle.RadialCentre:
                // The focus keeps its object-relative position and is clamped if the ellipse has
                // moved out from under it, so the spec never holds a highlight off the radial.
                return WithClampedFocus(spec with { Center = Normal(world) }, box);

            case GradientHandle.RadialFocus:
                // The gesture is clamped through the geometry the canvas paints with, so the two
                // cannot disagree: dragging past the ellipse lands on its edge along the ray from
                // the centre, and dragging onto the centre removes the focus - which is what the
                // SVG reader would have kept from a file naming that point.
                return WithClampedFocus(spec with { FocalPoint = Normal(world) }, box);

            case GradientHandle.RadialRadiusX:
            case GradientHandle.RadialRadiusY:
            {
                GradientGeometry geometry = GradientGeometry.For(spec, box);
                var centre = new Point2D(geometry.Centre.X, geometry.Centre.Y);
                Vector2D offset = world - centre;

                // Undo the ellipse's rotation so the offset reads along the ellipse's own axes.
                double radians = -geometry.RotationDegrees * Math.PI / 180.0;
                double cos = Math.Cos(radians);
                double sin = Math.Sin(radians);
                double alongX = (offset.X * cos) - (offset.Y * sin);
                double alongY = (offset.X * sin) + (offset.Y * cos);

                bool horizontal = handle == GradientHandle.RadialRadiusX;
                double radiusX = horizontal ? Math.Abs(alongX) : spec.RadiusX * box.Width;
                double radiusY = horizontal ? spec.RadiusY * box.Height : Math.Abs(alongY);

                if (shift && spec.RadiusX > 1e-9 && spec.RadiusY > 1e-9)
                {
                    // Keep the aspect: the dragged axis sets the scale for both.
                    double scale = horizontal
                        ? radiusX / (spec.RadiusX * box.Width)
                        : radiusY / (spec.RadiusY * box.Height);
                    radiusX = spec.RadiusX * box.Width * scale;
                    radiusY = spec.RadiusY * box.Height * scale;
                }

                // A shrunken ellipse can leave the focus outside it; clamping is what keeps the
                // picture the model describes drawable rather than relying on a reader's mercy.
                return WithClampedFocus(spec with
                {
                    RadiusX = radiusX / box.Width,
                    RadiusY = radiusY / box.Height,
                }, box);
            }

            default:
                return spec;
        }
    }

    /// <summary>
    /// The spec with its focus put back inside the ellipse by the geometry the canvas paints with,
    /// or with none at all when it has landed on the centre. A gradient with no focus is returned
    /// untouched: there is nothing to clamp, and null is a state rather than a coordinate.
    /// </summary>
    private static GradientSpec WithClampedFocus(GradientSpec spec, Rect box)
    {
        if (spec.FocalPoint is null)
        {
            return spec;
        }

        return GradientGeometry.For(spec, box).Focus is { } focus
            ? spec with
            {
                FocalPoint = new Point2D(
                    (focus.X - box.X) / box.Width,
                    (focus.Y - box.Y) / box.Height),
            }
            : spec with { FocalPoint = null };
    }

    /// <summary>A point on one of the ellipse's axes, rotated with the ellipse.</summary>
    private static Point2D RadialHandle(GradientGeometry geometry, double radius, double axisDegrees)
    {
        double radians = (geometry.RotationDegrees + axisDegrees) * Math.PI / 180.0;
        return new Point2D(
            geometry.Centre.X + (Math.Cos(radians) * radius),
            geometry.Centre.Y + (Math.Sin(radians) * radius));
    }

    /// <summary>Snaps the direction from <paramref name="from"/> to the nearest step, keeping the length.</summary>
    private static Point2D ConstrainToStep(Point2D from, Point2D to, double step)
    {
        Vector2D offset = to - from;
        double length = offset.Length;
        if (length < 1e-9)
        {
            return to;
        }

        double snapped = Math.Round(Math.Atan2(offset.Y, offset.X) / step) * step;
        return new Point2D(from.X + (Math.Cos(snapped) * length), from.Y + (Math.Sin(snapped) * length));
    }
}
