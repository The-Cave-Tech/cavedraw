using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>What an SVG import produced: the document, how many objects of each element type, and what was not found.</summary>
public sealed record SvgImportResult(
    CadDocument Document,
    IReadOnlyDictionary<string, int> ByElement,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Warnings)
{
    /// <summary>How many objects were imported in total.</summary>
    public int Objects => ByElement.Values.Sum();
}

/// <summary>
/// Reads SVG into the model.
///
/// **Coordinates need no flip.** SVG's y axis grows downward and so does the model's, so an SVG imports with its
/// geometry as authored and the two agree about which way is down. PDF is the format that disagrees, which is why
/// the flip lives in the PDF exporter.
///
/// **The grouping is the document.** A `g` becomes a group rather than being flattened into its children, because
/// the file's structure is meaning - flattening it is how a click ends up selecting the wrong thing, and it is a
/// mistake this project has already made once with PDF form XObjects. A group keeping its own transform is also
/// what makes nested transforms compose the way the file says rather than approximately.
///
/// What this reads today: the basic shapes, paths, groups, `use` and symbols, filters, gradients, `image`, `text`
/// with its runs, the view box, CSS and the presentation attributes that decide how they are painted, with every
/// length resolved through its unit, and a `svg` viewport's clip on what it holds. A `marker` is read and **placed
/// as art** at each vertex, because the model has no marker member on a stroke (<see cref="SvgMarkers"/>); a
/// `pattern` is named rather than painted, because the model has no pattern paint server
/// (<see cref="SvgPatterns"/>). The paint servers other than gradients are their own issue.
///
/// The text half lives in <c>SvgTextReader.cs</c>, which is a part of this class because a text element is walked
/// with the same context - the same stylesheets, the same viewport, the same list of things that could not be read -
/// as everything else.
/// </summary>
public static partial class SvgReader
{
    internal static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>
    /// The size a standalone SVG has when it declares neither a width nor a height, in user units.
    ///
    /// This is the default object size CSS gives a replaced element with no intrinsic dimensions, which is what a
    /// browser uses for such a file. Inventing something else - the content's bounding box, say - would make the
    /// same file a different size depending on what happened to be in it.
    /// </summary>
    private const double DefaultViewport = 300.0;

