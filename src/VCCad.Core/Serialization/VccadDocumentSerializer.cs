using System.Text.Json;
using System.Text.Json.Serialization;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Serialization;

// ---------------------------------------------------------------------------
// DTOs — the lossless wire format of the VCCad document model.
//
// These records mirror the object graph 1:1 and carry a version tag. Keeping a
// dedicated DTO layer (rather than decorating the model with serialisation
// attributes) gives us three things:
//   1. A stable, versioned on-disk contract that survives refactors of the live
//      model classes.
//   2. Full control over numeric formatting and null handling so two identical
//      documents always serialise to identical bytes (determinism the round-trip
//      tests depend on).
//   3. No attributes/polymorphism metadata leaking into the pure model.
// ---------------------------------------------------------------------------

internal sealed record ColorDto(double R, double G, double B, double A = 1.0);

/// <summary>
/// A fill as it travels in the sidecar.
///
/// <see cref="Gradient"/> is the last, optional member and is omitted from the JSON when
/// null, so a solid fill written before gradients existed is byte-identical to one
/// written now, and a sidecar without the member deserializes to exactly the solid fill
/// it meant.
/// </summary>
internal sealed record FillDto(
    bool Visible,
    ColorDto? Color,
    FillRule Rule,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GradientDto? Gradient = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HatchDto? Hatch = null);

/// <summary>
/// One family of parallel lines in a hatch, as it travels in the sidecar.
///
/// Every member is written, including the ones that are usually default, because a hatch is only worth
/// round-tripping exactly: an angle that comes back as zero is a differently-hatched drawing.
/// </summary>
internal sealed record HatchLineDto(
    double AngleDegrees,
    double OffsetX,
    double OffsetY,
    double Spacing,
    double Width,
    double[] Dash,
    double DashOffset,
    StrokeCap Cap);

/// <summary>A hatch paint as it travels in the sidecar.</summary>
internal sealed record HatchDto(HatchLineDto[] Lines);

/// <summary>One stop of a gradient ramp, as it travels in the sidecar.</summary>
internal sealed record GradientStopDto(
    double Position,
    ColorDto Color,
    double Opacity,
    double Midpoint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null);

/// <summary>One colour point of a freeform gradient, in artboard coordinates.</summary>
internal sealed record FreeformPointDto(Point2D Position, ColorDto Color, double Opacity);

/// <summary>One drawn line of a freeform gradient: an index pair into the point list.</summary>
internal sealed record FreeformLineDto(int From, int To);

/// <summary>
/// A gradient paint as it travels in the sidecar.
///
/// Every field of the model is carried, including geometry belonging to a kind other than
/// <see cref="Kind"/>: Illustrator lets a gradient's type be switched while its stops and
/// points are kept, so dropping the freeform points of a radial (or the radial radii of a
/// linear) would silently lose work the moment the type is switched back.
/// </summary>
internal sealed record GradientDto(
    GradientKind Kind,
    GradientSpread Spread,
    GradientStopDto[] Stops,
    Point2D Start,
    Point2D End,
    Point2D Center,
    double RadiusX,
    double RadiusY,
    double Rotation,
    double Angle,
    FreeformPointDto[] Points,
    FreeformMode FreeformMode,
    FreeformLineDto[] Lines,

    // The radial focal point, when the file names one. Absent means the file named none, which is not the same as
    // storing the centre - that would invent a coordinate the file never wrote, and it is the rule the rest of this
    // document follows for every member it adds.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Point2D? FocalPoint = null);

internal sealed record StrokeDto(bool Visible, ColorDto? Color, double Width, StrokeCap Cap, StrokeJoin Join, double MiterLimit, StrokeAlignment Alignment, double[]? Dash = null, double DashOffset = 0.0,

    // A width profile on the stroke, when it has one. Absent for an ordinary stroke, so nothing that has one
    // changes on the way out - the same rule the stroke stack and the gradients follow.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WidthProfileDto? WidthProfile = null,

    // The outline effects on the stroke, in the order they are applied. Absent when there are none.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OutlineEffectDto[]? Effects = null,

    // The raster effects on the stroke. Separate from the outline effects because they are a different kind of
    // thing: one reshapes the outline, the other changes the pixels, and a reader has to treat them differently.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RasterEffectDto[]? RasterEffects = null,

    // The tablet dynamics the stroke was drawn with, in target order. Absent when the model holds no spec at all,
    // which is a different state from a spec with every target switched off - that one is written and read back.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DynamicsTargetDto[]? Dynamics = null,

    // The brush the stroke is swept with, when it has one. Absent for an ordinary stroke - and the last member,
    // so a document written before brushes existed serialises to exactly the bytes it did then, which is the
    // rule the width profile, the effects and the stroke stack all follow.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    BrushDto? Brush = null,

    // The stroke's own opacity, when it states one. Nullable **and absent when unstated**, which is a different
    // document from one whose opacity is stated as 1: the first says nothing, the second records a decision. The
    // same distinction the dynamics member makes, and the reason an ordinary stroke still writes no member here.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Opacity = null,

    // How the stroke's colour combines with what is beneath it, when it states a mode. Written as the file's own
    // name, and absent when unstated - the same rule as the opacity above it.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Blend = null);

/// <summary>
/// One dynamics target on the wire: whether it is on, and the two control points of its curve.
///
/// An array in target order rather than a member per target, because the target list is an enum and a member per
/// target would have to be kept in step with it by hand. The order is the enum's, which is why it is not written
/// down anywhere.
/// </summary>
internal sealed record DynamicsTargetDto(bool Enabled, double X1, double Y1, double X2, double Y2);

/// <summary>
/// A brush on the wire: its kind, the name it is known by, and the parameters of that kind.
///
/// The kind is written on every brush rather than omitted for the first one, because the value of the default
/// is a decision this build made: a brush of a kind that arrives later must not be read as a calligraphic nib
/// by a reader that guessed. The calligraphic members travel together with it for the same reason every
/// gradient's geometry travels - a file says what it says, and dropping the members of a kind this build does
/// not draw yet would make the round trip lossy.
/// </summary>
internal sealed record BrushDto(
    string Name,
    BrushKind Kind,
    double AngleDegrees,
    double Roundness,
    double Diameter,

    // The tablet dynamics the brush is modulated by, in target order. Absent when the brush holds no spec at all,
    // for the reason the stroke's member gives.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DynamicsTargetDto[]? Dynamics = null,

    // The art brush's own members (#100): the item it maps, how the asset is laid along the path, the two
    // mirrors, and the colourisation. Optional **and written only for the art kind**, so a calligraphic brush -
    // including every one written before this member existed - serialises to exactly the bytes it did then. The
    // asset is an id rather than a copy: one definition of the art, which every stroke that uses the brush
    // follows.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ArtAsset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ArtStretch? Stretch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FlipAcross = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FlipAlong = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ArtColourisation? Colourisation = null,

    // The shade a tint-and-shade colourisation modulates against. Absent unless the brush states one.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColorDto? ShadeColour = null,

    // The pattern brush's own members (#101): its tile set, the gap between side tiles and the threshold that
    // decides what counts as a corner. Optional **and written only for the pattern kind**, so a nib or an art
    // brush - including every one written before this kind existed - serialises to exactly the bytes it did then.
    // A slot the brush does not fill is absent, so a brush with one tile writes one member.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PatternTileDto? PatternSideTile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PatternTileDto? PatternStartTile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PatternTileDto? PatternEndTile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PatternTileDto? PatternInnerTile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PatternTileDto? PatternOuterTile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PatternSpacing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PatternCornerThreshold = null,

    // The scatter brush's own member (#102), the fourth kind: its artwork and the five ranged controls a copy is
    // drawn from, in one optional spec. Written **only for the scatter kind**, so every brush written before this
    // kind existed - nib, art and pattern alike - serialises to exactly the bytes it did then.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScatterBrushDto? Scatter = null,

    // The bristle brush's own member (#103), the fifth kind: the whole bundle in one optional spec. Written **only
    // for the bristle kind**, so every brush written before this kind existed - nib, art, pattern and scatter alike
    // - serialises to exactly the bytes it did then. Every control inside is absent when it holds the model's own
    // default, so a bundle that states one number writes one number.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BristleBrushDto? Bristle = null);

/// <summary>
/// A bristle brush on the wire: how many bristles the bundle holds, how long and thick each is, how stiffly it
/// follows the path, how far it spreads, how much a bristle may stray, how far pressure and tilt move it, and how
/// far a bristle's colour may stray.
///
/// One member rather than nine on the brush for the reason the scatter spec is one: the bundle is one setting, and
/// a brush whose count arrived without its thickness would paint a bundle the file does not describe. Every control
/// is absent when it holds the model's own default, so a document written before a control existed keeps the bytes
/// it had and reads back as the model's default rather than as a value this reader chose.
/// </summary>
internal sealed record BristleBrushDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Length = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Stiffness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Thickness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Spread = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Randomness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PressureSpread = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TiltTurn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ColourJitter = null);

/// <summary>
/// A scatter brush on the wire: the item it repeats and the five controls each copy is drawn from.
///
/// The whole spec is one member rather than ten, for the reason the model states: the five controls are five of the
/// same thing - a value and a range - and a reader that took one without the other would draw a scatter the file
/// does not describe. The artwork is an id rather than a copy, so one definition of it is followed by every stroke
/// that uses the brush.
/// </summary>
internal sealed record ScatterBrushDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Asset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScatterParameterDto? Spacing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScatterParameterDto? Rotation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScatterParameterDto? Scale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScatterParameterDto? Offset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScatterParameterDto? Opacity = null);

/// <summary>
/// One of a scatter brush's controls on the wire: the value it is set to, and how far a copy may stray from it.
///
/// The value is always written, because it is the setting; the range is absent when it is zero, so a scatter that
/// was pinned down is written as the value alone rather than as a range of nothing - the rule a file written before
/// this kind existed depends on. A negative range is written as the model holds it and read back through
/// <see cref="ScatterParameter.Min"/>, which takes the magnitude, so a file cannot state a range that is narrower
/// than it says.
/// </summary>
internal sealed record ScatterParameterDto(
    double Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Randomness = null);

/// <summary>
/// One tile of a pattern brush on the wire: the item whose artwork it is, and the controls that are the tile's own.
///
/// The asset is an id rather than a copy for the reason the art brush's is: one definition of the artwork, which
/// every stroke that uses the brush follows. Every member is optional and absent when it holds its default, so a
/// tile that is the item and nothing else is written as the item - the rule a document written before this kind
/// existed depends on.
/// </summary>
internal sealed record PatternTileDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Asset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FlipAcross = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FlipAlong = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Rotation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Scale = null);

