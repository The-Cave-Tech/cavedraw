using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>
/// Draws a run of text with the glyph definitions the file's own font carries.
///
/// A document that supplies a face through `@font-face` is asking for *its* glyphs, and when the programme is
/// SVG-in-OpenType the drawings are in the file itself. The model has no face to hand a renderer - an SVG import has
/// no font registry behind it - so the honest way to draw the picture the file describes is to place the glyph
/// artwork, which is what this does: one group of paths, positioned where the model's own layout puts each
/// character.
///
/// **That turns text into outlines, and the caller says so.** The picture is right and the text is no longer text;
/// a reader that kept the `TextItem` instead would keep the words and draw a substituted face, which is the wrong
/// picture. The loss is reported rather than left for someone to discover.
///
/// The glyph documents are in **font units with Y up** - a coordinate system with the baseline at zero and the em
/// square a thousand units tall - so each shape is scaled by the run's size over the programme's em and flipped
/// about the baseline it is placed on. Shapes the reader does not draw are reported rather than dropped silently.
/// </summary>
internal static class SvgGlyphText
{
    /// <summary>
    /// The artwork for <paramref name="text"/>, or null when the block is not wholly drawn with a supplied face -
    /// half outlines and half substituted text would be a picture neither the file nor the model describes.
    /// </summary>
    public static ArtGroup? Build(TextItem text, SvgFontFaces faces, Action<string> warn)
    {
        var programmes = new SvgFontProgramme?[text.Runs.Count];
        for (int i = 0; i < text.Runs.Count; i++)
        {
            programmes[i] = faces.Find(text.Runs[i].FontFamily);
        }

        if (programmes.Any(p => p is null))
        {
            return null;
        }

        string flat = string.Concat(text.Runs.Select(r => r.Text));
        TextLayout layout = TextLayoutEngine.Compute(text);
        var group = new ArtGroup { Name = text.Name };
        int placed = 0;
        int missing = 0;

        foreach (GlyphBox glyph in layout.Glyphs)
        {
            TextRun run = text.Runs[glyph.Run];
            SvgFontProgramme programme = programmes[glyph.Run]!;
            double scale = programme.UnitsPerEm > 0 ? run.FontSize / programme.UnitsPerEm : 0;
            if (scale <= 0 || glyph.Index < 0 || glyph.Index >= flat.Length)
            {
                continue;
            }

            int id = programme.GlyphFor(flat[glyph.Index]);
            string? document = id != 0 ? programme.DocumentFor(id) : null;
            if (document is null)
            {
                // A character the supplied face has no drawing for: reported, because a missing glyph is invisible
                // on the page and looks deliberate.
                missing++;
                continue;
            }

            foreach (PathItem shape in Shapes(document, scale, text.Origin.X + glyph.X, text.Origin.Y + glyph.Y, warn))
            {
                group.AddItem(shape);
                placed++;
            }
        }

        if (missing > 0)
        {
            warn(
                $"the file's own font has no drawing for {missing} of its characters, and "
                + $"{placed} shapes were placed from the ones it has");
        }

        return placed > 0 ? group : null;
    }

    /// <summary>The paths one glyph document draws, placed at a pen position and scaled out of font units.</summary>
    private static IEnumerable<PathItem> Shapes(string document, double scale, double penX, double penY, Action<string> warn)
    {
        XDocument xml;
        try
        {
            xml = XDocument.Parse(document);
        }
        catch (System.Xml.XmlException exception)
        {
            warn($"a glyph drawing is not well-formed XML and was not drawn: {exception.Message}");
            yield break;
        }

        foreach (XElement element in xml.Descendants())
        {
            if (element.Name.LocalName != "path")
            {
                // Reported rather than skipped in silence: a glyph drawn with a rect or a circle is a picture this
                // reader does not make, and saying so is the difference between a gap and a guess.
                if (element.Name.LocalName is "rect" or "circle" or "ellipse" or "polygon" or "polyline" or "use")
                {
                    warn($"a glyph drawing uses a <{element.Name.LocalName}>, which this reader does not draw as a glyph");
                }

                continue;
            }

            IReadOnlyList<SubPath> parsed = SvgPathData.Parse(element.Attribute("d")?.Value ?? string.Empty, warn);
            if (parsed.Count == 0)
            {
                continue;
            }

            var path = new PathItem { Name = element.Attribute("id")?.Value ?? string.Empty };
            (FillSpec fill, StrokeSpec stroke) = Paint(element, scale, warn);
            path.Fill = fill;
            path.Strokes.Clear();
            path.Strokes.Add(stroke);

            foreach (SubPath source in parsed)
            {
                SubPath sub = path.AddSubPath(source.IsClosed);
                foreach (PathNode node in source.Nodes)
                {
                    sub.Nodes.Add(Place(node, scale, penX, penY));
                }
            }

            yield return path;
        }
    }

