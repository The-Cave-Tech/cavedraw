using System.Globalization;
using System.Text;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>
/// What an SVG export produced: the file, how many elements of each kind reached it, and everything the writer
/// could not express and so left out.
///
/// The counterpart of <see cref="SvgImportResult"/>. An import that meets something it cannot read says so - that
/// is what its own <c>Missing</c> and <c>Warnings</c> are for - and an export that cannot write something owes a
/// caller the same honesty. <see cref="Missing"/> is empty for a document the writer wrote completely, so an empty
/// list means "nothing was lost" rather than "nobody checked".
/// </summary>
public sealed record SvgWriteResult(
    string Svg,
    IReadOnlyDictionary<string, int> ByElement,
    IReadOnlyList<string> Missing);

/// <summary>
/// Writes the model as SVG.
///
/// **The honest boundary.** SVG can carry a simple stroke natively: width, caps, joins, miter limit, dashes and
/// their phase, opacity. It cannot carry a width profile, an effect, or a stroke alignment - so anything it cannot
/// express is written as the **outline** it draws, using the same builder the canvas and the PDF exporter use. The
/// file then looks right in any viewer, even though it is no longer "a stroke" in SVG's terms. What would not be
/// acceptable is dropping it, or substituting a plain stroke, or writing something only this application can read.
///
/// **Coordinates need no flip.** SVG's y grows downward and so does the model's, so the geometry is written as it
/// is and a round trip returns the numbers it started with - which is a stronger and simpler test than comparing
/// pictures.
///
/// **Element kinds are not preserved.** A rectangle is imported as a path and exported as a path: the geometry,
/// the paint and the structure survive exactly, and the element it came from does not. That is a deliberate
/// conversion rather than a loss, and it is what lets one writer handle every shape the reader accepts.
///
/// **What cannot be written is reported, not dropped.** Two things the model holds have no element this writer
/// emits - a text block and an embedded raster - and both are left out of the file and named, with their kind and
/// the reason, in <see cref="SvgWriteResult.Missing"/>. The rule this repository runs on is that a gap is said
/// rather than silently skipped, and an exporter is the one place where being quiet costs somebody a document: a
/// person opens the file and the words are simply not there. The list is empty for a document the writer wrote
/// completely, so "nothing was lost" is a fact the caller can rely on rather than a default it has to trust.
/// </summary>
public static class SvgWriter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    /// <summary>Writes the document as SVG.</summary>
    public static string Write(CadDocument document) => Write(document, page: null);

    /// <summary>Writes one artboard of the document, or all of it when <paramref name="page"/> is null.</summary>
    public static string Write(CadDocument document, int? page) => WriteResult(document, page).Svg;

    /// <summary>
    /// Writes the document as SVG **and reports what could not be written**.
    ///
    /// The file and the report come out together because a caller that wants one nearly always needs the other:
    /// a file handed back with no word about what is missing from it is the silent drop this writer exists to
    /// avoid, and a report that fires on a document it wrote completely is noise a driver learns to ignore.
    ///
    /// One page is written with **its own** view box and its content at the origin, so exporting a single page
    /// produces a file a person can use rather than a canvas with everything else cropped out of it.
    /// </summary>
    public static SvgWriteResult WriteResult(CadDocument document, int? page = null)
    {
        bool single = page is not null && page >= 0 && page < document.Artboards.Count;
        var root = new XElement(Svg + "svg", new XAttribute("version", "1.1"));

        // The canvas is the union of the artboards, so a multi-page document is written in one file with each page
        // in its place rather than the pages stacked on top of each other.
        Artboard? singlePage = single ? document.Artboards[page!.Value] : null;
        Rect2D extent = singlePage is null
            ? Extent(document)
            : new Rect2D(singlePage.X, singlePage.Y, singlePage.Width, singlePage.Height);
        // The model's extent is in points, so the unit is stated rather than left to the reader: a bare number is
        // 0.75pt in the corrected reader, and the three-way round trip has to stay the identity.
        root.Add(new XAttribute("width", Number(extent.Width) + "pt"));
        root.Add(new XAttribute("height", Number(extent.Height) + "pt"));
        root.Add(new XAttribute("viewBox",
            $"{Number(extent.X)} {Number(extent.Y)} {Number(extent.Width)} {Number(extent.Height)}"));

        var writer = new Writer(root, document.SvgNamespaces);

        // The prefixes the file declared, declared again - so every namespaced attribute written below uses the
        // name it had rather than a generated one.
        foreach (KeyValuePair<string, string> entry in document.SvgNamespaces)
        {
            root.SetAttributeValue(XNamespace.Xmlns + entry.Key, entry.Value);
        }

        // The root-level elements that are not artwork: the named view, the RDF. Written back verbatim, because a
        // file that loses them resets the document's own settings when it is opened again.
        foreach (string extra in document.SvgExtras)
        {
            try
            {
                root.Add(XElement.Parse(extra));
            }
            catch (System.Xml.XmlException)
            {
                // Unreadable XML is not worth failing an export over; the artwork is the part that has to survive.
            }
        }

        // Gradients first, because a path refers to them by id and a definition may come after its use.
        writer.WriteGradients(document);
        writer.WriteFilters(document);
        writer.WritePathEffects(document);

        // **One artboard at the origin is written without a wrapper group.** A group would be structure the document
        // does not have, and the reader would faithfully turn it back into one - so the model would gain a level on
        // every round trip. A group appears only when the content really is offset, which is what a second artboard
        // or a page away from the origin needs.
        if (singlePage is not null)
        {
            foreach (Layer layer in singlePage.Layers)
            {
                writer.WriteItems(layer.Children, root);
            }
        }
        else if (document.Artboards.Count == 1 &&
            document.Artboards[0].X == 0 && document.Artboards[0].Y == 0)
        {
            foreach (Layer layer in document.Artboards[0].Layers)
            {
                writer.WriteItems(layer.Children, root);
            }
        }
        else
        {
            foreach (Artboard artboard in document.Artboards)
            {
                writer.WriteArtboard(artboard);
            }
        }

        if (document.Orphans.Children.Count > 0)
        {
            var orphans = new XElement(Svg + "g", new XAttribute("id", "pasteboard"));
            writer.WriteItems(document.Orphans.Children, orphans);
            root.Add(orphans);
        }

        var xml = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        return new SvgWriteResult(xml.ToString(), writer.ByElement, writer.Missing);
    }

    private static Rect2D Extent(CadDocument document)
    {
        if (document.Artboards.Count == 0)
        {
            return new Rect2D(0, 0, 1, 1);
        }

        Rect2D first = new(document.Artboards[0].X, document.Artboards[0].Y,
            document.Artboards[0].Width, document.Artboards[0].Height);

        Rect2D extent = first;
        foreach (Artboard artboard in document.Artboards)
        {
            extent = extent.Union(new Rect2D(artboard.X, artboard.Y, artboard.Width, artboard.Height));
        }

        return extent.Width <= 0 || extent.Height <= 0 ? first : extent;
    }

    /// <summary>Holds the counters that name the definitions, so every id in one file is unique.</summary>
    private sealed class Writer
    {
        private readonly XElement _root;
        private readonly Dictionary<GradientSpec, string> _gradientIds = new(GradientKey.Instance);
        private readonly IReadOnlyDictionary<string, string> _namespaces;
        private readonly List<string> _missing = new();
        private readonly Dictionary<string, int> _written = new(StringComparer.Ordinal);
        private int _gradientCount;
        private int _clipCount;

        public Writer(XElement root, IReadOnlyDictionary<string, string>? namespaces = null)
        {
            _root = root;
            _namespaces = namespaces ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>Every item the writer could not express, in document order; empty when it wrote everything.</summary>
        public IReadOnlyList<string> Missing => _missing;

        /// <summary>How many SVG elements of each name the writer emitted, so a caller sees what the file holds.</summary>
        public IReadOnlyDictionary<string, int> ByElement => _written;

        /// <summary>Counts one written element, by its SVG name.</summary>
        private void Wrote(string element)
            => _written[element] = _written.TryGetValue(element, out int count) ? count + 1 : 1;

        /// <summary>
        /// Names an item that did not reach the file, with enough of its own identity to be found: what kind it is,
        /// what it is called or where it sits, and why it was left out.
        ///
        /// A report that only said "some text was dropped" would be true and useless - the whole point is that the
        /// person or driver reading it can tell **which** object the file is missing.
        /// </summary>
        private void Report(LayerItem item, string reason) => _missing.Add($"{Kind(item)} {Identity(item)}: {reason}");

        /// <summary>The word a person would use for the item, which is what the importer's own report speaks in.</summary>
        private static string Kind(LayerItem item) => item switch
        {
            TextItem => "text",
            ImageItem => "image",
            ArtGroup => "group",
            PathItem => "path",
            _ => item.GetType().Name,
        };

        /// <summary>The item's name, or where it is when it has none, plus the size that makes it recognisable.</summary>
        private static string Identity(LayerItem item)
        {
            string name = string.IsNullOrEmpty(item.Name) ? "(unnamed)" : $"'{item.Name}'";
            return item switch
            {
                TextItem text =>
                    $"{name} at {Number(text.Origin.X)},{Number(text.Origin.Y)} ({text.Runs.Count} run(s))",
                ImageItem image =>
                    $"{name} at {Number(image.Placement.X)},{Number(image.Placement.Y)} " +
                    $"({image.PixelWidth}x{image.PixelHeight} px)",
                _ => name,
            };
        }

        /// <summary>
        /// Puts back the namespaced attributes the file carried, and the label that is the item's name.
        ///
        /// The label is written from the **name**, not from the stored attribute: it is the same fact, and writing
        /// both would leave a renamed object disagreeing with itself.
        /// </summary>
        private void ApplyForeign(XElement element, LayerItem item)
        {
            foreach (KeyValuePair<string, string> attribute in item.ForeignAttributes)
            {
                int colon = attribute.Key.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string prefix = attribute.Key[..colon];
                string local = attribute.Key[(colon + 1)..];
                if (!_namespaces.TryGetValue(prefix, out string? uri))
                {
                    continue;
                }

                element.SetAttributeValue(XName.Get(local, uri), attribute.Value);
            }

            // A child the model does not understand goes back under the element it came from, before the label so
            // the written form keeps the file's own ordering.
            foreach (string foreign in item.ForeignElements)
            {
                if (ForeignChild(foreign) is not { } child)
                {
                    continue;
                }

                // A live path effect is a **definition** rather than a property of the element it was found on.
                // It is written into `defs` instead - see WritePathEffects - so that the file that comes back has
                // it where Inkscape wrote it rather than nested inside the path that refers to it by id.
                if (IsPathEffect(child))
                {
                    continue;
                }

                element.Add(child);
            }

            // The blend mode goes out as the CSS property the file uses, and only when it is not the default -
            // a document that never set one has to export byte for byte as it did before this existed.
            if (item.BlendMode != BlendMode.Normal)
            {
                element.SetAttributeValue("mix-blend-mode", item.BlendMode.ToSvgName());
            }

            if (!string.IsNullOrEmpty(item.Name) && _namespaces.ContainsKey("inkscape"))
            {
                element.SetAttributeValue(
                    XName.Get("label", _namespaces["inkscape"]), item.Name);
            }
        }

        /// <summary>
        /// Every gradient the document uses, written once each, in `defs`.
        ///
        /// Deduplicated by value rather than by instance: a document where twenty paths share one gradient should
        /// write one definition, and the same <see cref="GradientSpec"/> reached by two paths is the same gradient.
        /// </summary>
        public void WriteGradients(CadDocument document)
        {
            var defs = new XElement(Svg + "defs");

            foreach (PathItem path in document.AllPaths())
            {
                if (path.Fill.Gradient is { } gradient)
                {
                    WriteGradient(defs, gradient);
                }
            }

            if (defs.HasElements)
            {
                _root.Add(defs);
            }
        }

        /// <summary>
        /// A stored foreign child as an element, or null when it is not readable XML.
        ///
        /// Unreadable XML is not worth failing an export over - the artwork is the part that has to survive - and
        /// both callers here need to look at the element before deciding where it goes.
        /// </summary>
        private static XElement? ForeignChild(string xml)
        {
            try
            {
                return XElement.Parse(xml);
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether a foreign child is a live path effect, which is a definition and so belongs in `defs`.
        ///
        /// Matched on the **name**, not on the namespace, because Inkscape has written this element under more than
        /// one inkscape namespace over the years and a definition that stopped being recognised would silently
        /// come back as a child of the path instead.
        /// </summary>
        private static bool IsPathEffect(XElement child)
            => child.Name.LocalName == "path-effect" && child.Name.Namespace != XNamespace.None;

        /// <summary>
        /// Every live path effect the document carries, written back into `defs`.
        ///
        /// **Put back where Inkscape wrote it.** The element was found in `defs` and is written into `defs` again,
        /// rather than under the path that refers to it: the reference is an id, and the id has to lead somewhere
        /// the next reader will look.
        ///
        /// **Written verbatim.** Every attribute, spelled and ordered as the file spelled it. Inkscape decides what
        /// an effect means from its own `lpeversion` and a parameter set this build does not model, so rebuilding
        /// the element from the translated stroke would drop exactly the half that says how to shape it.
        ///
        /// **Written once per id**: two paths may share one effect, and a file with the same id twice is one that
        /// Inkscape resolves in whichever order it happens to walk the tree.
        ///
        /// **A definition nothing refers to is written too**, from the document-level home it was kept in - so a
        /// file that carries a library of named effects hands that library back rather than the entries something
        /// happens to point at (issue #155). The referenced definitions are collected first, so an id claimed by a
        /// path is written once and from the path it belongs to, and a document-level entry under the same id is
        /// the same definition written the same way rather than a second copy.
        /// </summary>
        public void WritePathEffects(CadDocument document)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var elements = new List<XElement>();

            foreach (LayerItem item in document.AllItems())
            {
                foreach (string foreign in item.ForeignElements)
                {
                    if (ForeignChild(foreign) is not { } child || !IsPathEffect(child))
                    {
                        continue;
                    }

                    if (child.Attribute("id")?.Value is { Length: > 0 } id && !ids.Add(id))
                    {
                        continue;
                    }

                    elements.Add(child);
                }
            }

            foreach (string foreign in document.ForeignPathEffects)
            {
                if (ForeignChild(foreign) is not { } child || !IsPathEffect(child))
                {
                    continue;
                }

                if (child.Attribute("id")?.Value is { Length: > 0 } id && !ids.Add(id))
                {
                    continue;
                }

                elements.Add(child);
            }

            if (elements.Count == 0)
            {
                return;
            }

            XElement defs = _root.Element(Svg + "defs") ?? new XElement(Svg + "defs");
            foreach (XElement element in elements)
            {
                defs.Add(element);
            }

            if (defs.Parent is null)
            {
                _root.Add(defs);
            }
        }

        /// <summary>
        /// Every filter the document holds, written into `defs`.
        ///
        /// A filter is a document asset referred to by id, so it is written once and pointed at - and the wiring is
        /// written back exactly as it was read, because the graph **is** the filter rather than an implementation
        /// detail of it.
        /// </summary>
        public void WriteFilters(CadDocument document)
        {
            if (document.Filters.Count == 0)
            {
                return;
            }

            XElement defs = _root.Element(Svg + "defs") ?? new XElement(Svg + "defs");

            foreach (FilterSpec filter in document.Filters)
            {
                var element = new XElement(Svg + "filter", new XAttribute("id", filter.Name));

                // Only when they differ from SVG's own defaults, so an ordinary filter is not buried in numbers
                // that say what a viewer already assumes.
                if (!filter.ObjectBoundingBox)
                {
                    element.Add(new XAttribute("filterUnits", "userSpaceOnUse"));
                }

                // `primitiveUnits` is a different question from `filterUnits` - what a step's own lengths mean
                // rather than where the region is - so it is written from its own member, and only when it is not
                // SVG's default either.
                if (filter.PrimitiveUnitsObjectBoundingBox)
                {
                    element.Add(new XAttribute("primitiveUnits", "objectBoundingBox"));
                }

                // The resolution the filter is sampled at, which is part of the picture. One number means both
                // axes, which is how a file that gives one is written back.
                if (filter.HasFilterResolution)
                {
                    element.Add(new XAttribute(
                        "filterRes",
                        filter.FilterResolutionX == filter.FilterResolutionY
                            ? Number(filter.FilterResolutionX!.Value)
                            : $"{Number(filter.FilterResolutionX!.Value)} {Number(filter.FilterResolutionY!.Value)}"));
                }

                foreach ((string name, double value, double expected) in new[]
                         {
                             ("x", filter.X, -0.1),
                             ("y", filter.Y, -0.1),
                             ("width", filter.Width, 1.2),
                             ("height", filter.Height, 1.2),
                         })
                {
                    if (Math.Abs(value - expected) > 1e-9)
                    {
                        element.Add(new XAttribute(name, Number(value)));
                    }
                }

                // Which buffer the filter **answers with**, when it is not the last step's. SVG has no attribute for
                // this - a viewer always takes the last primitive - but the model carries it, the reader reads it
                // from here, and a round trip that dropped it would hand back a graph whose last step is not the
                // one it was drawn from: a different picture, with nothing in the file to say so.
                if (filter.Output.Length > 0)
                {
                    element.Add(new XAttribute("result", filter.Output));
                }

                foreach (FilterPrimitive primitive in filter.Primitives)
                {
                    element.Add(PrimitiveElement(primitive));
                }

                defs.Add(element);
            }

            if (defs.Parent is null)
            {
                _root.Add(defs);
            }
        }

        /// <summary>One primitive, with its wiring and its parameters.</summary>
        private static XElement PrimitiveElement(FilterPrimitive primitive)
        {
            var element = new XElement(Svg + primitive.Kind switch
            {
                FilterPrimitiveKind.GaussianBlur => "feGaussianBlur",
                FilterPrimitiveKind.Offset => "feOffset",
                FilterPrimitiveKind.Flood => "feFlood",
                FilterPrimitiveKind.Composite => "feComposite",
                FilterPrimitiveKind.Blend => "feBlend",
                FilterPrimitiveKind.Morphology => "feMorphology",
                FilterPrimitiveKind.ColorMatrix => "feColorMatrix",
                FilterPrimitiveKind.DisplacementMap => "feDisplacementMap",
                FilterPrimitiveKind.Turbulence => "feTurbulence",
                FilterPrimitiveKind.SpecularLighting => "feSpecularLighting",
                _ => "feDiffuseLighting",
            });

            if (!string.IsNullOrEmpty(primitive.Input))
            {
                element.Add(new XAttribute("in", primitive.Input));
            }

            if (!string.IsNullOrEmpty(primitive.Input2))
            {
                element.Add(new XAttribute("in2", primitive.Input2));
            }

            if (!string.IsNullOrEmpty(primitive.Result))
            {
                element.Add(new XAttribute("result", primitive.Result));
            }

            switch (primitive.Kind)
            {
                case FilterPrimitiveKind.GaussianBlur:
                    element.Add(new XAttribute("stdDeviation", Number(primitive.Radius)));
                    break;

                case FilterPrimitiveKind.Offset:
                    element.Add(new XAttribute("dx", Number(primitive.Dx)));
                    element.Add(new XAttribute("dy", Number(primitive.Dy)));
                    break;

                case FilterPrimitiveKind.Flood:
                    element.Add(new XAttribute("flood-color", Hex(primitive.FloodColor ?? ColorRgb.Black)));
                    if (primitive.FloodOpacity < 1.0)
                    {
                        element.Add(new XAttribute("flood-opacity", Number(primitive.FloodOpacity)));
                    }

                    break;

                case FilterPrimitiveKind.Composite:
                    element.Add(new XAttribute("operator", primitive.Operator));
                    break;

                case FilterPrimitiveKind.Blend:
                    element.Add(new XAttribute("mode", primitive.Mode));
                    break;

                case FilterPrimitiveKind.Morphology:
                    element.Add(new XAttribute("operator", primitive.Operator));
                    element.Add(new XAttribute("radius", Number(primitive.Radius)));
                    break;

                case FilterPrimitiveKind.ColorMatrix:
                    WriteColourMatrix(element, primitive);
                    break;

                case FilterPrimitiveKind.DisplacementMap:
                    element.Add(new XAttribute("scale", Number(primitive.Scale)));
                    element.Add(new XAttribute("xChannelSelector", primitive.XChannel));
                    element.Add(new XAttribute("yChannelSelector", primitive.YChannel));
                    break;

                case FilterPrimitiveKind.Turbulence:
                    element.Add(new XAttribute("type", primitive.Type));
                    element.Add(new XAttribute("baseFrequency", Number(primitive.BaseFrequency)));
                    element.Add(new XAttribute("numOctaves", primitive.Octaves));
                    element.Add(new XAttribute("seed", primitive.Seed));
                    break;

                case FilterPrimitiveKind.SpecularLighting:
                    element.Add(new XAttribute("surfaceScale", Number(primitive.SurfaceScale)));
                    element.Add(new XAttribute("specularConstant", Number(primitive.SpecularConstant)));
                    element.Add(new XAttribute("specularExponent", Number(primitive.SpecularExponent)));
                    element.Add(new XAttribute("lighting-color", Hex(primitive.LightingColor ?? ColorRgb.White)));
                    LightSource(element, primitive);
                    break;

                default:
                    element.Add(new XAttribute("surfaceScale", Number(primitive.SurfaceScale)));
                    element.Add(new XAttribute("diffuseConstant", Number(primitive.DiffuseConstant)));
                    element.Add(new XAttribute("lighting-color", Hex(primitive.LightingColor ?? ColorRgb.White)));
                    LightSource(element, primitive);
                    break;
            }

            return element;
        }

        /// <summary>
        /// A colour matrix, written back in the spelling it was read in.
        ///
        /// A shorthand is written as the shorthand and its single number - `type="saturate" values="0"` - rather
        /// than as the twenty-number matrix it was expanded into, because the two spellings draw the same picture
        /// and the one that was in the file is the one a person will recognise. Anything else is written as the
        /// matrix it is.
        /// </summary>
        private static void WriteColourMatrix(XElement element, FilterPrimitive primitive)
        {
            string type = (primitive.Type ?? "matrix").Trim();
            string lower = type.ToLowerInvariant();

            if (lower == "saturate" || lower == "huerotate")
            {
                element.Add(new XAttribute("type", type));
                element.Add(new XAttribute("values", Number(SingleValue(primitive))));
                return;
            }

            if (lower == "luminancetoalpha")
            {
                element.Add(new XAttribute("type", "luminanceToAlpha"));
                return;
            }

            element.Add(new XAttribute("type", "matrix"));
            element.Add(new XAttribute(
                "values",
                string.Join(" ", (primitive.Matrix ?? FilterPrimitive.IdentityMatrix).Select(Number))));
        }

        /// <summary>
        /// The one number a colour matrix shorthand carries: the saturation, or the angle.
        ///
        /// A primitive built through the model's factory may hold either the one number or the twenty it stands
        /// for - the reader keeps the number, a caller may hand over the matrix - so this recognises the expansion
        /// and recovers the number from it. That is what makes `saturate` written by a caller and `saturate` read
        /// from a file come out of here spelled the same way, and it is why the twenty numbers are inverted rather
        /// than the shorthand being written out as a matrix.
        /// </summary>
        private static double SingleValue(FilterPrimitive primitive)
        {
            if (primitive.Matrix is { Length: 1 } only)
            {
                return only[0];
            }

            string type = (primitive.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (primitive.Matrix is { Length: 20 } full)
            {
                // SVG's saturate matrix is `(1 - s) * L + s * I`, so the first cell is `s + (1 - s) * 0.2125`.
                if (type == "saturate")
                {
                    return (full[0] - 0.2125) / 0.7875;
                }

                // And the hue-rotation matrix's first two cells are `cos * 0.787 - sin * 0.213` and
                // `0.715 - cos * 0.715 - sin * 0.715`, which recover the angle.
                if (type == "huerotate")
                {
                    double cosine = (full[0] - 0.2125) / 0.787;
                    double sine = -(full[1] - 0.7154) / 0.7154;
                    return Math.Atan2(sine, Math.Abs(cosine) < 1e-12 ? 1.0 : cosine) * 180.0 / Math.PI;
                }
            }

            return type == "saturate" ? 1.0 : 0.0;
        }

        /// <summary>
        /// The light a lighting primitive is lit by.
        ///
        /// Always a `feDistantLight`, because that is the only one the reader accepts - a primitive in the model can
        /// only have come from a distant light, so writing any other kind would be inventing a file this build
        /// cannot read back.
        /// </summary>
        private static void LightSource(XElement element, FilterPrimitive primitive)
            => element.Add(new XElement(
                Svg + "feDistantLight",
                new XAttribute("azimuth", Number(primitive.Azimuth)),
                new XAttribute("elevation", Number(primitive.Elevation))));

        private string WriteGradient(XElement defs, GradientSpec gradient)
        {
            if (_gradientIds.TryGetValue(gradient, out string? existing))
            {
                return existing;
            }

            string id = $"grad{++_gradientCount}";
            _gradientIds[gradient] = id;

            var element = new XElement(Svg + (gradient.Kind == GradientKind.Radial ? "radialGradient" : "linearGradient"));
            element.Add(new XAttribute("id", id));

            // The model stores normalised geometry, which is SVG's default unit mode - so this is written as it is
            // rather than converted, and the round trip returns the same numbers.
            element.Add(new XAttribute("gradientUnits", "objectBoundingBox"));

            string spread = gradient.Spread switch
            {
                GradientSpread.Reflect => "reflect",
                GradientSpread.Repeat => "repeat",
                _ => "pad",
            };
            element.Add(new XAttribute("spreadMethod", spread));

            if (gradient.Kind == GradientKind.Radial)
            {
                element.Add(new XAttribute("cx", Number(gradient.Center.X)));
                element.Add(new XAttribute("cy", Number(gradient.Center.Y)));
                element.Add(new XAttribute("r", Number(Math.Max(gradient.RadiusX, gradient.RadiusY))));

                // Written only when it is somewhere other than the centre. `fx`/`fy` default to `cx`/`cy`, so
                // spelling out a centred focus would put a coordinate in the file that the document does not
                // have - and a document that names no focus would grow two attributes on every export.
                if (gradient.FocalPoint is { } focus &&
                    (focus.X != gradient.Center.X || focus.Y != gradient.Center.Y))
                {
                    element.Add(new XAttribute("fx", Number(focus.X)));
                    element.Add(new XAttribute("fy", Number(focus.Y)));
                }
            }
            else
            {
                element.Add(new XAttribute("x1", Number(gradient.Start.X)));
                element.Add(new XAttribute("y1", Number(gradient.Start.Y)));
                element.Add(new XAttribute("x2", Number(gradient.End.X)));
                element.Add(new XAttribute("y2", Number(gradient.End.Y)));
            }

            foreach (GradientStop stop in gradient.Stops)
            {
                var stopElement = new XElement(Svg + "stop",
                    new XAttribute("offset", Number(stop.Position)),
                    new XAttribute("stop-color", Hex(stop.Color)));

                if (stop.Opacity < 1.0)
                {
                    stopElement.Add(new XAttribute("stop-opacity", Number(stop.Opacity)));
                }

                element.Add(stopElement);
            }

            defs.Add(element);
            return id;
        }

        public void WriteArtboard(Artboard artboard)
        {
            var group = new XElement(Svg + "g", new XAttribute("id", artboard.Id.ToString()));
            if (!string.IsNullOrEmpty(artboard.Name))
            {
                group.Add(new XAttribute("data-name", artboard.Name));
            }

            if (artboard.X != 0 || artboard.Y != 0)
            {
                group.Add(new XAttribute("transform", $"translate({Number(artboard.X)},{Number(artboard.Y)})"));
            }

            foreach (Layer layer in artboard.Layers)
            {
                WriteItems(layer.Children, group);
            }

            Wrote("g");
            _root.Add(group);
        }

        public void WriteItems(IEnumerable<LayerItem> items, XElement parent)
        {
            foreach (LayerItem item in items)
            {
                switch (item)
                {
                    case ArtGroup group:
                    {
                        var element = new XElement(Svg + "g");
                        ApplyForeign(element, group);
                        if (!string.IsNullOrEmpty(group.Name))
                        {
                            element.Add(new XAttribute("id", group.Name));
                        }

                        string transform = TransformAttribute(group.Transform);
                        if (transform.Length > 0)
                        {
                            element.Add(new XAttribute("transform", transform));
                        }

                        if (group.Opacity < 1.0)
                        {
                            element.Add(new XAttribute("opacity", Number(group.Opacity)));
                        }

                        // A group can be filtered too, and a filter that vanished on export would leave whatever
                        // it was softening drawn hard.
                        if (!string.IsNullOrEmpty(group.FilterId))
                        {
                            element.Add(new XAttribute("filter", $"url(#{group.FilterId})"));
                        }

                        if (!group.IsVisible)
                        {
                            element.Add(new XAttribute("display", "none"));
                        }

                        // An instance says what it is an instance of, so the link survives the round trip even
                        // though SVG would express it as a `use`.
                        if (group.SourceId is { Length: > 0 } source)
                        {
                            element.Add(new XAttribute("data-source", source));
                        }

                        WriteItems(group.Children, element);
                        Wrote("g");
                        parent.Add(element);
                        break;
                    }

                    case PathItem path:
                        WritePath(path, parent);
                        break;

                    // Neither of these has an element this writer emits, and both are content the document holds
                    // and a person would see on the canvas. Writing either one badly would be worse than not
                    // writing it - a wrong `text` element is a file that lies about the layout - so they are left
                    // out and **named**, which is the half that stops the omission being silent. Writing them
                    // properly is #132's work and turns these reports back into output.
                    case TextItem text:
                        Report(text, "the writer has no SVG text output, so the block is not in the file");
                        break;

                    case ImageItem image:
                        Report(image, "the writer has no SVG image output, so the raster is not in the file");
                        break;

                    // Anything the model grows later lands here rather than falling out of the switch: the one
                    // thing this writer must never do is drop an object without saying so.
                    default:
                        Report(item, $"the writer has no SVG output for a {item.GetType().Name}");
                        break;
                }
            }
        }

        /// <summary>
        /// One path, as one SVG element per stroke plus the fill.
        ///
        /// **A path with several strokes becomes several elements.** SVG gives an element one stroke, so the stack
        /// is written in order - bottom first - which is what makes the file draw the same picture the canvas does.
        /// The alternative, keeping one element and losing the rest of the stack, is the silent drop this exporter
        /// exists to avoid.
        /// </summary>
        private void WritePath(PathItem path, XElement parent)
        {
            string data = PathData(path);
            if (data.Length == 0)
            {
                return;
            }

            string? fill = FillAttribute(path);
            string fillRule = path.Fill.Rule == FillRule.EvenOdd ? "evenodd" : "nonzero";

            var strokes = path.Strokes.Where(s => s.HasVisibleOutline).ToList();
            bool plain = strokes.Count <= 1 && strokes.All(Native);

            // **One element when there is one plain stroke**, with the fill and the stroke on it - because that is
            // what a viewer draws and what the reader reads back. Splitting the fill and the stroke into two
            // elements would double the number of paths on every round trip, and a document that gains a path each
            // time it is saved is not one anybody can work with.
            if (plain)
            {
                var element = new XElement(Svg + "path", new XAttribute("d", data));
                if (!string.IsNullOrEmpty(path.Name))
                {
                    element.Add(new XAttribute("id", path.Name));
                }

                element.Add(new XAttribute("fill", fill ?? "none"));
                if (fill is not null)
                {
                    element.Add(new XAttribute("fill-rule", fillRule));
                }

                if (path.Opacity < 1.0)
                {
                    element.Add(new XAttribute("opacity", Number(path.Opacity)));
                }

                if (!string.IsNullOrEmpty(path.FilterId))
                {
                    element.Add(new XAttribute("filter", $"url(#{path.FilterId})"));
                }

                if (strokes.Count == 1)
                {
                    WriteNativeStrokeAttributes(element, strokes[0]);
                }
                else
                {
                    ApplyForeign(element, path);
                element.Add(new XAttribute("stroke", "none"));
                }

                parent.Add(element);
                Wrote("path");
                return;
            }

            // Otherwise the stack is written as one element per stroke, in order, which is what makes the file draw
            // the picture the canvas does. That a multi-stroke path gains a level of structure when it comes back is
            // a documented conversion: SVG gives an element one stroke, and the alternative is dropping the rest.
            if (path.Fill.IsVisible)
            {
                var element = new XElement(Svg + "path", new XAttribute("d", data));
                if (!string.IsNullOrEmpty(path.Name))
                {
                    element.Add(new XAttribute("id", path.Name));
                }

                element.Add(new XAttribute("fill", fill ?? "none"));
                if (fill is not null)
                {
                    element.Add(new XAttribute("fill-rule", fillRule));
                }

                ApplyForeign(element, path);
                element.Add(new XAttribute("stroke", "none"));
                parent.Add(element);
                Wrote("path");
            }

            foreach (StrokeSpec stroke in strokes)
            {
                WriteStroke(path, stroke, data, parent);
            }
        }

        /// <summary>Whether SVG can carry this stroke as a stroke rather than as an outline.</summary>
        private static bool Native(StrokeSpec stroke)
            => !stroke.HasWidthProfile && !stroke.HasEffects && stroke.Alignment == StrokeAlignment.Center;

        /// <summary>The attributes of a stroke SVG can carry natively.</summary>
        private static void WriteNativeStrokeAttributes(XElement element, StrokeSpec stroke)
        {
            element.Add(new XAttribute("stroke", Hex(stroke.Color)));

            if (stroke.Color.A < 1.0)
            {
                element.Add(new XAttribute("stroke-opacity", Number(stroke.Color.A)));
            }

            element.Add(new XAttribute("stroke-width", Number(stroke.Width)));
            element.Add(new XAttribute("stroke-linecap", stroke.Cap switch
            {
                StrokeCap.Round => "round",
                StrokeCap.Square => "square",
                _ => "butt",
            }));

            element.Add(new XAttribute("stroke-linejoin", stroke.Join switch
            {
                StrokeJoin.Round => "round",
                StrokeJoin.Bevel => "bevel",
                _ => "miter",
            }));

            // Written whenever it differs from the default, **not** only for a miter join. A miter limit has no
            // effect on a bevel, so omitting it there looks harmless - and it silently changes the value a round
            // trip returns, which is a fidelity loss whether or not it changes the picture.
            if (Math.Abs(stroke.MiterLimit - 4.0) > 1e-9)
            {
                element.Add(new XAttribute("stroke-miterlimit", Number(stroke.MiterLimit)));
            }

            if (!stroke.Dash.IsEmpty)
            {
                element.Add(new XAttribute("stroke-dasharray",
                    string.Join(" ", stroke.Dash.Segments.Select(Number))));

                // The phase is what makes a dashed line start where it was drawn rather than at the beginning of
                // the pattern, and it is the member most likely to be left behind.
                if (Math.Abs(stroke.Dash.Offset) > 1e-9)
                {
                    element.Add(new XAttribute("stroke-dashoffset", Number(stroke.Dash.Offset)));
                }
            }
        }

        /// <summary>
        /// One stroke: natively when SVG can carry it, as an outline when it cannot.
        ///
        /// The three cases that have to become outlines are a **width profile**, any **outline effect**, and an
        /// **alignment** other than centre. Each is a thing SVG has no attribute for, and each is drawn the same
        /// way the renderers draw it, so the file and the canvas agree.
        /// </summary>
        private void WriteStroke(PathItem path, StrokeSpec stroke, string data, XElement parent)
        {
            var element = new XElement(Svg + "path");
            if (!string.IsNullOrEmpty(path.Name))
            {
                element.Add(new XAttribute("id", path.Name));
            }

            ApplyForeign(element, path);
            element.Add(new XAttribute("fill", "none"));

            if (stroke.HasWidthProfile || stroke.HasEffects)
            {
                StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, stroke);
                string outline = Outlines(plan.Outlines);
                if (outline.Length == 0)
                {
                    return;
                }

                element.SetAttributeValue("d", outline);
                element.SetAttributeValue("fill", Hex(stroke.Color));
                element.SetAttributeValue("fill-rule", "nonzero");

                if (stroke.Color.A < 1.0)
                {
                    element.SetAttributeValue("fill-opacity", Number(stroke.Color.A));
                }

                parent.Add(element);
                Wrote("path");
                return;
            }

            element.Add(new XAttribute("d", data));
            element.Add(new XAttribute("stroke", Hex(stroke.Color)));

            if (stroke.Color.A < 1.0)
            {
                element.Add(new XAttribute("stroke-opacity", Number(stroke.Color.A)));
            }

            element.Add(new XAttribute("stroke-width", Number(stroke.Width)));
            element.Add(new XAttribute("stroke-linecap", stroke.Cap switch
            {
                StrokeCap.Round => "round",
                StrokeCap.Square => "square",
                _ => "butt",
            }));

            element.Add(new XAttribute("stroke-linejoin", stroke.Join switch
            {
                StrokeJoin.Round => "round",
                StrokeJoin.Bevel => "bevel",
                _ => "miter",
            }));

            // Written whenever it differs from the default, **not** only for a miter join. A miter limit has no
            // effect on a bevel, so omitting it there looks harmless - and it silently changes the value a round
            // trip returns, which is a fidelity loss whether or not it changes the picture.
            if (Math.Abs(stroke.MiterLimit - 4.0) > 1e-9)
            {
                element.Add(new XAttribute("stroke-miterlimit", Number(stroke.MiterLimit)));
            }

            if (!stroke.Dash.IsEmpty)
            {
                element.Add(new XAttribute("stroke-dasharray",
                    string.Join(" ", stroke.Dash.Segments.Select(Number))));

                // The phase is what makes a dashed line start where it was drawn rather than at the beginning of
                // the pattern, and it is the member most likely to be left behind.
                if (Math.Abs(stroke.Dash.Offset) > 1e-9)
                {
                    element.Add(new XAttribute("stroke-dashoffset", Number(stroke.Dash.Offset)));
                }
            }

            if (stroke.Alignment != StrokeAlignment.Center)
            {
                // SVG has no aligned stroke, so it is drawn as an outline at double width and clipped to the side
                // it belongs on - the same thing the PDF exporter does with a clipping path.
                string clip = WriteAlignmentClip(path, stroke.Alignment);
                element.SetAttributeValue("stroke-width", Number(stroke.Width * 2));
                element.SetAttributeValue("clip-path", $"url(#{clip})");
            }

            parent.Add(element);
            Wrote("path");
        }

        /// <summary>
        /// A clip path that keeps only the half of a double-width stroke the alignment asks for.
        ///
        /// Inside keeps the shape; outside keeps everything except it, which is one clip path with the shape and a
        /// rectangle wound the other way and the even-odd rule - the same construction the PDF exporter uses.
        /// </summary>
        private string WriteAlignmentClip(PathItem path, StrokeAlignment alignment)
        {
            string id = $"clip{++_clipCount}";
            string data = PathData(path);

            var clip = new XElement(Svg + "clipPath", new XAttribute("id", id));
            var shape = new XElement(Svg + "path", new XAttribute("d", data));
            clip.Add(shape);

            if (alignment == StrokeAlignment.Outside)
            {
                Rect2D box = path.BoundingBox().Inflated(Math.Max(path.Stroke.Width * 4, 100));
                string around = $"M {Number(box.X)} {Number(box.Y)} L {Number(box.Right)} {Number(box.Y)} " +
                                $"L {Number(box.Right)} {Number(box.Bottom)} L {Number(box.X)} {Number(box.Bottom)} Z";
                shape.SetAttributeValue("clip-rule", "evenodd");
                var outer = new XElement(Svg + "path", new XAttribute("d", around));
                clip.Add(outer);
            }

            _root.Element(Svg + "defs")?.Add(clip);
            if (_root.Element(Svg + "defs") is null)
            {
                var defs = new XElement(Svg + "defs");
                defs.Add(clip);
                _root.AddFirst(defs);
            }

            return id;
        }

        /// <summary>The `d` attribute for a path: an explicit curve per segment, and `Z` when it closes.</summary>
        private static string PathData(PathItem path)
        {
            var builder = new StringBuilder();

            foreach (SubPath sub in path.SubPaths)
            {
                if (sub.Nodes.Count < 2)
                {
                    continue;
                }

                PathNode first = sub.Nodes[0];
                builder.Append("M ").Append(Number(first.Anchor.X)).Append(' ').Append(Number(first.Anchor.Y));

                for (int i = 1; i < sub.Nodes.Count; i++)
                {
                    PathNode previous = sub.Nodes[i - 1];
                    PathNode node = sub.Nodes[i];
                    AppendSegment(builder, previous.Anchor, previous.OutHandle, node.InHandle, node.Anchor);
                }

                if (sub.IsClosed)
                {
                    PathNode last = sub.Nodes[^1];
                    AppendSegment(builder, last.Anchor, last.OutHandle, first.InHandle, first.Anchor);
                    builder.Append(" Z");
                }
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// One segment, as a line when it is straight and a cubic when it is not.
        ///
        /// A straight segment is written as `L` rather than as a cubic with its handles on the endpoints: the two
        /// describe the same geometry, and the line is what a person editing the file afterwards expects to see.
        /// </summary>
        private static void AppendSegment(
            StringBuilder builder, Point2D from, Point2D outHandle, Point2D inHandle, Point2D to)
        {
            bool straight = outHandle.NearlyEquals(from, 1e-9) && inHandle.NearlyEquals(to, 1e-9);
            if (straight)
            {
                builder.Append(" L ").Append(Number(to.X)).Append(' ').Append(Number(to.Y));
                return;
            }

            builder.Append(" C ")
                .Append(Number(outHandle.X)).Append(' ').Append(Number(outHandle.Y)).Append(' ')
                .Append(Number(inHandle.X)).Append(' ').Append(Number(inHandle.Y)).Append(' ')
                .Append(Number(to.X)).Append(' ').Append(Number(to.Y));
        }

        /// <summary>
        /// A fill: a gradient reference when there is one, a colour otherwise, or null when there is no fill.
        /// </summary>
        private string? FillAttribute(PathItem path)
        {
            if (!path.Fill.IsVisible)
            {
                return null;
            }

            if (path.Fill.Gradient is not null && _gradientIds.TryGetValue(path.Fill.Gradient, out string? id))
            {
                return $"url(#{id})";
            }

            return Hex(path.Fill.Color);
        }

        /// <summary>The outline loops of a converted stroke, as one even-odd-free path.</summary>
        private static string Outlines(IReadOnlyList<IReadOnlyList<Point2D>> loops)
        {
            var builder = new StringBuilder();

            foreach (IReadOnlyList<Point2D> loop in loops)
            {
                if (loop.Count == 0)
                {
                    continue;
                }

                builder.Append("M ").Append(Number(loop[0].X)).Append(' ').Append(Number(loop[0].Y));
                for (int i = 1; i < loop.Count; i++)
                {
                    builder.Append(" L ").Append(Number(loop[i].X)).Append(' ').Append(Number(loop[i].Y));
                }

                builder.Append(" Z ");
            }

            return builder.ToString().Trim();
        }
    }

    /// <summary>An `svg` transform attribute for the model's affine transform.</summary>
    private static string TransformAttribute(AffineTransform transform)
    {
        const double Tolerance = 1e-12;
        if (Math.Abs(transform.A - 1.0) < Tolerance && Math.Abs(transform.B) < Tolerance &&
            Math.Abs(transform.C) < Tolerance && Math.Abs(transform.D - 1.0) < Tolerance &&
            Math.Abs(transform.E) < Tolerance && Math.Abs(transform.F) < Tolerance)
        {
            return string.Empty;
        }

        // Written as a matrix rather than decomposed into translate/rotate/scale: a matrix is what the model holds,
        // and decomposing it would have to choose an order that the original might not have used.
        return $"matrix({Number(transform.A)},{Number(transform.B)},{Number(transform.C)}," +
               $"{Number(transform.D)},{Number(transform.E)},{Number(transform.F)})";
    }

    private static string Hex(ColorRgb colour)
    {
        static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255);

        return $"#{Channel(colour.R):x2}{Channel(colour.G):x2}{Channel(colour.B):x2}";
    }

    private static string Number(double value)
        => value.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Compares two gradients by value, so a document that uses one gradient twenty times writes one definition.
    ///
    /// The stops are an array, so the record's own equality would compare it by reference - the trap this codebase
    /// has had to avoid in three other places.
    /// </summary>
    private sealed class GradientKey : IEqualityComparer<GradientSpec>
    {
        public static GradientKey Instance { get; } = new();

        public bool Equals(GradientSpec? a, GradientSpec? b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a is null || b is null || a.Kind != b.Kind || a.Spread != b.Spread)
            {
                return false;
            }

            if (a.Start != b.Start || a.End != b.End || a.Center != b.Center ||
                Math.Abs(a.RadiusX - b.RadiusX) > 1e-9 || Math.Abs(a.RadiusY - b.RadiusY) > 1e-9 ||
                a.FocalPoint != b.FocalPoint)
            {
                return false;
            }

            if (a.Stops.Count != b.Stops.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Stops.Count; i++)
            {
                if (a.Stops[i] != b.Stops[i])
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(GradientSpec gradient)
        {
            var hash = new HashCode();
            hash.Add(gradient.Kind);
            hash.Add(gradient.Spread);
            hash.Add(gradient.Start);
            hash.Add(gradient.End);
            hash.Add(gradient.Center);
            hash.Add(gradient.FocalPoint);
            foreach (GradientStop stop in gradient.Stops)
            {
                hash.Add(stop);
            }

            return hash.ToHashCode();
        }
    }
}
