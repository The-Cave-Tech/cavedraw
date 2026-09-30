using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// The bounds an item occupies, before its artboard offset is applied.
///
/// This is in the model rather than beside one of the panels because more than one caller needs it and
/// they must agree: arranging, the layer list and the canvas all decide where a thing is, and three
/// copies of a switch is three chances to disagree about a group's transform.
/// </summary>
public static class ItemBounds
{
    /// <summary>The item's axis-aligned bounds, in its own coordinate space.</summary>
    public static Rect2D Of(LayerItem item) => item switch
    {
        PathItem path => path.BoundingBox(),
        TextItem text => text.BoundingBox(),
        ArtGroup group => group.Transform.Transform(group.BoundingBox()),
        ImageItem image => image.Placement,
        _ => Rect2D.Empty,
    };
}
