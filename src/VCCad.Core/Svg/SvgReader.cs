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
/// What this reads today is the foundation: the basic shapes, paths, groups, transforms, the view box, and the
/// presentation attributes that decide how they are painted. `use`, `text`, `image`, `style`, symbols and the paint
/// servers are their own issues, and each hangs off this.
/// </summary>
public static class SvgReader
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

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

        // The artboard is the view port, and the view box becomes a transform on the content rather than a change
        // to the artboard - so an artboard-sized object and a view-box-sized file describe the same picture, and
        // the file's own units survive in the model.
        (double width, double height, AffineTransform viewBox) = ReadViewBox(root);
        if (width <= 0 || height <= 0)
        {
            throw new SvgImportException(
                "the SVG has no usable size: it needs a width and height, or a viewBox");
        }

        Artboard artboard = document.AddArtboard(new Size2D(width, height), "SVG");
        Layer layer = artboard.AddLayer("SVG");

        // The view box applies to everything, so it goes on one group - but only when there is one to apply. A
        // group that carries the identity would be structure the file does not have.
        ArtGroup? viewGroup = IsIdentity(viewBox) ? null : new ArtGroup { Name = "viewBox", Transform = viewBox };

        // Every id in the file, indexed before anything is drawn: a `use` may refer to a definition that appears
        // after it, and a reader that indexed as it went would find nothing and silently drop the instance.
        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        Index(root, ids);

        // Every style element in the document, in document order - including the ones inside defs, which are
        // still stylesheets and still apply. Collecting them by walking the tree as it is read would miss a sheet
        // defined after the elements it styles, which multi-style.svg does.
        SvgStylesheet sheet = SvgStylesheet.Parse(CollectStyles(root), baseDirectory);
        SvgGradients gradients = SvgGradients.Collect(root, sheet);
        var warnings = new HashSet<string>(StringComparer.Ordinal);

        // Filters are document assets: an element refers to one by id, so they are collected once and held on the
        // document rather than copied into every element that uses them.
        foreach (FilterSpec filter in SvgFilters.Collect(root).All.Values)
        {
            document.AddFilter(filter);
        }

        var context = new Context
        {
            Layer = layer,
            Group = viewGroup,
            Style = PresentationStyle.Default,
            Counts = counts,
            Ids = ids,
            Resolving = new HashSet<string>(StringComparer.Ordinal),
            Missing = new List<string>(),
            Sheet = sheet,
            Gradients = gradients,
            Warnings = warnings,
        };

        foreach (XElement child in root.Elements())
        {
            ReadElement(child, context);
        }

        if (viewGroup is { Children.Count: > 0 })
        {
            layer.AddItem(viewGroup);
        }

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

    /// <summary>Reads an SVG document from a file.</summary>
    public static SvgImportResult ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new SvgImportException($"there is no file at '{path}'");
        }

        return Read(File.ReadAllText(path), System.IO.Path.GetDirectoryName(path));
    }

    // ------------------------------------------------------------------ the view box

    /// <summary>
    /// The artboard size and the transform the view box implies.
    ///
    /// `preserveAspectRatio` is honoured in its two common forms: **meet** fits the whole view box inside the view
    /// port and leaves space, **slice** fills the view port and crops, and **none** stretches - the one that
    /// changes an object's shape. Reading it wrong scales the artwork by the wrong factor in one axis, which looks
    /// like a font problem and is not one.
    /// </summary>
    private static (double Width, double Height, AffineTransform Transform) ReadViewBox(XElement root)
    {
        double? width = Length(root.Attribute("width")?.Value);
        double? height = Length(root.Attribute("height")?.Value);
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
            return (width ?? DefaultViewport, height ?? DefaultViewport, AffineTransform.Identity);
        }

        double boxWidth = box[2];
        double boxHeight = box[3];
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            throw new SvgImportException($"the viewBox has no area: {boxWidth} by {boxHeight}");
        }

        double viewWidth = width ?? boxWidth;
        double viewHeight = height ?? boxHeight;

        string aspect = root.Attribute("preserveAspectRatio")?.Value?.Trim() ?? "xMidYMid meet";
        bool stretch = aspect.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("none");
        bool slice = aspect.Contains("slice", StringComparison.Ordinal);

        double scaleX = viewWidth / boxWidth;
        double scaleY = viewHeight / boxHeight;
        double uniform = slice ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
        double finalX = stretch ? scaleX : uniform;
        double finalY = stretch ? scaleY : uniform;

        double usedWidth = boxWidth * finalX;
        double usedHeight = boxHeight * finalY;

        // Where the leftover space goes. The default is centred, which is what xMidYMid means and what every
        // viewer does when the attribute is absent.
        double offsetX = aspect.Contains("xMin", StringComparison.Ordinal) ? 0.0
            : aspect.Contains("xMax", StringComparison.Ordinal) ? viewWidth - usedWidth
            : (viewWidth - usedWidth) / 2.0;
        double offsetY = aspect.Contains("YMin", StringComparison.Ordinal) ? 0.0
            : aspect.Contains("YMax", StringComparison.Ordinal) ? viewHeight - usedHeight
            : (viewHeight - usedHeight) / 2.0;

        AffineTransform transform = AffineTransform.CreateTranslation(offsetX, offsetY)
            .Compose(AffineTransform.CreateScale(finalX, finalY))
            .Compose(AffineTransform.CreateTranslation(-box[0], -box[1]));

        return (viewWidth, viewHeight, transform);
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
        /// SVG elements the reader does not know, by name, each recorded once.
        ///
        /// Reported rather than silently skipped: an element that is not understood is artwork that went missing,
        /// and a list of names is a gap somebody can act on. Silently ignoring it produces a drawing that is
        /// simply wrong with nothing to say why.
        /// </summary>
        public required HashSet<string> Warnings { get; init; }

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

        PresentationStyle style = PresentationStyle.From(element, context.Style, context.Sheet.DeclarationsFor(
            element, element.Ancestors().ToArray()));

        switch (name)
        {
            case "g":
            case "a":
            case "switch":
            case "svg":
                ReadGroup(element, context, style);
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
                return;

            case "use":
                foreach (LayerItem used in ReadUse(element, context, style))
                {
                    context.Add(used);
                    context.Counts["use"] = context.Counts.GetValueOrDefault("use") + 1;
                }

                return;
        }

        // A shape's own transform is baked into its points once they exist - see ApplyTransform - because the
        // model's paths have no transform of their own: their coordinates are the artboard's.
        AffineTransform own = Transform(element.Attribute("transform")?.Value);

        // Anything that reaches here is an element this reader does not know. It is **reported** and then skipped:
        // artwork that quietly went missing is the worst kind of import bug, because the drawing looks deliberate.
        // The known elements are the ones above and the shapes ReadShape handles.
        if (name is not ("rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path"))
        {
            context.Warnings.Add(name);
            return;
        }

        foreach (LayerItem item in ReadShape(element, style))
        {
            if (item.FilterId is null && FilterReference(element) is { } filterId)
            {
                item.FilterId = filterId;
            }

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
                    else
                    {
                        shape.Fill = shape.Fill with
                        {
                            IsVisible = true,
                            Gradient = context.Gradients.Resolve(gradientId, shape.BoundingBox()),
                        };
                    }
                }

                if (!IsIdentity(own))
                {
                    ApplyTransform(shape, own);
                }
            }

            context.Add(item);
            context.Counts[name] = context.Counts.GetValueOrDefault(name) + 1;
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

    private static void ReadGroup(XElement element, Context context, PresentationStyle style)
    {
        // A group keeps its transform on the group, where the model can apply it to everything inside at once and
        // where a later edit can change it. Baking it into the children would make the group's transform
        // uneditable and would lose the structure the file has.
        AffineTransform transform = Transform(element.Attribute("transform")?.Value);
        var group = new ArtGroup { Name = element.Attribute("id")?.Value ?? string.Empty };
        group.Transform = transform;

        var inside = new Context
        {
            Layer = context.Layer,
            Group = group,
            Style = style,
            Counts = context.Counts,
            Ids = context.Ids,
            Resolving = context.Resolving,
            Missing = context.Missing,
            Sheet = context.Sheet,
            Gradients = context.Gradients,
            Warnings = context.Warnings,
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

    // ------------------------------------------------------------------ shapes

    private static IEnumerable<LayerItem> ReadShape(XElement element, PresentationStyle style)
    {
        switch (element.Name.LocalName)
        {
            case "rect":
            {
                double x = Length(element.Attribute("x")?.Value) ?? 0.0;
                double y = Length(element.Attribute("y")?.Value) ?? 0.0;
                double width = Length(element.Attribute("width")?.Value) ?? 0.0;
                double height = Length(element.Attribute("height")?.Value) ?? 0.0;
                if (width <= 0 || height <= 0)
                {
                    yield break;
                }

                double rx = Length(element.Attribute("rx")?.Value) ?? 0.0;
                double ry = Length(element.Attribute("ry")?.Value) ?? rx;
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
                double cx = Length(element.Attribute("cx")?.Value) ?? 0.0;
                double cy = Length(element.Attribute("cy")?.Value) ?? 0.0;
                double r = Length(element.Attribute("r")?.Value) ?? 0.0;
                if (r <= 0)
                {
                    yield break;
                }

                yield return Ellipse(element, style, cx, cy, r, r);
                break;
            }

            case "ellipse":
            {
                double cx = Length(element.Attribute("cx")?.Value) ?? 0.0;
                double cy = Length(element.Attribute("cy")?.Value) ?? 0.0;
                double rx = Length(element.Attribute("rx")?.Value) ?? 0.0;
                double ry = Length(element.Attribute("ry")?.Value) ?? 0.0;
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
                Add(sub, Length(element.Attribute("x1")?.Value) ?? 0.0, Length(element.Attribute("y1")?.Value) ?? 0.0);
                Add(sub, Length(element.Attribute("x2")?.Value) ?? 0.0, Length(element.Attribute("y2")?.Value) ?? 0.0);
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
                IReadOnlyList<SubPath> parsed = SvgPathData.Parse(element.Attribute("d")?.Value ?? string.Empty);
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
    /// </summary>
    private static IEnumerable<LayerItem> ReadUse(XElement element, Context context, PresentationStyle style)
    {
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        string? href = element.Attribute("href")?.Value ?? element.Attribute(xlink + "href")?.Value;

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

        double x = Length(element.Attribute("x")?.Value) ?? 0.0;
        double y = Length(element.Attribute("y")?.Value) ?? 0.0;

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
            Style = PresentationStyle.From(element, style),
            Counts = context.Counts,
            Ids = context.Ids,
            Resolving = context.Resolving,
            Missing = context.Missing,
            Sheet = context.Sheet,
            Gradients = context.Gradients,
            Warnings = context.Warnings,
        };

        if (target.Name.LocalName == "symbol")
        {
            // A symbol is sized by the `use` that draws it: the use's width and height over the symbol's view box,
            // with the symbol's own width and height - SVG 2's geometry properties - as the fallback.
            double symbolWidth = Length(element.Attribute("width")?.Value)
                ?? Length(target.Attribute("width")?.Value) ?? 0.0;
            double symbolHeight = Length(element.Attribute("height")?.Value)
                ?? Length(target.Attribute("height")?.Value) ?? 0.0;
            double[]? box = Numbers(target.Attribute("viewBox")?.Value);

            if (box is { Length: 4 } && box[2] > 0 && box[3] > 0 && symbolWidth > 0 && symbolHeight > 0)
            {
                group.Transform = group.Transform
                    .Compose(AffineTransform
                        .CreateScale(symbolWidth / box[2], symbolHeight / box[3])
                        .Compose(AffineTransform.CreateTranslation(-box[0], -box[1])));
            }

            foreach (XElement child in target.Elements())
            {
                ReadElement(child, inside);
            }
        }
        else
        {
            // A shape or a group: read it into this group, which is what makes the instance hold the definition's
            // content rather than pointing at it from nowhere.
            ReadElement(target, inside);
        }

        context.Resolving.Remove(id);
        yield return group;
    }

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

    // ------------------------------------------------------------------ lengths, numbers, transforms

    /// <summary>
    /// A length in user units.
    ///
    /// A unit suffix other than the user unit needs the document's DPI to convert, and assuming 96 would import a
    /// file authored in millimetres at the wrong size - so a physical unit is read as its number, and the issue
    /// that needs real unit handling says so.
    /// </summary>
    internal static double? Length(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        if (trimmed.EndsWith('%'))
        {
            return null;
        }

        int end = 0;
        bool seenDot = false;
        bool seenExponent = false;

        while (end < trimmed.Length)
        {
            char c = trimmed[end];
            if (char.IsDigit(c))
            {
                end++;
                continue;
            }

            if (c == '.' && !seenDot && !seenExponent)
            {
                seenDot = true;
                end++;
                continue;
            }

            if (c is '+' or '-' && (end == 0 || trimmed[end - 1] is 'e' or 'E'))
            {
                end++;
                continue;
            }

            if (c is 'e' or 'E' && !seenExponent)
            {
                seenExponent = true;
                end++;
                continue;
            }

            break;
        }

        return end > 0 && double.TryParse(
            trimmed[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

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