/// <summary>One outline effect on the wire: its kind, its parameters, and the seed its randomness comes from.</summary>
internal sealed record OutlineEffectDto(
    OutlineEffectKind Kind, double Size, double Detail, int Seed,

    // The parameters the outline effects gained. Optional, and absent when they hold their default, so a document
    // that names none does not grow members it never had - the rule the stroke stack, the gradients and the raster
    // effects already follow. Writing the defaults on every save would be inventing data.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Ridges = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Smooth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OutlineJoin? Join = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Density = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Overlap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Width = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Curviness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Scatter = null);

/// <summary>One raster effect on the wire, with its tint absent when it takes the stroke's own colour.</summary>
internal sealed record RasterEffectDto(
    RasterEffectKind Kind,
    double Radius,
    double OffsetX,
    double OffsetY,
    double Opacity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColorDto? Tint = null);

/// <summary>
/// A width profile on the wire: its name and its width points, in order.
///
/// The name travels with it because a profile is an asset a person picks by name, and one that came back
/// nameless would be a profile nobody could find again.
/// </summary>
internal sealed record WidthProfileDto(string Name, WidthPointDto[] Points);

internal sealed record WidthPointDto(
    double Position, double Left, double Right, WidthInterpolation Interpolation = WidthInterpolation.Linear);

/// <summary>
/// A filter on the wire: its name, its primitives in evaluation order, and the region it is evaluated over.
///
/// The primitives carry their wiring - `in`, `in2`, `result` - because that wiring **is** the filter: a graph
/// written back as a list would draw differently, which is exactly the loss this format exists to avoid.
/// </summary>
internal sealed record FilterDto(
    string Name,
    FilterPrimitiveDto[] Primitives,
    double X,
    double Y,
    double Width,
    double Height,
    bool ObjectBoundingBox,
    string Output,

    // The region's other two declarations - what a primitive's own lengths mean, and the resolution the filter is
    // evaluated at. Optional and absent at their defaults, so a sidecar written before they were modelled is
    // byte-identical and one written now does not grow members for filters that do not use them.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PrimitiveUnitsObjectBoundingBox = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FilterResolutionX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FilterResolutionY = null);

internal sealed record FilterPrimitiveDto(
    FilterPrimitiveKind Kind,
    string? Input,
    string? Input2,
    string Result,
    double Radius,
    double Dx,
    double Dy,
    ColorDto? FloodColor,
    double FloodOpacity,
    string Operator,
    string Mode,

    // The parameters the later primitives gained. Optional and absent when they hold their default, so a sidecar
    // written before these primitives existed is byte-identical, and one written now does not grow members for
    // filters that do not use them. The same rule the outline effects, the raster effects and the gradients follow.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Scale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? XChannel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? YChannel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? BaseFrequency = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Octaves = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Seed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double[]? Matrix = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? SurfaceScale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? SpecularConstant = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? SpecularExponent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DiffuseConstant = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColorDto? LightingColor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Azimuth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Elevation = null);

internal sealed record NodeDto(Point2D Anchor, Point2D InHandle, Point2D OutHandle);

internal sealed record SubPathDto(bool Closed, NodeDto[] Nodes);

/// <summary>
/// An embedded font programme and the metrics needed to re-emit it.
///
/// All of it travels: this is the file's own font, and the fidelity rule is that what we
/// did not have to change, we do not change. Dropping it here meant a save-and-reload
/// silently lost the embedded programme and the original glyph codes, and the document
/// could then only be re-exported by substituting a different face.
/// </summary>
internal sealed record EmbeddedFontDto(
    EmbeddedFontFormat Format,
    string Program,
    bool Composite,
    string BaseFont,
    string FamilyName,
    int FirstChar,
    double[] Widths,
    double MissingWidth,
    string? ToUnicode,
    string? EncodingName,
    string? BaseEncoding,
    (int Code, string Name)[] Differences,
    string DescendantSubtype,
    string DescendantBaseFont,
    string? Type0Encoding,
    string? Type0EncodingStream,
    string CidSystemInfo,
    double DefaultWidth,
    string WidthsSpec,
    string? CidToGidMapName,
    string? CidToGidMapStream,
    int Flags,
    double[] FontBBox,
    double ItalicAngle,
    double Ascent,
    double Descent,
    double CapHeight,
    double StemV);

internal sealed record TextRunDto(
    string Text,
    string FontFamily,
    double FontSize,
    bool Bold,
    bool Italic,
    double? AdvanceWidth = null,
    string? SourceFont = null,
    string? RawCodes = null,
    ushort[]? GlyphIds = null,
    EmbeddedFontDto? Embedded = null,
    double GapAfter = 0,
    double PlacedAscentEm = 0,

    // The tracking a run asks for, as lengths. Optional, and absent when they hold their default, so a document
    // whose text has no tracking does not grow members it never had - the rule the stroke stack, the gradients and
    // the outline effects follow. Written as the number zero rather than omitted when a file states one, because a
    // run whose tracking is exactly nothing and a run whose tracking was never recorded draw the same and the
    // absent-means-default rule keeps the file small.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double LetterSpacing = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double WordSpacing = 0,

    // The colour of a run that has one of its own. Absent for a run drawn in the block's colour, which is the
    // usual case and the reason the member is nullable rather than a second copy of the block's colour.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColorDto? Color = null,

    // The width axis and the variant the file asked the face for, in its own words. Absent when nothing said one.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FontStretch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FontVariant = null,

    // Whether the run's glyphs are turned on their side in vertical text, as of #127. Absent at its initial value,
    // so a document without vertical writing has no member here and its bytes do not change.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] GlyphOrientation FontOrientation =
        GlyphOrientation.Auto);

/// <summary>One parameter of a live path effect, keyed and spelled as the file spelled it.</summary>
internal sealed record PathEffectParameterDto(string Name, string Value);

/// <summary>
/// A live path effect as it travels in the sidecar: the description the path's converted stroke was derived from.
///
/// It travels because the converted result is *derived* state, and a save/reopen that kept only the conversion
/// would hand back a document whose effect could never be re-derived - the same flattening the SVG export avoids by
/// keeping the element. Absent for a path with no effect, so a document without live effects is byte-identical to
/// one written before this existed.
/// </summary>
internal sealed record PathEffectDto(
    string Effect,
    string Id,
    string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PathEffectParameterDto[]? Parameters = null);

internal sealed record PathDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    FillDto Fill,
    StrokeDto Stroke,
    double Opacity,
    SubPathDto[] SubPaths,
    double[]? SourceFillCmyk = null,
    double[]? SourceStrokeCmyk = null,

    // A shape stays a shape: the kind and parameters it was made from, so re-opening a document
    // gives back an editable star rather than ten anonymous points. Optional, so every file written
    // before shapes existed still loads unchanged.
    ShapeKind? ShapeKind = null,
    ShapeParameters? ShapeParameters = null,

    // The stroke stack, when a path has more than one. Omitting it for the ordinary single-stroke path is what
    // keeps a document written before strokes became a stack byte-identical to one written now, and a file
    // without the member loads as a stack of one - the same rule gradients and clips follow.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StrokeDto[]? Strokes = null,

    // The live path effect this path carries, when it has one, so the converted width profile the stroke holds can
    // be re-derived after the geometry changes. Absent for every ordinary path.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PathEffectDto? PathEffect = null) : ItemDto;

internal sealed record GroupDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    AffineTransform Transform,
    double Opacity,
    ItemDto[] Children,

    // The definition an instance of this group was made from, when it is one. Absent for an ordinary group.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceId = null) : ItemDto;

internal sealed record TextDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    Point2D Origin,
    ColorDto Color,
    double RotationRadians,
    TextAlignment Alignment,
    double FrameWidth,
    double LineSpacing,
    double ParagraphSpacing,
    TextRunDto[] Runs,
    double[]? SourceCmyk = null,

    // Which way the block runs, and the base direction its characters are ordered against, as of #127. Both are
    // SVG's initial values when absent, so a document without vertical writing or right-to-left text grows neither
    // member and its bytes are what they were.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] TextWritingMode WritingMode =
        TextWritingMode.HorizontalTb,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] TextDirection Direction =
        TextDirection.LeftToRight) : ItemDto;

/// <summary>
/// An embedded raster image. Samples travel as base64 because they are bytes, not text;
/// they are stored decoded, so a reload reproduces the image bit for bit without having
/// to know how it was compressed.
/// </summary>
internal sealed record ImageDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    int PixelWidth,
    int PixelHeight,
    int BitsPerComponent,
    ImageColorSpace ColorSpace,
    string Samples,
    string Palette,
    ImageColorSpace PaletteBase,
    string Mask,
    Rect2D Placement,
    double[]? Decode = null,
    double[]? ColourKey = null,
    string? Filter = null,
    string? MaskFilter = null) : ItemDto;

/// <summary>A clip path as it travels in the sidecar: an outline and its rule.</summary>
internal sealed record ClipDto(FillRule Rule, SubPathDto[] SubPaths);

/// <summary>
/// Discriminated union over the possible layer items. System.Text.Json picks the
/// concrete type from the <c>$kind</c> property written by the converter below.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(PathDto), "path")]
[JsonDerivedType(typeof(GroupDto), "group")]
[JsonDerivedType(typeof(TextDto), "text")]
[JsonDerivedType(typeof(ImageDto), "image")]
internal abstract record ItemDto
{
    /// <summary>
    /// The clip paths that were in force when the item was painted, outermost first.
    ///
    /// On the base rather than on each kind, because every kind of item can be clipped —
    /// a clipped photograph and a clipped paragraph are the same problem. Dropping it
    /// would not fail anything; it would silently lose the clip on the next save.
    /// </summary>
    public ClipDto[]? Clips { get; init; }

    /// <summary>
    /// The document filter this item is drawn through, by name, or null when it is not filtered.
    ///
    /// On the base for the same reason the clips are: every kind of item can be filtered, and a reference that was
    /// dropped on the way out would leave the shape unfiltered with nothing to say so.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? FilterId { get; init; }

    /// <summary>The namespaced attributes the file carried, by qualified name, or null when there were none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Foreign { get; init; }

    /// <summary>Child elements that are not artwork, verbatim, or null when there were none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ForeignElements { get; init; }

    /// <summary>How the item blends with what is under it, or null when it does not (the default).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BlendMode? Blend { get; init; }

    public static ItemDto From(LayerItem item) => WithForeign(item switch
    {
        PathItem path => ToPath(path) with { Clips = ToClips(item), FilterId = item.FilterId },
        ArtGroup group => ToGroup(group) with { Clips = ToClips(item), FilterId = item.FilterId },
        TextItem text => ToText(text) with { Clips = ToClips(item), FilterId = item.FilterId },
        ImageItem image => ToImage(image) with { Clips = ToClips(item), FilterId = item.FilterId },
        _ => throw new NotSupportedException($"Unsupported layer item type {item.GetType().Name}."),
    }, item);

