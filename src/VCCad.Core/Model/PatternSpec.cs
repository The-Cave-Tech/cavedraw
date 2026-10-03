using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A pattern paint server: artwork used as a paint, tiled across whatever it fills (issue #203).
///
/// **The tile's content is not here.** It is an <see cref="ArtGroup"/> in the document's library, named by
/// <see cref="Definition"/> - exactly as SVG writes a `&lt;pattern&gt;` as a document asset an element refers to by
/// id, and exactly what a `<marker>` already does in this model. Two consequences worth stating, because they are
/// why it is shaped this way: the tile is written back out in one place rather than copied into every paint that
/// uses it, and a person can reach the tile as ordinary artwork (the library panel) rather than through a second
/// editing surface that would drift from the first.
///
/// **What the file said is kept as the file said it.** The units and the `patternTransform` are held as the
/// attribute text rather than as a parsed value, because the units name a coordinate system this reader converts
/// precisely one of (`userSpaceOnUse`; `objectBoundingBox` is refused on import as it is for a clip path), and a
/// transform that has been re-serialised from a parse is a transform that can come back subtly different. The
/// numbers that *are* interpreted - the tile box - are numbers.
/// </summary>
public sealed record PatternSpec(
    string Definition,
    double Width,
    double Height,
    double X = 0.0,
    double Y = 0.0,
    string? Units = null,
    string? ContentUnits = null,
    string? Transform = null)
{
    /// <summary>
    /// The paint a library definition stands for, or null when the definition is not a pattern.
    ///
    /// Recognised by the attributes SVG gives a `&lt;pattern&gt;` - a tile `width` or `height`, or the units - the
    /// same way a marker definition is recognised by `markerWidth`/`refX` in <see cref="VCCad.Core.Svg.MarkerSpec"/>,
    /// so a symbol that happens to be named `dots` is not mistaken for a pattern.
    /// </summary>
    public static PatternSpec? From(string definition, IReadOnlyDictionary<string, string> attributes)
    {
        bool isPattern = attributes.ContainsKey("patternUnits") ||
            attributes.ContainsKey("patternContentUnits") ||
            attributes.ContainsKey("patternTransform") ||
            attributes.ContainsKey("width") ||
            attributes.ContainsKey("height");

        if (!isPattern)
        {
            return null;
        }

        return new PatternSpec(
            definition,
            Number(attributes, "width"),
            Number(attributes, "height"),
            Number(attributes, "x"),
            Number(attributes, "y"),
            Read(attributes, "patternUnits"),
            Read(attributes, "patternContentUnits"),
            Read(attributes, "patternTransform"));
    }

    /// <summary>
    /// The unit text this tile works in, or null when the file did not say. SVG's initial value is
    /// `objectBoundingBox`, which this reader refuses at import rather than guessing at; a tile that states
    /// `userSpaceOnUse` is in the space of the shape that uses it.
    /// </summary>
    public bool UserSpaceUnits => Units is null || Units.Equals("userSpaceOnUse", StringComparison.OrdinalIgnoreCase);

    private static string? Read(IReadOnlyDictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out string? value) && value.Trim().Length > 0 ? value.Trim() : null;

    private static double Number(IReadOnlyDictionary<string, string> attributes, string name)
        => double.TryParse(
            Read(attributes, name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double value)
            ? value
            : 0.0;
}
