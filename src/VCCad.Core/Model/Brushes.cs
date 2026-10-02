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

    /// <summary>
    /// An asset - vector artwork or an embedded image - mapped **along** the path instead of stroked as a line:
    /// repeated, stretched to fit, or scaled proportionally. See <see cref="ArtBrushPath"/>.
    /// </summary>
    Art,

    /// <summary>
    /// A **tile set** laid along the path: a side tile repeated between the turns, a corner tile at each turn,
    /// and one start and one end tile at the two ends. See <see cref="PatternBrushPath"/>.
    /// </summary>
    Pattern,
}

/// <summary>
/// The five slots a pattern brush's tile set can hold.
///
/// The names are Illustrator's, and the two corner slots are the pair that has to be told apart: a path can turn
/// either way, and the artwork that wraps the inside of a bend is not the artwork that wraps its outside. See
/// <see cref="PatternBrushPath"/> for which turn takes which.
/// </summary>
public enum PatternTileKind
{
    /// <summary>The tile repeated along the path between the turns and the ends.</summary>
    Side,

    /// <summary>The single tile at the beginning of an open path.</summary>
    Start,

    /// <summary>The single tile at the end of an open path.</summary>
    End,

    /// <summary>The tile at a turn the path makes **to its left** - the concave side of the bend.</summary>
    InnerCorner,

    /// <summary>The tile at a turn the path makes **to its right** - the convex side of the bend.</summary>
    OuterCorner,
}

/// <summary>
/// One tile of a pattern brush: the document item whose artwork is the tile, and the controls that are the tile's
/// own rather than the set's.
///
/// The item is a **document item** rather than a copy of its geometry, for the reason the art brush's asset is:
/// one definition of the tile, which every stroke that uses the brush follows, so editing the tile redraws every
/// brushed stroke with no brush re-applied. Which of the five slots it fills is not held here - the slot is the
/// member it is stored in on <see cref="BrushSpec"/>.
/// </summary>
/// <param name="Asset">The item whose artwork is drawn as this tile, or null for a slot with nothing in it.</param>
/// <param name="FlipAcross">Whether the artwork is mirrored across the path.</param>
/// <param name="FlipAlong">Whether the artwork is mirrored along the path.</param>
/// <param name="RotationDegrees">
/// An extra turn of the artwork about the tile's centre, in degrees, added to the turn the path's own direction
/// gives it. It turns the **artwork**, not the tile's foot: a rotated tile still covers the same length of path.
/// </param>
/// <param name="Scale">
/// The tile's own size as a multiple of the brush's size, so one tile of a set can be drawn larger than the rest
/// without the set being resized.
/// </param>
public sealed record PatternTileSpec(
    Guid? Asset,
    bool FlipAcross = false,
    bool FlipAlong = false,
    double RotationDegrees = 0.0,
    double Scale = 1.0);

/// <summary>
/// How the asset an art brush carries is laid along the path.
///
/// The three answers are Illustrator's own, and they differ in a way that is visible rather than cosmetic: the
/// first covers the path exactly once, the second draws the asset once at its own proportions and leaves the rest
/// of the path bare, and the third repeats it end to end until the path runs out.
/// </summary>
public enum ArtStretch
{
    /// <summary>The asset spans the whole path once, stretched along it - so its aspect is not kept.</summary>
    StretchToFit,

    /// <summary>The asset is scaled by the brush's size with its aspect kept, so it does not span the path.</summary>
    ScaleProportionally,

    /// <summary>The asset is repeated along the path, each repeat its own natural size.</summary>
    Repeat,
}

/// <summary>
/// How the stroke's colour is worked into the asset, which Illustrator calls the art brush's colourisation.
///
/// Held and reported, and **not applied**: this is a paint decision, and the model has no member that says the art
/// placed along a path is drawn in the stroke's colour. Recording the mode is what keeps a file that says "tint"
/// from coming back as a file that says nothing.
/// </summary>
public enum ArtColourisation
{
    /// <summary>The asset's own colours, untouched.</summary>
    None,

