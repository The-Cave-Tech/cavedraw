using System.Xml.Linq;

namespace VCCad.Core.Svg;

/// <summary>
/// One property's winning value for an element, in CSS's own order.
///
/// This was a local function inside <see cref="PresentationStyle"/> until text arrived, because a text property
/// comes from the same cascade as paint: `font-family` written in a `style` element has to beat a presentation
/// attribute for exactly the same reason `fill` does. Two implementations of that order would eventually disagree
/// about one of them, and the disagreement would look like a colour or a face that came from nowhere.
///
/// The order is: an important inline declaration, an important rule, an inline declaration, a rule, and last the
/// presentation attribute - which is **the lowest of all**, below every rule. It is easy to assume the attribute
/// written on the element is the most specific thing there is, and it is the opposite.
/// </summary>
internal static class SvgProperties
{
    /// <summary>The value a property resolves to for an element, or null when nothing states one.</summary>
    public static string? Value(
        XElement element,
        IReadOnlyDictionary<string, (string Value, bool Important)>? sheet,
        string name)
        => Value(
            element,
            sheet,
            PresentationStyle.ReadStyleAttribute(element),
            PresentationStyle.ReadStyleImportance(element),
            name);

    /// <summary>The same resolution, for a caller that has already read the element's inline style.</summary>
    public static string? Value(
        XElement element,
        IReadOnlyDictionary<string, (string Value, bool Important)>? sheet,
        Dictionary<string, string> inline,
        Dictionary<string, bool> inlineImportant,
        string name)
    {
        (string Value, bool Important) fromSheet = sheet is not null && sheet.TryGetValue(name, out var s)
            ? s
            : (string.Empty, false);
        bool hasSheet = sheet is not null && sheet.ContainsKey(name);
        bool hasInline = inline.TryGetValue(name, out string? fromInline);
        bool inlineIsImportant = inlineImportant.TryGetValue(name, out bool flag) && flag;

        // A vendor-prefixed property - `-inkscape-font-specification` is the one that matters - is written inside
        // the `style` attribute and never as an attribute of its own: XML does not allow a name to begin with a
        // hyphen, and SVG's presentation attributes are only the ones it defines. Asking the element for one is an
        // XmlException, not a null.
        string? fromAttribute = name.Length > 0 && name[0] != '-'
            ? element.Attribute(name)?.Value
            : null;

        if (inlineIsImportant && hasInline)
        {
            return fromInline;
        }

        if (hasSheet && fromSheet.Important)
        {
            return fromSheet.Value;
        }

        if (hasInline)
        {
            return fromInline;
        }

        return hasSheet ? fromSheet.Value : fromAttribute;
    }
}