    /// <summary>
    /// The namespaced baggage an item carried - `inkscape:`/`sodipodi:` attributes and any child element the model
    /// has no meaning for - or nothing at all when it had none.
    ///
    /// Absent rather than empty, like every other optional member: a document that never held one has to serialise
    /// exactly as it did before this existed, or the fidelity tests would be pinning a new shape.
    /// </summary>
    private static ItemDto WithForeign(ItemDto dto, LayerItem item) => dto with
    {
        Foreign = item.ForeignAttributes.Count == 0
            ? null
            : new Dictionary<string, string>(item.ForeignAttributes, StringComparer.Ordinal),
        ForeignElements = item.ForeignElements.Count == 0 ? null : item.ForeignElements.ToArray(),
        Blend = item.BlendMode == BlendMode.Normal ? null : item.BlendMode,
    };

    private static ClipDto[]? ToClips(LayerItem item)
        => item.Clips.Count == 0
            ? null
            : item.Clips.Select(c => new ClipDto(
                c.Rule,
                c.SubPaths.Select(sp => new SubPathDto(
                    sp.IsClosed,
                    sp.Nodes.Select(n => new NodeDto(n.Anchor, n.InHandle, n.OutHandle)).ToArray()))
                    .ToArray())).ToArray();

    /// <summary>Puts the clips back onto an item that has just been read.</summary>
    internal static LayerItem WithClips(LayerItem item, ItemDto dto)
    {
        foreach (ClipDto clip in dto.Clips ?? Array.Empty<ClipDto>())
        {
            var spec = new ClipSpec { Rule = clip.Rule };
            foreach (SubPathDto sub in VccadDocumentSerializer.RequireArray(clip.SubPaths, nameof(clip.SubPaths)))
            {
                var restored = new SubPath { IsClosed = sub.Closed };
                foreach (NodeDto node in VccadDocumentSerializer.RequireArray(sub.Nodes, nameof(sub.Nodes)))
                {
                    restored.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
                }

                spec.SubPaths.Add(restored);
            }

            item.Clips.Add(spec);
        }

        // The filter reference travels the same way as a clip: on the base DTO, restored here for every kind of
        // item, because a filter applies to a group as readily as to a path.
        item.FilterId = dto.FilterId;
        item.BlendMode = dto.Blend ?? BlendMode.Normal;

        foreach (KeyValuePair<string, string> attribute in dto.Foreign ?? new Dictionary<string, string>())
        {
            item.ForeignAttributes[attribute.Key] = attribute.Value;
        }

        item.ForeignElements.AddRange(dto.ForeignElements ?? Array.Empty<string>());

        return item;
    }

    private static TextDto ToText(TextItem t) => new(
        t.Id,
        t.Name,
        t.IsVisible,
        t.IsLocked,
        t.Origin,
        new ColorDto(t.Color.R, t.Color.G, t.Color.B, t.Color.A),
        t.RotationRadians,
        t.Alignment,
        t.FrameWidth,
        t.LineSpacing,
        t.ParagraphSpacing,
        t.Runs.Select(ToRun).ToArray(),
        t.SourceCmyk,
        t.WritingMode,
        t.Direction);

    private static TextRunDto ToRun(TextRun r) => new(
        r.Text,
        r.FontFamily,
        r.FontSize,
        r.Bold,
        r.Italic,
        r.AdvanceWidth,
        r.SourceFont,
        r.RawCodes,
        r.GlyphIds,
        r.EmbeddedFont is { } font ? ToEmbedded(font) : null,
        r.GapAfter,
        r.PlacedAscentEm,
        r.LetterSpacing,
        r.WordSpacing,
        r.Color is { } color ? new ColorDto(color.R, color.G, color.B, color.A) : null,
        r.FontStretch,
        r.FontVariant,
        r.FontOrientation);

    private static EmbeddedFontDto ToEmbedded(EmbeddedFont f) => new(
        f.Format,
        Convert.ToBase64String(f.Program),
        f.Composite,
        f.BaseFont,
        f.FamilyName,
        f.FirstChar,
        f.Widths,
        f.MissingWidth,
        f.ToUnicode is null ? null : Convert.ToBase64String(f.ToUnicode),
        f.EncodingName,
        f.BaseEncoding,
        f.Differences.ToArray(),
        f.DescendantSubtype,
        f.DescendantBaseFont,
        f.Type0Encoding,
        f.Type0EncodingStream is null ? null : Convert.ToBase64String(f.Type0EncodingStream),
        f.CidSystemInfo,
        f.DefaultWidth,
        f.WidthsSpec,
        f.CidToGidMapName,
        f.CidToGidMapStream is null ? null : Convert.ToBase64String(f.CidToGidMapStream),
        f.Flags,
        f.FontBBox,
        f.ItalicAngle,
        f.Ascent,
        f.Descent,
        f.CapHeight,
        f.StemV);

    private static ImageDto ToImage(ImageItem i) => new(
        i.Id,
        i.Name,
        i.IsVisible,
        i.IsLocked,
        i.PixelWidth,
        i.PixelHeight,
        i.BitsPerComponent,
        i.ColorSpace,
        Convert.ToBase64String(i.Samples),
        Convert.ToBase64String(i.Palette),
        i.PaletteBase,
        Convert.ToBase64String(i.Mask),
        i.Placement,
        i.Decode,
        i.ColourKey,
        i.Filter,
        i.MaskFilter);

    private static PathDto ToPath(PathItem p) => new(
        p.Id,
        p.Name,
        p.IsVisible,
        p.IsLocked,
        ToFill(p.Fill),
        ToStroke(p.Stroke),
        p.Opacity,
        p.SubPaths.Select(sp => new SubPathDto(
            sp.IsClosed,
            sp.Nodes.Select(n => new NodeDto(n.Anchor, n.InHandle, n.OutHandle)).ToArray())).ToArray(),
        p.SourceFillCmyk,
        p.SourceStrokeCmyk,
        p.Shape?.Kind,
        p.Shape?.Parameters,

        // Only when there is more than one: a single stroke travels as `Stroke` alone, so nothing that has one
        // changes on the way out.
        p.Strokes.Count > 1 ? p.Strokes.Select(ToStroke).ToArray() : null,

        ToPathEffect(p.PathEffect));

    /// <summary>
    /// A path's live effect as the sidecar carries it, or null when it has none.
    ///
    /// The parameters keep the file's own order, because that order is what the model's equality and hash depend
    /// on and a re-ordered set would make two reads of one effect compare unequal for no reason.
    /// </summary>
    private static PathEffectDto? ToPathEffect(PathEffectSpec? effect)
        => effect is null
            ? null
            : new PathEffectDto(
                effect.Effect,
                effect.Id,
                effect.Version,
                effect.Parameters.Count == 0
                    ? null
                    : effect.Parameters
                        .Select(parameter => new PathEffectParameterDto(parameter.Key, parameter.Value))
                        .ToArray());

    private static GroupDto ToGroup(ArtGroup g) => new(
        g.Id,
        g.Name,
        g.IsVisible,
        g.IsLocked,
        g.Transform,
        g.Opacity,
        g.Children.Select(From).ToArray(),
        g.SourceId);

    private static FillDto ToFill(FillSpec f)
        => new(
            f.IsVisible,
            f.IsVisible ? ToColor(f.Color) : null,
            f.Rule,
            ToGradient(f.Gradient),
            ToHatch(f.Hatch));

    private static GradientDto? ToGradient(GradientSpec? g)
        => g is null
            ? null
            : new GradientDto(
                g.Kind,
                g.Spread,
                g.Stops.Select(s => new GradientStopDto(
                    s.Position, ToColor(s.Color), s.Opacity, s.Midpoint, s.Name)).ToArray(),
                g.Start,
                g.End,
                g.Center,
                g.RadiusX,
                g.RadiusY,
                g.Rotation,
                g.Angle,
                g.Points.Select(p => new FreeformPointDto(p.Position, ToColor(p.Color), p.Opacity)).ToArray(),
                g.FreeformMode,
                g.Lines.Select(l => new FreeformLineDto(l.From, l.To)).ToArray(),
                g.FocalPoint);

    private static HatchDto? ToHatch(HatchSpec? hatch)
        => hatch is null
            ? null
            : new HatchDto(hatch.Lines
                .Select(l => new HatchLineDto(
                    l.AngleDegrees, l.OffsetX, l.OffsetY, l.Spacing, l.Width,
                    l.Dash.Segments.ToArray(), l.Dash.Offset, l.Cap))
                .ToArray());

    private static ColorDto ToColor(ColorRgb c) => new(c.R, c.G, c.B, c.A);

    private static StrokeDto ToStroke(StrokeSpec s)
        => new(
            s.IsVisible,

            // The colour is written when the stroke is visible, and also when it is invisible but carries one
            // worth keeping. An invisible stroke is a real member of a stack with real settings, and writing
            // null for it means it comes back black - the settings survive a save and the colour does not. A
            // plain "no stroke" is invisible *and* black, and stays null, which is what leaves the bytes of an
            // unstroked path exactly as they were.
            s.IsVisible || s.Color != ColorRgb.Black
                ? new ColorDto(s.Color.R, s.Color.G, s.Color.B, s.Color.A)
                : null,
            s.Width, s.Cap, s.Join, s.MiterLimit, s.Alignment,
            s.Dash.IsEmpty ? null : s.Dash.Segments.ToArray(), s.Dash.Offset,
            s.HasWidthProfile
                ? new WidthProfileDto(
                    s.WidthProfile!.Name,
                    s.WidthProfile.Points.Select(p => new WidthPointDto(
                        p.Position, p.LeftWidth, p.RightWidth, p.Interpolation)).ToArray())
                : null,
            s.HasEffects
                ? s.AllEffects.Select(e => new OutlineEffectDto(
                    e.Kind,
                    e.Size,
                    e.Detail,
                    e.Seed,
                    e.Ridges == 1 ? null : e.Ridges,
                    e.Smooth ? true : null,
                    e.Join == OutlineJoin.Miter ? null : e.Join,
                    e.Density == 1.0 ? null : e.Density,
                    e.Overlap == 0 ? null : e.Overlap,
                    e.Width == 0 ? null : e.Width,
                    e.Curviness == 0 ? null : e.Curviness,
                    e.Scatter == 0 ? null : e.Scatter)).ToArray()
                : null,
            s.HasRasterEffects
                ? s.AllRasterEffects.Select(e => new RasterEffectDto(
                    e.Kind,
                    e.Radius,
                    e.OffsetX,
                    e.OffsetY,
                    e.Opacity,
                    e.Tint is { } tint ? new ColorDto(tint.R, tint.G, tint.B, tint.A) : null)).ToArray()
                : null,
            s.Dynamics is not null ? ToDynamicsDto(s.Dynamics) : null,

            s.HasBrush ? ToBrushDto(s.Brush!) : null,

            // Read off the member rather than off `EffectiveOpacity`, so a stroke that states nothing writes
            // nothing while one that states 1.0 writes 1. The two are different documents, and a writer that
            // collapsed them would make the first impossible to tell from the second after a save.
            s.Opacity,

            // The name, not the enum's ordinal: the file's own vocabulary is what another reader understands, and
            // an unknown mode read back as Normal is the silent rewrite `BlendModes.Parse` exists to prevent.
            s.Blend?.ToSvgName());

