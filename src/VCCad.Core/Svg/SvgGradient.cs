using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>
/// Reads SVG paint servers into the model's gradients.
///
/// The model stores a gradient's geometry **normalised to the shape's box**, which is what SVG calls
/// `objectBoundingBox` and what the format uses by default - so the common case is a direct translation.
/// `userSpaceOnUse` is absolute and has to be converted, which needs the shape, so the conversion happens where
/// the shape is known rather than where the gradient is defined.
///
/// A gradient is usually written in **two pieces**: one element carries the stops and another carries the
/// geometry, linked by `href`. Reading either alone gives a gradient with the wrong colours or the wrong
/// direction, and it is how Inkscape writes them.
/// </summary>
internal sealed class SvgGradients
{
    private readonly Dictionary<string, RawGradient> _gradients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ColorRgb Colour, double Opacity)> _solids = new(StringComparer.Ordinal);

    /// <summary>One gradient as the file writes it, before any shape is known.</summary>
    private sealed record RawGradient(
        string Id,
        bool IsRadial,
        string? Href,
        bool UserSpace,
        AffineTransform Transform,
        GradientSpread Spread,
        Dictionary<string, string> Values,
        List<GradientStop> Stops);

    /// <summary>Reads every gradient in the document, so a `url(#id)` can be resolved from anywhere.</summary>
    public static SvgGradients Collect(XElement root, SvgStylesheet sheet, Action<string>? warn = null)
    {
        var gradients = new SvgGradients();

        foreach (XElement element in root.DescendantsAndSelf())
        {
            string? id = element.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            string name = element.Name.LocalName;
            if (string.Equals(name, "solidcolor", StringComparison.OrdinalIgnoreCase))
            {
                // SVG 1.2's solid colour paint server, which Inkscape still writes. The element is spelled
                // **lowercase** in the corpus file and XML is case-sensitive, so the name is matched ignoring
                // case - reading only the camel-case spelling would miss the one file that uses it.
                ColorRgb colour = SvgColour.Parse(CascadeValue(element, "solid-color", sheet) ?? "black")
                    ?? ColorRgb.Black;
                double opacity = CascadeNumber(element, "solid-opacity", sheet) ?? 1.0;
                gradients._solids[id] = (colour, Math.Clamp(opacity, 0.0, 1.0));
                continue;
            }

            if (name is not ("linearGradient" or "radialGradient"))
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XAttribute attribute in element.Attributes())
            {
                values[attribute.Name.LocalName] = attribute.Value;
            }

            // A radial gradient may put its **focal point** somewhere other than its centre - `fx`/`fy` - and the
            // model has no focal point, so it is dropped. Dropping it silently turns an off-centre highlight into
            // a centred one, which is a different picture, so it is said.
            if (name == "radialGradient" &&
                (values.ContainsKey("fx") || values.ContainsKey("fy")) &&
                !(string.Equals(values.GetValueOrDefault("fx"), values.GetValueOrDefault("cx"), StringComparison.Ordinal) &&
                  string.Equals(values.GetValueOrDefault("fy"), values.GetValueOrDefault("cy"), StringComparison.Ordinal)))
            {
                warn?.Invoke(
                    $"radial gradient '{id}' has a focal point (fx/fy); the model has no focal point, so the " +
                    "gradient is centred on cx/cy instead");
            }

            AffineTransform transform = SvgReader.Transform(values.GetValueOrDefault("gradientTransform"));

            var stops = new List<GradientStop>();
            foreach (XElement stop in element.Elements().Where(e => e.Name.LocalName == "stop"))
            {
                stops.Add(ReadStop(stop, sheet));
            }

            // Stops are **ordered by offset**, whatever order they were written in. A file may list them any way
            // round and the ramp is still the same; keeping the written order would make a gradient whose stops
            // were out of order render backwards, which looks like the direction is wrong rather than the order.
            stops.Sort((a, b) => a.Position.CompareTo(b.Position));

            gradients._gradients[id] = new RawGradient(
                id,
                name == "radialGradient",
                values.GetValueOrDefault("href") ?? values.GetValueOrDefault("xlink:href"),
                string.Equals(values.GetValueOrDefault("gradientUnits"), "userSpaceOnUse", StringComparison.OrdinalIgnoreCase),
                transform,
                ParseSpread(values.GetValueOrDefault("spreadMethod")),
                values,
                stops);
        }

        return gradients;
    }

    /// <summary>
    /// A property read through the cascade: an important rule, then an inline style, then a rule, then the
    /// attribute. The same order as paint, because these properties are paint.
    /// </summary>
    private static string? CascadeValue(XElement element, string name, SvgStylesheet sheet)
    {
        Dictionary<string, (string Value, bool Important)> declarations =
            sheet.DeclarationsFor(element, element.Ancestors().ToArray());

        bool hasSheet = declarations.TryGetValue(name, out var fromSheet);
        if (hasSheet && fromSheet.Important)
        {
            return fromSheet.Value;
        }

        string? inline = InlineValue(element, name);
        if (inline is not null)
        {
            return inline;
        }

        return hasSheet ? fromSheet.Value : element.Attribute(name)?.Value;
    }

    private static double? CascadeNumber(XElement element, string name, SvgStylesheet sheet)
        => CascadeValue(element, name, sheet) is { } text &&
           double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    /// <summary>
    /// The solid colour a paint-server id names, or null when it is not one.
    ///
    /// Kept separate from <see cref="Resolve"/> because a solid has no geometry: it is a colour with an opacity,
    /// and the shape's box - which every gradient needs - has nothing to do with it.
    /// </summary>
    public (ColorRgb Colour, double Opacity)? SolidFor(string id)
        => _solids.TryGetValue(id, out (ColorRgb Colour, double Opacity) solid) ? solid : null;

    /// <summary>One stop, with its colour and opacity taken through the cascade.
    ///
    /// A stop is styled like anything else: `stop-color` can arrive from a presentation attribute, from an inline
    /// `style`, or from a rule in a stylesheet, and the corpus uses all three. Reading only the attribute gets the
    /// colours of every stylesheet-styled gradient wrong, and it looks like the gradient's direction was wrong.
    /// </summary>
    private static GradientStop ReadStop(XElement stop, SvgStylesheet sheet)
    {
        Dictionary<string, (string Value, bool Important)> declarations =
            sheet.DeclarationsFor(stop, stop.Ancestors().ToArray());

        string? Value(string name)
        {
            bool hasSheet = declarations.TryGetValue(name, out var fromSheet);
            bool sheetIsImportant = hasSheet && fromSheet.Important;
            string? inline = InlineValue(stop, name);

            if (sheetIsImportant && hasSheet)
            {
                return fromSheet.Value;
            }

            if (inline is not null)
            {
                return inline;
            }

            return hasSheet ? fromSheet.Value : stop.Attribute(name)?.Value;
        }

        string? rawOffset = Value("offset");
        double offset = SvgReader.Length(rawOffset) ?? 0.0;
        if (rawOffset?.Trim().EndsWith('%') == true)
        {
            offset /= 100.0;
        }

        ColorRgb colour = SvgColour.Parse(Value("stop-color") ?? "black") ?? ColorRgb.Black;
        double opacity = Value("stop-opacity") is { } text &&
                         double.TryParse(text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? Math.Clamp(parsed, 0.0, 1.0)
            : 1.0;

        return new GradientStop(offset, colour, opacity).Clamped();
    }

    /// <summary>A property from an element's inline `style` attribute, or null.</summary>
    private static string? InlineValue(XElement element, string name)
    {
        string? style = element.Attribute("style")?.Value;
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        foreach (string piece in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            (string property, string value, _) = SvgStylesheet.SplitDeclaration(piece);
            if (property.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static GradientSpread ParseSpread(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "reflect" => GradientSpread.Reflect,
        "repeat" => GradientSpread.Repeat,
        _ => GradientSpread.Pad,
    };

    /// <summary>
    /// The gradient for a shape with this box, or null when there is no such gradient.
    ///
    /// The `href` chain is followed here rather than at collection time, because which attributes are missing is
    /// only known once the whole chain is in hand - and a gradient that carries only stops is the common shape.
    /// </summary>
    public GradientSpec? Resolve(string id, Rect2D box, IReadOnlyList<string>? chain = null)
    {
        if (!_gradients.TryGetValue(id, out RawGradient? raw))
        {
            return null;
        }

        // A gradient that refers to itself, directly or through a chain, stops rather than recursing.
        chain ??= Array.Empty<string>();
        if (chain.Contains(id, StringComparer.Ordinal) || chain.Count > 8)
        {
            return null;
        }

        RawGradient effective = raw;
        if (raw.Href is { Length: > 1 } href && href[0] == '#')
        {
            string target = href[1..];
            if (_gradients.TryGetValue(target, out RawGradient? referenced))
            {
                var deeper = chain.Append(id).ToArray();
                if (Resolve(target, box, deeper) is { } inherited)
                {
                    // The referenced gradient supplies what this one does not say: geometry from the parent,
                    // stops from whichever of the two actually has them.
                    effective = raw with
                    {
                        IsRadial = raw.Values.ContainsKey("cx") ? raw.IsRadial : referenced.IsRadial,
                        Values = referenced.Values
                            .Where(pair => !raw.Values.ContainsKey(pair.Key))
                            .Concat(raw.Values)
                            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                        Stops = raw.Stops.Count > 0 ? raw.Stops : referenced.Stops,
                        UserSpace = raw.Values.ContainsKey("gradientUnits") ? raw.UserSpace : referenced.UserSpace,
                        Transform = raw.Values.ContainsKey("gradientTransform") ? raw.Transform : referenced.Transform,
                    };

                    _ = inherited;
                }
            }
        }

        return ToSpec(effective, box);
    }

    private static GradientSpec? ToSpec(RawGradient raw, Rect2D box)
    {
        if (raw.Stops.Count == 0)
        {
            // A gradient with no stops paints nothing, and a model gradient with no stops would paint its default
            // ramp - which is a colour the file never mentioned.
            return null;
        }

        Dictionary<string, string> v = raw.Values;

        if (raw.IsRadial)
        {
            double cx = Coordinate(v, "cx", 0.5);
            double cy = Coordinate(v, "cy", 0.5);
            double r = Coordinate(v, "r", 0.5);

            Point2D Normalise(double x, double y)
                => raw.UserSpace
                    ? new Point2D(
                        box.Width <= 0 ? 0.5 : (x - box.X) / box.Width,
                        box.Height <= 0 ? 0.5 : (y - box.Y) / box.Height)
                    : new Point2D(x, y);

            Point2D centre = Normalise(cx, cy);
            double radiusX = raw.UserSpace ? (box.Width <= 0 ? r : r / box.Width) : r;
            double radiusY = raw.UserSpace ? (box.Height <= 0 ? r : r / box.Height) : r;

            if (!IsIdentity(raw.Transform))
            {
                // A gradient transform moves the centre and stretches the radii. The model has no matrix of its
                // own, so the effect is composed into the geometry it does have.
                Point2D moved = raw.Transform.Transform(centre);
                Point2D edgeX = raw.Transform.Transform(new Point2D(centre.X + radiusX, centre.Y));
                Point2D edgeY = raw.Transform.Transform(new Point2D(centre.X, centre.Y + radiusY));

                centre = moved;
                radiusX = Math.Sqrt(
                    ((edgeX.X - moved.X) * (edgeX.X - moved.X)) + ((edgeX.Y - moved.Y) * (edgeX.Y - moved.Y)));
                radiusY = Math.Sqrt(
                    ((edgeY.X - moved.X) * (edgeY.X - moved.X)) + ((edgeY.Y - moved.Y) * (edgeY.Y - moved.Y)));
            }

            return new GradientSpec
            {
                Kind = GradientKind.Radial,
                Spread = raw.Spread,
                Stops = raw.Stops,
                Center = centre,
                RadiusX = radiusX,
                RadiusY = radiusY,
            };
        }

        double x1 = Coordinate(v, "x1", 0.0);
        double y1 = Coordinate(v, "y1", 0.0);
        double x2 = Coordinate(v, "x2", 1.0);
        double y2 = Coordinate(v, "y2", 0.0);

        Point2D ToBox(double x, double y) => raw.UserSpace
            ? new Point2D(
                box.Width <= 0 ? 0.0 : (x - box.X) / box.Width,
                box.Height <= 0 ? 0.0 : (y - box.Y) / box.Height)
            : new Point2D(x, y);

        Point2D start = ToBox(x1, y1);
        Point2D end = ToBox(x2, y2);

        if (!IsIdentity(raw.Transform))
        {
            start = raw.Transform.Transform(start);
            end = raw.Transform.Transform(end);
        }

        return new GradientSpec
        {
            Kind = GradientKind.Linear,
            Spread = raw.Spread,
            Stops = raw.Stops,
            Start = start,
            End = end,
        };
    }

    /// <summary>
    /// A coordinate attribute, which may be a fraction or a percentage.
    ///
    /// SVG writes gradient coordinates as either, and both mean the same thing: `50%` and `0.5` are the middle. A
    /// reader that took the number and ignored the sign of a percentage would put every `50%` gradient at the
    /// fifty-times-too-far edge of the shape.
    /// </summary>
    private static double Coordinate(Dictionary<string, string> values, string name, double fallback)
    {
        if (!values.TryGetValue(name, out string? text) || string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string trimmed = text.Trim();
        bool percent = trimmed.EndsWith('%');
        double? value = SvgReader.Length(trimmed);
        if (value is null)
        {
            return fallback;
        }

        return percent ? value.Value / 100.0 : value.Value;
    }

    private static bool IsIdentity(AffineTransform t)
        => Math.Abs(t.A - 1.0) < 1e-12 &&
           Math.Abs(t.B) < 1e-12 &&
           Math.Abs(t.C) < 1e-12 &&
           Math.Abs(t.D - 1.0) < 1e-12 &&
           Math.Abs(t.E) < 1e-12 &&
           Math.Abs(t.F) < 1e-12;
}
