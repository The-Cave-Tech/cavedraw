using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// One piece of artwork a brush maps along a path: the **item** that carries the artwork, the item's own bounds,
/// and the placement that carries those bounds onto the path (issue #100).
///
/// **Both tile-laying kinds answer here.** An art brush maps one asset along the whole path (#100) and a pattern
/// brush lays a tile set of up to five of them at chosen places (#101). They are different questions - which is
/// why they have different geometry seams, <see cref="ArtBrushPath"/> and <see cref="PatternBrushPath"/> - but
/// what a renderer does with the answer is the same: draw that item under that transform. So this resolves both,
/// and the canvas and the exporter consume the one answer rather than each learning a second kind.
///
/// <see cref="ArtBrushPath"/> answers *where* a piece goes from the path and the brush's size, but it cannot name
/// the artwork: only the document knows which item a brush's asset id refers to, and only the item knows its own
/// bounds. This is that missing half, resolved into the two things a renderer needs - what to draw and the
/// transform to draw it under.
/// </summary>
/// <param name="Asset">
/// The document item whose artwork is drawn. A vector asset is a <see cref="PathItem"/> or an
/// <see cref="ArtGroup"/> and a raster one an <see cref="ImageItem"/>; all three are items, and all three are
/// drawn in the asset's **own frame**, which is what <see cref="ItemBounds.Of"/> measures and what the placement
/// transforms from.
/// </param>
/// <param name="AssetBounds">The asset's bounds in its own frame, which is what the placement was computed against.</param>
/// <param name="Placement">Where the piece sits, how far it reaches, and the transform carrying the asset's frame onto the path.</param>
public readonly record struct PlacedArt(LayerItem Asset, Rect2D AssetBounds, ArtBrushPlacement Placement)
{
    /// <summary>
    /// The artwork a stroke's brush maps along the path, in the order it is drawn.
    ///
    /// **This is the honouring step's input, and it is deliberately not held.** The model has no member that says
    /// "art is drawn here": the placements are recomputed from the path on every render, which is exactly what
    /// makes an art brush a *stroke property* - edit the path and the art follows with no brush re-applied and
    /// nothing re-baked. A renderer resolves this where the stroke becomes drawing commands, so the canvas and
    /// the exporter consume one answer rather than each deriving its own.
    ///
    /// A brush that places no art answers with nothing, and so does a brush whose asset the document does not
    /// have: the art is missing, and drawing something in its place would be inventing artwork the model never
    /// described. <c>brush.missingAssets</c> is where that is reported by name.
    /// </summary>
    /// <param name="scale">
    /// The renderer's own scale factor, the same one <see cref="StrokeOutlineBuilder"/> is handed: the brush's
    /// size is in the stroke's units, so a path inside a scaled group draws the art at that scale. The canvas
    /// passes 1 because it paints inside the transform; the exporter passes its stroke scale.
    /// </param>
    public static IReadOnlyList<PlacedArt> Resolve(
        CadDocument document, PathItem path, BrushSpec? brush, double scale = 1.0)
    {
        if (brush is not { } spec)
        {
            return Array.Empty<PlacedArt>();
        }

        // A pattern brush lays a **set** of tiles rather than one asset, so its own seam decides where each goes
        // and this only names the item each slot refers to.
        if (spec.IsPattern)
        {
            return ResolveTiles(document, path, spec, scale);
        }

        if (!spec.IsArt || spec.ArtAsset is not { } assetId)
        {
            return Array.Empty<PlacedArt>();
        }

        if (document.FindItem(assetId) is not { } asset)
        {
            // An asset the document does not have. The brush is still an art brush and the stroke still draws
            // what it draws; there is simply no artwork to place, and substituting one would be inventing it.
            return Array.Empty<PlacedArt>();
        }

        Rect2D bounds = ItemBounds.Of(asset);
        IReadOnlyList<ArtBrushPlacement> placements = ArtBrushPath.Placements(path, spec, bounds, scale);
        if (placements.Count == 0)
        {
            return Array.Empty<PlacedArt>();
        }

        var resolved = new PlacedArt[placements.Count];
        for (int i = 0; i < placements.Count; i++)
        {
            resolved[i] = new PlacedArt(asset, bounds, placements[i]);
        }

        return resolved;
    }

    /// <summary>
    /// A pattern brush's tiles as renderable artwork, in the order they are drawn.
    ///
    /// Each tile is a placement of its **own** item, so the item is resolved per tile rather than once: the side,
    /// the ends and the two corners may be five different pieces of artwork, and a slot the document has no item
    /// for places no tile rather than a tile of invented size - the same refusal the art half makes.
    ///
    /// The **slot** a tile filled is deliberately not carried here. It is what <see cref="PatternBrushPath"/>
    /// answers with and what <c>brush.tiles</c> reports, and it is the difference between "a corner was
    /// recognised" and "the side tile was used at it"; a renderer draws the same item under the same transform
    /// either way, so carrying the slot would be a second place for the fallback to be re-decided.
    /// </summary>
    private static IReadOnlyList<PlacedArt> ResolveTiles(
        CadDocument document, PathItem path, BrushSpec brush, double scale)
    {
        // The tree walk is not cheap and a tile set can be long, so each item is looked up once however many
        // times its slot is placed.
        var items = new Dictionary<Guid, (LayerItem Item, Rect2D Bounds)>();

        Rect2D? Bounds(Guid id)
        {
            if (items.TryGetValue(id, out (LayerItem Item, Rect2D Bounds) known))
            {
                return known.Bounds;
            }

            if (document.FindItem(id) is not { } item)
            {
                return null;
            }

            Rect2D bounds = ItemBounds.Of(item);
            items[id] = (item, bounds);
            return bounds;
        }

        IReadOnlyList<PatternTilePlacement> tiles = PatternBrushPath.Placements(path, brush, Bounds, scale);
        if (tiles.Count == 0)
        {
            return Array.Empty<PlacedArt>();
        }

        var resolved = new List<PlacedArt>(tiles.Count);
        foreach (PatternTilePlacement tile in tiles)
        {
            if (!items.TryGetValue(tile.Asset, out (LayerItem Item, Rect2D Bounds) asset))
            {
                continue;
            }

            // The tile's placement and an art brush's piece are the same seven facts about where artwork lands,
            // so one of them is stated as the other rather than a second renderer being written for it.
            resolved.Add(new PlacedArt(
                asset.Item,
                asset.Bounds,
                new ArtBrushPlacement(
                    tile.Position, tile.Point, tile.TangentRadians, tile.Length, tile.Transform)));
        }

        return resolved;
    }
}