    /// <summary>
    /// A brush on the wire, or null when the stroke has none.
    ///
    /// Every calligraphic member is written, including one that holds its default, because a nib's angle and
    /// roundness are the brush: a roundness that came back as the default would be a differently-shaped line,
    /// and a value that is only absent because it happens to equal the default is not something a reader can
    /// tell from one the file never stated.
    /// </summary>
    internal static BrushDto ToBrushDto(BrushSpec brush)
    {
        // The art members belong to the art kind and to nothing else. Writing them on a nib would be inventing
        // data - a nib has no asset to map - and would change the bytes of every brush written before this kind
        // existed, which the absence rule forbids.
        bool art = brush.IsArt;

        // The pattern members belong to the pattern kind for the same reason, and are written whenever it is that
        // kind - including the spacing and the threshold at their defaults, because a kind's own members travel
        // together: the art kind writes a stretch of "repeat" and two flips of false on every art brush it writes.
        bool pattern = brush.IsPattern;

        // The scatter members belong to the scatter kind for the same reason, and are written whenever it is that
        // kind: a kind's own members travel together, so a scatter brush says what it scatters even when every one of
        // its controls holds the model's default. A brush of another kind writes nothing here.
        bool scatter = brush.IsScatter;

        // The bristle members belong to the bristle kind for the same reason, and are written whenever it is that
        // kind: a kind's own members travel together, so a bristle brush says what its bundle is even when every
        // control holds the model's default. A brush of another kind writes nothing here.
        bool bristle = brush.IsBristle;

        return new BrushDto(
            brush.Name,
            brush.Kind,
            brush.AngleDegrees,
            brush.Roundness,
            brush.Diameter,
            brush.Dynamics is not null ? ToDynamicsDto(brush.Dynamics) : null,
            art ? brush.ArtAsset : null,
            art ? (ArtStretch?)brush.Stretch : null,
            art ? brush.FlipAcross : null,
            art ? brush.FlipAlong : null,
            art ? (ArtColourisation?)brush.Colourisation : null,
            art && brush.ShadeColour is { } shade
                ? new ColorDto(shade.R, shade.G, shade.B, shade.A)
                : null,
            pattern ? ToPatternTileDto(brush.PatternSideTile) : null,
            pattern ? ToPatternTileDto(brush.PatternStartTile) : null,
            pattern ? ToPatternTileDto(brush.PatternEndTile) : null,
            pattern ? ToPatternTileDto(brush.PatternInnerTile) : null,
            pattern ? ToPatternTileDto(brush.PatternOuterTile) : null,
            pattern ? brush.PatternSpacing : null,
            pattern ? brush.PatternCornerThresholdDegrees : null,
            scatter ? ToScatterDto(brush.ScatterSpec) : null,
            bristle ? ToBristleDto(brush.BristleSpec) : null);
    }

    /// <summary>
    /// A bristle brush's bundle on the wire, or null for a bristle brush that states none.
    ///
    /// A control the model holds is written, and a control that holds the model's own default is written as absent:
    /// a bundle that states its count and nothing else is a bundle of that many of the model's bristles, and a
    /// member written holding a default would be this build stating a control the file never mentioned.
    /// </summary>
    private static BristleBrushDto? ToBristleDto(BristleBrushSpec? spec)
    {
        if (spec is null)
        {
            return null;
        }

        BristleBrushSpec defaults = BristleBrushSpec.Default;
        return new BristleBrushDto(
            spec.Count != defaults.Count ? spec.Count : null,
            spec.Length != defaults.Length ? spec.Length : null,
            spec.Stiffness != defaults.Stiffness ? spec.Stiffness : null,
            spec.Thickness != defaults.Thickness ? spec.Thickness : null,
            spec.Spread != defaults.Spread ? spec.Spread : null,
            spec.Randomness != defaults.Randomness ? spec.Randomness : null,
            spec.PressureSpread != defaults.PressureSpread ? spec.PressureSpread : null,
            spec.TiltTurn != defaults.TiltTurn ? spec.TiltTurn : null,
            spec.ColourJitter != defaults.ColourJitter ? spec.ColourJitter : null);
    }

    /// <summary>
    /// A scatter brush's parameters on the wire, or null for a scatter brush that states none.
    ///
    /// A control the model holds is written, and the range inside it only when it says something: a copy drawn at
    /// the value with no range is the value, and a member written holding zero would be this build stating a
    /// randomness the file never mentioned.
    /// </summary>
    private static ScatterBrushDto? ToScatterDto(ScatterBrushSpec? spec)
        => spec is null
            ? null
            : new ScatterBrushDto(
                spec.Asset,
                ToScatterParameterDto(spec.Spacing),
                ToScatterParameterDto(spec.Rotation),
                ToScatterParameterDto(spec.Scale),
                ToScatterParameterDto(spec.Offset),
                ToScatterParameterDto(spec.Opacity));

    private static ScatterParameterDto ToScatterParameterDto(ScatterParameter parameter)
        => new(parameter.Value, Math.Abs(parameter.Randomness) > 1e-12 ? parameter.Randomness : null);

    /// <summary>
    /// One tile of a pattern brush's set on the wire, or null for a slot the brush does not fill.
    ///
    /// The two flips, the rotation and the scale are written only when they say something: a tile that is the item
    /// and nothing else is the item, and a member written holding its default would be this build stating a control
    /// the file never mentioned.
    /// </summary>
    private static PatternTileDto? ToPatternTileDto(PatternTileSpec? tile)
        => tile is null
            ? null
            : new PatternTileDto(
                tile.Asset,
                tile.FlipAcross ? true : null,
                tile.FlipAlong ? true : null,
                tile.RotationDegrees != 0.0 ? tile.RotationDegrees : null,
                Math.Abs(tile.Scale - 1.0) > 1e-12 ? tile.Scale : null);

    /// <summary>
    /// The dynamics on the wire, in target order: one entry per target the enum names.
    ///
    /// Written whenever the model **holds** a spec, whether or not a target is on, because the list's length
    /// **is** the target list and an all-off spec is a recorded decision rather than the absence of one. The
    /// model says so in as many words: a null spec stores nothing, an all-off spec stores that every response
    /// was switched off, and <see cref="StrokeDynamics"/> never fires for either. Collapsing the second to the
    /// first here made a stroke that had been switched off come back as one that had never been set up - the
    /// same "a value the model holds and the lossless store drops" defect the effect parameters had.
    ///
    /// The predicate is the member being non-null, not <see cref="StrokeSpec.HasDynamics"/>: that property
    /// answers "does this stroke respond to the pen", which is a rendering question, and a spec with every
    /// target off answers no while still being a value somebody chose.
    /// </summary>
    private static DynamicsTargetDto[]? ToDynamicsDto(DynamicsSpec? dynamics)
        => dynamics is not null
            ? Enum.GetValues<DynamicsTarget>()
                .Select(target =>
                {
                    DynamicsTargetSpec spec = dynamics.For(target);
                    return new DynamicsTargetDto(
                        spec.Enabled, spec.Curve.X1, spec.Curve.Y1, spec.Curve.X2, spec.Curve.Y2);
                })
                .ToArray()
            : null;
}

/// <summary>Explicit restoration from DTO back into a live model graph.</summary>
internal static class ItemDtoExtensions
{
    public static LayerItem ToModel(this ItemDto dto) => dto switch
    {
        PathDto p => ItemDto.WithClips(p.ToModel(), p),
        GroupDto g => ItemDto.WithClips(g.ToModel(), g),
        TextDto t => ItemDto.WithClips(t.ToModel(), t),
        ImageDto i => ItemDto.WithClips(i.ToModel(), i),
        _ => throw new NotSupportedException($"Unknown DTO kind {dto.GetType().Name}."),
    };

    private static TextRun ToModel(this TextRunDto dto)
    {
        var run = new TextRun
        {
            Text = dto.Text,
            FontFamily = dto.FontFamily,
            FontSize = dto.FontSize,
            Bold = dto.Bold,
            Italic = dto.Italic,
            AdvanceWidth = dto.AdvanceWidth,
            SourceFont = dto.SourceFont,
            RawCodes = dto.RawCodes,
            GlyphIds = dto.GlyphIds,
            EmbeddedFont = dto.Embedded is { } e ? ToModel(e) : null,
            GapAfter = dto.GapAfter,
            PlacedAscentEm = dto.PlacedAscentEm,
            LetterSpacing = dto.LetterSpacing,
            WordSpacing = dto.WordSpacing,
            Color = dto.Color is { } color ? new ColorRgb(color.R, color.G, color.B, color.A) : null,
            FontStretch = dto.FontStretch,
            FontVariant = dto.FontVariant,
            FontOrientation = dto.FontOrientation,
        };

        return run;
    }

    private static EmbeddedFont ToModel(this EmbeddedFontDto dto) => new()
    {
        Format = dto.Format,
        Program = Convert.FromBase64String(dto.Program),
        Composite = dto.Composite,
        BaseFont = dto.BaseFont,
        FamilyName = dto.FamilyName,
        FirstChar = dto.FirstChar,
        Widths = dto.Widths,
        MissingWidth = dto.MissingWidth,
        ToUnicode = dto.ToUnicode is null ? null : Convert.FromBase64String(dto.ToUnicode),
        EncodingName = dto.EncodingName,
        BaseEncoding = dto.BaseEncoding,
        Differences = dto.Differences,
        DescendantSubtype = dto.DescendantSubtype,
        DescendantBaseFont = dto.DescendantBaseFont,
        Type0Encoding = dto.Type0Encoding,
        Type0EncodingStream = dto.Type0EncodingStream is null
            ? null
            : Convert.FromBase64String(dto.Type0EncodingStream),
        CidSystemInfo = dto.CidSystemInfo,
        DefaultWidth = dto.DefaultWidth,
        WidthsSpec = dto.WidthsSpec,
        CidToGidMapName = dto.CidToGidMapName,
        CidToGidMapStream = dto.CidToGidMapStream is null
            ? null
            : Convert.FromBase64String(dto.CidToGidMapStream),
        Flags = dto.Flags,
        FontBBox = dto.FontBBox,
        ItalicAngle = dto.ItalicAngle,
        Ascent = dto.Ascent,
        Descent = dto.Descent,
        CapHeight = dto.CapHeight,
        StemV = dto.StemV,
    };

