using System.Xml.Linq;

namespace VCCad.Core.Svg;

/// <summary>
/// The `&lt;pattern&gt;` elements a document defines, and what each one's tile actually draws.
///
/// **A pattern is read, and then reported rather than painted.** The model's paint is <c>FillSpec</c>: a colour, a
/// gradient or a hatch. A pattern is *artwork used as a paint*, and there is no pattern paint server to put one in,
/// so a `url(#p)` that names a pattern cannot be honoured. This is the same finding the PDF importer reached for a
/// tiling pattern in #175: the tile is art the model has no place for, and the honest answer is to name it rather
/// than to substitute a colour, because a shape filled with a plausible solid is a drawing that looks deliberate
/// and is not the file.
///
/// **Why this reads the tile and not just the reference.** Before this existed, a `url(#p)` that named a pattern
/// fell past the gradient lookup and produced *"no paint server called 'p' for this fill"* - the right finding with
/// the wrong name, which is worse than no name because it sends the reader looking for a paint server that is
/// sitting in the `defs` all along. A report is only worth making if it says what the file has, so this reads the
/// tile's own shapes and the two unit properties, and <see cref="Facts"/> carries them to the message.
///
/// It is deliberately not a substitute for a reader: nothing here builds geometry, and no number in the tile is
/// interpreted. The pattern's coordinate system is exactly the part this reader declines to guess at.
/// </summary>
internal sealed class SvgPatterns
{
    private readonly Dictionary<string, PatternFacts> _patterns = new(StringComparer.Ordinal);

    /// <summary>
    /// One pattern as the file writes it: what it is called, which shapes its tile draws, and which of its unit
    /// properties name a coordinate system this reader does not convert.
    /// </summary>
    /// <param name="TileShapes">
    /// The tile's own graphical children, in document order and de-duplicated, as written - `&lt;rect&gt;`,
    /// `&lt;ellipse&gt;`. Empty when the tile holds nothing this reader draws.
    /// </param>
    /// <param name="UnitRefusals">
    /// One message per unit property stated as `objectBoundingBox`, naming the attribute. Under those units the
    /// tile's numbers are fractions of the painted shape's box rather than user space, so they are refused the same
    /// way `clipPathUnits="objectBoundingBox"` is - see <see cref="SvgReader"/>'s `ClipPathFor`.
    /// </param>
    internal sealed record PatternFacts(
        string Id,
        IReadOnlyList<string> TileShapes,
        IReadOnlyList<string> UnitRefusals);

    /// <summary>Every `pattern` in the document, by id. A pattern with no id cannot be referred to and is skipped.</summary>
    public static SvgPatterns Collect(XElement root)
    {
        var patterns = new SvgPatterns();

        foreach (XElement element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName != "pattern" ||
                (!string.IsNullOrEmpty(element.Name.NamespaceName) && element.Name.Namespace != SvgReader.Svg))
            {
                continue;
            }

            if (element.Attribute("id")?.Value is not { Length: > 0 } id)
            {
                continue;
            }

            patterns._patterns[id] = Facts(element, id);
        }

        return patterns;
    }

    /// <summary>What the pattern called <paramref name="id"/> is, or null when the document defines no such pattern.</summary>
    public PatternFacts? Facts(string id)
        => _patterns.TryGetValue(id, out PatternFacts? facts) ? facts : null;

    private static PatternFacts Facts(XElement pattern, string id)
    {
        var shapes = new List<string>();
        CollectShapes(pattern, shapes);

        var refusals = new List<string>();
        foreach (string attribute in new[] { "patternUnits", "patternContentUnits" })
        {
            if (pattern.Attribute(attribute)?.Value?.Trim() is { Length: > 0 } units &&
                units.Equals("objectBoundingBox", StringComparison.OrdinalIgnoreCase))
            {
                refusals.Add(
                    $"it states {attribute}=\"{units}\", a different coordinate system this reader does not " +
                    "convert, so the tile's numbers are fractions of the painted shape's box rather than user space");
            }
        }

        return new PatternFacts(id, shapes, refusals);
    }

    /// <summary>
    /// The shapes a tile draws, in document order and de-duplicated by name.
    ///
    /// Groups are walked into, because a tile that wraps its art in a `g` draws the same shapes a tile that does
    /// not - and a report that stopped at the group would name `&lt;g&gt;` and nothing a person can picture.
    /// </summary>
    private static void CollectShapes(XElement parent, List<string> shapes)
    {
        foreach (XElement child in parent.Elements())
        {
            string name = child.Name.LocalName;

            if (name == "g")
            {
                CollectShapes(child, shapes);
                continue;
            }

            if (name is not ("rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" or
                "text" or "use" or "image"))
            {
                continue;
            }

            if (!shapes.Contains(name, StringComparer.Ordinal))
            {
                shapes.Add(name);
            }
        }
    }
}
