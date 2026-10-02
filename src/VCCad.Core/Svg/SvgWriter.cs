using System.Globalization;
using System.Text;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Text;
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
/// **What cannot be written is reported, not dropped.** Text and rasters are both written now - one `text` per
/// baseline with a `tspan` per run, and an `image` at its placement carrying its bytes as a data URI - so the things
/// that reach <see cref="SvgWriteResult.Missing"/> are the ones still without an element here: a **hatch fill**,
/// which SVG states as a `pattern` this writer has no output for; a raster whose samples cannot be stated as a
/// picture at all (a CMYK scan, a mask that is still compressed); each individual value a `text` element cannot
/// carry, such as an embedded programme or a wrap width; and each value beside a raster that no image format states,
/// such as a colour key or a decode array. Every one is left out of the file and **named**, with its kind and the
/// reason. The rule this repository runs on is that a gap is said rather than silently skipped, and an exporter is
/// the one place where being quiet costs somebody a document: a person opens the file and the words are simply not
/// there. The list is empty for a document the writer wrote completely, so "nothing was lost" is a fact the caller
/// can rely on rather than a default it has to trust.
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

        var writer = new Writer(root, document, document.SvgNamespaces);

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
        writer.WriteDefinitions(document);

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
        private readonly CadDocument _document;
        private readonly Dictionary<GradientSpec, string> _gradientIds = new(GradientKey.Instance);
        private readonly IReadOnlyDictionary<string, string> _namespaces;
        private readonly List<string> _missing = new();
        private readonly Dictionary<string, int> _written = new(StringComparer.Ordinal);
        private int _gradientCount;
        private int _clipCount;

        /// <summary>
        /// How deep the walk is inside artwork a brush placed, and the ceiling on it.
        ///
        /// An art brush names an item by id, that item may be a path with an art brush of its own, and a cycle
        /// through two brushes would otherwise recurse forever. The cap is the same one the canvas and the PDF
        /// exporter use, so the picture stops nesting at the same place in all three renderers.
        /// </summary>
        private const int MaxArtDepth = 8;

        private int _artDepth;

        /// <summary>
        /// Whether what is being written is a repeat of content already written, so a loss must not be reported
        /// twice. Set only while <see cref="WriteDefinitions"/> writes a definition, whose content is the same
        /// picture the instances that use it already carry into the file.
        /// </summary>
        private bool _quiet;

        public Writer(XElement root, CadDocument document, IReadOnlyDictionary<string, string>? namespaces = null)
        {
            _root = root;
            _document = document;
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
        ///
        /// <see cref="_quiet"/> suppresses it for the one write that repeats content already reported - see
        /// <see cref="WriteDefinitions"/>.
        /// </summary>
        private void Report(LayerItem item, string reason)
        {
            if (_quiet)
            {
                return;
            }

            _missing.Add($"{Kind(item)} {Identity(item)}: {reason}");
        }

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
        /// Puts back the namespaced attributes the file carried, and - where the file had one - the label that is
        /// the item's name.
        ///
        /// The label is written from the **name**, not from the stored attribute, because a rename has to move it
        /// and a stale label is an object disagreeing with itself. It is written **only for an item that already
        /// carried a label**, because the name is carried by the `id` as well and inventing one for an element the
        /// file left unlabelled is a rewrite in the other direction - see the note at the bottom of the method.
        /// </summary>
        private void ApplyForeign(XElement element, LayerItem item)
        {
            _namespaces.TryGetValue("inkscape", out string? inkscape);
            bool labelled = false;

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

                if (local == "label" && inkscape is not null && uri == inkscape)
                {
                    labelled = true;
                }
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

            // **The label is written from the name only where the file had a label.** The name is already carried
            // by the `id`, which the reader reads back as the name - so writing a label as well inverts the file's
            // own fact: an element that carried no `inkscape:label` came back with one, and the model gained a
            // foreign attribute on a round trip that had lost none. Where the file *did* have a label, the name
            // wins, because that is the member a rename moves and a stale label is an object disagreeing with
            // itself.
            if (labelled && !string.IsNullOrEmpty(item.Name) && inkscape is not null)
            {
                element.SetAttributeValue(XName.Get("label", inkscape), item.Name);
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
        /// The document's **definitions**, written into `defs` so that the id an instance names still names
        /// something after a save.
        ///
        /// An instance is written as the copy it holds plus `data-source`, which is what keeps the link across a
        /// round trip and what the reader reads back. That alone leaves the definition out of the file, so a
        /// document saved and opened again would have instances naming an id nothing defines - the link would
        /// survive as a name with no referent, and the edit half of "a reference, not a copy" would be gone.
        /// Writing the definition beside them is what makes the saved file say the same thing the model says.
        ///
        /// **A `use` element is deliberately not emitted.** It would draw the target *instead of* the copy, and the
        /// copy is not redundant: an instance's content was read through its own `use`, so anything that `use`
        /// stated - `fill`, `opacity`, a CSS class - is baked into what the instance holds. Writing `<use>` would
        /// therefore change the picture on the next import, which is the substitution this writer is not allowed to
        /// make quietly. The reference travels as an attribute, which is exactly as much structure as the model
        /// has.
        ///
        /// **Its losses are not reported twice.** A definition's content is the same picture the instances that
        /// use it already carry into the file, and those are written with their own reports; naming the
        /// definition's copy as well would report one un-writable raster twice, which is how a report stops being
        /// read. The suppression is only for this write, and the picture is still named where it is drawn.
        /// </summary>
        public void WriteDefinitions(CadDocument document)
        {
            ArtGroup[] entries = document.Definitions.Children
                .OfType<ArtGroup>()
                .Where(entry => entry.Name.Length > 0 && entry.Children.Count > 0)
                .ToArray();

            if (entries.Length == 0)
            {
                return;
            }

            XElement? defs = _root.Element(Svg + "defs");
            bool created = defs is null;
            defs ??= new XElement(Svg + "defs");

            if (created)
            {
                _root.Add(defs);
            }

            bool wasQuiet = _quiet;
            _quiet = true;

            try
            {
                foreach (ArtGroup entry in entries)
                {
                    // One child that already carries the id **is** the definition - a `<rect id="box">` reads as a
                    // path named `box` - and wrapping it would state the same id twice.
                    if (entry.Children.Count == 1 && entry.Children[0].Name == entry.Name)
                    {
                        WriteItems(entry.Children, defs);
                        continue;
                    }

                    // Otherwise the entry is a holder, not a group the file had: a `symbol` is a viewport whose
                    // fit belongs to each instance, so its content is written under a plain group that states only
                    // the id. See SvgReader.ReadDefinitions.
                    var holder = new XElement(Svg + "g", new XAttribute("id", entry.Name));
                    WriteItems(entry.Children, holder);
                    defs.Add(holder);
                }
            }
            finally
            {
                _quiet = wasQuiet;
            }

            if (created && !defs.HasElements)
            {
                defs.Remove();
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
                        // **A viewport is written as the nested `svg` it was read from.** The reader records a
                        // nested viewport's port as a clip on the element's own group, and a nested `svg` is the
                        // one spelling it turns back into one - so writing the group as a plain `g` with a
                        // `clip-path` would draw the same picture in a viewer and still lose the crop the second
                        // time this editor opened the file. NestedViewport refuses anything whose port is not the
                        // axis-aligned rectangle a nested `svg` can state, and those fall through to the
                        // `clip-path` below, which is the general spelling.
                        XElement element = group.Clips.Count == 1 &&
                            NestedViewport(group, group.Clips[0]) is { } viewport
                                ? ViewportElement(viewport)
                                : new XElement(Svg + "g");

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

                            // And the presentation the use site established travels beside the link, so a reopen
                            // followed by a refresh does not revert the instance to the definition's own paint.
                            if (group.InstancePresentation is { IsDefault: false } presentation)
                            {
                                WriteInstancePresentation(element, presentation);
                            }
                        }

                        // A port written as a nested `svg` is already the clip; anything else is a `clip-path`,
                        // stated in the space the group's own transform maps its children into.
                        if (element.Name.LocalName != "svg" &&
                            WriteClips(group, group.Transform) is { } clipId)
                        {
                            element.SetAttributeValue("clip-path", $"url(#{clipId})");
                        }

                        WriteItems(group.Children, element);
                        Wrote(element.Name.LocalName);
                        parent.Add(element);
                        break;
                    }

                    case PathItem path:
                        WritePath(path, parent);
                        break;

                    case TextItem text:
                        WriteText(text, parent);
                        break;

                    // An embedded raster is written as an `image` at its placement, carrying its own bytes as a
                    // data URI - the file's bytes for a JPEG, a PNG built from the samples otherwise. A raster
                    // whose samples cannot be stated (CMYK, a mask that is still compressed) is named instead.
                    case ImageItem image:
                        WriteImage(image, parent);
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
        /// One raster, as an SVG `image` at its placement, carrying its own bytes.
        ///
        /// **The placement is the element's box, and `preserveAspectRatio="none"` is what makes it one.** The model
        /// records where a picture sits as a rectangle - the transform that placed it in the source file is folded
        /// into that rectangle - so `x`, `y`, `width` and `height` are the rectangle. SVG's *default* fits the
        /// picture inside the box, preserving its proportions and centring it, so a 2x2 raster placed in a 40x30 box
        /// would come back 30x30 at an offset: a rectangle the document never drew. `none` is the file stating the
        /// box the model holds. The coordinates are written **bare**, like a text run's, because the root states its
        /// size in `pt` and its `viewBox` in the model's own numbers, so the reader's view-box fit is the identity
        /// and one bare number is one model unit.
        ///
        /// **The picture travels inside the document, never as a reference to a file.** A `href` naming a file
        /// beside the SVG is a reference that resolves only if that file is still there, and the model holds no path
        /// to write - it holds the bytes. So they are encoded into the document: the file's own bytes when the
        /// raster is still a JPEG (re-encoding somebody else's photograph would be a lossy rewrite of it), and a PNG
        /// built from the samples otherwise - see <see cref="PngEncoder"/>. A raster whose samples cannot be stated
        /// that way is **named**, which is what the report is for.
        ///
        /// **A written raster and a named facet of it are different answers.** A picture whose pixels can be stated
        /// goes into the file even when a *value* beside them cannot travel - a colour key, a decode array, a hidden
        /// flag - and that value is named instead. Losing a whole photograph over one value would help nobody; this
        /// is the decision the text writer already takes for a run whose embedded programme cannot be written.
        ///
        /// **A flip is carried by the element's own transform.** The reader reads whether a picture is mirrored out
        /// of the determinant of that transform, so a mirrored raster is written with the placement folded into a
        /// `matrix` and the flip's sign in it - the samples are never resampled. Two flips are the one case that
        /// cannot travel: their determinant is positive, which is the same sign as no flip at all, so a raster
        /// flipped on both axes is drawn unflipped and said so.
        /// </summary>
        private void WriteImage(ImageItem image, XElement parent)
        {
            string? href = ImageHref(image, out string? reason);
            if (href is null)
            {
                Report(image, reason!);
                return;
            }

            // What the file cannot state beside a picture that IS in it. Named, and the raster is still written.
            if (image.Decode is { Length: > 0 })
            {
                Report(image, "the file's decode array is not in the image: no image format states a per-component " +
                    "remap, so the file holds the stored samples rather than the values the array maps them to");
            }

            if (image.ColourKey is { Length: > 0 })
            {
                Report(image, "the file's colour key is not in the image: the writer states no transparency " +
                    "beyond the soft mask, so the colour the key made paint nothing is written opaque");
            }

            if (image.MirrorX && image.MirrorY)
            {
                Report(image, "the raster is flipped on both axes, and the reader reads a flip from the sign of a " +
                    "transform's determinant - two flips have a positive determinant, which is the same sign as " +
                    "none, so the picture is written unflipped");
            }

            // SVG's `display="none"` is not a hidden element to this reader - it is an element it never reads - so
            // writing the raster hidden would delete it outright. Losing the flag is the smaller loss, and it is
            // named, exactly as it is for a text block.
            if (!image.IsVisible)
            {
                Report(image, "the raster is hidden and the file holds no hidden image the reader keeps: an element " +
                    "it does not draw is one it does not read");
            }

            // **A flip is the element's own transform**, and the placement moves into it: the box is written at the
            // origin and the matrix both places it and turns it over. The reader puts the box's two corners through
            // that matrix and reads the flip out of its determinant, so the placement comes back whole.
            AffineTransform own = AffineTransform.Identity;
            if (image.MirrorX || image.MirrorY)
            {
                double scaleX = image.MirrorX ? -1.0 : 1.0;
                double scaleY = image.MirrorY ? -1.0 : 1.0;
                double translateX = image.MirrorX ? image.Placement.X + image.Placement.Width : image.Placement.X;
                double translateY = image.MirrorY ? image.Placement.Y + image.Placement.Height : image.Placement.Y;
                own = AffineTransform.CreateTranslation(translateX, translateY)
                    .Compose(AffineTransform.CreateScale(scaleX, scaleY));
            }

            // The clip is carried by that same transform, for the reason the shape half states: a `clip-path` is
            // applied in the user space the element's own transform establishes, so an outline the model recorded in
            // the parent's space needs the inverse to be stated there.
            string? clip = WriteClips(image, own);

            var element = new XElement(Svg + "image");
            ApplyForeign(element, image);

            if (!string.IsNullOrEmpty(image.Name))
            {
                element.Add(new XAttribute("id", image.Name));
            }

            if (!string.IsNullOrEmpty(image.FilterId))
            {
                element.Add(new XAttribute("filter", $"url(#{image.FilterId})"));
            }

            bool flipped = image.MirrorX || image.MirrorY;
            element.Add(new XAttribute("x", Number(flipped ? 0.0 : image.Placement.X)));
            element.Add(new XAttribute("y", Number(flipped ? 0.0 : image.Placement.Y)));
            element.Add(new XAttribute("width", Number(image.Placement.Width)));
            element.Add(new XAttribute("height", Number(image.Placement.Height)));
            element.Add(new XAttribute("preserveAspectRatio", "none"));

            string transform = TransformAttribute(own);
            if (transform.Length > 0)
            {
                element.Add(new XAttribute("transform", transform));
            }

            if (clip is not null)
            {
                element.SetAttributeValue("clip-path", $"url(#{clip})");
            }

            element.Add(HrefAttribute(href));
            parent.Add(element);
            Wrote("image");
        }

        /// <summary>
        /// The bytes of a raster as a `data:` URI, or null with the reason they cannot be stated as a picture.
        ///
        /// A raster that is still compressed is the one case where the document **is** holding the encoded resource
        /// an `image` wants, and a JPEG is therefore carried through byte for byte: it is not decoded here, so
        /// encoding it would mean decoding it first and losing part of it on the way back. Anything else under a
        /// filter - a fax, a JBIG2, a Flate stream this reader kept - is a compressor that is not a picture format,
        /// and it is named.
        /// </summary>
        private static string? ImageHref(ImageItem image, out string? problem)
        {
            problem = null;

            if (image.Filter is { Length: > 0 } filter)
            {
                if (!filter.Equals("DCTDecode", StringComparison.OrdinalIgnoreCase) ||
                    image.Samples.Length < 3 ||
                    image.Samples[0] != 0xFF ||
                    image.Samples[1] != 0xD8)
                {
                    problem = $"the raster's samples are still in {filter}, which is a compressor rather than a " +
                              "picture format an image element can carry";
                    return null;
                }

                if (image.Mask.Length > 0 || image.MaskFilter is { Length: > 0 })
                {
                    problem = "the raster is a JPEG with a soft mask, and a JPEG data URI has nowhere to state one";
                    return null;
                }

                return DataUri("image/jpeg", image.Samples);
            }

            byte[]? png = PngEncoder.Encode(image, out problem);
            return png is null ? null : DataUri("image/png", png);
        }

        /// <summary>
        /// The `href` an image is referred to by.
        ///
        /// Written as `xlink:href` when the document declared the `xlink` prefix - which is how every SVG 1.1 file
        /// spells it and how the file this picture came from spelled it - and as the SVG 2 `href` otherwise, so a
        /// document with no `xlink` declaration never refers to a prefix nothing declares.
        /// </summary>
        private XAttribute HrefAttribute(string href)
        {
            _namespaces.TryGetValue("xlink", out string? xlink);
            return xlink is null
                ? new XAttribute("href", href)
                : new XAttribute(XName.Get("href", xlink), href);
        }

        /// <summary>A `data:` URI carrying an encoded picture, which is how the bytes travel inside the file.</summary>
        private static string DataUri(string mediaType, byte[] bytes)
            => $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";

        /// <summary>
        /// One text block, as an SVG `text` element with a `tspan` per run.
        ///
        /// **The frame is the reader's, not the model's.** SVG places a *baseline* and the model stores a block's
        /// *top-left*, so the writer and the reader must agree about the ascent between them or the numbers do not
        /// come back. `<see cref="SvgTextStyle"/>`'s reader places the top-left one ascent above the baseline, and
        /// where the model does not record the ascent it was placed with, that ascent is the reader's own
        /// `<see cref="TextMeasurement.TypicalAscentEm"/>.` A block that came from an SVG, or one this editor made,
        /// therefore round-trips its origin exactly. A run that *does* record an ascent is written at **its** own
        /// baseline - the picture is then where the model says it is - and the block is reported, because the reader
        /// will recover the origin against its own ascent and move the block by the difference.
        ///
        /// **Coordinates are written bare.** The root states its size in `pt` and its `viewBox` in the model's own
        /// numbers, so the reader's view-box fit is the identity and one bare number is one model unit. Spelling a
        /// length in `pt` here would send it through the CSS ratio a second time - `font-size="18pt"` reads back as
        /// 24 - which is the unit bridge this writer must not put text across.
        ///
        /// **A line is a `text` element.** SVG joins pieces that share a baseline into one run sequence, and so does
        /// this reader, so a block set on one baseline is written as one element. A block the model sets on several
        /// baselines - an explicit newline, or a frame that wrapped it - is written as one element per baseline: the
        /// content and the picture are then both right, and the block is reported, because each baseline comes back
        /// as a block of its own.
        ///
        /// **What the file cannot state is reported, and the text is still written.** A run's own colour, its
        /// tracking, its spacing, its face, its size and its position are all attributable to a `tspan`, so they go
        /// there. An embedded programme and its glyph ids, a trailing advance no run follows, a wrap width, a source
        /// face recording a substitution - none of those has a spelling an SVG reader would take - and each is named
        /// in <see cref="SvgWriteResult.Missing"/> rather than approximated, while the characters themselves still
        /// reach the file. Losing the words as well would help nobody.
        ///
        /// **A rotated or mirrored block is written as a transform.** SVG turns an element about a point and mirrors
        /// it about an axis, and the model's own signs say which of the two it is, so the block is written in its own
        /// frame and carried by a `matrix`. The reader puts a transform on a group, so such a block comes back inside
        /// one - a conversion, like a multi-stroke path gaining an element per stroke, rather than a loss.
        /// </summary>
        private void WriteText(TextItem text, XElement parent)
        {
            if (text.Runs.Count == 0 || text.PlainText.Length == 0)
            {
                Report(text, "the block holds no characters, so there is no text to write");
                return;
            }

            // **A framed block is written as the flowed text it is read from.** A `text` element has no attribute
            // that says where to break, so a frame written that way comes back as one block per line - a model that
            // has changed shape, which the corpus round trip caught the moment the reader learned to read a frame.
            // The flowed form holds the region, the paragraphs and the runs, so the block that goes out is the block
            // that came in. See #126.
            if (text.FrameWidth > 0)
            {
                WriteFlowRoot(text, parent);
                return;
            }

            TextLayout layout = TextLayoutEngine.Compute(text);
            ReportTextLosses(text, layout, flowed: false);

            string transform = TextTransform(text);
            bool preserve = PreservesSpace(text);
            string? clip = WriteClips(text, AffineTransform.Identity);

            // Where each run begins in the block's flattened text, which is the space a layout segment's `Start`
            // is an index into - a run's own string is a different space, and slicing one with the other's index
            // takes the wrong characters.
            var runStart = new int[text.Runs.Count];
            int flat = 0;
            for (int i = 0; i < text.Runs.Count; i++)
            {
                runStart[i] = flat;
                flat += text.Runs[i].Text.Length;
            }

            for (int line = 0; line < layout.Lines.Count; line++)
            {
                List<TextRunBox> segments = layout.Runs.Where(box => box.Line == line).ToList();
                if (segments.Count == 0)
                {
                    // A blank line of a multi-line block: SVG states no such line, and the block is reported for
                    // its baselines either way. Writing an empty `tspan` would add a run the model has not got.
                    continue;
                }

                TextLine placed = layout.Lines[line];
                TextRun lead = text.Runs[segments[0].Run];
                double ascent = AscentEm(lead) * lead.FontSize;

                // The anchor states where the file put the *start*, the *middle* or the *end* of the line, and the
                // reader moves the block's origin back by the width its own layout gives it - so every x is written
                // from the anchor rather than from the line's left edge, and the origin comes back where it was.
                double anchor = text.Alignment switch
                {
                    TextAlignment.Center => placed.Width / 2.0,
                    TextAlignment.Right => placed.Width,
                    _ => 0.0,
                };

                var element = new XElement(Svg + "text");
                if (line == 0)
                {
                    if (!string.IsNullOrEmpty(text.Name))
                    {
                        element.Add(new XAttribute("id", text.Name));
                    }

                    ApplyForeign(element, text);
                }

                if (transform.Length > 0)
                {
                    element.Add(new XAttribute("transform", transform));
                }

                // **The writing mode and the base direction are written before the coordinates depend on them.**
                // SVG's own rule is that `x`/`dx` run along the line and `y`/`dy` across it, so a reader that has the
                // mode writes the pen down the page and the columns sideways - which is what makes a vertical block
                // come back vertical instead of collapsing into one horizontal line.
                if (text.WritingMode != TextWritingMode.HorizontalTb)
                {
                    element.Add(new XAttribute("writing-mode", text.WritingModeName));
                }

                if (text.Direction == TextDirection.RightToLeft)
                {
                    element.Add(new XAttribute("direction", "rtl"));
                }

                if (text.WritingMode == TextWritingMode.HorizontalTb)
                {
                    element.Add(new XAttribute("y", Number(text.Origin.Y + placed.Top + ascent)));
                }
                else
                {
                    // A vertical column's own `x` is the line its pen runs down, which the reader takes the block's
                    // origin from; the `y` is where the first of its characters sits.
                    element.Add(new XAttribute(
                        "x", Number(text.Origin.X + placed.Cross - layout.CrossShift + anchor)));
                    element.Add(new XAttribute("y", Number(text.Origin.Y + placed.Top)));
                }


                // The block's leading, when it is not the default the reader assumes. A bare number is a multiple
                // of the font size, which is what the model stores, so it comes back as it went out.
                if (Math.Abs(text.LineSpacing - 1.2) > 1e-9)
                {
                    element.Add(new XAttribute("line-height", Number(text.LineSpacing)));
                }

                if (text.Alignment != TextAlignment.Left)
                {
                    element.Add(new XAttribute(
                        "text-anchor", text.Alignment == TextAlignment.Center ? "middle" : "end"));
                }

                // A clip on a text item is the same statement it is on a path, and leaving it out would draw the
                // whole block where the model shows a crop of it.
                if (clip is not null)
                {
                    element.Add(new XAttribute("clip-path", $"url(#{clip})"));
                }

                // The block's own colour paints the element, which is what the reader takes the block's colour
                // from; a run that states another goes on its own `tspan`.
                element.Add(new XAttribute("fill", Hex(text.Color)));
                if (text.Color.A < 1.0)
                {
                    element.Add(new XAttribute("fill-opacity", Number(text.Color.A)));
                }

                element.Add(new XAttribute("font-family", Face(lead.FontFamily)));
                if (lead.FontSize > 0)
                {
                    element.Add(new XAttribute("font-size", Number(lead.FontSize)));
                }

                // SVG collapses white space unless the file says otherwise, and a block whose text does not
                // survive that collapse - a leading space, a trailing one, two in a row - asks for it to be kept.
                // **On the `tspan`, not on the `text`.** `xml:space="preserve"` in scope makes an XML reader keep
                // every white space character, including the indentation this writer puts between the elements -
                // which would come back as a run of its own. Scoped to each run, the formatter's own white space
                // between the elements is out of scope and is still stripped.
                foreach (TextRunBox box in segments)
                {
                    element.Add(RunElement(text, box, anchor, runStart[box.Run], preserve, layout));
                }

                Wrote("text");
                parent.Add(element);
            }
        }

        /// <summary>One run's piece on one line, as a `tspan`.</summary>
        private XElement RunElement(
            TextItem text, TextRunBox box, double anchor, int runStart, bool preserve, TextLayout layout)
        {
            TextRun run = text.Runs[box.Run];
            int start = Math.Clamp(box.Start - runStart, 0, run.Text.Length);
            int length = Math.Clamp(box.Length, 0, run.Text.Length - start);
            string piece = run.Text.Substring(start, length);

            var element = new XElement(Svg + "tspan", new XText(piece));

            if (preserve)
            {
                element.Add(new XAttribute(XNamespace.Xml + "space", "preserve"));
            }

            if (text.WritingMode == TextWritingMode.HorizontalTb)
            {
                // The absolute x is the model's own, which is what lets a run whose advance differs from the face's
                // width come back with that advance: the reader stores the room before the next run as a gap.
                //
                // **A right-to-left line's `x` is where its pen *ends*.** SVG states the start of the text, and under
                // `rtl` the text starts at its right-hand end and runs back - so the position the file holds is the
                // far edge of the piece, which is its own width past where the model's frame has it begin. Writing
                // the model's own coordinate instead is what made the corpus round trip lose one line width on the
                // first read and another on every read after it.
                element.Add(new XAttribute("x", Number(text.Direction == TextDirection.RightToLeft
                    ? text.Origin.X + box.X + box.Width - anchor
                    : text.Origin.X + box.X + anchor)));
            }
            else
            {
                // **A vertical run states its pen, and relies on the element's own `x` for the column.** Repeating
                // the column on every `tspan` would say each run stands on a line of its own, and the reader would
                // take each one as a block - so only the pen moves here.
                TextLine line = layout.Lines[box.Line];
                element.Add(new XAttribute("y", Number(text.Origin.Y + line.Top)));
            }

            ColorRgb colour = text.ColourOf(run);
            if (colour != text.Color)
            {
                element.Add(new XAttribute("fill", Hex(colour)));
            }

            if (colour.A < 1.0)
            {
                element.Add(new XAttribute("fill-opacity", Number(colour.A)));
            }

            FaceAttributes(element, run);

            Wrote("tspan");
            return element;
        }

        /// <summary>
        /// A framed block, as SVG 1.2's flowed text: a `flowRoot`, the one rectangle it flows into, and a
        /// `flowPara` for every paragraph.
        ///
        /// **The frame goes out as the file's own form.** A `text` element has no attribute that says where to
        /// break, so a framed block written that way comes back as one block per line - the model changed shape,
        /// which the corpus round trip caught the moment the reader learned to read a region. The flowed form holds
        /// the region's width, the block's leading, its alignment and its paragraphs, so the reader's own answer to
        /// `flowRoot` reads this back as the block that was written.
        ///
        /// **One number here is the model's and not the file's.** A `flowRegion` is an area, so its `rect` needs a
        /// height; the model records a frame width and no height, and the height written is the block's own
        /// laid-out height. That is the one thing the model does not hold about a region, and it is named on the
        /// report rather than left for the reader to take on trust - the reader will say the same thing about it
        /// when the file comes back.
        ///
        /// **The first run's face is written on the `flowRoot`.** A paragraph break inherits the style of the
        /// element around it, and the reader gives the newline it sets between two paragraphs the `flowRoot`'s
        /// style - so writing the first run's face here is what makes a block whose first paragraph is empty come
        /// back with the run boundaries it went out with.
        /// </summary>
        private void WriteFlowRoot(TextItem text, XElement parent)
        {
            TextLayout layout = TextLayoutEngine.Compute(text);
            ReportTextLosses(text, layout, flowed: true);

            Report(text, "the region's height is the block's own laid-out height, which the model does not record: " +
                         "the frame a block holds is its width");

            TextRun lead = text.Runs[0];

            var element = new XElement(Svg + "flowRoot");
            if (!string.IsNullOrEmpty(text.Name))
            {
                element.Add(new XAttribute("id", text.Name));
            }

            ApplyForeign(element, text);

            string transform = TextTransform(text);
            if (transform.Length > 0)
            {
                element.Add(new XAttribute("transform", transform));
            }

            string? clip = WriteClips(text, AffineTransform.Identity);
            if (clip is not null)
            {
                element.Add(new XAttribute("clip-path", $"url(#{clip})"));
            }

            FaceAttributes(element, lead);

            // The block's own colour paints the `flowRoot`, which is where the reader takes a block's colour from;
            // a run that states another carries it on its own `flowPara` or `flowSpan`.
            element.Add(new XAttribute("fill", Hex(text.Color)));
            if (text.Color.A < 1.0)
            {
                element.Add(new XAttribute("fill-opacity", Number(text.Color.A)));
            }

            // **White space is asked for on the `flowRoot`, and the paragraphs carry no indentation to keep.**
            // `xml:space` in scope keeps every white space character, so the writer's own separation between the
            // paragraphs would otherwise become content - but the `flowRoot` is the one element the reader never
            // reads text from: it takes the region and the paragraphs, and ignores everything between them. Inside
            // a paragraph nothing is indented either, because a paragraph that holds text as well as elements is
            // mixed content and a writer does not lay mixed content out. The block asks once, so every paragraph
            // inherits the same answer and two of them cannot come back as differently styled runs.
            if (PreservesSpace(text))
            {
                element.Add(new XAttribute(XNamespace.Xml + "space", "preserve"));
            }

            if (Math.Abs(text.LineSpacing - 1.2) > 1e-9)
            {
                element.Add(new XAttribute("line-height", Number(text.LineSpacing)));
            }

            // Flowed text is aligned inside the region with `text-align`, which is not the same property as the
            // `text-anchor` a point-placed block uses - see the reader's FlowAlign.
            if (text.Alignment != TextAlignment.Left)
            {
                element.Add(new XAttribute(
                    "text-align", text.Alignment == TextAlignment.Center ? "center" : "right"));
            }

            element.Add(new XElement(Svg + "flowRegion", new XElement(Svg + "rect",
                new XAttribute("x", Number(text.Origin.X)),
                new XAttribute("y", Number(text.Origin.Y)),
                new XAttribute("width", Number(text.FrameWidth)),
                new XAttribute("height", Number(Math.Max(layout.Height, 1.0))))));

            foreach (XElement paragraph in FlowParagraphs(text))
            {
                element.Add(paragraph);
            }

            Wrote("flowRoot");
            parent.Add(element);
        }

        /// <summary>
        /// A block's paragraphs, as the `flowPara` elements the reader builds them back from.
        ///
        /// An explicit newline is the model's whole spelling of "a new line", and a paragraph is what SVG flows
        /// into a region - so the block's text is split at its newlines and each piece is a paragraph. A leading
        /// newline is an empty first paragraph and a trailing one an empty last paragraph, which is what keeps a
        /// blank line where the model has one.
        /// </summary>
        private static IEnumerable<XElement> FlowParagraphs(TextItem text)
        {
            string plain = text.PlainText;

            // Where each run begins in the block's flattened text, which is the space the paragraph offsets are in
            // - a run's own string is a different space, and slicing one with the other's index takes the wrong
            // characters.
            var runStart = new int[text.Runs.Count];
            int flat = 0;
            for (int i = 0; i < text.Runs.Count; i++)
            {
                runStart[i] = flat;
                flat += text.Runs[i].Text.Length;
            }

            int at = 0;
            while (true)
            {
                int end = plain.IndexOf('\n', at);
                int stop = end < 0 ? plain.Length : end;

                yield return FlowParagraph(text, plain, runStart, at, stop);

                if (end < 0)
                {
                    yield break;
                }

                at = end + 1;
            }
        }

        /// <summary>
        /// One paragraph: the runs that reach into it, the first as the element's own text and the rest as
        /// `flowSpan`s inside it.
        ///
        /// **The shape is the `text` element's, one level down.** The paragraph's own style is the first piece's -
        /// the same answer the `text` writer gives the lead run - and every piece after it states its own face and
        /// colour, so the reader's run boundaries are the model's. Nothing is written between the pieces and the
        /// paragraph is mixed content, which is what stops the writer laying it out with indentation the reader
        /// would have to collapse and could not tell from content.
        ///
        /// A paragraph with nothing in it is an empty element, which is the blank line the model spells as two
        /// newlines in a row.
        /// </summary>
        private static XElement FlowParagraph(TextItem text, string plain, int[] runStart, int start, int stop)
        {
            var paragraph = new XElement(Svg + "flowPara");
            bool lead = true;

            for (int i = 0; i < text.Runs.Count; i++)
            {
                TextRun run = text.Runs[i];
                int from = Math.Max(start, runStart[i]);
                int to = Math.Min(stop, runStart[i] + run.Text.Length);
                if (to <= from)
                {
                    continue;
                }

                string piece = plain[from..to];
                ColorRgb colour = text.ColourOf(run);

                XElement held = lead ? paragraph : new XElement(Svg + "flowSpan");

                if (colour != text.Color)
                {
                    held.Add(new XAttribute("fill", Hex(colour)));
                }

                if (colour.A < 1.0)
                {
                    held.Add(new XAttribute("fill-opacity", Number(colour.A)));
                }

                FaceAttributes(held, run);
                held.Add(new XText(piece));

                if (!lead)
                {
                    // Added after its text, so the paragraph's first child is content and the writer has no room
                    // to indent - see the note on `xml:space` in WriteFlowRoot.
                    paragraph.Add(held);
                }

                lead = false;
            }

            return paragraph;
        }

        /// <summary>
        /// The face and the two spacings a run carries, in the file's own spelling.
        ///
        /// One place writes them, so the `text` form of a run and the flowed form of it cannot come to disagree
        /// about a face, a size or a tracking - which is the same reason the reader resolves them once for both.
        /// </summary>
        private static void FaceAttributes(XElement element, TextRun run)
        {
            element.Add(new XAttribute("font-family", Face(run.FontFamily)));
            if (run.FontSize > 0)
            {
                element.Add(new XAttribute("font-size", Number(run.FontSize)));
            }

            if (run.Bold)
            {
                element.Add(new XAttribute("font-weight", "bold"));
            }

            if (run.Italic)
            {
                element.Add(new XAttribute("font-style", "italic"));
            }

            // Both are lengths the run holds, and a bare number here is that length in the file's own units - the
            // same units the geometry is written in, which is what keeps a letter-spaced run the width it was.
            if (run.LetterSpacing != 0)
            {
                element.Add(new XAttribute("letter-spacing", Number(run.LetterSpacing)));
            }

            if (run.WordSpacing != 0)
            {
                element.Add(new XAttribute("word-spacing", Number(run.WordSpacing)));
            }

            // The face's own width and variant, in the words the model kept. Nothing selects a face by them, and
            // the reader says so - but they are the file's values and dropping them here would lose them twice.
            if (run.FontStretch is { Length: > 0 } stretch)
            {
                element.Add(new XAttribute("font-stretch", stretch));
            }

            if (run.FontVariant is { Length: > 0 } variant)
            {
                element.Add(new XAttribute("font-variant", variant));
            }

            // **The turn a glyph carries in a vertical column goes back as the property it came from.** It is
            // written in SVG 1.1's own spelling - `auto`, `0` or `90` - because that is the property a vertical SVG
            // document states, and it is written only when the run asks for something other than the initial value,
            // so a document with no vertical text keeps every byte it had. Leaving it out made a column the file set
            // upright come back turned: the reader cannot tell the two apart from the geometry alone, because both
            // are a run of advance after advance down one x.
            if (run.FontOrientation != GlyphOrientation.Auto)
            {
                element.Add(new XAttribute(
                    "glyph-orientation-vertical",
                    run.FontOrientation == GlyphOrientation.Upright ? "0" : "90"));
            }
        }

        /// <summary>
        /// Everything about a block the file will not carry, named once each, while the text itself is still
        /// written.
        ///
        /// The rule this repository runs on is that a gap is said rather than silently skipped, and it applies to a
        /// half-carried block as much as to a dropped one: a file holding the words but not the programme they were
        /// set in looks complete and is not, and only the person who is told can act on it.
        /// </summary>
        private void ReportTextLosses(TextItem text, TextLayout layout, bool flowed)
        {
            // SVG's `display="none"` is not a hidden element to this reader - it is an element it never reads - so
            // writing the block hidden would delete it outright. Losing the state is the smaller loss, and it is
            // named.
            if (!text.IsVisible)
            {
                Report(text, "the block is hidden and the file holds no hidden text the reader keeps: an element " +
                             "it does not draw is one it does not read");
            }

            // **A frame is only a loss in the `text` form.** The flowed form has a region, so a framed block written
            // as a `flowRoot` keeps its width and every baseline it sets - and there is nothing to report. See
            // WriteFlowRoot.
            if (!flowed && text.FrameWidth > 0)
            {
                Report(text, "the wrap width is not in the file: a `text` element has no attribute that says where " +
                             "to break, so the reader recovers the block's width from the text it sets");
            }

            if (!flowed && layout.Lines.Count > 1)
            {
                Report(text, $"the block is set on {layout.Lines.Count} baselines and a `text` element states one " +
                             "baseline, so the reader reads one block back per line");
            }

            // The block's colour is whatever its first piece was painted with, so a block whose own colour is not
            // that piece's comes back painted with the piece's.
            if (text.Runs[0].Color is { } own && own != text.Color)
            {
                Report(text, "the block's own colour is not its first run's, and the reader takes the block's " +
                             "colour from the first run");
            }

            double firstBaseline = AscentEm(text.Runs[0]) * text.Runs[0].FontSize;

            // A block that records the ascent it was placed with is written at **that** baseline, so the file is
            // where the model says it is - and the reader, which measures a top-left back against its own ascent,
            // will therefore move the block by the difference. Saying so is the honest half of writing it right.
            // A flowed block states no baseline at all - the region is the block's whole position - so there is no
            // difference to report.
            if (!flowed && text.Runs[0].PlacedAscentEm > 0 &&
                Math.Abs(text.Runs[0].PlacedAscentEm - TextMeasurement.TypicalAscentEm) > 1e-9)
            {
                Report(text, $"the recorded ascent of {Number(text.Runs[0].PlacedAscentEm)} em is written where " +
                             "the model put the baseline, and the reader measures a block's origin back against " +
                             "the SVG reader's own ascent, so re-importing moves the block by the difference");
            }

            for (int i = 0; i < text.Runs.Count; i++)
            {
                TextRun run = text.Runs[i];
                string where = $"run {i}";

                // A run with no characters has no piece to write - there is no `tspan` a reader would make the run
                // from - so the empty run is named rather than quietly disappearing into its neighbours.
                if (run.Text.Length == 0)
                {
                    Report(text, $"{where} holds no characters, and a run is written as the text it sets");
                }

                if (run.EmbeddedFont is not null || run.RawCodes is { Length: > 0 } || run.GlyphIds is { Length: > 0 })
                {
                    Report(text, $"the embedded programme and glyph ids of {where} are not in the file: SVG " +
                                 "carries the characters and the face's name, and the reader draws them with a " +
                                 "face this machine supplies");
                }

                if (!string.IsNullOrEmpty(run.SourceFont) &&
                    !string.Equals(run.SourceFont, run.FontFamily, StringComparison.Ordinal))
                {
                    Report(text, $"the source face '{run.SourceFont}' of {where} is not in the file: a run is " +
                                 "written as the face it is drawn with");
                }

                if (i > 0 && Math.Abs((AscentEm(run) * run.FontSize) - firstBaseline) > 1e-9)
                {
                    Report(text, $"{where} sits on a different baseline and the file puts every run of a block " +
                                 "on one, so it is written on the block's");
                }

                // A run the last one follows has its advance stated as the room before the next run. Nothing
                // follows the block's last run, so an advance it carries past the face's own width is the one
                // number SVG has no attribute for - `textLength` is a measured length the reader reports rather
                // than holds - and it is named rather than quietly dropped.
                if (i == text.Runs.Count - 1 && run.AdvanceWidth is { } advance)
                {
                    double natural = NaturalWidth(run);
                    if (natural > 0 && Math.Abs(advance - natural) > 1e-6)
                    {
                        Report(text, $"{where} carries an advance of {Number(advance)} pt past the " +
                                     $"{Number(natural)} pt the face sets, and no run follows it for the file to " +
                                     "state that room before");
                    }
                }
            }
        }

        /// <summary>
        /// The ascent a block's top-left sits above its baseline by, in em.
        ///
        /// The ascent the block was placed with when the model recorded one, and the reader's own otherwise - which
        /// is the number the reader will measure the origin back with, so a block that records none comes back
        /// exactly where it started.
        /// </summary>
        private static double AscentEm(TextRun run)
            => run.PlacedAscentEm > 0 ? run.PlacedAscentEm : TextMeasurement.TypicalAscentEm;

        /// <summary>How wide a face sets a run, the recorded advance deliberately not included.</summary>
        private static double NaturalWidth(TextRun run)
        {
            double width = 0;
            foreach (double advance in run.Advances())
            {
                width += advance;
            }

            return width;
        }

        /// <summary>
        /// The transform a block is written with: its turn about its own origin, then its mirror about that
        /// origin's axes - the order <see cref="TextItem.BoundingBox"/> applies them in, so the file and the box
        /// agree. Empty for an upright, unmirrored block, which is every block that lets this change be invisible.
        /// </summary>
        private static string TextTransform(TextItem text)
        {
            double x = text.MirrorX ? -1.0 : 1.0;
            double y = text.MirrorY ? -1.0 : 1.0;
            double cos = Math.Cos(text.RotationRadians);
            double sin = Math.Sin(text.RotationRadians);

            double a = x * cos;
            double b = x * sin;
            double c = -y * sin;
            double d = y * cos;

            const double Tolerance = 1e-12;
            if (Math.Abs(a - 1.0) < Tolerance && Math.Abs(b) < Tolerance &&
                Math.Abs(c) < Tolerance && Math.Abs(d - 1.0) < Tolerance)
            {
                return string.Empty;
            }

            // Stated about the block's own origin, because that is the point the model turns and mirrors it about.
            double ox = text.Origin.X;
            double oy = text.Origin.Y;
            return $"matrix({Number(a)},{Number(b)},{Number(c)},{Number(d)}," +
                   $"{Number(ox - ((a * ox) + (c * oy)))},{Number(oy - ((b * ox) + (d * oy)))})";
        }

        /// <summary>
        /// Whether a block's text survives SVG's white-space collapse, and so needs `xml:space="preserve"`.
        ///
        /// The reader collapses runs of white space and strips the ends the way the specification says, which is
        /// right for the files that mean it and lossy for a block whose spacing is content - a run of spaces set
        /// deliberately, or a leading one. Asking for the spaces to be kept is what makes those come back.
        /// </summary>
        private static bool PreservesSpace(TextItem text) => NeedsPreserve(text.PlainText);

        /// <summary>
        /// Whether a stretch of text would survive a round trip through SVG's white-space collapse, and so has to
        /// ask for the spaces to be kept.
        ///
        /// The reader collapses runs of white space and strips the ends the way the specification says, which is
        /// right for the files that mean it and lossy for text whose spacing is content - a run of spaces set
        /// deliberately, or a leading one. It is asked **once for a block**, by both writers, so a block cannot come
        /// back with one paragraph asking for its spaces and the next not - which the reader would read as two
        /// differently styled runs.
        /// </summary>
        private static bool NeedsPreserve(string plain)
        {
            // **Every white space that is not a lone interior space is content the collapse would rewrite.** The
            // reader collapses with `char.IsWhiteSpace`, which is Unicode white space and not just the four XML
            // characters - so a non-breaking space, which is what a French typesetter puts before `!`, came back as
            // a plain one. The picture moves by less than a space and nothing was reported. The three cases the
            // block used to check (leading, trailing, doubled) are all here; what is added is the character that is
            // white space the file meant literally.
            for (int i = 0; i < plain.Length; i++)
            {
                char c = plain[i];
                if (!char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (c != ' ' || i == 0 || i == plain.Length - 1 ||
                    char.IsWhiteSpace(plain[i - 1]) || char.IsWhiteSpace(plain[i + 1]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A family name as CSS wants it: quoted when it is not a single word, because a family with a space in it
        /// is a list of two families to a parser that is not told otherwise.
        /// </summary>
        private static string Face(string family)
        {
            string name = family.Trim();
            if (name.Length == 0)
            {
                return TextItem.DefaultFontFamily;
            }

            bool simple = name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
            return simple ? name : $"'{name}'";
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

            // **A hatch is art, and a colour is not a stand-in for it.** The model's fill can be a set of ruled
            // lines, and SVG would state that as a `pattern`; this writer has no pattern output, so `FillAttribute`
            // falls back to the fill's own colour. That fallback is a solid where the document drew hatching - a
            // plausible substitute, which is the one answer this exporter is not allowed to give quietly - so the
            // path is named in the report instead. The SVG reader never builds a hatch, so this cannot fire on a
            // document that came from one; it fires on a document this editor made.
            if (path.Fill.Hatch is { IsEmpty: false })
            {
                Report(path, "the fill is hatching, and the writer has no SVG pattern output for one: the file " +
                    "states the fill's own colour as a solid instead of the lines the document draws");
            }

            // The clips this path carries, written once for however many elements it becomes: the outline is in the
            // path's own space, and a path is not a container, so there is no transform between them.
            string? clip = WriteClips(path, AffineTransform.Identity);

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

                // **`fill-rule` is written even when the fill is `none`.** The reader reads it either way, so a
                // file that stated `fill-rule:evenodd` on an unfilled shape came back with `NonZero`: a value the
                // file wrote, silently replaced. The picture is identical - there is no fill for a rule to apply
                // to - which is why this one is easy to miss and why the round trip is what catches it.
                if (fill is not null || path.Fill.Rule == FillRule.EvenOdd)
                {
                    element.Add(new XAttribute("fill-rule", fillRule));
                }

                if (fill is not null)
                {
                    WriteFillOpacity(element, path);
                }

                if (path.Opacity < 1.0)
                {
                    element.Add(new XAttribute("opacity", Number(path.Opacity)));
                }

                if (!string.IsNullOrEmpty(path.FilterId))
                {
                    element.Add(new XAttribute("filter", $"url(#{path.FilterId})"));
                }

                // **The namespaced baggage goes on whatever element carries the path, stroke or no stroke.** This
                // used to be inside the `else`, so a path with exactly one natively-writable stroke - the common
                // case, and the one every Inkscape file is made of - was written without any of its foreign
                // attributes and without its label. The picture was right, nothing was reported, and the
                // `sodipodi:nodetypes` a person's hand-edited path carried was gone.
                ApplyForeign(element, path);

                if (strokes.Count == 1)
                {
                    WriteNativeStrokeAttributes(element, strokes[0]);
                }
                else
                {
                    element.Add(new XAttribute("stroke", "none"));
                }

                if (clip is not null)
                {
                    element.SetAttributeValue("clip-path", $"url(#{clip})");
                }

                parent.Add(element);
                Wrote("path");

                // The artwork the stroke's brush maps goes over the stroke the pen drew, exactly where the canvas
                // and the PDF exporter put it - a brush is a stroke property, so it is written beside the stroke
                // rather than instead of it.
                if (strokes.Count == 1)
                {
                    WriteStrokeArt(path, strokes[0], parent, clip);
                }

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
                if (fill is not null || path.Fill.Rule == FillRule.EvenOdd)
                {
                    element.Add(new XAttribute("fill-rule", fillRule));
                }

                if (fill is not null)
                {
                    WriteFillOpacity(element, path);
                }

                ApplyForeign(element, path);
                element.Add(new XAttribute("stroke", "none"));
                if (clip is not null)
                {
                    element.SetAttributeValue("clip-path", $"url(#{clip})");
                }

                parent.Add(element);
                Wrote("path");
            }

            foreach (StrokeSpec stroke in strokes)
            {
                WriteStroke(path, stroke, data, parent, clip);
                WriteStrokeArt(path, stroke, parent, clip);
            }
        }

        /// <summary>
        /// The paint's own opacity, when it is not the default.
        ///
        /// A fill's alpha is part of the paint and not decoration: without it, a shape whose fill is transparent
        /// comes back fully opaque, which is a drawing that changed colour across a save. That matters here for a
        /// specific reason as well - a paint server this reader could not resolve is stored as a **visible but fully
        /// transparent** fill rather than as a colour the file did not name (see <c>SvgPatterns</c>), so dropping the
        /// alpha would turn "we could not paint this" into "this is black" on the way out.
        ///
        /// Written only when there is a fill to make transparent, and only when it is not 1, matching the stroke
        /// half below and the text writer above.
        /// </summary>
        private static void WriteFillOpacity(XElement element, PathItem path)
        {
            if (path.Fill.Color.A < 1.0)
            {
                element.Add(new XAttribute("fill-opacity", Number(path.Fill.Color.A)));
            }
        }

        /// <summary>
        /// Whether SVG can carry this stroke as a stroke rather than as an outline.
        ///
        /// **A bristle brush cannot.** SVG has no attribute for one and the stroke it draws is not the stroke the
        /// model means: the brush replaces the line with the union of its bristles, so writing `stroke-width` here
        /// and placing nothing would draw a line where the document paints a bundle. Every other brush *can*: an
        /// art, pattern or scatter brush is artwork laid over the stroke the pen drew, which
        /// <see cref="WriteStrokeArt"/> writes beside it.
        /// </summary>
        private static bool Native(StrokeSpec stroke)
            => !stroke.HasWidthProfile && !stroke.HasEffects && stroke.Alignment == StrokeAlignment.Center &&
               stroke.Brush is not { IsBristle: true };

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
        /// The artwork a stroke's brush maps along the path, written once per placement (issue #186).
        ///
        /// **A brush is a stroke property and the plan is not where it lives.** <see cref="StrokeRenderPlan"/> is
        /// widths and outlines and an art brush is neither, so `WriteStroke` wrote the stroke's own outline and the
        /// art the model places was dropped - the same defect the canvas and the PDF exporter were each fixed for,
        /// in the third renderer. The placements come from <see cref="PlacedArt.Resolve"/>, the one seam the other
        /// two consume, so the file and the screen cannot come to two answers about where the art sits.
        ///
        /// **Each piece is written through the existing item path**, as a group at the placement's own transform
        /// with the asset's artwork inside it: that is what "the item is drawn in its own frame and the placement
        /// carries that frame onto the path" means in SVG, and it reuses <see cref="WriteItems"/> rather than
        /// writing a second renderer for a path, a raster or a group. The transform is stated on the group rather
        /// than baked into the asset's coordinates, so a raster's placement - which an <see cref="ImageItem"/>
        /// cannot state, having no rotation - travels as the matrix SVG is turned by.
        ///
        /// **A pattern brush's tiles and a scatter brush's copies come through here too.** They are artwork placed
        /// along the path in the same sense, and <see cref="PlacedArt.Resolve"/> answers for all three, so no kind
        /// needs a writing route of its own.
        ///
        /// **The stroke's clip goes on a group of its own.** A `clip-path` is applied in the user space the
        /// element's own `transform` establishes, and a placement turns that space - so the outline, which the
        /// model recorded in the path's frame, would be moved and turned with the art if the two shared one
        /// element. The host group states no transform, and the pieces hang under it.
        ///
        /// A piece's own opacity is multiplied into the stroke's rather than replacing it, which is the product
        /// the canvas paints and the exporter writes: a scatter brush's translucent copies are a fact about the
        /// copy, and losing it would draw them fully opaque.
        /// </summary>
        private void WriteStrokeArt(PathItem path, StrokeSpec stroke, XElement parent, string? itemClip)
        {
            if (stroke.Brush is not { } brush || _artDepth >= MaxArtDepth ||
                (!brush.IsArt && !brush.IsPattern && !brush.IsScatter))
            {
                return;
            }

            IReadOnlyList<PlacedArt> art = PlacedArt.Resolve(_document, path, brush, 1.0, stroke.Pen);
            if (art.Count == 0)
            {
                // A brush with no artwork, or one whose asset the document does not have: there is nothing to
                // place, and substituting something would draw art the model never described.
                return;
            }

            XElement host = parent;
            if (itemClip is not null)
            {
                host = new XElement(Svg + "g", new XAttribute("clip-path", $"url(#{itemClip})"));
                parent.Add(host);
                Wrote("g");
            }

            _artDepth++;
            try
            {
                foreach (PlacedArt piece in art)
                {
                    var group = new XElement(Svg + "g");

                    string transform = TransformAttribute(piece.Placement.Transform);
                    if (transform.Length > 0)
                    {
                        group.Add(new XAttribute("transform", transform));
                    }

                    double opacity = path.Opacity * stroke.EffectiveOpacity * piece.Opacity;
                    if (opacity < 1.0)
                    {
                        group.Add(new XAttribute("opacity", Number(opacity)));
                    }

                    WriteItems(new[] { piece.Asset }, group);

                    // **The copy is anonymous.** The asset is an item of the document and is written where it
                    // lives; its artwork here is a second element of the same shape, so keeping the `id` would
                    // state one id twice - which a reader resolves in whichever order it happens to walk the tree.
                    foreach (XElement written in group.DescendantsAndSelf())
                    {
                        written.Attribute("id")?.Remove();
                    }

                    host.Add(group);
                    Wrote("g");
                }
            }
            finally
            {
                _artDepth--;
            }
        }

        /// <summary>
        /// One stroke: natively when SVG can carry it, as an outline when it cannot.
        ///
        /// The three cases that have to become outlines are a **width profile**, any **outline effect**, and an
        /// **alignment** other than centre. Each is a thing SVG has no attribute for, and each is drawn the same
        /// way the renderers draw it, so the file and the canvas agree.
        /// </summary>
        private void WriteStroke(PathItem path, StrokeSpec stroke, string data, XElement parent, string? itemClip)
        {
            var element = new XElement(Svg + "path");
            if (!string.IsNullOrEmpty(path.Name))
            {
                element.Add(new XAttribute("id", path.Name));
            }

            ApplyForeign(element, path);
            element.Add(new XAttribute("fill", "none"));

            if (stroke.HasWidthProfile || stroke.HasEffects || stroke.Brush is { IsBristle: true })
            {
                StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, stroke);
                string outline = Outlines(plan.Outlines);
                if (outline.Length == 0)
                {
                    return;
                }

                // **A brush whose bristles differ in colour writes one path per bristle.** A colour jitter is a
                // colour per loop, and an SVG `path` carries one fill, so the loops are written apart rather than
                // merged - which is also the only way the file can say what the canvas paints. A brush that does
                // not jitter leaves `Paints` null and takes the one-path route below, unchanged.
                if (plan.Paints is { } paints)
                {
                    WriteBristles(path, stroke, plan, paints, parent, itemClip);
                    return;
                }

                element.SetAttributeValue("d", outline);
                element.SetAttributeValue("fill", Hex(stroke.Color));
                element.SetAttributeValue("fill-rule", "nonzero");
                if (itemClip is not null)
                {
                    element.SetAttributeValue("clip-path", $"url(#{itemClip})");
                }

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
            if (itemClip is not null)
            {
                element.SetAttributeValue("clip-path", $"url(#{itemClip})");
            }

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
                string alignment = WriteAlignmentClip(path, stroke.Alignment);
                element.SetAttributeValue("stroke-width", Number(stroke.Width * 2));

                // An element carries one `clip-path`, so when the item is clipped as well the two outlines are
                // composed by putting the item's clip on a group around it - the intersection, which is what the
                // model means by an item inside two clips. Overwriting one with the other would delete a crop.
                if (itemClip is not null)
                {
                    element.SetAttributeValue("clip-path", $"url(#{alignment})");
                    var wrapper = new XElement(Svg + "g", new XAttribute("clip-path", $"url(#{itemClip})"));
                    wrapper.Add(element);
                    parent.Add(wrapper);
                    Wrote("g");
                    Wrote("path");
                    return;
                }

                element.SetAttributeValue("clip-path", $"url(#{alignment})");
            }

            parent.Add(element);
            Wrote("path");
        }

        /// <summary>
        /// A bristle brush written as one `path` per bristle, each filled with the colour the model gave it.
        ///
        /// **A brush whose bristles differ in colour has no single `path` that states it.** The plan carries one
        /// colour per loop, and seeing them drawn is the whole point of a colour jitter, so the loops are written
        /// apart. That is also why this is not folded into <see cref="WriteStroke"/>'s one-element route: an
        /// element carries one `fill`, and merging the bristles would paint them all the stroke's colour - the
        /// recording-without-honouring defect this writer has already been fixed for twice.
        ///
        /// The `id` and the item's foreign attributes go on the first bristle only. An `id` states one element, and
        /// repeating it would leave a reader resolving one name to whichever it happened to walk first, which is
        /// the rule <see cref="WriteStrokeArt"/> already follows for a copy of an asset.
        /// </summary>
        private void WriteBristles(
            PathItem path,
            StrokeSpec stroke,
            StrokeRenderPlan plan,
            IReadOnlyList<ColorRgb> paints,
            XElement parent,
            string? itemClip)
        {
            for (int i = 0; i < plan.Outlines.Count; i++)
            {
                var element = new XElement(Svg + "path");
                if (i == 0 && !string.IsNullOrEmpty(path.Name))
                {
                    element.Add(new XAttribute("id", path.Name));
                }

                if (i == 0)
                {
                    ApplyForeign(element, path);
                }

                string data = Outlines(new[] { plan.Outlines[i] });
                if (data.Length == 0)
                {
                    continue;
                }

                element.SetAttributeValue("d", data);
                element.SetAttributeValue("fill-rule", "nonzero");

                ColorRgb paint = i < paints.Count ? paints[i] : stroke.Color;
                element.SetAttributeValue("fill", Hex(paint));
                if (paint.A < 1.0)
                {
                    element.SetAttributeValue("fill-opacity", Number(paint.A));
                }

                if (itemClip is not null)
                {
                    element.SetAttributeValue("clip-path", $"url(#{itemClip})");
                }

                parent.Add(element);
                Wrote("path");
            }
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

        /// <summary>
        /// Every clip an item carries, as one `clipPath` in `defs`, and the id to refer to it by - or null when
        /// there is nothing to write or nothing this writer can honestly write.
        ///
        /// **The frame is the model's, and the model's frame is the containing space.** The PDF exporter is the
        /// established reading of that (see `PdfDocumentExporter.AppendClip`, which is handed the item's
        /// `toDoc`), and the SVG reader states the same thing when it puts a viewport port on the element's own
        /// group "in the space the enclosing element wrote `x`/`y`/`width`/`height` in": a clip is the outline an
        /// item is drawn inside, so the group's own transform maps its children into the clip's space rather than
        /// moving the clip out of it.
        ///
        /// SVG applies an element's `clip-path` in the user space **its own** `transform` establishes, so a
        /// group's outline is carried into that space by the inverse of the group's transform. The rendered result
        /// is then the outline in the frame the model recorded, for any affine transform including a rotation or a
        /// skew. An item that is not a group has an identity transform, and composes with nothing.
        ///
        /// **What cannot be written is reported, not dropped.** A group whose transform is singular has no local
        /// space for its clip to be stated in - the outline would collapse - and a clip whose outline holds
        /// non-finite coordinates is one the model itself refuses to apply (`ClipSpec.Contains` answers "nothing is
        /// inside" for it). Both are named in <see cref="Missing"/>, the same surface a dropped text block or raster
        /// goes to, because a crop that silently disappears from a file is the defect this writer exists to avoid.
        ///
        /// Nothing is written at all for an item with no clips, which is what keeps the output of every document
        /// that holds none byte-identical to what it was before clips were written.
        /// </summary>
        private string? WriteClips(LayerItem item, AffineTransform toParent)
        {
            if (item.Clips.Count == 0)
            {
                return null;
            }

            AffineTransform outlineFrame = AffineTransform.Identity;
            bool composes = Math.Abs(toParent.A - 1.0) > 1e-12 || Math.Abs(toParent.B) > 1e-12 ||
                            Math.Abs(toParent.C) > 1e-12 || Math.Abs(toParent.D - 1.0) > 1e-12 ||
                            Math.Abs(toParent.E) > 1e-12 || Math.Abs(toParent.F) > 1e-12;
            if (composes)
            {
                // A singular transform maps every point to a line, so the item has no interior and there is no
                // outline to state in its space; saying so is the only honest answer.
                if (!toParent.IsInvertible)
                {
                    Report(item, "the clip could not be written: the group's transform is not invertible, so the " +
                                 "outline has no local space to be stated in");
                    return null;
                }

                outlineFrame = toParent.Inverted();
            }

            foreach (ClipSpec clip in item.Clips)
            {
                foreach (SubPath sub in clip.SubPaths)
                {
                    foreach (PathNode node in sub.Nodes)
                    {
                        if (!double.IsFinite(node.Anchor.X) || !double.IsFinite(node.Anchor.Y) ||
                            !double.IsFinite(node.InHandle.X) || !double.IsFinite(node.InHandle.Y) ||
                            !double.IsFinite(node.OutHandle.X) || !double.IsFinite(node.OutHandle.Y))
                        {
                            Report(item, "the clip could not be written: its outline holds a coordinate that is " +
                                         "not finite, so there is no shape to write");
                            return null;
                        }
                    }
                }
            }

            string id = $"clip{++_clipCount}";
            var element = new XElement(Svg + "clipPath",
                new XAttribute("id", id),
                new XAttribute("clipPathUnits", "userSpaceOnUse"));

            foreach (ClipSpec clip in item.Clips)
            {
                foreach (SubPath sub in clip.SubPaths)
                {
                    // A shape with no extent restricts nothing, so it states nothing the file did not already say.
                    if (sub.Nodes.Count < 2)
                    {
                        continue;
                    }

                    var path = new XElement(Svg + "path");
                    if (clip.Rule == FillRule.EvenOdd)
                    {
                        path.Add(new XAttribute("clip-rule", "evenodd"));
                    }

                    path.Add(new XAttribute("d", Outline(new[] { Transformed(sub, outlineFrame) })));
                    element.Add(path);
                }
            }

            if (!element.HasElements)
            {
                Report(item, "the clip could not be written: its outline holds no subpath with geometry");
                return null;
            }

            XElement defs = _root.Element(Svg + "defs") ?? new XElement(Svg + "defs");
            defs.Add(element);
            if (defs.Parent is null)
            {
                _root.AddFirst(defs);
            }

            return id;
        }

        /// <summary>One clip subpath through a transform, so its outline is stated in the frame being written in.</summary>
        private static SubPath Transformed(SubPath sub, AffineTransform transform)
        {
            var copy = new SubPath { IsClosed = sub.IsClosed };
            foreach (PathNode node in sub.Nodes)
            {
                copy.Nodes.Add(new PathNode(
                    transform.Transform(node.Anchor),
                    transform.Transform(node.InHandle),
                    transform.Transform(node.OutHandle)));
            }

            return copy;
        }

        /// <summary>
        /// A nested `svg` whose viewport is the port it clips to.
        ///
        /// **The port's position is carried by the transform, not by `x` and `y`.** The reader composes a nested
        /// element's `transform` with its `x` and `y` (§7.4), and the model's group transform already carries the
        /// port's own offset - so writing the offset in both places puts the viewport at twice the distance. Only
        /// the size is written here, and the transform `WriteItems` writes alongside it places it: the port is the
        /// rectangle of that size at the origin of a space the transform has already moved.
        ///
        /// `overflow="hidden"` is SVG's own default on a viewport - the reader honours that default, and a file
        /// that said `visible` would not have been given a clip at all - so it is written out rather than left
        /// implicit: a person reading the file should see the crop stated, not have to know the default to find it.
        /// </summary>
        private static XElement ViewportElement((AffineTransform Transform, Rect2D Port) viewport)
            => new(Svg + "svg",
                new XAttribute("width", Number(viewport.Port.Width)),
                new XAttribute("height", Number(viewport.Port.Height)),
                new XAttribute("overflow", "hidden"));

        /// <summary>
        /// The viewport a nested `svg` would inherit from a group whose clip is the port itself, or null when the
        /// clip is not one this writer can put back as a nested `svg` honestly.
        ///
        /// **Why a nested `svg` at all.** The reader records a nested viewport's port as a clip on the element's own
        /// group, and it reads a nested `svg` back into exactly that: the port is the crop *and* the declaration of
        /// where the content is placed. Writing the group back as a plain `g` with a `clip-path` draws the same
        /// picture in a viewer - the reference rendering is identical - but this reader drops a bare `clip-path`, so
        /// the crop would be gone the second time the file was opened. The nested `svg` is the one spelling that
        /// survives the round trip, and it is what the file said in the first place.
        ///
        /// **Only when the port really is the clip.** The port is written in the containing space, so a viewport
        /// can carry it only when the group's transform moves the content without turning or scaling the space the
        /// port is stated in - otherwise the port is a parallelogram, which `x`/`y`/`width`/`height` cannot say.
        /// Anything else, including every arbitrary clip the PDF importer records for a form's `/BBox`, falls back
        /// to `clip-path`, which is the general and always-honest spelling.
        /// </summary>
        private static (AffineTransform Transform, Rect2D Port)? NestedViewport(ArtGroup group, ClipSpec clip)
        {
            if (group.Clips.Count != 1 || clip.Rule != FillRule.NonZero)
            {
                return null;
            }

            AffineTransform transform = group.Transform;
            const double Tolerance = 1e-9;
            bool moves = Math.Abs(transform.B) > Tolerance || Math.Abs(transform.C) > Tolerance;
            bool turns = Math.Abs(transform.A - 1.0) > Tolerance || Math.Abs(transform.D - 1.0) > Tolerance;
            if (moves || turns)
            {
                return null;
            }

            List<Point2D> corners = clip.SubPaths
                .Where(sub => sub.IsClosed && sub.Nodes.Count == 4)
                .SelectMany(sub => sub.Nodes)
                .Select(node => node.Anchor)
                .ToList();

            if (corners.Count != 4)
            {
                return null;
            }

            double left = corners.Min(p => p.X);
            double top = corners.Min(p => p.Y);
            double right = corners.Max(p => p.X);
            double bottom = corners.Max(p => p.Y);

            // The four corners have to **be** the axis-aligned rectangle they are written as. A rectangle read from
            // a rotated element, or one whose edges are diagonal, would come back as a different crop.
            foreach (Point2D corner in corners)
            {
                bool onCorner = (Math.Abs(corner.X - left) < Tolerance || Math.Abs(corner.X - right) < Tolerance) &&
                                (Math.Abs(corner.Y - top) < Tolerance || Math.Abs(corner.Y - bottom) < Tolerance);
                if (!onCorner)
                {
                    return null;
                }
            }

            if (right - left <= Tolerance || bottom - top <= Tolerance)
            {
                return null;
            }

            return (transform, new Rect2D(left, top, right - left, bottom - top));
        }

        /// <summary>The `d` attribute for a path: an explicit curve per segment, and `Z` when it closes.</summary>
        private static string PathData(PathItem path) => Outline(path.SubPaths);

        /// <summary>
        /// The `d` attribute for a run of subpaths.
        ///
        /// Shared with the clip outlines, because a clip path in SVG is written in exactly the same path syntax as
        /// the artwork it restricts - one spelling means a clip cannot drift away from the shape it was read from.
        /// </summary>
        private static string Outline(IReadOnlyList<SubPath> subpaths)
        {
            var builder = new StringBuilder();

            foreach (SubPath sub in subpaths)
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
        private string? FillAttribute(PathItem path) => FillAttribute(path.Fill);

        /// <summary>
        /// The same answer for a fill that is not on a path - an instance's use-site presentation, which is written
        /// on the group because the model has no `use` element to state it on.
        /// </summary>
        private string? FillAttribute(FillSpec fill)
        {
            if (!fill.IsVisible)
            {
                return null;
            }

            if (fill.Gradient is not null && _gradientIds.TryGetValue(fill.Gradient, out string? id))
            {
                return $"url(#{id})";
            }

            return Hex(fill.Color);
        }

        /// <summary>
        /// The presentation a `use` site established for the instance, written on the group element beside
        /// `data-source`.
        ///
        /// **Why on the group and not as a `<use>`.** The writer deliberately inlines the instance as the copy it
        /// holds, because the copy is what is drawn - the use's paint is baked into it - and emitting a real `use`
        /// would draw the *target* instead and change the picture on the next import. The reference therefore
        /// travels as an attribute, and the style the reference carried has to travel beside it: without it,
        /// reopening a saved file and refreshing an instance would repaint it from the definition and lose the
        /// paint the file states, which is the loss the member exists to close.
        ///
        /// A viewer reads these attributes as inherited paint for anything inside the group that does not state its
        /// own, which is exactly what they mean, and every item the writer emits states its own - so the exported
        /// picture is unchanged.
        /// </summary>
        private void WriteInstancePresentation(XElement element, InstancePresentation presentation)
        {
            string? fill = FillAttribute(presentation.Fill);
            element.Add(new XAttribute("fill", fill ?? "none"));

            if (fill is not null)
            {
                if (presentation.Fill.Color.A < 1.0)
                {
                    element.Add(new XAttribute("fill-opacity", Number(presentation.Fill.Color.A)));
                }

                if (presentation.Fill.Rule == FillRule.EvenOdd)
                {
                    element.Add(new XAttribute("fill-rule", "evenodd"));
                }
            }

            if (presentation.Stroke is { IsVisible: true } stroke)
            {
                // The recorded presentation is a `StrokeSpec` the reader parsed, so its stroke is SVG-native by
                // construction: no brush, no width profile, no outline effects and a centred alignment.
                WriteNativeStrokeAttributes(element, stroke);
            }
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
    {
        // Negative zero is the same number as zero and a different string, and a transform written from a mirror
        // or a quarter turn produces one on every axis the sign does not reach. Writing `-0` where the file means
        // `0` is a difference nobody asked for, and it survives a byte comparison.
        if (value == 0.0)
        {
            value = 0.0;
        }

        // **The shortest spelling that reads back as the same double.** This was `0.#####`, which rounds every
        // coordinate to five decimals - and a round trip is required to return the numbers it started with, not a
        // rendering of them. The loss is invisible in the picture, which is why it survived until a model-dump
        // comparison was run over the corpus: a node rounded by 0.00004pt draws the same and is a different
        // document, and a transform composed from a view-box fit loses more than that.
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

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