    private static ImageItem ToModel(this ImageDto i)
    {
        var item = new ImageItem
        {
            Name = i.Name,
            IsVisible = i.IsVisible,
            IsLocked = i.IsLocked,
            PixelWidth = i.PixelWidth,
            PixelHeight = i.PixelHeight,
            BitsPerComponent = i.BitsPerComponent,
            ColorSpace = i.ColorSpace,
            Samples = Convert.FromBase64String(i.Samples),
            Palette = Convert.FromBase64String(i.Palette),
            PaletteBase = i.PaletteBase,
            Mask = Convert.FromBase64String(i.Mask),
            Placement = i.Placement,
            Decode = i.Decode,
            ColourKey = i.ColourKey,
            Filter = i.Filter,
            MaskFilter = i.MaskFilter,
        };
        item.RestoreIdentity(i.Id);
        return item;
    }

    private static TextItem ToModel(this TextDto t)
    {
        var item = new TextItem
        {
            Name = t.Name,
            IsVisible = t.IsVisible,
            IsLocked = t.IsLocked,
            Origin = t.Origin,
            Color = new ColorRgb(t.Color.R, t.Color.G, t.Color.B, t.Color.A),
            RotationRadians = t.RotationRadians,
            Alignment = t.Alignment,
        };
        item.FrameWidth = t.FrameWidth;
        item.LineSpacing = t.LineSpacing;
        item.ParagraphSpacing = t.ParagraphSpacing;
        item.SourceCmyk = t.SourceCmyk;
        item.WritingMode = t.WritingMode;
        item.Direction = t.Direction;
        item.RestoreIdentity(t.Id);
        foreach (TextRunDto run in VccadDocumentSerializer.RequireArray(t.Runs, nameof(t.Runs)))
        {
            item.Runs.Add(run.ToModel());
        }

        return item;
    }

    private static PathItem ToModel(this PathDto p)
    {
        var path = new PathItem
        {
            Name = p.Name,
            IsVisible = p.IsVisible,
            IsLocked = p.IsLocked,
            Fill = p.Fill.ToModel(),
            Stroke = p.Stroke.ToModel(),
            Opacity = p.Opacity,
            SourceFillCmyk = p.SourceFillCmyk,
            SourceStrokeCmyk = p.SourceStrokeCmyk,

            // Read back after the geometry, so a definition that no longer matches its nodes is still
            // reported: the shape is what the file says it is, and regenerating is a deliberate act.
            Shape = p.ShapeKind is { } kind && p.ShapeParameters is { } parameters
                ? new ShapeDefinition(kind, parameters)
                : null,

            // The description the stroke's width profile was derived from. Without it a reopened document has the
            // converted widths and no way to re-derive them, which is the flattening the sidecar exists to avoid.
            PathEffect = p.PathEffect is { } effect
                ? new PathEffectSpec(
                    effect.Effect,
                    effect.Id,
                    effect.Version,
                    (effect.Parameters ?? Array.Empty<PathEffectParameterDto>())
                        .Select(parameter => new KeyValuePair<string, string>(parameter.Name, parameter.Value)))
                : null,
        };
        path.RestoreIdentity(p.Id);

        // The stack, when the file carried one. A file without the member holds a single stroke - already set
        // through the initialiser above - so this only ever adds.
        if (p.Strokes is { Length: > 0 } stack)
        {
            path.Strokes.Clear();
            foreach (StrokeDto stroke in stack)
            {
                path.Strokes.Add(stroke.ToModel());
            }

            path.NotifyStrokesChanged();
        }

        foreach (SubPathDto sp in VccadDocumentSerializer.RequireArray(p.SubPaths, nameof(p.SubPaths)))
        {
            var sub = path.AddSubPath(sp.Closed);
            foreach (NodeDto n in sp.Nodes)
            {
                sub.Nodes.Add(new PathNode(n.Anchor, n.InHandle, n.OutHandle));
            }
        }

        return path;
    }

    private static ArtGroup ToModel(this GroupDto g)
    {
        var group = new ArtGroup
        {
            Name = g.Name,
            IsVisible = g.IsVisible,
            IsLocked = g.IsLocked,
            Transform = g.Transform,
            Opacity = g.Opacity,
            SourceId = g.SourceId,
        };
        group.RestoreIdentity(g.Id);
        foreach (ItemDto child in VccadDocumentSerializer.RequireArray(g.Children, nameof(g.Children)))
        {
            group.AddItem(child.ToModel());
        }

        return group;
    }

    /// <summary>Rebuilds a hatch line by line. A family with no spacing has no repetitions and is dropped.</summary>
    private static HatchSpec ToModel(this HatchDto h)
        => new(h.Lines
            .Where(l => l.Spacing > 0)
            .Select(l => new HatchLineSpec(
                l.AngleDegrees,
                l.OffsetX,
                l.OffsetY,
                l.Spacing,
                l.Width,
                new DashPattern(l.Dash, l.DashOffset),
                l.Cap))
            .ToArray());

    private static FillSpec ToModel(this FillDto f)
    {
        GradientSpec? gradient = f.Gradient?.ToModel();
        HatchSpec? hatch = f.Hatch?.ToModel();

        // A fill is only "none" when it is invisible and there is no gradient behind it.
        // A gradient on an invisible fill is kept: switching a fill off and on again must
        // not lose the paint that was configured behind it.
        if (gradient is not null)
        {
            ColorRgb gradientColor = f.Color is null
                ? ColorRgb.White
                : new ColorRgb(f.Color.R, f.Color.G, f.Color.B, f.Color.A);
            return new FillSpec(f.Visible, gradientColor, f.Rule, gradient, hatch);
        }

        // A hatch on an invisible fill is kept for the same reason a gradient is: turning the fill off and on
        // again must not lose the paint configured behind it.
        if (hatch is not null)
        {
            ColorRgb hatchColor = f.Color is null
                ? ColorRgb.White
                : new ColorRgb(f.Color.R, f.Color.G, f.Color.B, f.Color.A);
            return new FillSpec(f.Visible, hatchColor, f.Rule, null, hatch);
        }

        return f.Visible && f.Color is not null
            ? FillSpec.Solid(new ColorRgb(f.Color.R, f.Color.G, f.Color.B, f.Color.A), f.Rule)
            : FillSpec.None;
    }

    /// <summary>
    /// Rebuilds a gradient, refusing the members that cannot mean anything.
    ///
    /// Every finite value is carried through verbatim, including a stop outside 0..1 and
    /// an unsorted or duplicated stop list: <see cref="GradientSpec.Normalised"/> is what
    /// evaluation consults, and reordering or clamping on load would make the sidecar
    /// lossy. What is refused is a value that has no sensible interpretation at all — a
    /// non-finite number (which JSON could not write back, so a silent accept would only
    /// defer the failure) and a freeform line index that would read past the point list.
    /// A missing or empty stop list is not refused: the model's contract is that the ramp
    /// is never empty, so the record's own default two-stop ramp is the sensible load.
    /// </summary>
    private static GradientSpec ToModel(this GradientDto g)
    {
        ValidateGradient(g);

        FreeformPointDto[] points = g.Points ?? Array.Empty<FreeformPointDto>();
        FreeformLineDto[] lines = g.Lines ?? Array.Empty<FreeformLineDto>();
        GradientStopDto[] stops = g.Stops ?? Array.Empty<GradientStopDto>();

        var spec = new GradientSpec
        {
            Kind = g.Kind,
            Spread = g.Spread,
            Start = g.Start,
            End = g.End,
            Center = g.Center,
            RadiusX = g.RadiusX,
            RadiusY = g.RadiusY,
            Rotation = g.Rotation,
            Angle = g.Angle,
            FreeformMode = g.FreeformMode,
            Points = points
                .Select(p => new FreeformPoint(
                    p.Position,
                    new ColorRgb(p.Color.R, p.Color.G, p.Color.B, p.Color.A),
                    p.Opacity))
                .ToArray(),
            Lines = lines.Select(l => (l.From, l.To)).ToArray(),
            FocalPoint = g.FocalPoint,
        };

        return stops.Length == 0
            ? spec
            : spec with
            {
                Stops = stops
                    .Select(s => new GradientStop(
                        s.Position,
                        new ColorRgb(s.Color.R, s.Color.G, s.Color.B, s.Color.A),
                        s.Opacity,
                        s.Midpoint,
                        s.Name))
                    .ToArray(),
            };
    }

    private static void ValidateGradient(GradientDto g)
    {
        RequireDefined(g.Kind, "Kind");
        RequireDefined(g.Spread, "Spread");
        RequireDefined(g.FreeformMode, "FreeformMode");

        GradientStopDto[] stops = g.Stops ?? Array.Empty<GradientStopDto>();
        for (int i = 0; i < stops.Length; i++)
        {
            RequireFinite(stops[i].Position, $"Stops[{i}].Position");
            RequireFinite(stops[i].Opacity, $"Stops[{i}].Opacity");
            RequireFinite(stops[i].Midpoint, $"Stops[{i}].Midpoint");
            RequireFinite(stops[i].Color, $"Stops[{i}].Color");
        }

        RequireFinite(g.Start, "Start");
        RequireFinite(g.End, "End");
        RequireFinite(g.Center, "Center");
        RequireFinite(g.RadiusX, "RadiusX");
        RequireFinite(g.RadiusY, "RadiusY");
        RequireFinite(g.Rotation, "Rotation");
        RequireFinite(g.Angle, "Angle");

        // Optional, but when a file gives one it has to be a real number: a NaN survives arithmetic silently and
        // lands nowhere, which is the failure this guard exists for everywhere else.
        if (g.FocalPoint is { } focal)
        {
            RequireFinite(focal, "FocalPoint");
        }

        FreeformPointDto[] points = g.Points ?? Array.Empty<FreeformPointDto>();
        for (int i = 0; i < points.Length; i++)
        {
            RequireFinite(points[i].Position, $"Points[{i}].Position");
            RequireFinite(points[i].Opacity, $"Points[{i}].Opacity");
            RequireFinite(points[i].Color, $"Points[{i}].Color");
        }

        FreeformLineDto[] lines = g.Lines ?? Array.Empty<FreeformLineDto>();
        for (int i = 0; i < lines.Length; i++)
        {
            FreeformLineDto line = lines[i];
            if (line.From < 0 || line.From >= points.Length || line.To < 0 || line.To >= points.Length)
            {
                throw new JsonException(
                    $"Gradient Lines[{i}] references point {line.From}->{line.To}, but the gradient "
                    + $"has {points.Length} freeform point(s).");
            }
        }
    }