    /// <summary>Reads an SVG document from a string.</summary>
    public static SvgImportResult Read(string svg, string? baseDirectory = null)
    {
        XDocument xml;
        try
        {
            xml = XDocument.Parse(svg, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new SvgImportException($"the SVG is not well-formed XML: {exception.Message}", exception);
        }

        XElement? root = xml.Root;
        if (root is null || root.Name.LocalName != "svg")
        {
            throw new SvgImportException("the document's root element is not <svg>");
        }

        var document = new CadDocument { Name = "Imported SVG" };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var warnings = new HashSet<string>(StringComparer.Ordinal);

        // The artboard is the view port, and the view box becomes a transform on the content rather than a change
        // to the artboard - so an artboard-sized object and a view-box-sized file describe the same picture, and
        // the file's own coordinates survive in the model.
        //
        // **The port is in points and the content is not.** A user unit is a CSS pixel and the model stores points
        // (AGENTS.md §8), so the artboard is three quarters of the number the file writes and the transform below
        // carries the content from the file's units into the model's. Doing it here, once, is what keeps one
        // physical length from being two different numbers depending on where it was written.
        (double width, double height, SvgViewport? viewport, AffineTransform viewBox) =
            ReadViewBox(root, warning => warnings.Add(warning));
        if (width <= 0 || height <= 0)
        {
            throw new SvgImportException(
                "the SVG has no usable size: it needs a width and height, or a viewBox");
        }

        Artboard artboard = document.AddArtboard(new Size2D(width, height), "SVG");
        Layer layer = artboard.AddLayer("SVG");

        // The file's own space applies to everything, so the transform that carries it into the model's points goes
        // on one group - but only when there is something to apply. A group that carries the identity would be
        // structure the file does not have.
        //
        // It is named after the view box only where the file has one. Where it does not, the group carries the unit
        // conversion alone, and calling that a view box would be a name the file never wrote.
        bool stated = Numbers(root.Attribute("viewBox")?.Value) is { Length: 4 };
        ArtGroup? viewGroup = IsIdentity(viewBox)
            ? null
            : new ArtGroup { Name = stated ? "viewBox" : string.Empty, Transform = viewBox };

        // Every id in the file, indexed before anything is drawn: a `use` may refer to a definition that appears
        // after it, and a reader that indexed as it went would find nothing and silently drop the instance.
        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        Index(root, ids);

        // Every style element in the document, in document order - including the ones inside defs, which are
        // still stylesheets and still apply. Collecting them by walking the tree as it is read would miss a sheet
        // defined after the elements it styles, which multi-style.svg does.
        string css = CollectStyles(root);
        SvgStylesheet sheet = SvgStylesheet.Parse(css, baseDirectory);

        // A webfont the file carries is a face this reader loads when it can: the stylesheet's `@font-face` rules
        // name a programme beside the document, and when that programme holds the glyph drawings the text can be
        // drawn with the file's own glyphs rather than with whatever this machine has. The stylesheet reader
        // deliberately does not read at-rules as rules, so they are read here instead.
        SvgFontFaces fontFaces = SvgFontFaces.Load(css, baseDirectory, root);

        // What could not be loaded is said out loud, because the difference is the design: a file that supplies its
        // own face and is drawn with a substitute has a picture nobody asked for.
        if (fontFaces.Any)
        {
            warnings.Add(
                $"the stylesheet declares @font-face and this reader loaded {string.Join(", ", fontFaces.Families)} "
                + "from the document's own directory");
        }
        else if (css.Contains("@font-face", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("the stylesheet declares @font-face, and this reader does not load fonts from the document");
        }

        SvgGradients gradients = SvgGradients.Collect(root, sheet);

        // The patterns, so that a `url(#p)` naming one can be **named as a pattern**. Without this the reference
        // falls past the gradient lookup and is reported as a paint server the document does not have - the right
        // finding with the wrong name, which sends the reader looking in the wrong place (issue #175's lesson, and
        // this reader's own `ClipPathFor` refusal for a value it cannot honour).
        SvgPatterns patterns = SvgPatterns.Collect(root);

        // The markers - Inkscape's arrowheads. A marker is a document asset the way a gradient is, and placing one
        // needs the path's own tangent, so the definitions are collected here and used where a path is read.
        SvgMarkers markers = SvgMarkers.Collect(root);

        // Every `<marker>` becomes a **definition in the library** once the context exists (issue #202): a marker is
        // a reusable definition - a name, content, and attributes saying how to place it - so the model keeps it
        // where a picker can list it and a renderer can place it, rather than in a registry that lives only as long
        // as the import. `ReadMarkerDefinitions` below does that.

        // The live path effects the file defines. Collected up front rather than where `defs` is met, because the
        // reference and the definition can be in either order and a path read before its `defs` would find nothing.
        IReadOnlyDictionary<string, PathEffectDefinition> pathEffects = CollectPathEffects(root);

        // Filters are document assets: an element refers to one by id, so they are collected once and held on the
        // document rather than copied into every element that uses them.
        foreach (FilterSpec filter in SvgFilters.Collect(root, sheet, warning => warnings.Add(warning)).All.Values)
        {
            document.AddFilter(filter);
        }

        // Root-level elements that are neither artwork nor ours: `sodipodi:namedview` holds the grid, the zoom and
        // the page settings, and `<metadata>` holds the RDF. Kept verbatim, because a file that comes back without
        // them resets the document's own settings in Inkscape - a silent rewrite of somebody's file.
        //
        // **The walk below adds to this list rather than only reading it.** An element the reader has to refuse is
        // kept here too, so a refusal costs the drawing and not the file's own text: a `flowRoot` whose region the
        // model cannot hold is the case this exists for (`SvgFlowText.cs`), and dropping it while naming
        // it would still be a silent rewrite of a different kind. It is written back at the root, which is where the
        // file's root-level baggage belongs.
        var kept = new List<string>(root.Elements()
            .Where(child => child.Name.LocalName == "metadata" ||
                            (child.Name.Namespace != XNamespace.None && child.Name.Namespace != Svg))
            .Select(child => child.ToString()));

        // The prefixes the file declared, so what is written back uses the same ones. A namespace is a namespace to
        // a parser, but a file that comes back as `p1:label` instead of `inkscape:label` is not the file that went
        // in, and a person reading it would reasonably call that a rewrite.
        document.SetSvgNamespaces(root.Attributes()
            .Where(attribute => attribute.IsNamespaceDeclaration)
            .Select(attribute => new KeyValuePair<string, string>(
                attribute.Name.LocalName == "xmlns" ? string.Empty : attribute.Name.LocalName,
                attribute.Value)));

        var context = new Context
        {
            Layer = layer,
            Group = viewGroup,
            Style = PresentationStyle.Default,
            Counts = counts,
            Ids = ids,
            Resolving = new HashSet<string>(StringComparer.Ordinal),
            Missing = new List<string>(),
            UsedPathEffects = new HashSet<string>(StringComparer.Ordinal),
            Sheet = sheet,
            Gradients = gradients,
            Patterns = patterns,
            Markers = markers,
            PathEffects = pathEffects,
            Warnings = warnings,
            Kept = kept,
            Viewport = viewport,
            BaseDirectory = baseDirectory,

            // The faces the document supplies for itself, loaded from its own `@font-face` rules. The sheet reader
            // does not read at-rules as rules - right for `@media`, wrong for this one, which is a resource rather
            // than a style - so they are read here and the glyph definitions travel with the import.
            FontFaces = fontFaces,

            // The root's own font properties, which a text element inherits like anything else. `xml:space` is
            // declared on the root in Inkscape's own files, so a reader that only read it on the elements it walked
            // would collapse white space the file asked it to keep.
            Text = SvgTextStyle.From(
                root,
                SvgTextStyle.Default,
                sheet.DeclarationsFor(root, Array.Empty<XElement>()),
                warning => warnings.Add(warning)),
        };

        // The marker definitions, before the elements are walked: a path read before the `defs` that holds its
        // marker is the ordinary case in a file where `defs` comes last.
        ReadMarkerDefinitions(root, context);

        foreach (XElement child in root.Elements())
        {
            ReadElement(child, context);
        }

        if (viewGroup is { Children.Count: > 0 })
        {
            layer.AddItem(viewGroup);
        }

        // **The definitions themselves.** An instance records the id it came from and holds a copy of the content,
        // which is the picture and the link; neither is the same as the definition being *there*. Without it there
        // is nothing for an edit to change and nothing for a re-resolution to read, which is exactly the half of
        // #117 that storing the link did not deliver. Read after the tree, from the same id index the `use`
        // elements were resolved through, so a definition that appears after its use is still found - and before
        // the unreferenced effects are collected, because an effect a definition names is one something refers to.
        ReadDefinitions(root, context, document);

        // An effect no path in the document named is a definition with no home on any item, so it is kept on the
        // document itself. Collected by id from what was read up front rather than from the XML again, so the
        // element that goes back out is the one the file wrote. See CadDocument.ForeignPathEffects (issue #155).
        document.SetForeignPathEffects(pathEffects
            .Where(entry => !context.UsedPathEffects.Contains(entry.Key))
            .Select(entry => entry.Value.Xml));

        // Everything kept verbatim: the file's own root-level baggage, and whatever the walk had to refuse.
        document.SetSvgExtras(kept);

        return new SvgImportResult(document, counts, context.Missing, warnings.OrderBy(w => w, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Every stylesheet in the document, joined in document order.
    ///
    /// Document order matters: two rules of equal specificity are decided by which comes later, so a reader that
    /// applied each sheet as it met it would get a file with two `style` elements wrong whenever the later one
    /// restates a class - which is exactly what Inkscape's multi-style file tests.
    ///
    /// A sheet inside `defs` counts. `defs` means "not drawn here", not "not applied": it is still a stylesheet,
    /// and skipping it because its parent is not drawn loses the colours of everything it styles.
    /// </summary>
    private static string CollectStyles(XElement root)
    {
        var builder = new System.Text.StringBuilder();

        foreach (XElement style in root.DescendantsAndSelf().Where(e =>
                     e.Name.LocalName == "style" &&
                     (string.IsNullOrEmpty(e.Name.NamespaceName) || e.Name.Namespace == Svg)))
        {
            // A CDATA section and a comment inside a stylesheet both arrive as content; `Value` gives the text
            // either way, and the CSS reader strips the comments.
            string text = style.Value;
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.Append('\n').Append(text).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Keeps the namespaced attributes the model has no meaning for, and reads the two that matter.
    ///
    /// SVG files are dense with `inkscape:` and `sodipodi:` attributes - Inkscape's own test files use `inkscape:`
    /// 226 times and `sodipodi:` 94 - and this repository's rule is *reflect the file, never invent*. Dropping them
    /// silently rewrites every document that passes through.
    ///
    /// Two of them are not decoration: **`inkscape:label` IS the layer or object name** (it is what a person sees
    /// in the layer list), and `sodipodi:insensitive` is what locks a layer. Everything else is carried verbatim.
    /// </summary>
    private static void CaptureForeign(XElement element, LayerItem item)
    {
        foreach (KeyValuePair<string, string> attribute in ReadForeign(element))
        {
            item.ForeignAttributes[attribute.Key] = attribute.Value;
        }

        // A child element the model has no meaning for is kept the same way its attributes are. Reading only the
        // attributes would drop a file's own data while looking as though it had been preserved.
        foreach (XElement child in element.Elements())
        {
            if (child.Name.Namespace != XNamespace.None && child.Name.Namespace != Svg)
            {
                item.ForeignElements.Add(child.ToString());
            }
        }

        if (element.Attribute(Inkscape + "label")?.Value is { Length: > 0 } label)
        {
            item.Name = label;
        }

        if (string.Equals(element.Attribute(Sodipodi + "insensitive")?.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            item.IsLocked = true;
        }
    }

    /// <summary>
    /// The namespaced attributes this element carries, keyed by the name the **file** used - prefix included - so
    /// the writer can put them back under the same prefixes rather than machine-generated ones.
    /// </summary>
    private static Dictionary<string, string> ReadForeign(XElement element)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (XAttribute attribute in element.Attributes())
        {
            // **Our own marker-art tag is read back** (issue #202). It has no namespace - the writer states it as a
            // plain `data-` attribute - and it records that this artwork is the arrowhead a path's reference stands
            // for, which the writer consults. Dropping it here would lose that on every reopen. Only this name is
            // treated so: a file's own `data-*` attributes are not this reader's to keep, and keeping them would
            // change what a round trip produces for files that have them.
            if (attribute.Name.Namespace == XNamespace.None && attribute.Name.LocalName == SvgWriter.MarkerArtTag)
            {
                attributes[attribute.Name.LocalName] = attribute.Value;
                continue;
            }

            if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace == XNamespace.None)
            {
                continue;
            }

            // `xlink:href` and `xml:*` are the parser's business, not the file's baggage.
            if (attribute.Name.NamespaceName is "http://www.w3.org/1999/xlink"
                or "http://www.w3.org/XML/1998/namespace")
            {
                continue;
            }

            string? prefix = element.GetPrefixOfNamespace(attribute.Name.Namespace);
            if (prefix is null)
            {
                // No prefix in scope means the attribute's namespace is the default one, which attributes never
                // take - so there is nothing to write back under a prefix.
                continue;
            }

            attributes[$"{prefix}:{attribute.Name.LocalName}"] = attribute.Value;
        }

        return attributes;
    }

    private static readonly XNamespace Inkscape = "http://www.inkscape.org/namespaces/inkscape";
    private static readonly XNamespace Sodipodi = "http://sodipodi.sourceforge.net/DTD/sodipodi-0.0.dtd";

    /// <summary>
    /// One live path effect the file wrote, with the element itself kept exactly as it was written.
    ///
    /// The <see cref="Spec"/> is what the effect <em>means</em> - the name and the parameters, read out by attribute
    /// so they can be translated into a stroke. The <see cref="Xml"/> is what the effect <em>is</em>: the element,
    /// verbatim, so an export can put it back with Inkscape's own attribute spellings rather than reconstructing
    /// them from the spec and getting one of the many `<c>lpeversion</c>` meanings wrong.
    /// </summary>
    private sealed record PathEffectDefinition(PathEffectSpec Spec, string Xml);

    /// <summary>
    /// Every live path effect the document defines, by the id a path refers to it by.
    ///
    /// **A definition is found by its id, not by where it sits.** Inkscape writes these into `defs`, but `defs` is
    /// not the point - the reference is - and the reader meets a path's effect while it is inside a `use`, a nested
    /// `svg` or a symbol, all of which are walked from their own context. Collecting them once, before anything is
    /// drawn, is what lets a path that appears before its `defs` still find its effect.
    ///
    /// **Only foreign-namespace elements.** An effect this reader has no meaning for is still kept, because a
    /// document that comes back without it has lost the reason its stroke looks the way it does - but an SVG
    /// element inside `defs` is not an effect and is handled (or deliberately not handled) as the SVG element it is.
    ///
    /// **A definition nothing refers to is kept at the document level.** An effect no path names says nothing about
    /// how anything is drawn, so no item can hold it - but it is still content the file carried, and a file whose
    /// `defs` keeps a library of named effects would otherwise lose the unused half of it in silence (issue #155).
    /// What nothing referred to is therefore handed to the document, which is the only thing that can hold a
    /// definition with no user, and the writer puts it back into `defs` beside the referenced ones.
    /// </summary>
    private static IReadOnlyDictionary<string, PathEffectDefinition> CollectPathEffects(XElement root)
    {
        var effects = new Dictionary<string, PathEffectDefinition>(StringComparer.Ordinal);

        foreach (XElement element in root.Descendants())
        {
            if (element.Name.LocalName != "path-effect" ||
                string.IsNullOrEmpty(element.Name.NamespaceName) ||
                element.Name.Namespace == Svg)
            {
                continue;
            }

            string? id = element.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(id) || effects.ContainsKey(id))
            {
                continue;
            }

            // Everything the element carries except the three attributes read out by name, in the order the file
            // wrote them - which is what makes an export put them back in the order they were found.
            var parameters = new List<KeyValuePair<string, string>>();
            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None)
                {
                    continue;
                }

                if (attribute.Name.LocalName is "effect" or "id" or "lpeversion")
                {
                    continue;
                }

                parameters.Add(new KeyValuePair<string, string>(attribute.Name.LocalName, attribute.Value));
            }

            effects[id] = new PathEffectDefinition(
                new PathEffectSpec(
                    element.Attribute("effect")?.Value ?? string.Empty,
                    id,
                    element.Attribute("lpeversion")?.Value ?? string.Empty,
                    parameters),
                element.ToString());
        }

        return effects;
    }

    /// <summary>
    /// Resolves and applies the live path effect a shape refers to, and says what happened when it cannot.
    ///
    /// **The effect is kept either way.** The element travels on the shape that points at it, so an export can
    /// write it back into `defs` and a second import finds it again - a translation that threw the description away
    /// would draw the right picture once and hand back a file with no effect on it. It is hung here rather than on
    /// the document because the reference is on the path: this is the item that can find it, and the one an export
    /// has to reach it from.
    ///
    /// **An effect this build does not implement is reported by name and the geometry is left alone.** The path
    /// already holds the effect's own output, because that is what `d` is; drawing it without the effect is the
    /// right picture, and redrawing it as an ordinary stroke would be a plausible picture of something the file
    /// never drew. The same goes for a reference that points at nothing: the geometry stands, and the dangling id
    /// is reported beside the missing `use` and image targets rather than only being discoverable by comparing the
    /// file with the drawing.
    ///
    /// **A definition nothing refers to is not lost here.** The id is recorded as used the moment a path names it,
    /// so whatever the walk leaves unclaimed is handed to the document at the end - see
    /// <c>CadDocument.ForeignPathEffects</c>.
    /// </summary>
    private static void ApplyPathEffect(PathItem path, Context context)
    {
        if (PathEffects.ReferenceOn(path) is not { } id)
        {
            return;
        }

        // Recorded before the lookup: a reference that resolves to nothing is still a reference, and the dangling
        // case is reported rather than answered a second time by keeping a definition under the same id.
        context.UsedPathEffects.Add(id);

        if (!context.PathEffects.TryGetValue(id, out PathEffectDefinition? definition))
        {
            context.Missing.Add(
                $"path-effect '{id}' (a path refers to it and the document defines no such effect)");
            return;
        }

        if (!path.ForeignElements.Contains(definition.Xml, StringComparer.Ordinal))
        {
            path.ForeignElements.Add(definition.Xml);
        }

        // The description is kept as state as well as as XML, so the converted profile can be re-derived against
        // the geometry as it stands later (issue #180). Kept whether or not the translation succeeds: the path
        // carries the effect either way, and an effect this build cannot translate is one a driver should still be
        // told about by name when a refresh is asked for.
        path.PathEffect = definition.Spec;

        PathEffectTranslation translation = PathEffects.Translate(definition.Spec, path, path.Stroke);

        // Named by the id the file used *and* by the effect's own name, because the two are what a person has to
        // match against the file and a driver has to match against what this build implements.
        foreach (string note in translation.Notes)
        {
            context.Warnings.Add($"path-effect '{id}': {note}");
        }

        if (!translation.IsSupported)
        {
            context.Warnings.Add($"path-effect '{id}': {translation.Refusal}");
            return;
        }

        path.Stroke = translation.Stroke!;
    }

    /// <summary>`filter="url(#id)"` resolves to the id, or null when the element is not filtered.</summary>
    private static string? FilterReference(XElement element)
    {
        string? value = element.Attribute("filter")?.Value?.Trim();
        if (value is null || !value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int open = value.IndexOf('(');
        int close = value.IndexOf(')');
        if (close <= open)
        {
            return null;
        }

        string reference = value[(open + 1)..close].Trim().Trim('"', '\'');
        return reference.StartsWith('#') && reference.Length > 1 ? reference[1..] : null;
    }

    /// <summary>Indexes every element that has an id, so a reference resolves whichever way round it is written.</summary>
    private static void Index(XElement element, Dictionary<string, XElement> ids)
    {
        string? id = element.Attribute("id")?.Value;
        if (!string.IsNullOrEmpty(id) && !ids.ContainsKey(id))
        {
            ids[id] = element;
        }

        foreach (XElement child in element.Elements())
        {
            Index(child, ids);
        }
    }

    /// <summary>
    /// Reads an SVG document from a file.
    ///
    /// **Gzipped SVG is SVG.** A `.svgz` is an ordinary document whose bytes are gzip-compressed, and both
    /// SVG 1.1 and SVG 2 expect a viewer to decompress it on the way in. Reading it as text hands the parser
    /// binary - the document does not merely lose detail, it fails to parse - so the magic number is checked
    /// first.
    ///
    /// **The output is checked, not only for an exception.** `GZipStream` does not throw on a *truncated*
    /// stream: it silently decompresses to nothing, and the empty string then reaches the XML parser, so the
    /// diagnosis a person gets points at XML rather than at the file being unreadable. A buffer with nothing
    /// in it is therefore treated as a failure in its own right.
    /// </summary>
    public static SvgImportResult ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new SvgImportException($"there is no file at '{path}'");
        }

        byte[] bytes = File.ReadAllBytes(path);

        return Read(
            IsGzipped(bytes) ? Inflate(bytes, path) : Decode(bytes),
            System.IO.Path.GetDirectoryName(path));
    }

    /// <summary>The gzip magic number, which is what distinguishes a `.svgz` from a `.svg` on disk.</summary>
    private static bool IsGzipped(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    private static string Inflate(byte[] bytes, string path)
    {
        byte[] plain;

        try
        {
            using var source = new MemoryStream(bytes);
            using var gzip = new System.IO.Compression.GZipStream(
                source, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            plain = output.ToArray();
        }
        catch (InvalidDataException exception)
        {
            throw new SvgImportException($"'{path}' is not a readable gzip stream: {exception.Message}");
        }

        if (plain.Length == 0)
        {
            throw new SvgImportException(
                $"'{path}' carries a gzip header but decompressed to nothing; the file is truncated or corrupt");
        }

        return Decode(plain);
    }

    /// <summary>
    /// Decodes document bytes as text, honouring a byte-order mark. This replaces <c>File.ReadAllText</c>,
    /// which did the same thing - reading the bytes directly is what makes the gzip check possible, and it
    /// must not quietly change how an ordinary document is decoded.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(
            stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // ------------------------------------------------------------------ the view box

    /// <summary>
    /// The artboard size, the viewport a percentage resolves against, and the transform the file's own space implies.
    ///
    /// **The port is a page and the model measures pages in points.** SVG writes the page in the same user units its
    /// content is written in - one CSS pixel each - and the model stores PDF points, so the number the file writes is
    /// three quarters of the size it describes. A bare `width="800"` is 600 pt of paper, not 800.
    ///
    /// **The conversion happens here, once.** Every length the reader takes anywhere in the document stays in the
    /// file's own units; what carries them into the model is the transform returned below, which maps the file's
    /// space onto the page in points. A length therefore cannot resolve differently depending on which attribute
    /// read it, and a nested element cannot apply the factor a second time. The view box is fitted against the port
    /// **in points** and the box in the file's units, so the fit itself is where the two spaces meet.
    ///
    /// The viewport a percentage resolves against stays in the file's units, because that is the space the geometry
    /// it is measuring sits in until the transform is applied.
    ///
    /// **That reference is the view box where the file states one, and the port only where it does not.** SVG 1.1
    /// §7.10 defines *actual-width* and *actual-height* as the viewport dimension "within the user coordinate system
    /// for the viewport element", and a view box is what establishes that system: the specification's own units
    /// example is `<svg width="400px" height="200px" viewBox="0 0 4000 2000">`, where a percentage resolves against
    /// **4000** rather than 400. Resolving it against the port instead doubles the geometry of every file whose box
    /// differs from its page, and it does so while agreeing with the nested `svg` path about nothing - the same
    /// specification answered two ways in one reader.
    ///
    /// `preserveAspectRatio` is honoured in its two common forms: **meet** fits the whole view box inside the view
    /// port and leaves space, **slice** fills the view port and crops, and **none** stretches - the one that
    /// changes an object's shape. Reading it wrong scales the artwork by the wrong factor in one axis, which looks
    /// like a font problem and is not one.
    ///
    /// The reference is **nullable**, because a file that states neither a view box nor a size has none: the
    /// viewport it imports at is then CSS's default object size rather than the file's own statement, and a
    /// percentage measured against it is a length nobody wrote. Null says exactly that and the caller reports it.
    /// </summary>
    private static (double Width, double Height, SvgViewport? Viewport, AffineTransform Transform) ReadViewBox(
        XElement root, Action<string> warn)
    {
        double? width = RootLength(root.Attribute("width")?.Value, "width", warn);
        double? height = RootLength(root.Attribute("height")?.Value, "height", warn);
        double[]? box = Numbers(root.Attribute("viewBox")?.Value);

        if (box is not { Length: 4 })
        {
            // No view box: the declared size is the space, and a dimension the file does not declare is the CSS
            // default object size - which is what a viewer uses for a standalone SVG with no intrinsic size.
            //
            // That case is real rather than theoretical. Inkscape's own test corpus contains glyph fragments in
            // `svginotf/`: an `svg` element with no width, no height and no view box, holding `<glyph>` definitions
            // and nothing drawable. Refusing those made four corpus files fail to import, and the honest answer is
            // not to invent a size from content that does not exist - it is the default every viewer already uses.
            double fallbackWidth = width ?? DefaultViewport;
            double fallbackHeight = height ?? DefaultViewport;

            // With no box to fit there is nothing to place but the unit itself: the page is the declared size in
            // points, and the content's own space is that size in user units.
            //
            // A percentage inside resolves against the size **the file states**. Where it states none the size above
            // is the CSS default rather than the file's, so there is no reference at all and the caller reports the
            // percentage instead of measuring it against a number nobody wrote. This mirrors `SvgFilters`' view of
            // the same document, which has answered the question this way since the filter region work.
            SvgViewport? declared = width is { } statedWidth && height is { } statedHeight
                ? new SvgViewport(statedWidth, statedHeight)
                : null;

            return (fallbackWidth * SvgLength.UserUnitsToPoints,
                fallbackHeight * SvgLength.UserUnitsToPoints,
                declared,
                AffineTransform.CreateScale(SvgLength.UserUnitsToPoints, SvgLength.UserUnitsToPoints));
        }

        double boxWidth = box[2];
        double boxHeight = box[3];
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            throw new SvgImportException($"the viewBox has no area: {boxWidth} by {boxHeight}");
        }

        double viewWidth = width ?? boxWidth;
        double viewHeight = height ?? boxHeight;

        (double finalX, double finalY, double offsetX, double offsetY) = Fit(
            viewWidth * SvgLength.UserUnitsToPoints,
            viewHeight * SvgLength.UserUnitsToPoints,
            boxWidth,
            boxHeight,
            root.Attribute("preserveAspectRatio")?.Value);

        AffineTransform transform = AffineTransform.CreateTranslation(offsetX, offsetY)
            .Compose(AffineTransform.CreateScale(finalX, finalY))
            .Compose(AffineTransform.CreateTranslation(-box[0], -box[1]));

        return (viewWidth * SvgLength.UserUnitsToPoints,
            viewHeight * SvgLength.UserUnitsToPoints,
            new SvgViewport(boxWidth, boxHeight),
            transform);
    }

    /// <summary>
    /// The view port's declared size, in the file's own units.
    ///
    /// A percentage here is **reported rather than substituted**: the width and height of the outermost `svg` ARE
    /// the viewport, so there is no containing block for a percentage to be a percentage *of*, and the reader's
    /// fallback - the view box, or CSS's default object size - is a value the file did not write.
    /// </summary>
    private static double? RootLength(string? text, string attribute, Action<string> warn)
    {
        (double Value, bool IsPercent)? parsed = SvgLength.ParseWithUnit(text, warn);
        if (parsed is null)
        {
            return null;
        }

        if (parsed.Value.IsPercent)
        {
            warn(
                $"the document's {attribute}=\"{text}\" is a percentage, and a standalone SVG has no " +
                "containing block to resolve it against");
            return null;
        }

        return parsed.Value.Value;
    }

    /// <summary>
    /// How a box of content maps into a view port: the scale on each axis, and where the leftover space goes.
    ///
    /// One piece of arithmetic serves both the root `viewBox` and an `image`, because they are the same question -
    /// the file gives a box and a thing that has to fit inside it - and answering it twice is how the two answers
    /// come to disagree. The default is `xMidYMid meet`, which is what a viewer assumes when the attribute is
    /// absent: fit everything, centred.
    /// </summary>
    internal static (double ScaleX, double ScaleY, double OffsetX, double OffsetY) Fit(
        double viewWidth, double viewHeight, double contentWidth, double contentHeight, string? aspect)
    {
        string alignment = string.IsNullOrWhiteSpace(aspect) ? "xMidYMid meet" : aspect.Trim();
        bool stretch = alignment.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("none");
        bool slice = alignment.Contains("slice", StringComparison.Ordinal);

        double scaleX = viewWidth / contentWidth;
        double scaleY = viewHeight / contentHeight;
        double uniform = slice ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
        double finalX = stretch ? scaleX : uniform;
        double finalY = stretch ? scaleY : uniform;

        double usedWidth = contentWidth * finalX;
        double usedHeight = contentHeight * finalY;

        // Where the leftover space goes. The default is centred, which is what xMidYMid means and what every
        // viewer does when the attribute is absent.
        double offsetX = alignment.Contains("xMin", StringComparison.Ordinal) ? 0.0
            : alignment.Contains("xMax", StringComparison.Ordinal) ? viewWidth - usedWidth
            : (viewWidth - usedWidth) / 2.0;
        double offsetY = alignment.Contains("YMin", StringComparison.Ordinal) ? 0.0
            : alignment.Contains("YMax", StringComparison.Ordinal) ? viewHeight - usedHeight
            : (viewHeight - usedHeight) / 2.0;

        return (finalX, finalY, offsetX, offsetY);
    }

    // ------------------------------------------------------------------ walking the tree

    private sealed class Context
    {
        public required Layer Layer { get; init; }
        public ArtGroup? Group { get; init; }
        public required PresentationStyle Style { get; init; }
        public required Dictionary<string, int> Counts { get; init; }

        /// <summary>Every element with an id, so a `use` can find its target wherever it is defined.</summary>
        public required Dictionary<string, XElement> Ids { get; init; }

        /// <summary>The ids currently being expanded, so a `use` that refers to itself stops rather than recurses.</summary>
        public required HashSet<string> Resolving { get; init; }

        /// <summary>The ids that were referred to and not found, which are reported rather than dropped.</summary>
        public required List<string> Missing { get; init; }

        /// <summary>The document's stylesheets, cascaded together.</summary>
        public required SvgStylesheet Sheet { get; init; }

        /// <summary>The document's paint servers, by id.</summary>
        public required SvgGradients Gradients { get; init; }

        /// <summary>
        /// The document's patterns, by id.
        ///
        /// Collected once and carried on every context, like the paint servers, because a pattern is a **document
        /// asset**: the shape that paints with one refers to it by id and the definition is not beside the shape.
        /// They are read so that a `url(#p)` naming a pattern can be **named as a pattern** rather than misnamed as
        /// a paint server that does not exist - see <see cref="SvgPatterns"/>.
        /// </summary>
        public required SvgPatterns Patterns { get; init; }

        /// <summary>
        /// The document's markers, by id.
        ///
        /// Collected once and carried on every context, because a marker is a **document asset** in exactly the way
        /// a gradient is: the path that uses one refers to it by id, and the definition is in `defs` rather than
        /// beside the path. See <see cref="SvgMarkers"/>.
        /// </summary>
        public required SvgMarkers Markers { get; init; }

        /// <summary>
        /// The document's live path effects, by the id a path refers to them by.
        ///
        /// Collected once from the whole tree and carried on every context, like the paint servers, because an
        /// effect is a **document asset**: the path that uses one refers to it by id, and the definition is not
        /// inside the path any more than a gradient is.
        /// </summary>
        public IReadOnlyDictionary<string, PathEffectDefinition> PathEffects { get; init; } =
            new Dictionary<string, PathEffectDefinition>(StringComparer.Ordinal);

        /// <summary>
        /// The ids of the live path effects a path in this document actually named.
        ///
        /// Collected while the tree is walked because that is the only point at which a reference is known, and
        /// needed afterwards: whatever is left in <see cref="PathEffects"/> when the walk ends is a definition with
        /// no user, which the document keeps rather than dropping. See <c>CadDocument.ForeignPathEffects</c>.
        ///
        /// An id is recorded whether or not its definition was found - a reference to an effect the file does not
        /// define is reported as a dangling reference, and keeping a definition nothing refers to under that id
        /// would be a second, contradicting answer to the same question.
        /// </summary>
        public required HashSet<string> UsedPathEffects { get; init; }

        /// <summary>
        /// The font and line properties in force here, which a text element inherits the way it inherits paint.
        ///
        /// Kept beside the paint rather than inside it because the two are read by different code and answered by
        /// different questions - a shape has no font and a run has no stroke - while the cascade over them is one.
        /// </summary>
        public required SvgTextStyle Text { get; init; }

        /// <summary>
        /// SVG elements the reader does not know, by name, each recorded once.
        ///
        /// Reported rather than silently skipped: an element that is not understood is artwork that went missing,
        /// and a list of names is a gap somebody can act on. Silently ignoring it produces a drawing that is
        /// simply wrong with nothing to say why.
        /// </summary>
        public required HashSet<string> Warnings { get; init; }

        /// <summary>
        /// Elements the walk had to refuse, kept **verbatim** so that refusing them costs the drawing and not the
        /// file.
        ///
        /// It starts as the document's root-level baggage and the walk appends to it - a `flowRoot` whose region the
        /// model cannot hold is the first such element (see `SvgFlowText.cs`). Written back at the root by the
        /// writer, beside the rest of what the model carries but does not draw.
        /// </summary>
        public required List<string> Kept { get; init; }

        /// <summary>Reports something the reader could not do, as an <see cref="Action{T}"/> for helpers to take.</summary>
        public void Warn(string message) => Warnings.Add(message);

        /// <summary>Where a shape is added: the group it is inside, or the layer when there is no group.</summary>
        public void Add(LayerItem item)
        {
            if (Group is not null)
            {
                Group.AddItem(item);
            }
            else
            {
                Layer.AddItem(item);
            }
        }

        /// <summary>
        /// The viewport a percentage resolves against, in the file's own units, or null when the document
        /// establishes none.
        ///
        /// Null is a real case and not a defect: a percentage inside a symbol the file never sizes has nothing to
        /// be a percentage of, and SVG's own answer there is to use the value as if the viewport were the default -
        /// which is a value the file did not write.
        /// </summary>
        public required SvgViewport? Viewport { get; set; }

        /// <summary>
        /// The viewport a `use` states for an `svg` target it draws, in the file's own units, or null when this
        /// read is not that case.
        ///
        /// SVG 2 lets a `use` override the width and height of an `svg` or `symbol` it refers to. The symbol half is
        /// arithmetic on the instance group in <see cref="ReadUse"/>; the `svg` half is a *viewport*, so it is
        /// carried here and consumed by <see cref="ReadNestedSvg"/>, which is the one place that knows how to fit
        /// a view box into a port. Deliberately **not** copied into a child context: it sizes the target element
        /// alone, not anything the target happens to contain.
        /// </summary>
        public SvgViewport? PortOverride { get; set; }

        /// <summary>The directory a file reference in the document is resolved against, when it came from disk.</summary>
        public required string? BaseDirectory { get; init; }

        /// <summary>The faces the document supplies for itself, or null when it supplies none this reader can use.</summary>
        public SvgFontFaces? FontFaces { get; init; }

        /// <summary>
        /// A length on a known axis, with a percentage resolved against the viewport.
        ///
        /// **A percentage that cannot be resolved is reported.** The reader used to return null for every
        /// percentage and let the caller's `?? default` stand in - so `x="25%"` imported as the attribute's
        /// default position, which looks deliberate and is not the file. Reporting it is the difference between a
        /// gap somebody can act on and a drawing that is quietly wrong.
        /// </summary>
        public double? Length(string? text, SvgAxis axis, string attribute)
        {
            (double Value, bool IsPercent)? parsed = SvgLength.ParseWithUnit(text, Warn);
            if (parsed is null)
            {
                return null;
            }

            if (!parsed.Value.IsPercent)
            {
                return parsed.Value.Value;
            }

            if (Viewport is not { } viewport)
            {
                Warnings.Add(
                    $"{attribute}=\"{text}\" is a percentage with no viewport to resolve it against");
                return null;
            }

            double reference = axis switch
            {
                SvgAxis.X => viewport.Width,
                SvgAxis.Y => viewport.Height,
                _ => viewport.Diagonal,
            };

            return parsed.Value.Value / 100.0 * reference;
        }
    }

    private static void ReadElement(XElement element, Context context)
    {
        string name = element.Name.LocalName;

        // An element in a namespace this reader does not know is skipped rather than guessed at. Inkscape and
        // Sodipodi carry their own elements - 226 and 94 attributes' worth in the corpus - and treating one as
        // geometry would invent objects the file does not draw.
        if (!string.IsNullOrEmpty(element.Name.NamespaceName) && element.Name.Namespace != Svg)
        {
            return;
        }

        if (IsHidden(element))
        {
            return;
        }

        // The declarations the cascade resolves for this element, read once and handed to both readers: the font
        // properties travel separately from the paint and inherit the same way, so they must not answer the cascade
        // differently or twice.
        IReadOnlyDictionary<string, (string Value, bool Important)> declarations =
            context.Sheet.DeclarationsFor(element, element.Ancestors().ToArray());

        PresentationStyle style = PresentationStyle.From(
            element, context.Style, declarations, context.Viewport, context.Warn);

        SvgTextStyle text = SvgTextStyle.From(element, context.Text, declarations, context.Warn, context.FontFaces);

        // `clip-path` is read once here and handed to whichever reader below builds the item, because it is a
        // property of every element that can be drawn rather than of one kind of element. Reading it per element
        // kind would eventually answer "which frame" differently in two places - see ClipPathFor.
        ClipPath? clip = ClipPathFor(element, context, declarations);

        switch (name)
        {
            case "g":
            case "a":
            case "switch":
                ReadGroup(element, context, style, text, clip);
                return;

            // A nested `svg` establishes a viewport of its own, which is a second view box transform laid on the
            // enclosing one rather than a plain group - see ReadNestedSvg.
            case "svg":
                ReadNestedSvg(element, context, style, text, clip);
                return;

            case "text":
                ReadTextElement(element, context, style, text);
                return;

            // SVG 1.2's flowed text: a region to flow into rather than a point to place at. Read where the model's
            // frame can hold the region, and refused by name - with the element kept verbatim - where it cannot.
            case "flowRoot":
                ReadFlowRoot(element, context, style, text);
                return;

            case "defs":
            case "symbol":
            case "style":
            case "title":
            case "desc":
            case "metadata":
            case "namedview":
            case "script":
                // Not drawn here. A symbol is drawn where it is **used**, not where it is defined, and defs
                // holds definitions for the issues that instance them.
                //
                // Skipping `defs` is right for the SVG children - a gradient is resolved by id and a symbol is
                // drawn at its `use` - but it is **not** a reason to walk past a foreign-namespace definition: a
                // live path effect is resolved from the whole tree up front, by CollectPathEffects, and hung on the
                // path that refers to it. Reading the element here would only reach the shapes a defs happens to
                // contain, which is not where an effect is used from.
                return;

            case "use":
                foreach (LayerItem used in ReadUse(element, context, style))
                {
                    // `clip-path` is in force on the `use` element itself, so it clips the instance the same way
                    // any ancestor clips what it holds. `ReadUse` keeps the element's own transform on the group,
                    // and the clip belongs in that group's frame - which is the frame it is written in - so the
                    // outline goes on unaltered.
                    if (clip is { } useClipPath && useClipPath.Spec is { } useClip)
                    {
                        used.Clips.Add(useClip);
                    }

                    context.Add(used);
                    context.Counts["use"] = context.Counts.GetValueOrDefault("use") + 1;
                }

                if (clip is { } refusedUseClip)
                {
                    context.Warnings.UnionWith(refusedUseClip.Warnings);
                }

                return;
        }

        // A shape's own transform is baked into its points once they exist - see ApplyTransform - because the
        // model's paths have no transform of their own: their coordinates are the artboard's.
        AffineTransform own = Transform(element.Attribute("transform")?.Value);

        if (name == "image")
        {
            if (ReadImage(element, context, own) is { } image)
            {
                image.BlendMode = style.Blend;
                if (FilterReference(element) is { } imageFilter)
                {
                    image.FilterId = imageFilter;
                }

                // **The crop, in the same frame the placement ended up in.** `ReadImage` carried the element's own
                // transform into the placement rectangle, so the outline is carried by it too - the shape branch
                // below states the same rule for the same reason. A `clip-path` on an image used to be resolved and
                // then dropped on the floor here, because this branch returned before the shape reader got to it.
                if (clip is { } imageClipPath)
                {
                    if (imageClipPath.Spec is { } imageClip)
                    {
                        image.Clips.Add(ApplyTransform(imageClip, own));
                    }

                    context.Warnings.UnionWith(imageClipPath.Warnings);
                }

                CaptureForeign(element, image);
                context.Add(image);
                context.Counts["image"] = context.Counts.GetValueOrDefault("image") + 1;
            }

            return;
        }

        // Anything that reaches here is an element this reader does not know. It is **reported** and then skipped:
        // artwork that quietly went missing is the worst kind of import bug, because the drawing looks deliberate.
        // The known elements are the ones above and the shapes ReadShape handles.
        if (name is not ("rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path"))
        {
            context.Warnings.Add(name);
            return;
        }

        foreach (LayerItem item in ReadShape(element, context, style))
        {
            if (item.FilterId is null && FilterReference(element) is { } filterId)
            {
                item.FilterId = filterId;
            }

            item.BlendMode = style.Blend;
            CaptureForeign(element, item);

            // The arrowheads this element's marker properties place. They are added **after** the shape they belong
            // to, because SVG draws markers after the object is filled and stroked.
            List<ArtGroup>? arrowheads = null;

            if (item is PathItem shape)
            {
                // A gradient is normalised against the shape's own box, in the space the shape is **written** in -
                // which is before its own transform, and is what `userSpaceOnUse` means. So it is resolved here:
                // after the geometry exists, and before the transform is composed into the points.
                if (style.FillGradientId is { Length: > 0 } gradientId)
                {
                    if (context.Gradients.SolidFor(gradientId) is { } solid)
                    {
                        // SVG 1.2's solid colour: a paint server that is only a colour and an opacity, so there is
                        // no geometry to resolve and the shape's box has nothing to do with it.
                        shape.Fill = FillSpec.Solid(
                            solid.Colour with { A = solid.Colour.A * solid.Opacity }, shape.Fill.Rule);
                    }
                    else if (context.Gradients.Resolve(gradientId, shape.BoundingBox()) is { } resolved)
                    {
                        shape.Fill = shape.Fill with { IsVisible = true, Gradient = resolved };
                    }
                    else if (context.Patterns.Facts(gradientId) is { } pattern)
                    {
                        // **A pattern is not a colour, and the model has nowhere to put a tile.** Reading the
                        // reference as a missing paint server would name the wrong thing - the pattern is in the
                        // document - so it is named as what it is, together with what its tile draws. See
                        // SvgPatterns, and #175 for the same finding on the PDF side.
                        context.Warnings.Add(PatternReport(element, "fill", PatternValue(element, declarations, "fill", gradientId), pattern));
                    }
                    else
                    {
                        // A reference to a paint server the document does not contain. The shape stays drawable,
                        // but a fill that is quietly not the one the file asked for is worth saying.
                        context.Warnings.Add($"no paint server called '{gradientId}' for this fill");
                    }
                }

                // A paint server reaches the stroke half of the paint too. Gradients on a stroke are a separate
                // gap, but a *pattern* named as a stroke is the same tile the fill case above cannot honour, and
                // saying so costs nothing - silently painting it black would be the substitution this file refuses
                // to make.
                if (SvgProperties.Value(element, declarations, "stroke") is { } strokeValue &&
                    LocalReference(strokeValue) is { } strokeId &&
                    context.Patterns.Facts(strokeId) is { } strokePattern)
                {
                    context.Warnings.Add(PatternReport(element, "stroke", strokeValue, strokePattern));
                }

                // Markers are placed **before** the element's own transform is baked into the points, because SVG
                // places them in the user space that transform establishes: the tangent, the vertex and the stroke
                // width are all that space's own, and the placement below carries `own` itself. Computing them from
                // the transformed points instead would turn a skew or a non-uniform scale into a marker aimed the
                // wrong way.
                List<ArtGroup>? placed = PlaceMarkers(element, context, style, shape, own);
                arrowheads = placed;

                if (!IsIdentity(own))
                {
                    ApplyTransform(shape, own);
                }

                // Last, so the stroke the effect replaces its own width, cap and join with is the one the file's
                // own paint and cascade already decided - and so the profile is read on the path as it will be
                // drawn, after its own transform is baked into the points.
                ApplyPathEffect(shape, context);

                // **The crop, in the same frame the shape's points ended up in.** SVG applies `clip-path` in the
                // user space the element's own `transform` establishes, and the points above were carried out of
                // that space by exactly that transform, so the outline is carried by it too rather than being left
                // at coordinates the file wrote for a space the shape no longer occupies. The model records a
                // shape's clip where the shape is, so both halves move together.
                if (clip is { } shapeClipPath)
                {
                    if (shapeClipPath.Spec is { } shapeClip)
                    {
                        shape.Clips.Add(ApplyTransform(shapeClip, own));
                    }

                    // Said whether or not there was a clip to attach: a `clip-path` this reader could not honour
                    // is the report this whole change exists for, and an item that was drawn is where it belongs.
                    context.Warnings.UnionWith(shapeClipPath.Warnings);
                }
            }

            context.Add(item);
            context.Counts[name] = context.Counts.GetValueOrDefault(name) + 1;

            // Drawn after the object, which is what SVG says: markers are painted once the fill and the stroke are
            // down, so they cover the end of the line rather than being covered by it.
            if (arrowheads is not null)
            {
                foreach (ArtGroup arrowhead in arrowheads)
                {
                    context.Add(arrowhead);
                }
            }
        }
    }

    /// <summary>
    /// Whether a transform does nothing.
    ///
    /// The type has no such member and no equality override, so the six components are compared here. A tolerance
    /// rather than exact equality, because a transform that came out of an SVG is arithmetic and an identity that
    /// is off by 1e-17 is still an identity - and treating it as one avoids wrapping every imported file in a group
    /// that does nothing.
    /// </summary>
    private static bool IsIdentity(AffineTransform transform)
        => Math.Abs(transform.A - 1.0) < 1e-12 &&
           Math.Abs(transform.B) < 1e-12 &&
           Math.Abs(transform.C) < 1e-12 &&
           Math.Abs(transform.D - 1.0) < 1e-12 &&
           Math.Abs(transform.E) < 1e-12 &&
           Math.Abs(transform.F) < 1e-12;

    private static void ReadGroup(
        XElement element, Context context, PresentationStyle style, SvgTextStyle text, ClipPath? clip = null)
    {
        // A group keeps its transform on the group, where the model can apply it to everything inside at once and
        // where a later edit can change it. Baking it into the children would make the group's transform
        // uneditable and would lose the structure the file has.
        AffineTransform transform = Transform(element.Attribute("transform")?.Value);
        var group = new ArtGroup { Name = element.Attribute("id")?.Value ?? string.Empty };
        group.SourceId = SourceOf(element);

        // **A written instance carries its use site's presentation as attributes on this element.** The writer
        // states them because the model has no `<use>` to state it on, and reading them back here is what keeps the
        // paint across a save: without it, reopening a file and refreshing would revert the instance to the
        // definition's own paint, which is the very loss this member exists to close. Read against the initial
        // values rather than against what this element inherits, because the presentation is what the **use site**
        // established, not what the reopened tree around it happens to say. The element's own `opacity` is the
        // group's and is not part of it.
        if (group.SourceId is { Length: > 0 })
        {
            group.InstancePresentation = PresentationOf(
                PresentationStyle.From(element, PresentationStyle.Default, readObjectOpacity: false));
        }

        group.Transform = transform;
        group.BlendMode = style.Blend;
        CaptureForeign(element, group);

        // The file's own crop of everything this group holds. SVG applies `clip-path` in the user space the
        // element's own `transform` establishes, so the outline is carried into the containing space - the frame
        // the model records a group's clip in, and the same one a viewport port is recorded in.
        if (clip is { } groupClipPath)
        {
            if (groupClipPath.Spec is { } groupClip)
            {
                group.Clips.Add(ApplyTransform(groupClip, transform));
            }

            context.Warnings.UnionWith(groupClipPath.Warnings);
        }

        var inside = new Context
        {
            Layer = context.Layer,
            Group = group,
            Style = style,
            Counts = context.Counts,
            Ids = context.Ids,
            Resolving = context.Resolving,
            Missing = context.Missing,
            UsedPathEffects = context.UsedPathEffects,
            Sheet = context.Sheet,
            Gradients = context.Gradients,
            Patterns = context.Patterns,
            Markers = context.Markers,
            PathEffects = context.PathEffects,
            Warnings = context.Warnings,
            Kept = context.Kept,
            Viewport = context.Viewport,
            BaseDirectory = context.BaseDirectory,
            FontFaces = context.FontFaces,
            Text = text,
        };

        foreach (XElement child in element.Elements())
        {
            ReadElement(child, inside);
        }

        // An empty group is a group the file has and the model keeps: dropping it would lose the name and the
        // transform, and a later element referring to it would refer to nothing.
        context.Add(group);
        context.Counts[element.Name.LocalName] = context.Counts.GetValueOrDefault(element.Name.LocalName) + 1;
    }

    /// <summary>
    /// A nested `svg`: a viewport of its own, and with it a second `viewBox` transform one level in.
    ///
    /// **The enclosing transform is composed with, not replaced.** The root's group already carries the file's user
    /// units into the model's points, so the transform established here is written in the file's units and stays
    /// there; measuring the nested fit in points would apply the 72/96 factor a second time and draw everything
    /// inside it three quarters of the size the file asks for.
    ///
    /// `x` and `y` place the new port in the containing space and `width` and `height` size it - and per SVG 1.1
    /// §5.1.2 an `svg` that declares neither is `100%` of the viewport around it, which is a containing block a
    /// nested element has and the outermost one does not. A `viewBox` then fits the content into that port exactly
    /// as it does at the root. What cannot be established honestly is **reported and skipped** rather than drawn at
    /// the outer scale, because drawing it at the outer scale is precisely the defect this closes: a plausible
    /// wrong answer is harder to notice than a refusal.
    ///
    /// **The port then clips what the viewport holds**, unless the file states `overflow: visible` - the property's
    /// value on an `svg` element is `hidden`, and a nested viewport is usually there to crop. See below for the
    /// outline's frame and for why a port that cuts nothing is not recorded.
    /// </summary>
    private static void ReadNestedSvg(
        XElement element, Context context, PresentationStyle style, SvgTextStyle text, ClipPath? clip = null)
    {
        if (NestedOrigin(element, "x", SvgAxis.X, context) is not { } x ||
            NestedOrigin(element, "y", SvgAxis.Y, context) is not { } y)
        {
            return;
        }

        // **A `use` of this element may state the port itself.** SVG 2 sizes the viewport a `use` establishes over
        // an `svg` target from the use's own `width` and `height`, so those are the dimensions carried on the
        // context by ReadUse; without them the element's own attributes - or SVG 1.1 §5.1.2's `100%` - are the port,
        // exactly as for any other nested `svg`. Reading them the same way the element's own are read means the
        // override cannot be resolved twice or against a different reference.
        double? statedWidth = context.PortOverride?.Width ?? NestedPort(element, "width", SvgAxis.X, context);
        double? statedHeight = context.PortOverride?.Height ?? NestedPort(element, "height", SvgAxis.Y, context);
        if (statedWidth is not { } portWidth || statedHeight is not { } portHeight)
        {
            return;
        }

        if (portWidth <= 0 || portHeight <= 0)
        {
            // SVG draws nothing into a viewport with no area. Reported rather than quietly skipped, because a
            // drawing that is simply missing a piece says nothing about why it is missing.
            context.Warnings.Add(
                $"a nested <svg> is {portWidth} by {portHeight}, and SVG draws nothing into a viewport with no area");
            return;
        }

        double[]? box = Numbers(element.Attribute("viewBox")?.Value);
        AffineTransform fit = AffineTransform.Identity;

        // What a percentage inside resolves against is measured **in the user coordinate system the content is
        // written in**: SVG 1.1 §7.10 makes actual-width the viewport dimension "within the user coordinate system
        // for the viewport element", and the view box is what establishes that system. So a `50%` inside a 50-unit
        // box is 25 units - half the drawing - and not 50, which is the box doubled and would fill the port.
        var viewport = box is { Length: 4 } ? new SvgViewport(box[2], box[3]) : new SvgViewport(portWidth, portHeight);

        if (box is { Length: 4 })
        {
            if (box[2] <= 0 || box[3] <= 0)
            {
                context.Warnings.Add($"a nested <svg> has a viewBox with no area: {box[2]} by {box[3]}");
                return;
            }

            // The port and the box are both in the file's units here, so the fit between them is a pure ratio and
            // carries no points of its own - the enclosing group is what converts the result.
            (double scaleX, double scaleY, double offsetX, double offsetY) = Fit(
                portWidth, portHeight, box[2], box[3], element.Attribute("preserveAspectRatio")?.Value);

            fit = AffineTransform.CreateTranslation(offsetX, offsetY)
                .Compose(AffineTransform.CreateScale(scaleX, scaleY))
                .Compose(AffineTransform.CreateTranslation(-box[0], -box[1]));
        }

        // The element's own `transform` applies to its `x` and `y` as it does to any other of its attributes
        // (§7.4), so it sits outside them; the placement and the fit are inner, where the new viewport is
        // established.
        var group = new ArtGroup
        {
            Name = element.Attribute("id")?.Value ?? string.Empty,
            SourceId = SourceOf(element),
            Transform = Transform(element.Attribute("transform")?.Value)
                .Compose(AffineTransform.CreateTranslation(x, y))
                .Compose(fit),
        };
        group.BlendMode = style.Blend;
        CaptureForeign(element, group);

        // A written instance may be a nested viewport: its use site's presentation travels on this element exactly
        // as it does on a plain group, and the same read puts it back.
        if (group.SourceId is { Length: > 0 })
        {
            group.InstancePresentation = PresentationOf(
                PresentationStyle.From(element, PresentationStyle.Default, readObjectOpacity: false));
        }

        var inside = new Context
        {
            Layer = context.Layer,
            Group = group,
            Style = style,
            Counts = context.Counts,
            Ids = context.Ids,
            Resolving = context.Resolving,
            Missing = context.Missing,
            UsedPathEffects = context.UsedPathEffects,
            Sheet = context.Sheet,
            Gradients = context.Gradients,
            Patterns = context.Patterns,
            Markers = context.Markers,
            PathEffects = context.PathEffects,
            Warnings = context.Warnings,
            Kept = context.Kept,
            Viewport = viewport,
            BaseDirectory = context.BaseDirectory,
            FontFaces = context.FontFaces,
            Text = text,
        };

        foreach (XElement child in element.Elements())
        {
            ReadElement(child, inside);
        }

        // **A viewport clips what is inside it, and that is usually why the file nested one.**
        //
        // SVG's `overflow` is `hidden` on an `svg` element unless the file says otherwise, so content that reaches
        // past the port is cut at it. The port belongs to the element and everything the element holds is what it
        // clips, so the outline goes on the element's own group - the shape the PDF importer gives a form's /BBox,
        // which is the same statement one format over. A viewport around this one keeps its own outline, and
        // several clips on the way down intersect, which is what the model has always said they mean.
        //
        // The outline is written **in the space the enclosing element wrote `x`, `y`, `width` and `height` in** -
        // the frame a group's own transform maps into rather than the frame its children are written in - because
        // that is how the model records and applies a clip on a group, and because that is where the port is: a
        // 50-unit port at (50,50) is a 50-unit rectangle at (50,50), not one at the origin.
        //
        // A port that cuts nothing is not recorded, for the reason a form's /BBox that already contains its
        // content is not: a clip that removes nothing is a mask on every file that happens to nest a viewport,
        // and it states nothing the file did not already say.
        if (OverflowClips(element, context))
        {
            // The element's own `transform` sits outside its `x` and `y` (§7.4), so it carries the port into the
            // containing space exactly as it carries the content, and the port is a parallelogram when it turns.
            AffineTransform own = Transform(element.Attribute("transform")?.Value);
            Rect2D port = own.Transform(new Rect2D(x, y, portWidth, portHeight));
            Rect2D content = group.Transform.Transform(ContentBounds(group, AffineTransform.Identity));

            if (PortCuts(port, content))
            {
                group.Clips.Add(PortClip(own, x, y, portWidth, portHeight));
            }
        }

        // **The element's own `clip-path` composes with the port rather than replacing it.** They are two
        // outlines in the same frame - the one the enclosing element's numbers are written in - and a second clip
        // on the way down means "and also inside this", so both belong on the element's own group. A reader that
        // let one overwrite the other would show content the file hides while the survivor appeared to work,
        // which is the harder failure to notice.
        //
        // Placed **after** the port so the order a person reads the model matches the order the file nests them,
        // and attached whether or not the port cut anything: a port that removes nothing is not a clip, and the
        // `clip-path` is unaffected by that decision.
        if (clip is { } viewportClipPath)
        {
            if (viewportClipPath.Spec is { } viewportClip)
            {
                group.Clips.Add(ApplyTransform(viewportClip, Transform(element.Attribute("transform")?.Value)));
            }

            context.Warnings.UnionWith(viewportClipPath.Warnings);
        }

        // Kept even when empty, the way a group is: the file has the element, and the transform and the name are
        // the element's own.
        context.Add(group);
        context.Counts["svg"] = context.Counts.GetValueOrDefault("svg") + 1;
    }

    // ------------------------------------------------------------------ `clip-path`

    /// <summary>
    /// An element's `clip-path`, resolved into the model's <see cref="ClipSpec"/> - or the reason it could not be.
    ///
    /// <paramref name="Spec"/> is the outline **in the user space the element's own `transform` establishes**, which
    /// is the space SVG 1.1 §14.3 evaluates the property in; the caller carries it into the item's frame with
    /// <see cref="ApplyTransform(ClipSpec, AffineTransform)"/>, exactly as a viewport port is carried by the same
    /// transform. <paramref name="Warnings"/> holds what was said about a value this reader could not honour: those
    /// are **returned rather than added to the context** so that an element with no paint server somewhere it could
    /// not be drawn from - a `clipPath`'s own definition, a shape skipped for having no area - does not produce a
    /// report about a crop that was never going to be applied anyway.
    ///
    /// A value that is neither `none` nor a reference is reported rather than guessed at, and one that resolves to
    /// something which is not a `clipPath` is reported too: a CSS shape (`circle()`, `inset()`, `path()`) and an
    /// external reference are both SVG the model can hold but this reader does not read, and drawing the content
    /// *unclipped in silence* is the defect this whole rule exists to close. The content is still drawn, because a
    /// crop the reader cannot compute is no reason to lose the artwork underneath it.
    /// </summary>
    private static ClipPath? ClipPathFor(
        XElement element,
        Context context,
        IReadOnlyDictionary<string, (string Value, bool Important)>? declarations)
    {
        string? stated = SvgProperties.Value(element, declarations, "clip-path");
        if (stated is null)
        {
            return null;
        }

        string value = stated.Trim();
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            // `none` is the property's initial value, so it states the absence of a crop rather than one this
            // reader failed to read. Reporting it would make every file that spells out its default look broken.
            return null;
        }

        // A refusal names the value and the reason, in one message, because a person reading the report has to be
        // able to find the attribute in the file and know why it was not honoured.
        ClipPath Refuse(string reason)
        {
            string named = element.Attribute("id")?.Value is { Length: > 0 } name
                ? $"the element '{name}' states clip-path=\"{value}\""
                : $"an element states clip-path=\"{value}\"";

            return new ClipPath(null, new[] { $"{named}, and {reason}" });
        }

        // `url(#id)` is the only form this reader can honour, and the id has to name a `clipPath` element.
        string? id = LocalReference(value);
        if (id is null)
        {
            return Refuse(
                "it is not a reference to a <clipPath>, and this reader does not read CSS clip shapes or a " +
                "reference into another document");
        }

        if (!context.Ids.TryGetValue(id, out XElement? target) || !IsClipPath(target))
        {
            return Refuse($"the document defines no <clipPath> called '{id}', so there is no outline to clip to");
        }

        string? units = target.Attribute("clipPathUnits")?.Value?.Trim();
        if (units is not null && !units.Equals("userSpaceOnUse", StringComparison.OrdinalIgnoreCase))
        {
            // **A different coordinate system, not a different number.** `objectBoundingBox` means the outline's
            // coordinates are fractions of the element's own bounding box, so `0 0 1 1` is the whole element rather
            // than a one-unit square at its origin. Reading those numbers as user space would clip to a shape
            // nobody wrote - a plausible wrong answer, which is worse than a reported gap.
            return Refuse(
                $"it refers to a <clipPath> in clipPathUnits=\"{units}\", a different coordinate system this " +
                "reader does not convert, so the outline's numbers are not user space");
        }

        var clip = new ClipSpec { Rule = ClipRuleFor(target, context) };
        var unread = new List<string>();
        bool ruleStated = false;

        // The outline is read by the same shape reader the artwork is, so a `clipPath` holding a `rect`, a `circle`
        // or a `path` cannot mean one thing here and another there - and one spelling of "what shape is this"
        // means a clip cannot drift away from the geometry it was written beside.
        foreach (XElement child in target.Elements())
        {
            if (child.Name.LocalName is not ("rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"
                or "path"))
            {
                unread.Add(
                    $"an element states clip-path=\"{value}\", and the <clipPath> called '{id}' holds a " +
                    $"<{child.Name.LocalName}> that this reader does not read as an outline");
                continue;
            }

            // `clip-rule` is a property of the outline's own shapes, so the first shape that states one decides the
            // rule for the clip - a shape is a whole region here, not a layer to be combined, so there is no
            // per-shape rule to keep. The `clipPath` element's own value is the inherited default read above.
            if (!ruleStated &&
                SvgProperties.Value(
                    child,
                    context.Sheet.DeclarationsFor(child, child.Ancestors().ToArray()),
                    "clip-rule") is { } childRule)
            {
                clip.Rule = childRule.Trim().Equals("evenodd", StringComparison.OrdinalIgnoreCase)
                    ? FillRule.EvenOdd
                    : FillRule.NonZero;
                ruleStated = true;
            }

            foreach (LayerItem item in ReadShape(child, context, PresentationStyle.Default))
            {
                if (item is PathItem outline)
                {
                    foreach (SubPath sub in outline.SubPaths)
                    {
                        clip.SubPaths.Add(sub.Clone());
                    }
                }
            }
        }

        if (clip.IsEmpty)
        {
            unread.Add(
                $"an element states clip-path=\"{value}\", and the <clipPath> called '{id}' holds no outline this " +
                "reader can read");
            return new ClipPath(null, unread);
        }

        return new ClipPath(clip, unread);
    }

    /// <summary>
    /// The id a `url(...)` reference names, or null when the value is not a local fragment reference.
    ///
    /// A quoted URL and a whitespace-padded one are both valid CSS, and the check for the fragment is what keeps an
    /// external reference (`url(sprite.svg#c)`) out: this reader opens one file, so a reference into another one is
    /// a value it cannot honour and must report rather than treat as user space.
    /// </summary>
    private static string? LocalReference(string value)
    {
        if (!value.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || !value.EndsWith(')'))
        {
            return null;
        }

        string inner = value[4..^1].Trim().Trim('"', '\'');
        return inner.Length > 1 && inner[0] == '#' ? inner[1..] : null;
    }

    /// <summary>
    /// The message a paint server naming a `&lt;pattern&gt;` produces: what was painted, the pattern it names, what
    /// the tile draws, and why the model cannot paint with it.
    ///
    /// **It reaches the tile on purpose.** A report that only noticed the reference would say "a pattern was seen"
    /// and stop, which is not something a person can act on; naming the shapes the tile holds is the difference
    /// between a message and a finding. The refusal for `objectBoundingBox` comes along in the same message, because
    /// it is the same pattern at the same moment - see <see cref="SvgPatterns.PatternFacts"/>.
    /// </summary>
    private static string PatternReport(
        XElement element, string property, string value, SvgPatterns.PatternFacts pattern)
    {
        string subject = element.Attribute("id")?.Value is { Length: > 0 } id
            ? $"the element '{id}'"
            : "an element";

        string tile = pattern.TileShapes.Count == 0
            ? "its tile holds no shape this reader draws"
            : $"its tile draws {NameList(pattern.TileShapes)}";

        string units = pattern.UnitRefusals.Count == 0
            ? string.Empty
            : " " + string.Join(" ", pattern.UnitRefusals.Select(r => char.ToUpperInvariant(r[0]) + r[1..] + "."));

        return $"{subject} is painted with {property}=\"{value}\", which names a <pattern>: {tile}.{units} " +
               "A pattern is artwork used as a paint, and this model has no pattern paint server, so the " +
               $"{property} is left unpainted rather than substituted with a colour or a tile the file did not write";
    }

    /// <summary>The value an element writes for a paint property, or the reference itself when it was inherited.</summary>
    private static string PatternValue(
        XElement element,
        IReadOnlyDictionary<string, (string Value, bool Important)> declarations,
        string property,
        string id)
        => SvgProperties.Value(element, declarations, property) ?? $"url(#{id})";

    /// <summary>A list of element names as prose: `&lt;rect&gt; and &lt;ellipse&gt;`, `a`, `b` and `c`.</summary>
    private static string NameList(IReadOnlyList<string> names)
    {
        if (names.Count == 1)
        {
            return $"<{names[0]}>";
        }

        return string.Join(", ", names.Take(names.Count - 1).Select(n => $"<{n}>")) +
               $" and <{names[^1]}>";
    }

    // ------------------------------------------------------------------ `marker-start` / `-mid` / `-end`

    /// <summary>Which of the three marker properties places an arrowhead at a vertex.</summary>
    private enum MarkerSlot
    {
        Start,
        Mid,
        End,
    }

    /// <summary>
    /// Records which marker one of the three properties names (issue #202).
    ///
    /// This is the reference the model keeps: the name of the `<marker>` the file asked for, whether or not the
    /// document defines it. It is what a marker picker reads, what an export has to write back, and what lets a
    /// renderer place the arrowheads itself rather than drawing artwork somebody else materialised.
    /// </summary>
    private static void RecordMarker(PathItem shape, MarkerSlot slot, string id)
    {
        switch (slot)
        {
            case MarkerSlot.Start:
                shape.MarkerStart = id;
                break;
            case MarkerSlot.Mid:
                shape.MarkerMid = id;
                break;
            default:
                shape.MarkerEnd = id;
                break;
        }
    }

    /// <summary>
    /// Reads every `<marker>` a document defines into the document's own library (issue #202).
    ///
    /// A marker is a reusable definition: a name, content, and the attributes that say how to place it. Reading it
    /// into `CadDocument.Definitions` is what lets a picker list the markers a file has, an operation name one, and
    /// a renderer place it - rather than the marker existing only inside the import that read it.
    ///
    /// The content goes through the **ordinary element walk** into a group, so a marker holding a path, a group, a
    /// gradient or a filter is read exactly as it would be anywhere else. The attributes the model does not model
    /// (`refX`, `refY`, `markerUnits`, `orient`, the view box) are carried as foreign attributes, which is the
    /// model's own rule for a fact it must not lose and cannot yet express.
    ///
    /// The arrowheads are **still materialised** at each vertex by <see cref="PlaceMarkers"/> as well, so the
    /// picture is unchanged; stopping that is what a renderer drawing from this definition makes possible.
    /// </summary>
    private static void ReadMarkerDefinitions(XElement root, Context context)
    {
        if (context.Layer.Document is not { } document)
        {
            return;
        }

        foreach (XElement marker in root.DescendantsAndSelf())
        {
            if (marker.Name.LocalName != "marker")
            {
                continue;
            }

            string id = marker.Attribute("id")?.Value ?? string.Empty;
            if (id.Length == 0 || document.FindDefinition(id) is not null)
            {
                continue;
            }

            var definition = new ArtGroup { Name = id };
            CaptureForeign(marker, definition);

            // **The marker's own placement attributes, which the model has no member for.** `CaptureForeign` keeps
            // the file's *namespaced* attributes; these live in SVG's own namespace and would otherwise be lost with
            // the element - and losing them is not cosmetic: a written `<marker>` without its `refX`/`refY` places
            // its content at the wrong point, and without `orient` it points the wrong way. The list is the set the
            // specification defines for a marker, kept verbatim.
            foreach (string attribute in new[]
                     {
                         "refX", "refY", "markerWidth", "markerHeight", "markerUnits", "orient", "viewBox",
                         "preserveAspectRatio",
                     })
            {
                if (marker.Attribute(attribute) is { } value)
                {
                    definition.ForeignAttributes[attribute] = value.Value;
                }
            }

            var inside = new Context
            {
                Layer = context.Layer,
                Group = definition,
                Style = context.Style,
                Counts = context.Counts,
                Ids = context.Ids,
                Resolving = context.Resolving,
                Missing = context.Missing,
                UsedPathEffects = context.UsedPathEffects,
                Sheet = context.Sheet,
                Gradients = context.Gradients,
                Patterns = context.Patterns,
                Markers = context.Markers,
                PathEffects = context.PathEffects,
                Warnings = context.Warnings,
                Kept = context.Kept,
                Viewport = context.Viewport,
                BaseDirectory = context.BaseDirectory,
                FontFaces = context.FontFaces,
                Text = context.Text,
            };

            foreach (XElement child in marker.Elements())
            {
                ReadElement(child, inside);
            }

            document.Definitions.AddItem(definition);
        }
    }


    /// <summary>
    /// The arrowheads a path's marker properties place, as groups in the space the element is **written** in - the
    /// caller has not yet baked <paramref name="own"/> into the points, so the tangent, the vertex and the stroke
    /// width are all that space's own, and the placement carries the transform itself.
    ///
    /// **Placed as art, and the reference now recorded.** SVG makes a marker a stroke property: it belongs to the
    /// path and moves with it. The marker each property names is recorded on the path (`PathItem.MarkerStart`,
    /// `MarkerMid`, `MarkerEnd` - issue #202), and the arrowhead's *content* is still placed as an object of its own
    /// beside the path, because the renderers do not yet draw from the reference. That is a real loss of editability,
    /// so every marker that is placed is reported, naming the marker: the picture is right, the reference survives,
    /// and what is left - drawing and exporting from the reference rather than from materialised art - is the rest of
    /// #202.
    ///
    /// A reference this reader cannot honour - a dangling id, an id that names something else - is reported too, and
    /// no geometry is invented for it.
    /// </summary>
    private static List<ArtGroup>? PlaceMarkers(
        XElement element, Context context, PresentationStyle style, PathItem shape, AffineTransform own)
    {
        if (style.Markers.IsEmpty ||
            element.Name.LocalName is not ("path" or "line" or "polyline" or "polygon"))
        {
            return null;
        }

        var placed = new List<ArtGroup>();

        foreach ((string property, string? id, MarkerSlot slot) in new[]
                 {
                     ("marker-start", style.Markers.Start, MarkerSlot.Start),
                     ("marker-mid", style.Markers.Mid, MarkerSlot.Mid),
                     ("marker-end", style.Markers.End, MarkerSlot.End),
                 })
        {
            if (id is null)
            {
                continue;
            }

            if (context.Markers.Definition(id) is not { } marker)
            {
                context.Warnings.Add(context.Markers.Defines(id)
                    ? $"{Subject(element)} states {property}=\"url(#{id})\", and '#{id}' is not a <marker>, so no " +
                      "arrowhead is drawn"
                    : $"{Subject(element)} states {property}=\"url(#{id})\", and the document defines no <marker> " +
                      $"called '{id}', so no arrowhead is drawn");

                // **The reference is recorded even when it cannot be honoured** (issue #202). A dangling marker is a
                // fact about the file - the path asked for an id the document does not define - and a picker that
                // showed "no marker" where the file named one would say the opposite of what the file says. The
                // warning above is what reports that no arrowhead was drawn for it.
                RecordMarker(shape, slot, id);
                continue;
            }

            RecordMarker(shape, slot, id);

            foreach (string problem in marker.Problems)
            {
                context.Warnings.Add(problem);
            }

            bool placedOne = false;
            foreach (SubPath sub in shape.SubPaths)
            {
                foreach (int vertex in VerticesFor(sub, slot))
                {
                    if (Heading(sub, vertex, slot) is not { } heading)
                    {
                        continue;
                    }

                    double rotation = marker.Orient switch
                    {
                        SvgMarkers.MarkerOrient.Angle => marker.AngleDegrees * Math.PI / 180.0,
                        SvgMarkers.MarkerOrient.AutoStartReverse when slot == MarkerSlot.Start => heading + Math.PI,
                        _ => heading,
                    };

                    ArtGroup art = ReadMarkerInstance(
                        marker, sub.Nodes[vertex].Anchor, rotation, style.StrokeWidth, own, context);

                    // **Marked as the artwork a reference stands for** (issue #202). The writer has to be able to
                    // tell this apart from art a person drew, or a document would gain a second arrowhead on every
                    // save: the reference is written on the path, and this copy would be written beside it.
                    //
                    // The tag names the **slot and the marker**, never the path's id: an id is generated afresh on
                    // every import, so a tag carrying one could never match across a round trip - which is exactly
                    // what the corpus round trip caught.
                    art.ForeignAttributes[SvgWriter.MarkerArtTag] = $"{slot} {id}";
                    placed.Add(art);
                    placedOne = true;
                }
            }

            if (placedOne)
            {
                context.Warnings.Add(
                    $"{Subject(element)} is decorated with {property}=\"url(#{id})\"; this model has no marker " +
                    "member on a stroke, so the arrowhead's content is placed as art at the vertex and will not " +
                    "follow the path if the path is edited");
            }
        }

        return placed.Count == 0 ? null : placed;
    }

    /// <summary>An element named the way a report names it: by its id where it has one.</summary>
    private static string Subject(XElement element)
        => element.Attribute("id")?.Value is { Length: > 0 } id ? $"the element '{id}'" : "an element";

    /// <summary>
    /// The vertices of a subpath a marker property decorates: the first for a start, the last for an end, and every
    /// vertex in between for a mid.
    ///
    /// A closed subpath's last vertex is its first, which is why `marker-end` on a closed path can draw a second
    /// marker on the initial point - the specification says so rather than leaving it to the reader.
    /// </summary>
    private static IEnumerable<int> VerticesFor(SubPath sub, MarkerSlot slot)
    {
        if (sub.Nodes.Count == 0)
        {
            yield break;
        }

        switch (slot)
        {
            case MarkerSlot.Start:
                yield return 0;
                break;

            case MarkerSlot.End:
                yield return sub.Nodes.Count - 1;
                break;

            default:
                for (int i = 1; i < sub.Nodes.Count - 1; i++)
                {
                    yield return i;
                }

                break;
        }
    }

    /// <summary>
    /// The direction of travel at a vertex, in radians, or null when the file gives no tangent there.
    ///
    /// A start marker uses the segment leaving the vertex and an end marker the segment arriving at it; a mid marker
    /// uses the bisector of the two, which is the specification's own rule for a corner. At a corner where the two
    /// tangents cancel - the path doubles back on itself - the bisector is undefined, and the specification says to
    /// assume a slope of zero; that is what the zero here means.
    /// </summary>
    private static double? Heading(SubPath sub, int vertex, MarkerSlot slot)
    {
        (double X, double Y)? incoming = slot == MarkerSlot.Start && !sub.IsClosed
            ? null
            : Tangent(sub, vertex, outbound: false);

        (double X, double Y)? outgoing = slot == MarkerSlot.End && !sub.IsClosed
            ? null
            : Tangent(sub, vertex, outbound: true);

        if (incoming is { } a && outgoing is { } b)
        {
            (double ax, double ay) = Normalize(a);
            (double bx, double by) = Normalize(b);
            (double dx, double dy) = Normalize((ax + bx, ay + by));
            return dx == 0.0 && dy == 0.0 ? 0.0 : Math.Atan2(dy, dx);
        }

        if ((incoming ?? outgoing) is not { } only)
        {
            return null;
        }

        (double x, double y) = Normalize(only);
        return x == 0.0 && y == 0.0 ? null : Math.Atan2(y, x);
    }

    /// <summary>
    /// The tangent at a vertex, outbound or inbound.
    ///
    /// The control handle is what decides it for a curve - a straight segment's handles sit on its own anchors, so
    /// the fallback to the neighbouring anchor is what a line uses and is the same answer for a curve written
    /// without handles.
    /// </summary>
    private static (double X, double Y)? Tangent(SubPath sub, int vertex, bool outbound)
    {
        PathNode node = sub.Nodes[vertex];

        int other = outbound
            ? (vertex + 1 < sub.Nodes.Count ? vertex + 1 : sub.IsClosed ? 0 : -1)
            : (vertex - 1 >= 0 ? vertex - 1 : sub.IsClosed ? sub.Nodes.Count - 1 : -1);

        if (other < 0)
        {
            return null;
        }

        Point2D handle = outbound ? node.OutHandle : node.InHandle;
        double dx = outbound ? handle.X - node.Anchor.X : node.Anchor.X - handle.X;
        double dy = outbound ? handle.Y - node.Anchor.Y : node.Anchor.Y - handle.Y;

        if (dx == 0.0 && dy == 0.0)
        {
            Point2D to = sub.Nodes[other].Anchor;
            dx = outbound ? to.X - node.Anchor.X : node.Anchor.X - to.X;
            dy = outbound ? to.Y - node.Anchor.Y : node.Anchor.Y - to.Y;
        }

        return (dx, dy);
    }

    /// <summary>A vector scaled to unit length, or the zero vector left alone.</summary>
    private static (double X, double Y) Normalize((double X, double Y) vector)
    {
        double length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
        return length == 0.0 ? (0.0, 0.0) : (vector.X / length, vector.Y / length);
    }

    /// <summary>
    /// One placed arrowhead: the marker's own content read into a group carrying the placement.
    ///
    /// The content is read with the marker's **own** inherited style, not the referencing element's - SVG says
    /// properties inherit into a marker from its ancestors and explicitly not from the element that references it,
    /// so a red path's arrowhead is not red unless the file says so.
    /// </summary>
    private static ArtGroup ReadMarkerInstance(
        SvgMarkers.MarkerDefinition marker,
        Point2D vertex,
        double rotation,
        double strokeWidth,
        AffineTransform own,
        Context context)
    {
        var group = new ArtGroup
        {
            Name = marker.Id,
            Transform = own.Compose(marker.Placement(vertex, rotation, strokeWidth)),
        };

        if (marker.ViewportClip(vertex, rotation, strokeWidth) is { } viewportClip)
        {
            group.Clips.Add(ApplyTransform(viewportClip, own));
        }

        // A marker whose content names the marker again would recurse until the stack ran out; the id is claimed
        // while the content is read, exactly as a `use` claims its target.
        string key = "marker:" + marker.Id;
        if (!context.Resolving.Add(key))
        {
            context.Warnings.Add($"the <marker> '{marker.Id}' refers to itself, so its content is not expanded");
            return group;
        }

        PresentationStyle markerStyle = MarkerStyle(marker.Element, context);
        var inside = new Context
        {
            Layer = context.Layer,
            Group = group,
            Style = markerStyle,
            Counts = context.Counts,
            Ids = context.Ids,
            Resolving = context.Resolving,
            Missing = context.Missing,
            UsedPathEffects = context.UsedPathEffects,
            Sheet = context.Sheet,
            Gradients = context.Gradients,
            Patterns = context.Patterns,
            Markers = context.Markers,
            PathEffects = context.PathEffects,
            Warnings = context.Warnings,
            Kept = context.Kept,
            Viewport = context.Viewport,
            BaseDirectory = context.BaseDirectory,
            FontFaces = context.FontFaces,
            Text = context.Text,
        };

        foreach (XElement child in marker.Element.Elements())
        {
            ReadElement(child, inside);
        }

        context.Resolving.Remove(key);
        return group;
    }

    /// <summary>
    /// The style in force inside a marker: its own ancestors' paint folded from the root down, which is where SVG
    /// says a marker's properties inherit from.
    /// </summary>
    private static PresentationStyle MarkerStyle(XElement marker, Context context)
    {
        PresentationStyle style = PresentationStyle.Default;

        foreach (XElement ancestor in marker.AncestorsAndSelf().Reverse())
        {
            style = PresentationStyle.From(
                ancestor,
                style,
                context.Sheet.DeclarationsFor(ancestor, ancestor.Ancestors().ToArray()),
                context.Viewport,
                context.Warn);
        }

        return style;
    }

    private static bool IsClipPath(XElement element)
        => element.Name.LocalName == "clipPath" &&
           (string.IsNullOrEmpty(element.Name.NamespaceName) || element.Name.Namespace == Svg);

    /// <summary>
    /// Which side of a `clipPath`'s outline is inside.
    ///
    /// `clip-rule` decides it and it is a property on the outline's shapes, so the default is inherited from the
    /// `clipPath` element itself - where Inkscape writes it. Ignoring it turns a ring into a disc with identical
    /// coordinates, which no assertion about geometry would catch.
    /// </summary>
    private static FillRule ClipRuleFor(XElement clipPath, Context context)
    {
        string? stated = SvgProperties.Value(
            clipPath,
            context.Sheet.DeclarationsFor(clipPath, clipPath.Ancestors().ToArray()),
            "clip-rule");

        return stated is not null && stated.Trim().Equals("evenodd", StringComparison.OrdinalIgnoreCase)
            ? FillRule.EvenOdd
            : FillRule.NonZero;
    }

    /// <summary>
    /// A clip outline carried into another space by an affine transform.
    ///
    /// All three points of a node move together, for the same reason a path's do: transforming only the anchor
    /// leaves a curve whose handles no longer describe it, which shows up as a crop that is subtly the wrong shape
    /// rather than one that is visibly missing.
    /// </summary>
    private static ClipSpec ApplyTransform(ClipSpec clip, AffineTransform transform)
    {
        if (IsIdentity(transform))
        {
            // A clip with no transform is already where the model wants it, and the common case is worth not
            // rebuilding - it is also the case a mistake here would be invisible in.
            return clip;
        }

        var moved = new ClipSpec { Rule = clip.Rule };
        foreach (SubPath sub in clip.SubPaths)
        {
            SubPath copy = sub.Clone();
            for (int i = 0; i < copy.Nodes.Count; i++)
            {
                PathNode node = copy.Nodes[i];
                node.Anchor = transform.Transform(node.Anchor);
                node.InHandle = transform.Transform(node.InHandle);
                node.OutHandle = transform.Transform(node.OutHandle);
            }

            moved.SubPaths.Add(copy);
        }

        return moved;
    }

    /// <summary>
    /// An element's `clip-path` as this reader resolved it.
    ///
    /// <paramref name="Spec"/> is null when the value could not be honoured, and <paramref name="Warnings"/> then
    /// says why. Keeping the two together is what stops a caller attaching a clip it did not resolve, and what lets
    /// a caller that never draws the element stay quiet about it.
    /// </summary>
    private readonly record struct ClipPath(ClipSpec? Spec, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Whether a viewport cuts its content at the port.
    ///
    /// `overflow` decides it, and its value on an `svg` element is `hidden` unless the file states otherwise, so
    /// content drawn past a port is meant to be cut - which is the reason to nest a viewport in the first place.
    ///
    /// `visible` is the other answer SVG has for this property, and it is honoured by recording no clip at all:
    /// a file that says its content is not cut keeps all of it. Read through the same cascade as every other
    /// property, so `overflow` as an attribute, in a `style` attribute, or in a stylesheet rule all mean the same
    /// thing - a stylesheet that says `visible` and a presentation attribute that says `hidden` are one property
    /// with one winner, decided by CSS's own order, in which the attribute ranks below every rule.
    /// </summary>
    private static bool OverflowClips(XElement element, Context context)
    {
        string? stated = SvgProperties.Value(
            element,
            context.Sheet.DeclarationsFor(element, element.Ancestors().ToArray()),
            "overflow");

        return stated is null || !stated.Trim().Equals("visible", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The port's outline, carried into the containing space by the element's own transform.
    ///
    /// Four corners transformed rather than a rectangle's width and height, because a rotation or a skew makes the
    /// port a parallelogram and a clip is geometry, not a box.
    /// </summary>
    private static ClipSpec PortClip(AffineTransform own, double x, double y, double width, double height)
    {
        var clip = new ClipSpec { Rule = FillRule.NonZero };
        var rect = new SubPath { IsClosed = true };

        foreach ((double px, double py) in new[]
                 {
                     (x, y), (x + width, y), (x + width, y + height), (x, y + height),
                 })
        {
            rect.Nodes.Add(new PathNode(own.Transform(new Point2D(px, py))));
        }

        clip.SubPaths.Add(rect);
        return clip;
    }

    /// <summary>
    /// Whether the port removes any of what the viewport holds.
    ///
    /// Compared as boxes, which is enough for the question being asked - does anything reach past the port - and
    /// generous by a hundredth of a point so that content sitting exactly on the port's edge is not called an
    /// overflow by the last bit of a double. A viewport with nothing in it has nothing to cut.
    /// </summary>
    private static bool PortCuts(Rect2D port, Rect2D content)
    {
        const double Tolerance = 0.01;

        return !content.IsEmpty &&
               (content.Left < port.Left - Tolerance ||
                content.Top < port.Top - Tolerance ||
                content.Right > port.Right + Tolerance ||
                content.Bottom > port.Bottom + Tolerance);
    }

    /// <summary>
    /// Everything a container holds, in that container's own coordinate space, with any groups inside it composed
    /// in.
    ///
    /// <see cref="ArtGroup.BoundingBox"/> answers a narrower question - it measures the shapes a group draws - so a
    /// viewport whose content is text or an image reads as empty there, and a port one of them clearly overflows
    /// would go unclipped. What a port cuts is everything the viewport holds, so this asks that question instead.
    /// </summary>
    private static Rect2D ContentBounds(IItemContainer container, AffineTransform toContainer)
    {
        Rect2D box = Rect2D.Empty;

        foreach (LayerItem child in container.Children)
        {
            Rect2D childBox = child switch
            {
                ArtGroup inner => ContentBounds(inner, toContainer.Compose(inner.Transform)),
                PathItem path => toContainer.Transform(path.BoundingBox()),
                TextItem text => toContainer.Transform(text.BoundingBox()),
                ImageItem image => toContainer.Transform(image.Placement),
                _ => Rect2D.Empty,
            };

            box = box.Union(childBox);
        }

        return box;
    }

    /// <summary>
    /// One coordinate of a nested viewport's origin, in the containing space, or null when it cannot be
    /// established.
    ///
    /// An attribute the file does not write is SVG's own default of **0**, which the specification gives it and
    /// nobody invented. A coordinate the file *does* write is never replaced by that default: the port is what the
    /// content is cut to, so a port dropped at the origin because its `x` could not be read cuts the drawing
    /// somewhere the file never said while looking deliberate. The caller reports the gap and leaves the subtree
    /// undrawn, which is the rule its `width` and `height` already follow - these are the other two sides of the
    /// same rectangle.
    /// </summary>
    private static double? NestedOrigin(XElement element, string attribute, SvgAxis axis, Context context)
    {
        string? text = element.Attribute(attribute)?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0.0;
        }

        if (context.Length(text, axis, attribute) is { } stated)
        {
            return stated;
        }

        context.Warnings.Add(
            $"a nested <svg> states {attribute}=\"{text}\", which this reader cannot resolve, so its viewport is " +
            "not established and its content is not drawn rather than drawn somewhere the file did not put it");
        return null;
    }

    /// <summary>
    /// One dimension of a nested viewport, in the containing space, or null when it cannot be established.
    ///
    /// An attribute the file does not write takes SVG 1.1 §5.1.2's `100%` of the containing viewport, because that
    /// is the value the specification gives it and a nested viewport has one to be a percentage of. An attribute
    /// the file *does* write is never replaced by that default: a stated value swapped for one the file did not
    /// write is the silent-loss family this repository keeps filing, and here it would draw the content at a scale
    /// nobody asked for. The caller reports the gap and leaves the subtree undrawn rather than drawn wrongly.
    /// </summary>
    private static double? NestedPort(XElement element, string attribute, SvgAxis axis, Context context)
    {
        string? text = element.Attribute(attribute)?.Value;
        if (text is null)
        {
            if (context.Viewport is { } containing)
            {
                return axis == SvgAxis.X ? containing.Width : containing.Height;
            }

            context.Warnings.Add(
                $"a nested <svg> states no {attribute}, and there is no containing viewport for SVG's default of " +
                "100% to be a percentage of - so its content is not drawn");
            return null;
        }

        if (context.Length(text, axis, attribute) is { } stated)
        {
            return stated;
        }

        context.Warnings.Add(
            $"a nested <svg> states {attribute}=\"{text}\", which this reader cannot resolve, so its viewport is " +
            "not established and its content is not drawn rather than drawn at the outer scale");
        return null;
    }

    // ------------------------------------------------------------------ shapes

    private static IEnumerable<LayerItem> ReadShape(XElement element, Context context, PresentationStyle style)
    {
        switch (element.Name.LocalName)
        {
            case "rect":
            {
                double x = context.Length(element.Attribute("x")?.Value, SvgAxis.X, "x") ?? 0.0;
                double y = context.Length(element.Attribute("y")?.Value, SvgAxis.Y, "y") ?? 0.0;
                double width = context.Length(element.Attribute("width")?.Value, SvgAxis.X, "width") ?? 0.0;
                double height = context.Length(element.Attribute("height")?.Value, SvgAxis.Y, "height") ?? 0.0;
                if (width <= 0 || height <= 0)
                {
                    yield break;
                }

                double rx = context.Length(element.Attribute("rx")?.Value, SvgAxis.X, "rx") ?? 0.0;
                double ry = context.Length(element.Attribute("ry")?.Value, SvgAxis.Y, "ry") ?? rx;
                rx = Math.Min(rx, width / 2.0);
                ry = Math.Min(ry, height / 2.0);

                PathItem rectangle = Path(element, style);
                SubPath sub = rectangle.AddSubPath(closed: true);

                if (rx <= 0 || ry <= 0)
                {
                    Add(sub, x, y);
                    Add(sub, x + width, y);
                    Add(sub, x + width, y + height);
                    Add(sub, x, y + height);
                }
                else
                {
                    // Clockwise from the end of the first corner, with each corner a quarter ellipse.
                    Add(sub, x + rx, y);
                    Add(sub, x + width - rx, y);
                    Corner(sub, x + width, y + ry, rx, ry, 0);
                    Add(sub, x + width, y + height - ry);
                    Corner(sub, x + width - rx, y + height, rx, ry, 0);
                    Add(sub, x + rx, y + height);
                    Corner(sub, x, y + height - ry, rx, ry, 0);
                    Add(sub, x, y + ry);
                    Corner(sub, x + rx, y, rx, ry, 0);
                }

                yield return rectangle;
                break;
            }

            case "circle":
            {
                double cx = context.Length(element.Attribute("cx")?.Value, SvgAxis.X, "cx") ?? 0.0;
                double cy = context.Length(element.Attribute("cy")?.Value, SvgAxis.Y, "cy") ?? 0.0;
                double r = context.Length(element.Attribute("r")?.Value, SvgAxis.Diagonal, "r") ?? 0.0;
                if (r <= 0)
                {
                    yield break;
                }

                yield return Ellipse(element, style, cx, cy, r, r);
                break;
            }

            case "ellipse":
            {
                double cx = context.Length(element.Attribute("cx")?.Value, SvgAxis.X, "cx") ?? 0.0;
                double cy = context.Length(element.Attribute("cy")?.Value, SvgAxis.Y, "cy") ?? 0.0;
                double rx = context.Length(element.Attribute("rx")?.Value, SvgAxis.X, "rx") ?? 0.0;
                double ry = context.Length(element.Attribute("ry")?.Value, SvgAxis.Y, "ry") ?? 0.0;
                if (rx <= 0 || ry <= 0)
                {
                    yield break;
                }

                yield return Ellipse(element, style, cx, cy, rx, ry);
                break;
            }

            case "line":
            {
                PathItem line = Path(element, style);
                SubPath sub = line.AddSubPath(closed: false);
                Add(sub,
                    context.Length(element.Attribute("x1")?.Value, SvgAxis.X, "x1") ?? 0.0,
                    context.Length(element.Attribute("y1")?.Value, SvgAxis.Y, "y1") ?? 0.0);
                Add(sub,
                    context.Length(element.Attribute("x2")?.Value, SvgAxis.X, "x2") ?? 0.0,
                    context.Length(element.Attribute("y2")?.Value, SvgAxis.Y, "y2") ?? 0.0);
                yield return line;
                break;
            }

            case "polyline":
            case "polygon":
            {
                double[] points = Numbers(element.Attribute("points")?.Value) ?? Array.Empty<double>();
                if (points.Length < 4)
                {
                    yield break;
                }

                PathItem poly = Path(element, style);
                SubPath sub = poly.AddSubPath(closed: element.Name.LocalName == "polygon");
                for (int i = 0; i + 1 < points.Length; i += 2)
                {
                    Add(sub, points[i], points[i + 1]);
                }

                yield return poly;
                break;
            }

            case "path":
            {
                IReadOnlyList<SubPath> parsed = SvgPathData.Parse(
                    element.Attribute("d")?.Value ?? string.Empty,
                    context.Warn);
                if (parsed.Count == 0)
                {
                    yield break;
                }

                PathItem path = Path(element, style);
                foreach (SubPath source in parsed)
                {
                    SubPath sub = path.AddSubPath(source.IsClosed);
                    foreach (PathNode node in source.Nodes)
                    {
                        sub.Nodes.Add(node);
                    }
                }

                yield return path;
                break;
            }
        }
    }

    /// <summary>
    /// A path carrying the element's style, with its own transform composed into its coordinates.
    ///
    /// A shape's transform is baked into its points rather than kept anywhere, because the model's paths have no
    /// transform of their own - their coordinates are the artboard's. Baking is also what makes a transformed shape
    /// selectable and editable where it appears, instead of at some other place that only the renderer knows about.
    /// The transforms of the groups it sits inside are **not** baked: those belong to the groups and stay there.
    /// </summary>
    private static PathItem Path(XElement element, PresentationStyle style)
    {
        var path = new PathItem
        {
            Name = element.Attribute("id")?.Value ?? string.Empty,
            Fill = style.Fill,
            Stroke = style.Stroke,
        };

        return path;
    }

    private static PathItem Ellipse(
        XElement element, PresentationStyle style, double cx, double cy, double rx, double ry)
    {
        // The Bezier constant for a quarter circle: 4/3 * tan(pi/8). Using it rather than an approximation is what
        // makes an imported circle round to the eye and to a pixel count.
        const double Kappa = 0.5522847498307936;
        double ox = rx * Kappa;
        double oy = ry * Kappa;

        PathItem path = Path(element, style);
        SubPath sub = path.AddSubPath(closed: true);

        sub.Nodes.Add(new PathNode(
            new Point2D(cx, cy - ry), new Point2D(cx - ox, cy - ry), new Point2D(cx + ox, cy - ry)));
        sub.Nodes.Add(new PathNode(
            new Point2D(cx + rx, cy), new Point2D(cx + rx, cy - oy), new Point2D(cx + rx, cy + oy)));
        sub.Nodes.Add(new PathNode(
            new Point2D(cx, cy + ry), new Point2D(cx + ox, cy + ry), new Point2D(cx - ox, cy + ry)));
        sub.Nodes.Add(new PathNode(
            new Point2D(cx - rx, cy), new Point2D(cx - rx, cy + oy), new Point2D(cx - rx, cy - oy)));

        return path;
    }

    private static void Add(SubPath sub, double x, double y)
        => sub.Nodes.Add(new PathNode(new Point2D(x, y)));

    /// <summary>
    /// Composes a transform into a path's points.
    ///
    /// All three points of a node move together: the anchor and both handles are in the same space, so transforming
    /// only the anchor would leave a curve whose handles no longer describe it - which shows up as a shape that
    /// moves but does not turn with its group.
    /// </summary>
    private static void ApplyTransform(PathItem path, AffineTransform transform)
    {
        foreach (SubPath sub in path.SubPaths)
        {
            for (int i = 0; i < sub.Nodes.Count; i++)
            {
                PathNode node = sub.Nodes[i];
                node.Anchor = transform.Transform(node.Anchor);
                node.InHandle = transform.Transform(node.InHandle);
                node.OutHandle = transform.Transform(node.OutHandle);
            }
        }

        path.GeometryChanged();
    }

    /// <summary>A rounded rectangle's corner: a quarter ellipse from the current point to the next.</summary>
    private static void Corner(SubPath sub, double x, double y, double rx, double ry, double unused)
    {
        const double Kappa = 0.5522847498307936;
        _ = unused;

        Point2D from = sub.Nodes[^1].Anchor;
        bool horizontalFirst = Math.Abs(x - from.X) > Math.Abs(y - from.Y);

        // The handle along the edge the corner leaves, then the one along the edge it joins.
        Point2D outHandle = horizontalFirst
            ? new Point2D(from.X + ((x - from.X) * Kappa), from.Y)
            : new Point2D(from.X, from.Y + ((y - from.Y) * Kappa));
        Point2D inHandle = horizontalFirst
            ? new Point2D(x, y - ((y - from.Y) * Kappa))
            : new Point2D(x - ((x - from.X) * Kappa), y);

        sub.Nodes[^1].OutHandle = outHandle;
        sub.Nodes.Add(new PathNode(new Point2D(x, y), inHandle, new Point2D(x, y)));
        _ = rx;
        _ = ry;
    }

    /// <summary>
    /// The ids this document refers to as definitions: `use`'s target, and the `data-source` this reader's own
    /// writer records on an instance it wrote as a copy.
    ///
    /// A `data-source` counts because a saved file has to come back with its definitions intact: the writer states
    /// the copy and the id it is a copy of, and the definition itself travels in `defs` beside it, so reading only
    /// `use` would lose the library on every save and every instance would resolve to nothing.
    ///
    /// A reference to another document (`other.svg#root`) is not an id in this one and is left to
    /// <see cref="ReadUse"/>, which reports it - resolving it here would be guessing at a file nothing has opened.
    /// </summary>
    private static IEnumerable<string> ReferencedIds(XElement root)
    {
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (XElement element in root.Descendants())
        {
            bool isUse = element.Name.LocalName == "use";
            string? reference = isUse
                ? element.Attribute("href")?.Value ?? element.Attribute(xlink + "href")?.Value
                : element.Attribute("data-source")?.Value;

            if (reference is not { Length: > 1 })
            {
                continue;
            }

            // A local id is written `#id`. A `use` naming another file is not this document's definition and is
            // reported by ReadUse rather than silently looked up as though it were.
            if (isUse && reference[0] != '#')
            {
                continue;
            }

            string id = reference[0] == '#' ? reference[1..] : reference;

            // The empty id is not a definition; `#` alone is reported by ReadUse as a reference to nothing.
            if (id.Length > 0 && seen.Add(id))
            {
                yield return id;
            }
        }
    }

    /// <summary>
    /// The element a definition id names, preferring the definition over a **copy** of one.
    ///
    /// A file this writer produced states an instance as its content plus `data-source`, and that content carries
    /// the definition's own id - so the id appears twice: once inside the instance, and once in `defs` where the
    /// writer put the definition back. The copy is inside an element with `data-source`, which is precisely what
    /// makes it a copy rather than the definition, so a match under one is used only when there is nothing else.
    /// Taking the copy would read the definition out of an instance, which is the wrong place for it and would
    /// make the library a copy of a copy.
    /// </summary>
    private static XElement? DefinitionOf(XElement root, string id)
    {
        XElement? copy = null;

        foreach (XElement element in root.Descendants())
        {
            if (element.Attribute("id")?.Value != id)
            {
                continue;
            }

            if (element.Ancestors().Any(ancestor => ancestor.Attribute("data-source") is not null))
            {
                copy ??= element;
                continue;
            }

            return element;
        }

        return copy;
    }

    /// <summary>
    /// The document's definitions, read once each into <see cref="CadDocument.Definitions"/>.
    ///
    /// **Why the document needs them at all.** The reader keeps an instance's content as a copy, which makes one
    /// render right and loses the thing the file meant: an edit to the definition has to change every instance of
    /// it. A copy has nowhere to be edited *from*, so the definition is kept as an asset beside the instances - the
    /// same shape a width profile or a filter has - and <see cref="InstanceResolver"/> is what reads it back into
    /// the instances.
    ///
    /// The content is **not drawn** and does not join the layer tree: `defs` means "not here", so an entry lives in
    /// its own library and only its children travel into an instance.
    ///
    /// A `symbol` is the case that has to be read differently: SVG sizes it from the `use` that draws it, so its
    /// own root (and the fit into the port) belongs to each instance rather than to the definition. Only the
    /// symbol's **content** is a definition, which is exactly what <see cref="ReadUse"/> puts inside an instance.
    ///
    /// Counts go to a scratch dictionary and the cycle set is its own: reading a definition is not drawing it, and
    /// folding its elements into the import's own census would say the file holds two of everything a `use` names.
    /// </summary>
    private static void ReadDefinitions(XElement root, Context context, CadDocument document)
    {
        foreach (string id in ReferencedIds(root))
        {
            if (DefinitionOf(root, id) is not { } target)
            {
                // No element with that id. ReadUse has already reported the reference itself, and inventing an
                // empty definition here would turn "the file does not contain it" into "the definition is empty".
                continue;
            }

            ArtGroup entry = document.AddDefinition(id);

            var inside = new Context
            {
                Layer = context.Layer,
                Group = entry,
                Style = PresentationStyle.Default,
                Counts = new Dictionary<string, int>(StringComparer.Ordinal),
                Ids = context.Ids,
                Resolving = new HashSet<string>(StringComparer.Ordinal),
                Missing = context.Missing,
                UsedPathEffects = context.UsedPathEffects,
                Sheet = context.Sheet,
                Gradients = context.Gradients,
                Patterns = context.Patterns,
                Markers = context.Markers,
                PathEffects = context.PathEffects,
                Warnings = context.Warnings,
                Kept = context.Kept,
                Viewport = context.Viewport,
                BaseDirectory = context.BaseDirectory,
                FontFaces = context.FontFaces,
                Text = context.Text,
            };

            if (target.Name.LocalName == "symbol")
            {
                foreach (XElement child in target.Elements())
                {
                    ReadElement(child, inside);
                }
            }
            else
            {
                ReadElement(target, inside);
            }
        }
    }

    /// <summary>
    /// The presentation a `use` site establishes, as the model records it on the instance - or null when it
    /// establishes nothing beyond SVG's initial values, which is what keeps an unstyled ordinary document free of
    /// the member.
    ///
    /// This is the **inherited** half of the style the target is read with: the fill and the stroke (width, caps,
    /// joins, miter limit and dash included) that cascade into the definition's content. It is deliberately not the
    /// whole computed style - <c>mix-blend-mode</c> does not inherit and travels on the group, and a marker
    /// reference becomes art at read time rather than a member a repaint could re-apply.
    /// </summary>
    private static InstancePresentation? PresentationOf(PresentationStyle style)
    {
        var presentation = new InstancePresentation(style.Fill, style.Stroke, style.Color);
        return presentation.IsDefault ? null : presentation;
    }

    /// <summary>
    /// A `use`: an instance of whatever it refers to.
    ///
    /// **The instance is a group marked with the id it came from**, not a flattened copy. The geometry inside is the
    /// definition's content as it was read, so the picture is right, and the link is recorded so an edit to the
    /// definition can reach every instance - which is what the person who wrote the file meant, and what a copy
    /// loses while looking identical in one render.
    ///
    /// Both reference forms are read: `xlink:href`, which Inkscape writes, and the SVG 2 bare `href`. A target that
    /// is not there is **reported**, and so is a reference that leads back to itself - a `use` inside the defs it
    /// refers to would otherwise recurse until the stack ran out.
    ///
    /// A cycle is refused by **name**, not by a depth limit: the id being resolved is remembered for as long as it
    /// is being resolved, so a second attempt at the same id is the cycle itself and the report says which one.
    /// That is what makes a file that would loop terminate and say why rather than recursing without bound.
    ///
    /// A `use` that names **another document** is resolved when the reader has that document, and reported with
    /// its reason when it does not - see <see cref="ReadExternalUse"/>, which owns that path.
    /// </summary>
    private static IEnumerable<LayerItem> ReadUse(XElement element, Context context, PresentationStyle style)
    {
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        string? href = element.Attribute("href")?.Value ?? element.Attribute(xlink + "href")?.Value;

        // **A reference to another document.** `other.svg#id`, or `other.svg` for the whole file. This is tested
        // before the local form because the two are distinguished by the first character and a reference that is
        // not `#`-prefixed is never an id in this document.
        if (href is { Length: > 1 } && href[0] != '#')
        {
            foreach (LayerItem used in ReadExternalUse(element, href, context, style))
            {
                yield return used;
            }

            yield break;
        }

        string id = href is { Length: > 1 } && href[0] == '#' ? href[1..] : string.Empty;
        if (id.Length == 0)
        {
            context.Missing.Add(href ?? "(no href)");
            yield break;
        }

        if (!context.Ids.TryGetValue(id, out XElement? target))
        {
            context.Missing.Add(id);
            yield break;
        }

        if (!context.Resolving.Add(id))
        {
            // A reference that leads back to itself. Reported rather than followed, because following it is a
            // stack overflow rather than a drawing.
            context.Missing.Add(id + " (circular)");
            yield break;
        }

        double x = context.Length(element.Attribute("x")?.Value, SvgAxis.X, "x") ?? 0.0;
        double y = context.Length(element.Attribute("y")?.Value, SvgAxis.Y, "y") ?? 0.0;

        var group = new ArtGroup
        {
            Name = element.Attribute("id")?.Value ?? id,
            SourceId = id,
            Transform = AffineTransform.CreateTranslation(x, y)
                .Compose(Transform(element.Attribute("transform")?.Value)),
        };

        var inside = new Context
        {
            Layer = context.Layer,
            Group = group,
            Style = PresentationStyle.From(element, style, viewport: context.Viewport, warn: context.Warn),
            Counts = context.Counts,
            Ids = context.Ids,
            Resolving = context.Resolving,
            Missing = context.Missing,
            UsedPathEffects = context.UsedPathEffects,
            Sheet = context.Sheet,
            Gradients = context.Gradients,
            Patterns = context.Patterns,
            Markers = context.Markers,
            PathEffects = context.PathEffects,
            Warnings = context.Warnings,
            Kept = context.Kept,
            Viewport = context.Viewport,
            BaseDirectory = context.BaseDirectory,
            FontFaces = context.FontFaces,
            Text = SvgTextStyle.From(
                element,
                context.Text,
                context.Sheet.DeclarationsFor(element, element.Ancestors().ToArray()),
                context.Warn),
        };

        ReadUsedTarget(target, id, element, context, inside, group);

        // What this `use` says about the paint of what it draws, recorded so a re-resolution can put it back: the
        // definition is read under SVG's initial values, so re-materialising from it would otherwise drop the paint
        // the use site established. The composed style is the value here - the use's own declarations over whatever
        // it inherits - because that is the style the target was just read under.
        group.InstancePresentation = PresentationOf(inside.Style);

        context.Resolving.Remove(id);
        yield return group;
    }

    /// <summary>
    /// Reads a `use`'s target into the instance group, honouring the two things SVG 2 lets a `use` state about the
    /// viewport an `svg` or `symbol` target establishes.
    ///
    /// **`width` and `height` on a `use` size a `symbol` or an `svg` target, and nothing else.** The symbol half is
    /// arithmetic on the instance group - the use's size over the symbol's view box, falling back to the symbol's
    /// own geometry properties - and the `svg` half is a *viewport*, so it is handed to the nested-`svg` reader as
    /// <see cref="Context.PortOverride"/> and fitted there with `preserveAspectRatio` exactly as any other nested
    /// port is. A plain shape is the case where the attributes have no effect at all, so leaving them alone is
    /// honouring the specification rather than dropping a value.
    ///
    /// The two contexts are separate because the target may come from **another document**, whose assets are its
    /// own: <paramref name="context"/> is the frame the `use` is written in, and <paramref name="inside"/> carries
    /// whichever document's ids, stylesheet and paint servers the target must be read against.
    /// </summary>
    private static void ReadUsedTarget(
        XElement target, string id, XElement use, Context context, Context inside, ArtGroup group)
    {
        if (target.Name.LocalName == "symbol")
        {
            // A symbol is sized by the `use` that draws it: the use's width and height over the symbol's view box,
            // with the symbol's own width and height - SVG 2's geometry properties - as the fallback.
            double symbolWidth = context.Length(use.Attribute("width")?.Value, SvgAxis.X, "width")
                ?? context.Length(target.Attribute("width")?.Value, SvgAxis.X, "width") ?? 0.0;
            double symbolHeight = context.Length(use.Attribute("height")?.Value, SvgAxis.Y, "height")
                ?? context.Length(target.Attribute("height")?.Value, SvgAxis.Y, "height") ?? 0.0;
            double[]? box = Numbers(target.Attribute("viewBox")?.Value);

            if (box is { Length: 4 } && box[2] > 0 && box[3] > 0 && symbolWidth > 0 && symbolHeight > 0)
            {
                group.Transform = group.Transform
                    .Compose(AffineTransform
                        .CreateScale(symbolWidth / box[2], symbolHeight / box[3])
                        .Compose(AffineTransform.CreateTranslation(-box[0], -box[1])));
            }

            // A symbol is a viewport of its own, and one the file may never size - in which case a percentage
            // inside it has nothing to be a percentage of, and saying so is better than measuring it against the
            // document and calling that the answer.
            inside.Viewport = symbolWidth > 0 && symbolHeight > 0
                ? new SvgViewport(symbolWidth, symbolHeight)
                : null;

            foreach (XElement child in target.Elements())
            {
                ReadElement(child, inside);
            }

            return;
        }

        // **An `svg` target takes the use's own port.** SVG 2 sizes that viewport from the use; the dimensions are
        // read here, in the frame the use is written in, and fitted against the target's view box by the nested
        // reader. A use that names only one of the two falls back to the target's own for the other rather than
        // inventing a value or reporting a width it can honour.
        if (target.Name.LocalName == "svg" && StatesSize(use))
        {
            inside.PortOverride = new SvgViewport(
                context.Length(use.Attribute("width")?.Value, SvgAxis.X, "width")
                    ?? NestedPort(target, "width", SvgAxis.X, context) ?? 0.0,
                context.Length(use.Attribute("height")?.Value, SvgAxis.Y, "height")
                    ?? NestedPort(target, "height", SvgAxis.Y, context) ?? 0.0);
        }

        // A shape or a group: read it into this group, which is what makes the instance hold the definition's
        // content rather than pointing at it from nowhere.
        ReadElement(target, inside);
    }

    /// <summary>
    /// A `use` that names **another document**: `other.svg#id`, or `other.svg` for the whole file.
    ///
    /// **It is resolved when the reader has the other document.** `test-use.svg` in the corpus is exactly this - a
    /// two-line document whose only content is `xlink:href="test-use-ref.svg#root"` - and it used to import as
    /// **zero objects** with the raw href in the report. The reader already opens a file beside the document for
    /// `image`, so it does the same here: the other file is parsed, the fragment is looked up among its ids, and
    /// the element is read with **that document's** own ids, stylesheet, paint servers, markers and path effects.
    /// Reading it against this document's assets would draw the right shapes in the wrong paint, which is the
    /// substitution this reader does not make.
    ///
    /// **What cannot be resolved is named with its reason.** A URL is not fetched, a file that is not there is
    /// named with its path, a file that is not an SVG or is not well-formed says which, and a fragment the other
    /// document does not define is named as that. A bare href would merge four different findings into one line
    /// that sends a person looking in the wrong place.
    ///
    /// **The definition is not invented here.** The id belongs to the other document, and this document holds the
    /// resolved copy on the instance plus the id it came from, exactly as a local `use` does. The model has no
    /// cross-document provenance, so a local definition entry would assert the file contains something it does
    /// not; the instances that name it are reported by <see cref="CadDocument.MissingDefinitions"/> as the links
    /// this document cannot follow on its own.
    ///
    /// A cycle **through another document** is refused by name for the same reason a local one is: the file and the
    /// fragment together are the key, because two documents may hold an id of the same name.
    /// </summary>
    private static IEnumerable<LayerItem> ReadExternalUse(
        XElement element, string href, Context context, PresentationStyle style)
    {
        if (href.Contains("://", StringComparison.Ordinal))
        {
            context.Missing.Add(
                $"{href} (a reference to another document, and this reader does not fetch over the network)");
            yield break;
        }

        int hash = href.IndexOf('#');
        string file = hash >= 0 ? href[..hash] : href;
        string fragment = hash >= 0 ? href[(hash + 1)..] : string.Empty;

        if (file.Length == 0)
        {
            // `#id` with no file is the local form, which ReadUse handled before calling this.
            context.Missing.Add(href);
            yield break;
        }

        if (context.BaseDirectory is not { Length: > 0 } directory)
        {
            context.Missing.Add(
                $"{href} (a reference to another document, and this import has no directory to resolve it against)");
            yield break;
        }

        string path;
        try
        {
            path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, Uri.UnescapeDataString(file)));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or UriFormatException or PathTooLongException)
        {
            context.Missing.Add($"{href} (the reference is not a path this reader can open: {exception.Message})");
            yield break;
        }

        if (!File.Exists(path))
        {
            context.Missing.Add($"{href} (there is no file at '{path}')");
            yield break;
        }

        if (LoadExternal(path, href, context) is not { } external)
        {
            // LoadExternal has reported what is wrong with the file itself.
            yield break;
        }

        XElement? target = fragment.Length == 0 ? external.Root : external.Ids.GetValueOrDefault(fragment);
        if (target is null)
        {
            context.Missing.Add($"{href} (the referenced document defines no element with the id '{fragment}')");
            yield break;
        }

        string key = path + "#" + fragment;
        if (!context.Resolving.Add(key))
        {
            context.Missing.Add($"{href} (circular)");
            yield break;
        }

        try
        {
            string id = fragment.Length > 0 ? fragment : System.IO.Path.GetFileNameWithoutExtension(path);
            double x = context.Length(element.Attribute("x")?.Value, SvgAxis.X, "x") ?? 0.0;
            double y = context.Length(element.Attribute("y")?.Value, SvgAxis.Y, "y") ?? 0.0;

            var group = new ArtGroup
            {
                Name = element.Attribute("id")?.Value ?? id,
                SourceId = id,
                Transform = AffineTransform.CreateTranslation(x, y)
                    .Compose(Transform(element.Attribute("transform")?.Value)),
            };

            // The referenced document's filters are document state here, because a `filter="url(#f)"` on the
            // element that was read has to name one this document can find. A name this document already defines
            // is left alone: two documents may legitimately both call a filter `blur`, and replacing this
            // document's own with the other one's would repaint artwork that was never involved.
            if (context.Layer.Document is { } document)
            {
                foreach (FilterSpec filter in external.Filters.All.Values)
                {
                    if (document.FindFilter(filter.Name) is null)
                    {
                        document.AddFilter(filter);
                    }
                }
            }

            // The `use`'s own declarations cascade into the instance, and the other document's root on top of
            // them - that is the order the SVG tree has, since the target is rendered as though it were a child of
            // the use.
            SvgTextStyle useText = SvgTextStyle.From(
                element,
                context.Text,
                context.Sheet.DeclarationsFor(element, element.Ancestors().ToArray()),
                context.Warn);

            var inside = new Context
            {
                Layer = context.Layer,
                Group = group,
                Style = PresentationStyle.From(element, style, viewport: context.Viewport, warn: context.Warn),
                Counts = context.Counts,
                Ids = external.Ids,
                Resolving = context.Resolving,
                Missing = context.Missing,
                // A fresh set: the other document's effect ids are its own, and marking one used in *this*
                // document's set would drop a local effect that nothing here refers to.
                UsedPathEffects = new HashSet<string>(StringComparer.Ordinal),
                Sheet = external.Sheet,
                Gradients = external.Gradients,
                Patterns = external.Patterns,
                Markers = external.Markers,
                PathEffects = external.PathEffects,
                Warnings = context.Warnings,
                Kept = context.Kept,
                Viewport = context.Viewport,
                BaseDirectory = external.BaseDirectory,
                FontFaces = external.FontFaces,
                Text = SvgTextStyle.From(
                    external.Root,
                    useText,
                    external.Sheet.DeclarationsFor(external.Root, Array.Empty<XElement>()),
                    context.Warn,
                    context.FontFaces),
            };

            ReadUsedTarget(target, id, element, context, inside, group);
            group.InstancePresentation = PresentationOf(inside.Style);
            yield return group;
        }
        finally
        {
            context.Resolving.Remove(key);
        }
    }

    /// <summary>
    /// Everything a read of an element needs from the document it lives in, so a `use` of another file reads it
    /// against the other file's assets rather than this one's.
    ///
    /// The definitions and the root's own properties are the document's, not the element's: a paint server is
    /// referred to by id and lives in `defs` wherever it was written, and a font or a line property on the other
    /// root is inherited down into everything the referenced element holds.
    /// </summary>
    private sealed record ExternalSvg(
        XElement Root,
        Dictionary<string, XElement> Ids,
        SvgStylesheet Sheet,
        SvgGradients Gradients,
        SvgPatterns Patterns,
        SvgMarkers Markers,
        IReadOnlyDictionary<string, PathEffectDefinition> PathEffects,
        SvgFilters Filters,
        string? BaseDirectory,

        // **A referenced document's own `@font-face` belongs to it** (issue #129). The faces are loaded here, from
        // this document's stylesheet and this document's directory, because `url(...)` in a referenced file is
        // relative to *that* file - and the element being read through `use` is that document's, so it is that
        // document's face it means. Inheriting the referencing document's faces instead was the unverified half this
        // closes.
        SvgFontFaces FontFaces);

    /// <summary>
    /// Parses another document and collects its assets, or reports why it could not be read and returns null.
    ///
    /// Nothing but the file's own definitions is gathered here; the referenced element is read by the caller with
    /// the context this builds. **No size and no view port**: a file this reader was pointed at is a source of
    /// definitions, not a page, and the artboard belongs to the document that was opened.
    /// </summary>
    private static ExternalSvg? LoadExternal(string path, string href, Context context)
    {
        XDocument xml;
        try
        {
            xml = XDocument.Parse(File.ReadAllText(path), LoadOptions.None);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            context.Missing.Add($"{href} (the file could not be read: {exception.Message})");
            return null;
        }

        if (xml.Root is not { } root || root.Name.LocalName != "svg")
        {
            context.Missing.Add($"{href} (the file's root element is not <svg>)");
            return null;
        }

        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        Index(root, ids);

        string? directory = System.IO.Path.GetDirectoryName(path);
        string css = CollectStyles(root);
        var sheet = SvgStylesheet.Parse(css, directory);

        return new ExternalSvg(
            root,
            ids,
            sheet,
            SvgGradients.Collect(root, sheet),
            SvgPatterns.Collect(root),
            SvgMarkers.Collect(root),
            CollectPathEffects(root),
            SvgFilters.Collect(root, sheet, context.Warn),
            directory,
            SvgFontFaces.Load(css, directory, root));
    }

    /// <summary>
    /// The provenance the writer records for an instance it had to write as a copy.
    ///
    /// The model has no separate definition object to point a real `use` at - an instance group *holds* the
    /// definition's content - so the writer inlines the content and states the id it came from in `data-source`
    /// rather than losing it. Reading that back is what makes the round trip return the model it started with:
    /// without it an instance silently becomes a plain copy on the second open, with the link gone and nothing
    /// said, which is the substitution this repository forbids.
    ///
    /// It is deliberately **not** resolved against the id index the way a real `use` is. The content is already
    /// inside this group, so a target this file no longer contains is not a missing definition - it is what a copy
    /// looks like - and reporting it would be a false alarm on every instance this editor ever wrote.
    /// </summary>
    private static string? SourceOf(XElement element)
        => element.Attribute("data-source") is { Value.Length: > 0 } source ? source.Value : null;

    /// <summary>Whether a `use` states a size of its own, which SVG 2 applies only to a `symbol` or `svg` target.</summary>
    private static bool StatesSize(XElement element)
        => element.Attribute("width") is { Value.Length: > 0 } || element.Attribute("height") is { Value.Length: > 0 };

    private static bool IsHidden(XElement element)
    {
        string? style = element.Attribute("style")?.Value;
        if (style is not null && style.Replace(" ", string.Empty)
                .Contains("display:none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return element.Attribute("display")?.Value.Trim()
            .Equals("none", StringComparison.OrdinalIgnoreCase) == true;
    }

    // ------------------------------------------------------------------ images

    /// <summary>
    /// An `image`: the raster it refers to, placed where the element puts it.
    ///
    /// **The bytes come from inside the document or from a file beside it, and from nowhere else.** A reference
    /// that cannot be resolved - a file that is not there, a URL, a format this reader does not open - is added to
    /// <see cref="Context.Missing"/> rather than being dropped: the document asked for a picture, and an import
    /// that says which one it did not get is one somebody can act on. Fetching it from the network is not reading
    /// the file, so it is never done.
    ///
    /// The geometry is the element's box **after `preserveAspectRatio`**, because that is what a viewer draws: the
    /// box is where the picture is allowed to go, and the picture's own proportions decide how much of it is
    /// filled.
    /// </summary>
    private static ImageItem? ReadImage(XElement element, Context context, AffineTransform own)
    {
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        string? href = element.Attribute("href")?.Value ?? element.Attribute(xlink + "href")?.Value;
        string name = element.Attribute("id")?.Value ?? "image";

        ImageItem? image = SvgImages.Load(href, context.BaseDirectory, name, out string? problem);
        if (image is null)
        {
            context.Missing.Add(problem ?? $"image '{href}' could not be read");
            return null;
        }

        double x = context.Length(element.Attribute("x")?.Value, SvgAxis.X, "x") ?? 0.0;
        double y = context.Length(element.Attribute("y")?.Value, SvgAxis.Y, "y") ?? 0.0;

        // SVG 2 gives an image with no width or height its intrinsic size; SVG 1.1 simply leaves it undrawn. The
        // intrinsic size is what a viewer does with the attributes the specification made optional, and it is the
        // picture's own dimensions rather than a number invented for it.
        double width = context.Length(element.Attribute("width")?.Value, SvgAxis.X, "width")
            ?? image.PixelWidth;
        double height = context.Length(element.Attribute("height")?.Value, SvgAxis.Y, "height")
            ?? image.PixelHeight;

        if (width <= 0 || height <= 0)
        {
            context.Warnings.Add($"image '{name}' has no area, so there is nothing to draw");
            return null;
        }

        (double fitX, double fitY, double offsetX, double offsetY) = Fit(
            width, height, image.PixelWidth, image.PixelHeight,
            element.Attribute("preserveAspectRatio")?.Value);

        var box = new Rect2D(
            x + offsetX, y + offsetY, image.PixelWidth * fitX, image.PixelHeight * fitY);
        image.Placement = Place(box, own, name, context);

        // A flip is state rather than resampled pixels - the samples are the file's bytes and stay that way - and
        // the determinant is what says whether the transform turns the picture over.
        if ((own.A * own.D) - (own.B * own.C) < 0)
        {
            image.MirrorX = own.A < 0;
            image.MirrorY = own.D < 0;
        }

        return image;
    }

    /// <summary>
    /// The element's own transform applied to an image's box.
    ///
    /// A path's transform is baked into its points; an image has no points, only a rectangle - so a translate or a
    /// scale moves the rectangle, and a **rotation or skew is reported**, because a rectangle cannot express one
    /// and quietly drawing the unrotated box would be a picture the file did not ask for.
    /// </summary>
    private static Rect2D Place(Rect2D box, AffineTransform transform, string name, Context context)
    {
        if (IsIdentity(transform))
        {
            return box;
        }

        if (Math.Abs(transform.B) > 1e-12 || Math.Abs(transform.C) > 1e-12)
        {
            context.Warnings.Add(
                $"image '{name}' is turned or skewed, and the model places an image with a rectangle");
        }

        Point2D first = transform.Transform(new Point2D(box.X, box.Y));
        Point2D second = transform.Transform(new Point2D(box.Right, box.Bottom));
        return Rect2D.FromPoints(first, second);
    }

    // ------------------------------------------------------------------ lengths, numbers, transforms

    /// <summary>
    /// A length in the file's own user units, which is what every coordinate inside a document is written in.
    ///
    /// **The unit suffix is converted, not ignored.** `1in` is ninety-six user units, `72pt` is ninety-six as well,
    /// and a bare number is already in user units - so two lengths SVG calls equal come out equal, which they did
    /// not while the suffix was dropped and `72pt` read as seventy-two. The conversion itself lives in
    /// <see cref="SvgLength"/>; this is the shorthand for code that has no viewport to offer.
    ///
    /// A **percentage returns null here** rather than a number, because what it resolves against depends on the
    /// axis and the viewport. Every coordinate the reader takes goes through
    /// <see cref="Context.Length(string?, SvgAxis, string)"/> instead, which has both and reports one it cannot
    /// resolve rather than substituting the attribute's default.
    /// </summary>
    internal static double? Length(string? text) => SvgLength.Parse(text);

    /// <summary>
    /// Every number in a list, which is what `points`, `viewBox` and `stroke-dasharray` are.
    ///
    /// A number ends where the next character cannot continue it, so `10-5` is two numbers and `1.5.5` is two -
    /// which is why this does not split on separators. Splitting on commas reads those as one malformed token and
    /// loses the rest of the list.
    /// </summary>
    internal static double[]? Numbers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var values = new List<double>();
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            int start = i;
            bool seenDot = false;
            bool seenExponent = false;

            while (i < text.Length)
            {
                char n = text[i];
                if (char.IsDigit(n))
                {
                    i++;
                    continue;
                }

                if (n == '.' && !seenDot && !seenExponent)
                {
                    seenDot = true;
                    i++;
                    continue;
                }

                if (n is '+' or '-')
                {
                    bool afterExponent = i > start && text[i - 1] is 'e' or 'E';
                    if (i != start && !afterExponent)
                    {
                        break;
                    }

                    i++;
                    continue;
                }

                if (n is 'e' or 'E' && !seenExponent)
                {
                    seenExponent = true;
                    i++;
                    continue;
                }

                break;
            }

            if (i == start)
            {
                i++;
                continue;
            }

            if (double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    /// <summary>
    /// An SVG `transform` list.
    ///
    /// The functions apply **in the order written**: `translate(10,0) scale(2,2)` moves the space and then scales
    /// it, so the origin ends up at x=10 - the scale does not multiply a translation that has already happened.
    /// `Compose` applies its argument first, so composing left to right here is what produces that order; folding
    /// the other way gives x=20 and is the classic symptom of getting this backwards.
    /// </summary>
    internal static AffineTransform Transform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return AffineTransform.Identity;
        }

        AffineTransform result = AffineTransform.Identity;
        int i = 0;

        while (i < text.Length)
        {
            int open = text.IndexOf('(', i);
            if (open < 0)
            {
                break;
            }

            int close = text.IndexOf(')', open);
            if (close < 0)
            {
                break;
            }

            string name = text[i..open].Trim().TrimStart(',', ' ', '\t', '\n', '\r');
            double[] args = Numbers(text[(open + 1)..close]) ?? Array.Empty<double>();

            AffineTransform step = name switch
            {
                "matrix" when args.Length >= 6 =>
                    new AffineTransform(args[0], args[1], args[2], args[3], args[4], args[5]),
                "translate" when args.Length >= 1 =>
                    AffineTransform.CreateTranslation(args[0], args.Length > 1 ? args[1] : 0.0),
                "scale" when args.Length >= 1 =>
                    AffineTransform.CreateScale(args[0], args.Length > 1 ? args[1] : args[0]),
                "rotate" when args.Length >= 3 => AffineTransform
                    .CreateTranslation(args[1], args[2])
                    .Compose(AffineTransform.CreateRotation(args[0] * Math.PI / 180.0))
                    .Compose(AffineTransform.CreateTranslation(-args[1], -args[2])),
                "rotate" when args.Length >= 1 =>
                    AffineTransform.CreateRotation(args[0] * Math.PI / 180.0),
                "skewX" when args.Length >= 1 =>
                    AffineTransform.CreateSkew(Math.Tan(args[0] * Math.PI / 180.0), 0.0),
                "skewY" when args.Length >= 1 =>
                    AffineTransform.CreateSkew(0.0, Math.Tan(args[0] * Math.PI / 180.0)),
                _ => AffineTransform.Identity,
            };

            result = result.Compose(step);
            i = close + 1;
        }

        return result;
    }
}
