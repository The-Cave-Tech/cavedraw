using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// What a stroke should be drawn as: an ordinary stroked path at a width, or a filled outline.
///
/// The distinction is the whole point of the builder. A stroke of constant width is **stroked** - the renderer
/// works out the caps and joins, and the outline of a stroked path is not what gets drawn. A stroke that varies
/// in width, or that a brush has mapped, has no stroke to draw, so it becomes the outline that would be filled
/// instead. Both answers come out of here so that no two renderers have to decide for themselves.
/// </summary>
public sealed record StrokeRenderPlan(
    bool IsOutline,
    double Width,
    IReadOnlyList<IReadOnlyList<Point2D>> Outlines)
{
    /// <summary>An ordinary stroked path at this width.</summary>
    public static StrokeRenderPlan Stroked(double width)
        => new(false, width, Array.Empty<IReadOnlyList<Point2D>>());

    /// <summary>A filled outline covering this region.</summary>
    public static StrokeRenderPlan Filled(IReadOnlyList<IReadOnlyList<Point2D>> outlines)
        => new(true, 0.0, outlines);
}

/// <summary>
/// The one place a stroke becomes geometry: path plus width profile plus everything that comes later, resolved to
/// either a width to stroke at or the outlines to fill.
///
/// **Why this exists as a single named step.** The canvas and the PDF exporter each grew their own answer, and
/// they agreed while a stroke was one width with a colour: there was nothing to disagree about. The moment a
/// stroke can vary along its length that stops being true, because two implementations of offsetting a path
/// differ at exactly the corners a person is looking at when they compare the canvas with an export. So the
/// decision is made once, here, in model coordinates, and both renderers consume it.
///
/// The builder is **pure**: the same path and stroke produce the same plan, and nothing is accumulated between
/// calls. That is not an incidental property. An effect applied during planning that also mutates the model
/// would be applied again on the next render, and again on export - the double-application bug, which shows up as
/// an effect that gets stronger every time the window is redrawn.
///
/// Coordinates are **path-local**: the artboard offset is the renderer's business, because only the renderer
/// knows what space it is drawing in.
/// </summary>
public static class StrokeOutlineBuilder
{
    /// <summary>
    /// Resolves a stroke to the geometry that draws it.
    ///
    /// <paramref name="scale"/> is the renderer's own scale factor - the group transform's scale, for the
    /// exporter. The profile's widths are in the same units as the stroke's own width, so both are scaled
    /// together; the canvas passes 1 because it paints inside the transform.
    /// </summary>
    public static StrokeRenderPlan Plan(PathItem path, StrokeSpec stroke, double scale = 1.0)
    {
        double width = Math.Max(0.0, stroke.Width * scale);

        if (!stroke.HasWidthProfile)
        {
            // A constant-width stroke stays a stroke. Turning it into an outline would change what it looks
            // like, because a stroked path's caps and joins are the renderer's, not a filled region's.
            return StrokeRenderPlan.Stroked(width);
        }

        return StrokeRenderPlan.Filled(Outline(path, stroke, scale));
    }

    /// <summary>
    /// The outlines covering a stroked path, in path-local coordinates.
    ///
    /// Also the answer for a plain stroke, for callers that need the region rather than a plan - the flattening
    /// and offsetting are the same work either way.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Outline(PathItem path, StrokeSpec stroke, double scale = 1.0)
    {
        WidthProfileSpec? profile = stroke.WidthProfile;
        if (profile is { IsEmpty: false } && scale != 1.0)
        {
            profile = new WidthProfileSpec(
                profile.Name,
                profile.Points.Select(point => new WidthPoint(
                    point.Position,
                    point.LeftWidth * scale,
                    point.RightWidth * scale,
                    point.Interpolation)));
        }

        return PathOffset.Outline(
            PathFlattener.FlattenForStroke(path),
            profile,
            stroke.Width * scale,
            stroke.MiterLimit);
    }
}