    private static void RequireDefined<TEnum>(TEnum value, string member)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new JsonException($"Gradient {member} has an unknown value '{value}'.");
        }
    }

    /// <summary>
    /// A non-finite number has no interpretation and cannot be written back as JSON, so a
    /// silent accept would only defer the failure.
    ///
    /// System.Text.Json usually refuses these spellings first, naming the member in its
    /// <c>Path</c> — but not always: the artboard path in this same codebase accepts a
    /// quoted <c>"1e400"</c> as infinity (see <see cref="RequireFiniteArtboard"/> and its
    /// test). Relying on the framework's inconsistency would leave the guarantee to chance,
    /// so the refusal is asserted here as well, in the model's own terms.
    /// </summary>
    private static void RequireFinite(double value, string member)
    {
        if (!double.IsFinite(value))
        {
            throw new JsonException($"Gradient {member} is not finite: {value}.");
        }
    }

    private static void RequireFinite(Point2D point, string member)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new JsonException($"Gradient {member} is not finite: {point.X},{point.Y}.");
        }
    }

    private static void RequireFinite(ColorDto colour, string member)
    {
        if (!double.IsFinite(colour.R) || !double.IsFinite(colour.G)
            || !double.IsFinite(colour.B) || !double.IsFinite(colour.A))
        {
            throw new JsonException(
                $"Gradient {member} has a non-finite channel: {colour.R},{colour.G},{colour.B},{colour.A}.");
        }
    }

    /// <summary>
    /// A stroke read back from the file.
    ///
    /// **An invisible stroke keeps its width, caps and joins.** It used to collapse to <see cref="StrokeSpec.None"/>,
    /// which threw them away - and once a path can carry a stack, an invisible stroke is a real member with real
    /// settings that a person is about to switch back on. Losing them means the stroke comes back a hairline.
    ///
    /// The colour is the one member this cannot preserve: an absent colour is how the format says "no stroke", so
    /// a colour on an invisible stroke is not written and therefore cannot be read back. Keeping it would mean
    /// writing a colour where the format has always written null, changing the bytes of every document with an
    /// unstroked path - which is most of them.
    /// </summary>
    private static StrokeSpec ToModel(this StrokeDto s)
        => s.Color is null
            ? StrokeSpec.None with
            {
                Width = s.Width,
                Cap = s.Cap,
                Join = s.Join,
                MiterLimit = s.MiterLimit,
                Alignment = s.Alignment,
                Dash = new DashPattern(s.Dash ?? Array.Empty<double>(), s.DashOffset),
                WidthProfile = s.WidthProfile?.ToModel(),
                Effects = ToEffects(s.Effects),
                RasterEffects = ToRasterEffects(s.RasterEffects),
                Dynamics = ToDynamics(s.Dynamics),
                Brush = s.Brush?.ToModel(),
                Opacity = ReadOpacity(s.Opacity),
                Blend = ReadBlend(s.Blend),
            }
            : new StrokeSpec(s.Visible, new ColorRgb(s.Color.R, s.Color.G, s.Color.B, s.Color.A),
                s.Width, s.Cap, s.Join, s.MiterLimit, s.Alignment,
                new DashPattern(s.Dash ?? Array.Empty<double>(), s.DashOffset),
                s.WidthProfile?.ToModel(),
                ToEffects(s.Effects),
                ToRasterEffects(s.RasterEffects),
                ToDynamics(s.Dynamics),
                s.Brush?.ToModel(),
                ReadOpacity(s.Opacity),
                ReadBlend(s.Blend));

    /// <summary>
    /// The stroke's opacity as the file stated it, or null when it stated none.
    ///
    /// Clamped into [0,1] rather than refused, because a value outside it is a file that means "invisible" or
    /// "opaque" and a reader that dropped the whole stroke over an out-of-range number would lose the artwork. A
    /// **non-finite** value is treated as unstated: NaN would poison every comparison the stroke takes part in and
    /// an infinity is not an opacity, so neither is worth keeping even to report.
    /// </summary>
    private static double? ReadOpacity(double? opacity)
        => opacity is { } value && double.IsFinite(value)
            ? Math.Clamp(value, 0.0, 1.0)
            : null;

    /// <summary>
    /// The stroke's blend mode as the file named it, or null when it stated none.
    ///
    /// A name this build does not know is read as **unstated** rather than as Normal. Normal is a real value that
    /// composites the stroke over the backdrop, and calling an unrecognised mode Normal would draw a picture the
    /// file did not ask for while reporting nothing - the outcome <see cref="BlendModes.Parse"/> returns null to
    /// avoid. Unstated at least keeps the stroke's own colour and opacity intact.
    /// </summary>
    private static BlendMode? ReadBlend(string? blend)
        => blend is { Length: > 0 } name ? BlendModes.Parse(name) : null;

    /// <summary>
    /// A brush read back from the file, or null when the stroke has none.
    ///
    /// The kind is read as the file wrote it rather than assumed: a reader that met a kind it had never heard of
    /// and called it calligraphic would draw a different line and say nothing.
    /// </summary>
    private static BrushSpec? ToModel(this BrushDto? dto)
        => dto is null ? null : ToBrushModel(dto);

    /// <summary>
    /// The brush library entry as a model value; the document-level reader uses this.
    ///
    /// The art members are read as the file wrote them, and a member the file does not state keeps the model's
    /// own default rather than a value this reader chose: a brush written before the art kind existed has no
    /// stretch to read, and calling that "repeat" is the model's default, not an invention about the file.
    /// </summary>
    internal static BrushSpec ToBrushModel(BrushDto dto)
        => new(
            dto.Name,
            dto.AngleDegrees,
            dto.Roundness,
            dto.Diameter,
            dto.Kind,
            ToDynamics(dto.Dynamics),
            dto.ArtAsset,
            dto.Stretch ?? ArtStretch.Repeat,
            dto.FlipAcross ?? false,
            dto.FlipAlong ?? false,
            dto.Colourisation ?? ArtColourisation.None,
            dto.ShadeColour is { } shade ? new ColorRgb(shade.R, shade.G, shade.B, shade.A) : null,
            ToPatternTile(dto.PatternSideTile),
            ToPatternTile(dto.PatternStartTile),
            ToPatternTile(dto.PatternEndTile),
            ToPatternTile(dto.PatternInnerTile),
            ToPatternTile(dto.PatternOuterTile),

            // The spacing and the threshold are read as the file wrote them, and a member it does not state keeps
            // the model's own default rather than a value this reader chose - the same rule the art members follow.
            dto.PatternSpacing ?? 0.0,
            dto.PatternCornerThreshold ?? 30.0,

            // The scatter parameters, for the reason the art and pattern members are read: a member the file does
            // not state keeps the model's own default rather than a value this reader invented.
            ToScatterModel(dto.Scatter),
            ToBristleModel(dto.Bristle));

    /// <summary>
    /// A bristle brush's bundle read back from the file, or null for a brush that states none.
    ///
    /// A control the file does not mention keeps the model's own default, for the reason the art, pattern and
    /// scatter members do: those defaults are the model's, not a value this reader chose. A file that states the
    /// spec but no controls is a bundle of the model's own bristles, which is a real setting rather than an absence.
    /// </summary>
    private static BristleBrushSpec? ToBristleModel(BristleBrushDto? dto)
        => dto is null
            ? null
            : BristleBrushSpec.Stating(
                dto.Count,
                dto.Length,
                dto.Stiffness,
                dto.Thickness,
                dto.Spread,
                dto.Randomness,
                dto.PressureSpread,
                dto.TiltTurn,
                dto.ColourJitter);

    /// <summary>
    /// A scatter brush's parameters read back from the file, or null for a brush that states none.
    ///
    /// A control the file does not mention keeps the model's own default - no pitch, no turn, the brush's own size,
    /// no offset, full opacity - for the reason the art and pattern members do: those defaults are the model's, not
    /// a value this reader chose.
    /// </summary>
    private static ScatterBrushSpec? ToScatterModel(ScatterBrushDto? dto)
        => dto is null
            ? null
            : ScatterBrushSpec.Stating(
                dto.Asset,
                ToScatterParameter(dto.Spacing, 0.0),
                ToScatterParameter(dto.Rotation, 0.0),
                ToScatterParameter(dto.Scale, 1.0),
                ToScatterParameter(dto.Offset, 0.0),
                ToScatterParameter(dto.Opacity, 1.0));

    /// <summary>One scatter control read back, with the model's default value when the file states none of it.</summary>
    private static ScatterParameter ToScatterParameter(ScatterParameterDto? dto, double fallback)
        => dto is null
            ? new ScatterParameter(fallback)
            : new ScatterParameter(dto.Value, dto.Randomness ?? 0.0);

    /// <summary>
    /// One tile of a pattern brush's set read back from the file, or null for a slot the file does not state.
    ///
    /// A control the file does not mention keeps the model's default: a tile written as its item alone is a tile
    /// with no flip, no rotation and no scale of its own, which is what the model's defaults mean rather than an
    /// invention about the file.
    /// </summary>
    private static PatternTileSpec? ToPatternTile(PatternTileDto? dto)
        => dto is null
            ? null
            : new PatternTileSpec(
                dto.Asset,
                dto.FlipAcross ?? false,
                dto.FlipAlong ?? false,
                dto.Rotation ?? 0.0,
                dto.Scale ?? 1.0);

    /// <summary>
    /// The dynamics on the wire, or null when the stroke responds to nothing.
    ///
    /// Read back in target order into a spec whose list is as long as the enum - a short list would silently mean
    /// "the remaining targets are off", which is a different document.
    /// </summary>
    private static DynamicsSpec? ToDynamics(DynamicsTargetDto[]? targets)
        => targets is { Length: > 0 }
            ? new DynamicsSpec(Enum.GetValues<DynamicsTarget>().Select((_, index) =>
                index < targets.Length
                    ? new DynamicsTargetSpec(
                        targets[index].Enabled,
                        new DynamicsCurve(
                            targets[index].X1, targets[index].Y1, targets[index].X2, targets[index].Y2))
                    : DynamicsTargetSpec.Off))
            : null;

    /// <summary>
    /// The raster effects on the wire, or null when there are none - the same absent-means-none rule the outline
    /// effects and the width profile follow.
    /// </summary>
    private static RasterEffectStack? ToRasterEffects(RasterEffectDto[]? effects)
        => effects is { Length: > 0 }
            ? new RasterEffectStack(effects.Select(e => new RasterEffectSpec(
                e.Kind,
                e.Radius,
                e.OffsetX,
                e.OffsetY,
                e.Opacity,
                e.Tint is { } tint ? new ColorRgb(tint.R, tint.G, tint.B, tint.A) : null)))
            : null;

    /// <summary>
    /// The effects on the wire, or null when there are none.
    ///
    /// Null rather than an empty stack, so "no effects" and "a stroke that once had effects and has none now" are
    /// one state - the same reason an empty width profile is not a width profile.
    /// </summary>
    private static EffectStack? ToEffects(OutlineEffectDto[]? effects)
        => effects is { Length: > 0 }
            ? new EffectStack(effects.Select(e => new OutlineEffectSpec(e.Kind, e.Size, e.Detail, e.Seed)
            {
                Ridges = e.Ridges ?? 1,
                Smooth = e.Smooth ?? false,
                Join = e.Join ?? OutlineJoin.Miter,
                Density = e.Density ?? 1.0,
                Overlap = e.Overlap ?? 0,
                Width = e.Width ?? 0,
                Curviness = e.Curviness ?? 0,
                Scatter = e.Scatter ?? 0,
            }))
            : null;

    private static WidthProfileSpec? ToModel(this WidthProfileDto? dto)
        => dto is null
            ? null
            : new WidthProfileSpec(
                dto.Name,
                (dto.Points ?? Array.Empty<WidthPointDto>()).Select(p => new WidthPoint(
                    p.Position, p.Left, p.Right, p.Interpolation)));
}