    /// <summary>The asset takes the stroke's colour at full strength.</summary>
    Tint,

    /// <summary>The asset's luminance modulates the tint against a shade colour.</summary>
    TintAndShade,
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
///
/// **The art brush is the second kind (#100), and it is not a width at all.** It maps an asset along the path
/// instead of sweeping a nib along it, so it does not answer <see cref="Halves"/> and does not travel through the
/// outline seam; its geometry is <see cref="ArtBrushPath"/>, and its own members are the asset it maps, how the
/// asset is stretched, whether it is mirrored in either direction and how it is coloured. Those members live on
/// this same record rather than in a second type for the reason the kind exists: a brush is one field on a
/// stroke, and which kind it is decides which of its members mean anything.
///
/// **The pattern brush is the third kind (#101) and is a tile set rather than one asset.** Its side, start, end
/// and two corner tiles are five optional <see cref="PatternTileSpec"/> members, each naming an item of the
/// document, plus a spacing and the threshold that decides what counts as a corner; its geometry is
/// <see cref="PatternBrushPath"/>. It is not a nib either, for the art brush's reason: reading its size as a
/// half-width would draw a line where the file has tiles.
/// </summary>
public sealed record BrushSpec(
    string Name,
    double AngleDegrees,
    double Roundness,
    double Diameter,
    BrushKind Kind = BrushKind.Calligraphic,
    DynamicsSpec? Dynamics = null,
    Guid? ArtAsset = null,
    ArtStretch Stretch = ArtStretch.Repeat,
    bool FlipAcross = false,
    bool FlipAlong = false,
    ArtColourisation Colourisation = ArtColourisation.None,
    ColorRgb? ShadeColour = null,

    // The pattern brush's own members (issue #101), the third kind. Each slot holds the tile that goes in it or
    // nothing, and the members are optional and written only for the pattern kind - so a nib or an art brush,
    // including every one written before this kind existed, serialises to exactly the bytes it did then.
    PatternTileSpec? PatternSideTile = null,
    PatternTileSpec? PatternStartTile = null,
    PatternTileSpec? PatternEndTile = null,
    PatternTileSpec? PatternInnerTile = null,
    PatternTileSpec? PatternOuterTile = null,

    // The gap left between consecutive side tiles, in the stroke's own units. Zero is tiles laid end to end,
    // which is what a pattern brush does unless it is told otherwise; it holds its default, so it is absent from
    // the wire at that default.
    double PatternSpacing = 0.0,

    // How much the direction of travel has to change at a node for that node to count as a corner, in degrees.
    // Thirty is the model's own default and is what a right angle and a gentle curve are told apart by.
    double PatternCornerThresholdDegrees = 30.0)
{
    /// <summary>An elliptical nib: the calligraphic brush, named for the shape rather than the kind.</summary>
    public static BrushSpec Calligraphic(
        string name, double angleDegrees, double roundness, double diameter, DynamicsSpec? dynamics = null)
        => new(name, angleDegrees, roundness, diameter, BrushKind.Calligraphic, dynamics);

    /// <summary>
    /// An art brush: the asset called <paramref name="asset"/> mapped along the path, <paramref name="size"/>
    /// across it.
    ///
    /// The asset is a **document item** rather than a copy, so the art is edited in one place and every stroke
    /// that uses the brush follows - the same relationship a stroke has with the brush it names. A raster asset is
    /// an <see cref="ImageItem"/> and a vector one a group or a path; all three are items and all three can be
    /// named here.
    /// </summary>
    public static BrushSpec Art(
        string name,
        Guid? asset,
        double size,
        ArtStretch stretch = ArtStretch.Repeat,
        bool flipAcross = false,
        bool flipAlong = false,
        ArtColourisation colourisation = ArtColourisation.None,
        ColorRgb? shadeColour = null)
        => new(
            name, 0.0, 1.0, size, BrushKind.Art, null,
            asset, stretch, flipAcross, flipAlong, colourisation, shadeColour);

    /// <summary>
    /// A **pattern brush**: the tile set that fills the named slots, each tile drawn <paramref name="size"/> wide
    /// across the path, with <paramref name="spacing"/> left between consecutive side tiles.
    ///
    /// Every slot is optional and the fallbacks are Illustrator's: a corner slot with no tile of its own is filled
    /// with the side tile, and a missing start or end tile simply leaves the side tiles to run to that end. A
    /// slot naming nothing the document has is not a tile either, and is reported by
    /// <see cref="CadDocument.MissingBrushAssets"/> rather than drawn as a gap nobody can see.
    /// </summary>
    public static BrushSpec Pattern(
        string name,
        double size,
        PatternTileSpec? side = null,
        PatternTileSpec? start = null,
        PatternTileSpec? end = null,
        PatternTileSpec? innerCorner = null,
        PatternTileSpec? outerCorner = null,
        double spacing = 0.0,
        double cornerThresholdDegrees = 30.0)
        => new(
            name, 0.0, 1.0, size, BrushKind.Pattern, null,
            null, ArtStretch.Repeat, false, false, ArtColourisation.None, null,
            side, start, end, innerCorner, outerCorner,
            Math.Max(0.0, spacing), Math.Clamp(cornerThresholdDegrees, 0.0, 180.0));

    /// <summary>
    /// Whether this brush is a **nib** a stroke is swept with, as against art mapped along the path.
    ///
    /// The distinction is load-bearing rather than descriptive: a nib answers "how far is the edge from the
    /// centreline here", and an art brush has no such answer to give - asking it for one would read its size as a
    /// width and draw a nib where the file has artwork. See <see cref="StrokeOutlineBuilder"/>.
    /// </summary>
    public bool IsNib => Kind is BrushKind.Calligraphic;

    /// <summary>Whether this brush maps an asset along the path rather than sweeping a nib along it.</summary>
    public bool IsArt => Kind is BrushKind.Art;

    /// <summary>
    /// Whether this brush lays a tile set along the path. Like the art brush it has no half-width to give, so it
    /// is not a nib - and unlike the art brush its geometry is a **set of tiles** at chosen places rather than one
    /// asset covering the path. See <see cref="PatternBrushPath"/>.
    /// </summary>
    public bool IsPattern => Kind is BrushKind.Pattern;

    /// <summary>The tile in one of the five slots, or null when that slot holds nothing.</summary>
    public PatternTileSpec? Tile(PatternTileKind slot) => slot switch
    {
        PatternTileKind.Side => PatternSideTile,
        PatternTileKind.Start => PatternStartTile,
        PatternTileKind.End => PatternEndTile,
        PatternTileKind.InnerCorner => PatternInnerTile,
        PatternTileKind.OuterCorner => PatternOuterTile,
        _ => null,
    };

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
    ///
    /// **A nib's answer, and only a nib's.** An art brush has no half-width to give - it maps an asset along the
    /// path - so a caller asks this only of a brush whose <see cref="IsNib"/> is true. Reading a diameter as a
    /// width here would draw a line where the file has artwork, which is the substitution the whole brush family
    /// exists to avoid.
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

    /// <summary>
    /// A copy whose size is scaled, which is what a renderer's group transform does to it.
    ///
    /// <see cref="Diameter"/> is the brush's size whichever kind it is - the nib's long axis, or the width an
    /// art asset is drawn across the path - so one scale answers for both.
    /// </summary>
    public BrushSpec Scaled(double scale) => this with { Diameter = Diameter * scale };

    /// <summary>Whether this brush modulates itself from the pen. False when it records no dynamics.</summary>
    public bool HasDynamics => Dynamics is { IsEmpty: false };
}