    /// <summary>
    /// A node in the model's frame: font units scaled by the run's size, with Y flipped about the baseline the glyph
    /// is placed on. A node's handles are absolute points, so they are mapped exactly as its anchor is.
    /// </summary>
    private static PathNode Place(PathNode node, double scale, double penX, double penY)
    {
        Point2D Map(Point2D point) => new(penX + (point.X * scale), penY - (point.Y * scale));
        return new PathNode(Map(node.Anchor), Map(node.InHandle), Map(node.OutHandle));
    }

    /// <summary>
    /// A glyph path's paint, from the attributes and the `style` the document states.
    ///
    /// A glyph is filled or stroked or both, and the difference is the picture: the corpus's own test font draws a
    /// stroked outline, which filled would be a solid silhouette. Anything this cannot read is reported and the
    /// shape is drawn with no paint rather than with a colour the file never named.
    /// </summary>
    private static (FillSpec Fill, StrokeSpec Stroke) Paint(XElement element, double scale, Action<string> warn)
    {
        string? fill = Value(element, "fill");
        string? stroke = Value(element, "stroke");
        string? width = Value(element, "stroke-width");

        FillSpec fillSpec;
        if (fill is null || fill.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            fillSpec = FillSpec.None;
        }
        else if (Colour(fill) is { } fillColour)
        {
            fillSpec = FillSpec.Solid(fillColour);
        }
        else
        {
            fillSpec = Unreadable(fill, "fill", warn);
        }

        StrokeSpec strokeSpec;
        if (stroke is null || stroke.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            strokeSpec = StrokeSpec.None;
        }
        else if (Colour(stroke) is { } strokeColour)
        {
            strokeSpec = StrokeSpec.Hairline(strokeColour) with
            {
                Width = double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                    ? w * scale
                    : 1.0,
            };
        }
        else
        {
            Unreadable(stroke, "stroke", warn);
            strokeSpec = StrokeSpec.None;
        }

        return (fillSpec, strokeSpec);
    }

    private static FillSpec Unreadable(string value, string property, Action<string> warn)
    {
        warn($"a glyph drawing states {property}=\"{value}\", which this reader cannot read, so the shape is drawn unpainted");
        return FillSpec.None;
    }

    /// <summary>An attribute, or the same property from the element's inline `style`.</summary>
    private static string? Value(XElement element, string property)
        => element.Attribute(property)?.Value ?? Inline(element.Attribute("style")?.Value, property);

    private static string? Inline(string? style, string property)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        foreach (string declaration in style.Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon > 0 && declaration[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>`#rgb`, `#rrggbb` and SVG's `none`, `black` and `white` - what a glyph drawing actually uses.</summary>
    private static ColorRgb? Colour(string value)
    {
        string text = value.Trim();
        if (text.StartsWith('#'))
        {
            string hex = text[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            if (hex.Length == 6 &&
                int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed))
            {
                return new ColorRgb((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF);
            }

            return null;
        }

        return text.ToLowerInvariant() switch
        {
            "black" => new ColorRgb(0, 0, 0),
            "white" => new ColorRgb(255, 255, 255),
            _ => null,
        };
    }
}