internal sealed record ArtboardDto(
    Guid Id,
    string Name,
    double X,
    double Y,
    double Width,
    double Height,
    LayerDto[] Layers);

internal sealed record LayerDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    double Opacity,
    ItemDto[] Items);

/// <summary>
/// The Illustrator private-data payload as it travels in the sidecar: decoded
/// text plus the container format it came from. Kept null (and therefore absent
/// from the JSON, see <see cref="DocumentDto.AiPrivateData"/>) whenever the
/// document carries no Illustrator data, so sidecars and PDF bytes of ordinary
/// documents are unchanged.
/// </summary>
internal sealed record AiPrivateDataDto(string Text, AiPrivateDataFormat Format);

internal sealed record DocumentDto(
    int Version,
    Guid Id,
    string Name,
    ArtboardDto[] Artboards,
    ItemDto[] Orphans,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AiPrivateDataDto? AiPrivateData = null,

    // The reusable width profiles, when the document has any.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WidthProfileDto[]? WidthProfiles = null,

    // The filters, when the document has any. Document state, like the profiles: an element refers to one by name.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FilterDto[]? Filters = null,

    // Root-level elements that are not artwork (the Inkscape named view, the RDF), verbatim, when there are any.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? SvgExtras = null,

    // Foreign-namespace definitions nothing in the document points at, verbatim, when there are any. Document-level
    // because nothing else can hold a definition with no user, and absent rather than an empty array so a document
    // that keeps none is written exactly as it was before this existed (issue #155).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ForeignPathEffects = null,

    // The namespace prefixes the file declared, so the writer uses the same ones rather than generated ones.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? SvgNamespaces = null,

    // The brushes, when the document has any. The last member, so a document that has none - which is every
    // document written before brushes existed - serialises to exactly the bytes it did then.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BrushDto[]? Brushes = null);

/// <summary>
/// Lossless, deterministic serializer for <see cref="CadDocument"/>.
///
/// "Lossless" means: <c>Deserialize(Serialize(doc))</c> yields a document that is
/// structurally identical to the original — same hierarchy, geometry, styling and
/// identifiers. This is the format embedded in the PDF sidecar (M4) and used over
/// the automation API, so its determinism is load-bearing.
///
/// Format version 1. On any future breaking change bump <see cref="DocumentDto.Version"/>
/// and add migration logic here; never mutate old payloads in place.
/// </summary>
public static class VccadDocumentSerializer
{
    /// <summary>Format version written by this build of the serializer.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        // Stable output: no whitespace, fixed member order from the record
        // declarations, explicit nulls only where meaningful.
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null,
        // Enums travel as readable strings, not opaque ints, so sidecars survive
        // enum renumbering and are human-auditable.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes a document to a compact JSON string.</summary>
    public static string Serialize(CadDocument document)
    {
        DocumentDto dto = ToDto(document);
        try
        {
            return JsonSerializer.Serialize(dto, Options);
        }
        catch (ArgumentException ex)
        {
            throw NotWritable(document, ex);
        }
    }

    /// <summary>Serializes a document to UTF-8 bytes (for embedding and hashing).</summary>
    public static byte[] SerializeToBytes(CadDocument document)
    {
        DocumentDto dto = ToDto(document);
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(dto, Options);
        }
        catch (ArgumentException ex)
        {
            throw NotWritable(document, ex);
        }
    }

    /// <summary>Deserializes a document previously produced by <see cref="Serialize"/>.</summary>
    public static CadDocument Deserialize(string json)
    {
        DocumentDto? dto = JsonSerializer.Deserialize<DocumentDto>(json, Options);
        if (dto is null)
        {
            throw new FormatException("Serialized document is empty.");
        }

        return ToModel(dto);
    }

    /// <summary>Deserializes a document from UTF-8 bytes.</summary>
    public static CadDocument Deserialize(byte[] bytes)
    {
        DocumentDto? dto = JsonSerializer.Deserialize<DocumentDto>(bytes, Options);
        if (dto is null)
        {
            throw new FormatException("Serialized document is empty.");
        }

        return ToModel(dto);
    }

    private static DocumentDto ToDto(CadDocument d)
    {
        ValidateFiniteArtboards(d);
        return new(
            CurrentVersion,
            d.Id,
            d.Name,
            d.Artboards.Select(a => new ArtboardDto(
                a.Id,
                a.Name,
                a.X,
                a.Y,
                a.Width,
                a.Height,
                a.Layers.Select(l => new LayerDto(
                    l.Id,
                    l.Name,
                    l.IsVisible,
                    l.IsLocked,
                    l.Opacity,
                    l.Children.Select(ItemDto.From).ToArray())).ToArray())).ToArray(),
            d.Orphans.Children.Select(ItemDto.From).ToArray(),
            d.AiPrivateData is null
                ? null
                : new AiPrivateDataDto(d.AiPrivateData.Text, d.AiPrivateData.Format),

            // The reusable width profiles. Absent when the document has none, so a document that never used one
            // is written exactly as it was before profiles existed.
            d.WidthProfiles.Count == 0
                ? null
                : d.WidthProfiles.Select(ToDto).ToArray(),

            d.Filters.Count == 0 ? null : d.Filters.Select(ToFilterDto).ToArray(),

            d.SvgExtras.Count == 0 ? null : d.SvgExtras.ToArray(),

            d.ForeignPathEffects.Count == 0 ? null : d.ForeignPathEffects.ToArray(),

            d.SvgNamespaces.Count == 0 ? null : new Dictionary<string, string>(d.SvgNamespaces),

            // The brushes. Absent when the document has none, so a document that never used one is written
            // exactly as it was before brushes existed.
            d.Brushes.Count == 0
                ? null
                : d.Brushes.Select(ItemDto.ToBrushDto).ToArray());
    }

    private static WidthProfileDto ToDto(WidthProfileSpec profile)
        => new(
            profile.Name,
            profile.Points.Select(p => new WidthPointDto(p.Position, p.LeftWidth, p.RightWidth, p.Interpolation))
                .ToArray());

    /// <summary>A filter on the wire, with every primitive's wiring and parameters.</summary>
    private static FilterDto ToFilterDto(FilterSpec filter)
        => new(
            filter.Name,
            filter.Primitives.Select(p => new FilterPrimitiveDto(
                p.Kind,
                p.Input,
                p.Input2,
                p.Result,
                p.Radius,
                p.Dx,
                p.Dy,
                p.FloodColor is { } colour
                    ? new ColorDto(colour.R, colour.G, colour.B, colour.A)
                    : null,
                p.FloodOpacity,
                p.Operator,
                p.Mode,
                p.Kind == FilterPrimitiveKind.DisplacementMap ? p.Scale : null,
                p.Kind == FilterPrimitiveKind.DisplacementMap ? p.XChannel : null,
                p.Kind == FilterPrimitiveKind.DisplacementMap ? p.YChannel : null,
                p.Kind is FilterPrimitiveKind.ColorMatrix or FilterPrimitiveKind.Turbulence ? p.Type : null,
                p.Kind == FilterPrimitiveKind.Turbulence ? p.BaseFrequency : null,
                p.Kind == FilterPrimitiveKind.Turbulence ? p.Octaves : null,
                p.Kind == FilterPrimitiveKind.Turbulence ? p.Seed : null,
                p.Kind == FilterPrimitiveKind.ColorMatrix && p.Matrix is { Length: > 0 } matrix
                    ? PadMatrix(matrix)
                    : null,
                p.Kind is FilterPrimitiveKind.SpecularLighting or FilterPrimitiveKind.DiffuseLighting
                    ? p.SurfaceScale
                    : null,
                p.Kind == FilterPrimitiveKind.SpecularLighting ? p.SpecularConstant : null,
                p.Kind == FilterPrimitiveKind.SpecularLighting ? p.SpecularExponent : null,
                p.Kind == FilterPrimitiveKind.DiffuseLighting ? p.DiffuseConstant : null,
                p.Kind is FilterPrimitiveKind.SpecularLighting or FilterPrimitiveKind.DiffuseLighting
                    ? p.LightingColor is { } light ? new ColorDto(light.R, light.G, light.B, light.A) : null
                    : null,
                p.Kind is FilterPrimitiveKind.SpecularLighting or FilterPrimitiveKind.DiffuseLighting
                    ? p.Azimuth
                    : null,
                p.Kind is FilterPrimitiveKind.SpecularLighting or FilterPrimitiveKind.DiffuseLighting
                    ? p.Elevation
                    : null)).ToArray(),
            filter.X,
            filter.Y,
            filter.Width,
            filter.Height,
            filter.ObjectBoundingBox,
            filter.Output,
            filter.PrimitiveUnitsObjectBoundingBox ? true : null,
            filter.HasFilterResolution ? filter.FilterResolutionX : null,
            filter.HasFilterResolution ? filter.FilterResolutionY : null);

    /// <summary>
    /// A colour matrix, padded to the twenty numbers the format names.
    ///
    /// The shorthand forms are stored with a single number - the amount - and written back that way, because a
    /// `saturate` read as a matrix and written back as one would still draw the same picture but would no longer be
    /// the filter that was read. Only a matrix that is short of twenty numbers is padded, which is the one case
    /// where a reader would otherwise have to guess the missing columns.
    /// </summary>
    private static double[] PadMatrix(double[] values)
    {
        // One number is a shorthand's amount, twenty is the whole matrix, and both travel as they are. Sixteen is
        // the "four straight rows, no constants" spelling, and the four missing numbers are the fifth column - zeros
        // - so the padding invents nothing.
        if (values.Length is 1 or 20)
        {
            return values;
        }

        if (values.Length != 16)
        {
            return values;
        }

        var padded = new double[20];
        Array.Copy(values, padded, 16);
        return padded;
    }

    private static FilterSpec ToModel(FilterDto dto)
    {
        // A resolution is a pair or nothing: half of one is not a resolution, and a model that carried one would be
        // refused by the engine at paint time - where the reason is far from the sidecar that caused it.
        if (dto.FilterResolutionX is not null || dto.FilterResolutionY is not null)
        {
            if (dto.FilterResolutionX is not { } rx || dto.FilterResolutionY is not { } ry ||
                !FilterSpec.AcceptsFilterResolution(rx, ry))
            {
                throw new NotSupportedException(
                    $"filter '{dto.Name}' names a resolution of {dto.FilterResolutionX} by {dto.FilterResolutionY}; " +
                    $"a resolution is a pair of whole numbers between 1 and {FilterSpec.MaximumFilterResolution}");
            }
        }

        return new(
            dto.Name,
            (dto.Primitives ?? Array.Empty<FilterPrimitiveDto>()).Select(ToModel).ToArray())
        {
            X = dto.X,
            Y = dto.Y,
            Width = dto.Width,
            Height = dto.Height,
            ObjectBoundingBox = dto.ObjectBoundingBox,
            PrimitiveUnitsObjectBoundingBox = dto.PrimitiveUnitsObjectBoundingBox ?? false,
            FilterResolutionX = dto.FilterResolutionX,
            FilterResolutionY = dto.FilterResolutionY,
            Output = dto.Output,
        };
    }

    /// <summary>
    /// One primitive off the wire.
    ///
    /// An absent member is the **declared default** rather than a zero, which is what lets a sidecar written before
    /// these primitives existed come back as the filter it meant: a colour matrix with no matrix is identity, a
    /// specular highlight with no exponent is one.
    /// </summary>
    private static FilterPrimitive ToModel(FilterPrimitiveDto dto)
        => new(
            dto.Kind, dto.Input, dto.Input2, dto.Result, dto.Radius, dto.Dx, dto.Dy,
            dto.FloodColor is { } colour ? new ColorRgb(colour.R, colour.G, colour.B, colour.A) : null,
            dto.FloodOpacity, dto.Operator, dto.Mode)
        {
            Scale = dto.Scale ?? 0.0,
            XChannel = dto.XChannel ?? "A",
            YChannel = dto.YChannel ?? "A",
            Type = dto.Type ?? (dto.Kind == FilterPrimitiveKind.ColorMatrix ? "matrix" : "turbulence"),
            BaseFrequency = dto.BaseFrequency ?? 0.0,
            Octaves = dto.Octaves ?? 1,
            Seed = dto.Seed ?? 0,
            Matrix = dto.Matrix,
            SurfaceScale = dto.SurfaceScale ?? 1.0,
            SpecularConstant = dto.SpecularConstant ?? 1.0,
            SpecularExponent = dto.SpecularExponent ?? 1.0,
            DiffuseConstant = dto.DiffuseConstant ?? 1.0,
            LightingColor = dto.LightingColor is { } light
                ? new ColorRgb(light.R, light.G, light.B, light.A)
                : null,
            Azimuth = dto.Azimuth ?? 0.0,
            Elevation = dto.Elevation ?? 0.0,
        };

    private static CadDocument ToModel(DocumentDto dto)
    {
        if (dto.Version > CurrentVersion)
        {
            throw new NotSupportedException(
                $"Document format v{dto.Version} is newer than this build supports (v{CurrentVersion}).");
        }

        ArtboardDto[] artboards = RequireArray(dto.Artboards, nameof(dto.Artboards));
        ItemDto[] orphans = RequireArray(dto.Orphans, nameof(dto.Orphans));

        var document = new CadDocument { Name = dto.Name };
        document.RestoreIdentity(dto.Id);

        // One asset at a time, and a profile with no points is not a profile: loading one would put a stroke
        // into the outline route to draw exactly what it drew before.
        document.SetWidthProfiles(
            (dto.WidthProfiles ?? Array.Empty<WidthProfileDto>())
                .Where(p => p.Points is { Length: > 0 })
                .Select(p => new WidthProfileSpec(
                    p.Name,
                    p.Points!.Select(point => new WidthPoint(
                        point.Position, point.Left, point.Right, point.Interpolation)))));

        // The brushes, for the same reason as the profiles: a stroke refers to one by name, so a library that
        // did not load would leave every brush-stroked path naming an asset the document does not have.
        document.SetBrushes((dto.Brushes ?? Array.Empty<BrushDto>()).Select(ItemDtoExtensions.ToBrushModel));

        // The root-level baggage - the Inkscape named view, the RDF - verbatim, so a saved and reopened document
        // still carries the settings the file gave it.
        document.SetSvgExtras(dto.SvgExtras ?? Array.Empty<string>());

        // The foreign definitions nothing points at, which only the document can hold - so a document saved and
        // reopened keeps the effects it was imported with rather than losing the unreferenced ones on the first save.
        document.SetForeignPathEffects(dto.ForeignPathEffects ?? Array.Empty<string>());
        document.SetSvgNamespaces(dto.SvgNamespaces ?? new Dictionary<string, string>(StringComparer.Ordinal));

        // The filters, for the same reason as the profiles above: an element refers to one by name, so a library
        // that did not load would leave every filtered shape unfiltered.
        document.SetFilters(
            (dto.Filters ?? Array.Empty<FilterDto>())
                .Where(f => f.Primitives is { Length: > 0 })
                .Select(ToModel));

        foreach (ArtboardDto a in artboards)
        {
            RequireFiniteArtboard(a);
            var artboard = new Artboard(new Size2D(a.Width, a.Height), new Point2D(a.X, a.Y)) { Name = a.Name };
            artboard.RestoreIdentity(a.Id);
            foreach (LayerDto l in RequireArray(a.Layers, nameof(a.Layers)))
            {
                var layer = new Layer
                {
                    Name = l.Name,
                    IsVisible = l.IsVisible,
                    IsLocked = l.IsLocked,
                    Opacity = l.Opacity,
                };
                layer.RestoreIdentity(l.Id);
                foreach (ItemDto item in RequireArray(l.Items, nameof(l.Items)))
                {
                    layer.AddItem(item.ToModel());
                }

                artboard.AddLayer(layer);
            }

            document.AddArtboard(artboard);
        }

        foreach (ItemDto orphan in orphans)
        {
            document.Orphans.AddItem(orphan.ToModel());
        }

        // A sidecar written before the payload existed has no member here; a
        // payload property that is present but null means "no Illustrator data".
        if (dto.AiPrivateData is { } ai)
        {
            document.AiPrivateData = new AiPrivateData(ai.Text ?? string.Empty, ai.Format);
        }

        EnsureUniqueIdentities(document);
        return document;
    }

    /// <summary>
    /// A structural array that a well-formed sidecar always carries. System.Text.Json
    /// leaves a missing member null; dereferencing it is a
    /// <see cref="NullReferenceException"/> that names neither the document nor the
    /// member, so every array the reader walks is checked here first.
    /// </summary>
    internal static T[] RequireArray<T>(T[]? values, string member)
        => values ?? throw new JsonException($"Serialized document is missing the '{member}' array.");

    /// <summary>
    /// Refuses an artboard whose rectangle is not finite before it reaches layout.
    ///
    /// System.Text.Json parses the quoted string <c>"1e400"</c> as a number (its
    /// UTF-8 parser reports overflow as infinity) while refusing <c>"NaN"</c>,
    /// <c>"Infinity"</c> and a bare <c>1e400</c>. Validating the parsed value
    /// therefore catches every spelling, and a non-finite artboard — which would
    /// otherwise compute NaN fits and silently draw nothing — is refused by name.
    /// </summary>
    private static void RequireFiniteArtboard(ArtboardDto a)
    {
        if (double.IsFinite(a.X) && double.IsFinite(a.Y)
            && double.IsFinite(a.Width) && double.IsFinite(a.Height))
        {
            return;
        }

        throw new JsonException(
            $"Artboard '{a.Name}' ({a.Id}) has a non-finite rectangle: "
            + $"X={a.X}, Y={a.Y}, Width={a.Width}, Height={a.Height}. "
            + "A non-finite artboard cannot be laid out, fitted or rendered.");
    }

    /// <summary>
    /// Refuses a document holding a non-finite artboard before any JSON is written.
    ///
    /// System.Text.Json's own refusal is an <see cref="ArgumentException"/> about
    /// "positive and negative infinity" that names neither the document nor the
    /// artboard, so a person whose imported page arrived with a NaN MediaBox has
    /// nothing to act on. Naming the value and the artboard it sits on turns the
    /// failure into a diagnosis. (Keeping a non-finite value out of the model in the
    /// first place is the importer's job; this is the last line of defence.)
    /// </summary>
    private static void ValidateFiniteArtboards(CadDocument document)
    {
        for (int i = 0; i < document.Artboards.Count; i++)
        {
            Artboard a = document.Artboards[i];
            if (double.IsFinite(a.X) && double.IsFinite(a.Y)
                && double.IsFinite(a.Width) && double.IsFinite(a.Height))
            {
                continue;
            }

            throw new ArgumentException(
                $"Document '{document.Name}' cannot be saved: artboard {i} '{a.Name}' has a non-finite "
                + $"rectangle (X={a.X}, Y={a.Y}, Width={a.Width}, Height={a.Height}). A non-finite "
                + "artboard cannot be laid out, fitted or rendered.", nameof(document));
        }
    }

    private static ArgumentException NotWritable(CadDocument document, ArgumentException inner)
        => new(
            $"Document '{document.Name}' contains a number that cannot be written as JSON: {inner.Message}",
            nameof(document),
            inner);

    /// <summary>
    /// Gives every object a distinct identity.
    ///
    /// A crafted sidecar can carry the same id on two items. Restoring both verbatim
    /// leaves two objects answering to one id, and every id-addressed operation then
    /// silently picks one of them — a wrong answer, not a crash. The first holder
    /// keeps the id; each later duplicate is renumbered, so the drawing still loads
    /// and both objects stay addressable.
    /// </summary>
    private static void EnsureUniqueIdentities(CadDocument document)
    {
        var seen = new HashSet<Guid>();
        document.RestoreIdentity(UniqueIdentity(document.Id, seen));
        foreach (Artboard artboard in document.Artboards)
        {
            artboard.RestoreIdentity(UniqueIdentity(artboard.Id, seen));
            foreach (Layer layer in artboard.Layers)
            {
                layer.RestoreIdentity(UniqueIdentity(layer.Id, seen));
                foreach (LayerItem item in layer.Children)
                {
                    EnsureUniqueIdentity(item, seen);
                }
            }
        }

        foreach (LayerItem item in document.Orphans.Children)
        {
            EnsureUniqueIdentity(item, seen);
        }
    }

    private static void EnsureUniqueIdentity(LayerItem item, HashSet<Guid> seen)
    {
        item.RestoreIdentity(UniqueIdentity(item.Id, seen));
        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                EnsureUniqueIdentity(child, seen);
            }
        }
    }

    private static Guid UniqueIdentity(Guid id, HashSet<Guid> seen)
    {
        if (seen.Add(id))
        {
            return id;
        }

        Guid fresh;
        do
        {
            fresh = Guid.NewGuid();
        }
        while (!seen.Add(fresh));

        return fresh;
    }
}
