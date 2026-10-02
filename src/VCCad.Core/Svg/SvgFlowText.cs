using System.Text;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>
/// SVG 1.2's **flowed text**: `flowRoot`, `flowRegion`, `flowPara` and `flowSpan`.
///
/// **The file states an area and a sequence of paragraphs; where the lines break is the renderer's business.** That
/// is the whole of what makes flowed text different from `text`, and it is also the whole of what the model can and
/// cannot hold: a <see cref="TextItem"/> is a block with a <see cref="TextItem.FrameWidth"/> that wraps, and an area
/// that is not a rectangle has no spelling in it.
///
/// So a `flowRoot` whose region is **one upright rectangle** is read as a framed block: the rectangle's own `x` and
/// `y` are the block's origin, its `width` is the frame, and every `flowPara` is a paragraph of that block - held
/// apart by the newline the model sets a new baseline with, so the paragraphs keep their order, their own styling
/// and a blank one keeps its blank line. The region's height, and the crop it makes, have no field on the model and
/// are **named** for what they are.
///
/// **A region the model cannot hold is refused, not approximated.** A `path`, a `circle`, several shapes in one
/// region, several regions, a rounded rectangle, a rectangle with a transform of its own: each of those is an area
/// whose wrapping a single width cannot express, and reflowing the text into a different shape is the one answer
/// that looks deliberate on the page and is wrong. The element is named **and kept verbatim** on the document, so
/// refusing it costs the drawing and not the file's own text. <c>SvgReader.ClipPathFor</c> refuses a
/// `clipPathUnits="objectBoundingBox"` outline the same way and for the same reason.
///
/// **Nothing is measured into the document that the file did not say.** The region's numbers are the file's, in the
/// file's own units; the wrapping is the model's own layout, through the same measurer the canvas, the caret and the
/// exported page use (<see cref="VCCad.Core.Text.TextLayoutEngine"/>), so the block that is drawn is the block that
/// is reported. See #126.
/// </summary>
public static partial class SvgReader
{
    /// <summary>
    /// Reads a `flowRoot` into the model's framed text block, or refuses it by name and keeps it.
    ///
    /// The element's own `transform` is treated exactly as it is on a `text` element: one block with no transform
    /// needs no container, and anything else becomes the group the file has.
    /// </summary>
    private static void ReadFlowRoot(XElement element, Context context, PresentationStyle style, SvgTextStyle text)
    {
        if (!FlowRegion(element, context, out Rect2D region))
        {
            // The region said why it could not be read. Kept, so the words and the geometry go back out with the
            // file instead of living on only in a warning.
            context.Kept.Add(element.ToString());
            return;
        }

        var item = new TextItem
        {
            Name = element.Attribute("id")?.Value ?? string.Empty,
            Origin = new Point2D(region.X, region.Y),
            FrameWidth = region.Width,
            LineSpacing = text.LineSpacing,
        };

        var runs = new FlowRuns(context);
        TextAlignment block = FlowAlign(element, context, TextAlignment.Left);
        bool alignmentReported = false;
        bool first = true;

        foreach (XElement paragraph in element.Elements().Where(child => child.Name == Svg + "flowPara"))
        {
            // The paragraph ends the one before it, which is the newline the model sets a new baseline with. It is
            // written before the paragraph's own text, so an empty `flowPara` is a blank line rather than nothing.
            if (!first)
            {
                runs.Break(style, text);
            }

            // The block is aligned the way its **first** paragraph is, because that is the one the model can set the
            // whole block by; a later paragraph that asks for another alignment is a real difference between the
            // file and the block, and it is named once rather than repeating for every paragraph.
            TextAlignment aligned = FlowAlign(paragraph, context, block);
            if (first)
            {
                block = aligned;
            }
            else if (aligned != block && !alignmentReported)
            {
                alignmentReported = true;
                context.Warnings.Add(
                    "the paragraphs of this flowRoot are aligned differently, and the model aligns a whole block: " +
                    $"every paragraph is set {Word(block)}");
            }

            first = false;

            if (paragraph.Attribute("id") is not null || ReadForeign(paragraph).Count > 0)
            {
                // A paragraph is a run of the block, and a run holds text and its style and nothing else - the same
                // answer this reader gives a `tspan`, for the same reason.
                context.Warnings.Add(
                    "a flowPara's own attributes are not kept: the model holds a paragraph as its text and style");
            }

            // A `flowPara` states its own face, size and paint, and the text inside it inherits from there exactly
            // as a `tspan`'s does from its `text` - so the paragraph's own style is resolved before its content is
            // read, not taken from the flowRoot.
            (PresentationStyle paragraphPaint, SvgTextStyle paragraphStyle) =
                FlowStyle(paragraph, context, style, text);

            ReadFlowContent(paragraph, context, paragraphPaint, paragraphStyle, runs);
        }

        item.Alignment = block;

        bool holdsText = runs.Build(item, context);
        if (item.Runs.Count == 0)
        {
            if (!holdsText)
            {
                context.Warnings.Add($"{FlowSubject(element)} holds no characters, so there is no text to import");
            }

            // Nothing was painted: `Paintable` has said which half of it was the reason.
            return;
        }

        if (IsIdentity(Transform(element.Attribute("transform")?.Value)))
        {
            CaptureForeign(element, item);
            context.Add(item);
        }
        else
        {
            // The transform goes on a group, where it is exact rather than decomposed into a rotation and a hope -
            // the same answer the `text` reader gives a block it cannot turn by itself.
            var group = new ArtGroup
            {
                Name = element.Attribute("id")?.Value ?? string.Empty,
                Transform = Transform(element.Attribute("transform")?.Value),
            };

            group.AddItem(item);
            CaptureForeign(element, group);
            context.Add(group);
        }

        context.Counts["flowRoot"] = context.Counts.GetValueOrDefault("flowRoot") + 1;
    }

    /// <summary>Adds a `flowSpan`'s or an `a`'s content, and the text between the elements, to the paragraphs.</summary>
    private static void ReadFlowContent(
        XElement parent, Context context, PresentationStyle paint, SvgTextStyle text, FlowRuns runs)
    {
        foreach (XNode node in parent.Nodes())
        {
            if (node is XText written)
            {
                runs.Append(written.Value, paint, text);
                continue;
            }

            if (node is not XElement child || child.Name.Namespace != Svg)
            {
                continue;
            }

            string name = child.Name.LocalName;
            if (name is "title" or "desc" or "metadata" or "style" or "script" or "defs")
            {
                // The same elements the reader does not draw anywhere else: a title is metadata, not content.
                continue;
            }

            if (name is not ("flowSpan" or "a"))
            {
                context.Warnings.Add(name);
                continue;
            }

            (PresentationStyle childPaint, SvgTextStyle childStyle) = FlowStyle(child, context, paint, text);

            if (child.Attribute("id") is not null || ReadForeign(child).Count > 0)
            {
                context.Warnings.Add(
                    "a flowSpan's own attributes are not kept: a run holds text and its style");
            }

            if (child.Attribute("transform") is { Value.Length: > 0 } transform && transform.Value.Trim().Length > 0)
            {
                context.Warnings.Add("a flowSpan transform is not kept: the model transforms a block, not a run");
            }

            if (Positions(child))
            {
                context.Warnings.Add(
                    "a flowSpan states a position, and text in a flowRoot is placed by the region rather than by " +
                    "the span");
            }

            ReadFlowContent(child, context, childPaint, childStyle, runs);
        }
    }

    /// <summary>
    /// A flow element's own paint and text properties, over the ones it inherits.
    ///
    /// One place resolves them, for the same reason <c>ReadElement</c> resolves them once and hands them to both
    /// readers: a `flowPara` and a `flowSpan` come from the same cascade as a `text` and its `tspan`, and two
    /// readings of it would eventually disagree about a colour or a face.
    /// </summary>
    private static (PresentationStyle Paint, SvgTextStyle Text) FlowStyle(
        XElement element, Context context, PresentationStyle paint, SvgTextStyle text)
    {
        IReadOnlyDictionary<string, (string Value, bool Important)> declarations =
            context.Sheet.DeclarationsFor(element, element.Ancestors().ToArray());

        return (PresentationStyle.From(element, paint, declarations, context.Viewport, context.Warn),
            SvgTextStyle.From(element, text, declarations, context.Warn));
    }

    /// <summary>Whether a flow element states a position of its own, which a flowed region does not read.</summary>
    private static bool Positions(XElement element)
        => element.Attributes().Any(attribute => attribute.Name.Namespace == XNamespace.None &&
                                                 attribute.Name.LocalName is "x" or "y" or "dx" or "dy");

    /// <summary>
    /// The region a `flowRoot` flows into, when it is the one shape the model's frame can hold.
    ///
    /// **The four refusals are the four ways an area stops being a width**: no region, more than one of them, a
    /// region of several shapes, and a shape that is not an upright unrounded rectangle. Each is reported with the
    /// reason and with what happens to the element instead, because "flowRoot" alone says nothing a person can act
    /// on and a reader of the drawing has no way to tell a refused region from one that was never drawn.
    /// </summary>
    private static bool FlowRegion(XElement element, Context context, out Rect2D region)
    {
        region = default;

        List<XElement> regions = element.Elements().Where(child => child.Name == Svg + "flowRegion").ToList();
        if (regions.Count == 0)
        {
            return Refuse(context, element, "states no <flowRegion>, so there is no area to flow into");
        }

        if (regions.Count > 1)
        {
            return Refuse(context, element,
                $"states {regions.Count} <flowRegion>s, and the model's text block has one frame");
        }

        var shapes = new List<XElement>();
        foreach (XElement child in regions[0].Elements())
        {
            if (child.Name.Namespace != Svg)
            {
                continue;
            }

            switch (child.Name.LocalName)
            {
                case "title" or "desc" or "metadata" or "style" or "script" or "defs":
                    continue;

                case "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path":
                    shapes.Add(child);
                    continue;

                default:
                    return Refuse(context, element,
                        $"has a <{child.Name.LocalName}> in its region, which is not a shape this reader measures");
            }
        }

        if (shapes.Count != 1)
        {
            return Refuse(context, element, shapes.Count == 0
                ? "has a <flowRegion> with no shape in it, so there is no area to flow into"
                : $"has a <flowRegion> of {shapes.Count} shapes, and the model's text block is a single width");
        }

        XElement shape = shapes[0];
        if (shape.Name.LocalName != "rect")
        {
            return Refuse(context, element,
                $"has a <{shape.Name.LocalName}> for its region, and the model's text block wraps to a width");
        }

        if (!IsIdentity(Transform(shape.Attribute("transform")?.Value)))
        {
            return Refuse(context, element,
                "has a region whose <rect> carries a transform of its own, and the model's text block is an " +
                "upright frame");
        }

        double x = context.Length(shape.Attribute("x")?.Value, SvgAxis.X, "x") ?? 0.0;
        double y = context.Length(shape.Attribute("y")?.Value, SvgAxis.Y, "y") ?? 0.0;
        double width = context.Length(shape.Attribute("width")?.Value, SvgAxis.X, "width") ?? 0.0;
        double height = context.Length(shape.Attribute("height")?.Value, SvgAxis.Y, "height") ?? 0.0;

        if (width <= 0 || height <= 0)
        {
            return Refuse(context, element,
                $"has a <rect> region {width} by {height} with no area, and SVG flows nothing into an empty region");
        }

        double rx = context.Length(shape.Attribute("rx")?.Value, SvgAxis.X, "rx") ?? 0.0;
        double ry = context.Length(shape.Attribute("ry")?.Value, SvgAxis.Y, "ry") ?? rx;
        if (rx > 0 || ry > 0)
        {
            return Refuse(context, element,
                "has a rounded <rect> for its region, and the model's text block is a rectangle without corners");
        }

        // **The width is honoured and the height is named.** A `TextItem` holds a frame width and grows downwards
        // to fit what it sets; it has no frame height, so text the region would crop is drawn in full. That is a
        // real difference from the file for a block that outgrows its region, and it is said rather than left for
        // somebody to notice on the page.
        context.Warnings.Add(
            $"{FlowSubject(element)} flows into a rectangle {width} by {height}, and the model's text frame is a " +
            "width: the region's height, and the crop it makes, are not held");

        region = new Rect2D(x, y, width, height);
        return true;
    }

    /// <summary>
    /// Reports a region the model cannot hold, with the reason and with where the element goes instead.
    ///
    /// False, so a caller reads as "and therefore do not place it" rather than branching on the message.
    /// </summary>
    private static bool Refuse(Context context, XElement element, string reason)
    {
        context.Warnings.Add(
            $"{FlowSubject(element)} {reason}, so the model holds no text for it and the element is kept verbatim " +
            "on the document");

        return false;
    }

    /// <summary>A `flowRoot` named the way the reports name it: by its id where it has one.</summary>
    private static string FlowSubject(XElement element)
        => element.Attribute("id")?.Value is { Length: > 0 } id ? $"the flowRoot '{id}'" : "a flowRoot";

    /// <summary>
    /// A `text-align`, as the alignment the model's block holds.
    ///
    /// Flowed text is aligned with `text-align` and not with `text-anchor`: the anchor places a whole string at a
    /// point, and the alignment aligns the lines **inside the frame** - which is exactly what
    /// <see cref="TextItem.Alignment"/> means for a framed block
    /// (<c>TextLayoutEngine.Align</c>). Reading the anchor here instead would leave every centred paragraph
    /// left-aligned, which is a substitution the file did not ask for.
    ///
    /// `justify` is a real value with no spelling in the model - a block is aligned, and its lines are not
    /// stretched - so it is reported and the inherited alignment stands, which is this reader's answer to every
    /// other value it cannot hold.
    /// </summary>
    private static TextAlignment FlowAlign(XElement element, Context context, TextAlignment inherited)
    {
        IReadOnlyDictionary<string, (string Value, bool Important)> declarations =
            context.Sheet.DeclarationsFor(element, element.Ancestors().ToArray());

        if (SvgProperties.Value(element, declarations, "text-align") is not { Length: > 0 } stated)
        {
            return inherited;
        }

        switch (stated.Trim().ToLowerInvariant())
        {
            case "start":
            case "left":
                return TextAlignment.Left;

            case "center":
                return TextAlignment.Center;

            case "end":
            case "right":
                return TextAlignment.Right;

            case "justify":
                context.Warnings.Add(
                    "text-align=\"justify\" stretches the lines of a flowRoot to the region, and " +
                    "the model aligns a block without stretching its lines");
                return inherited;

            default:
                context.Warnings.Add($"text-align=\"{stated}\" is not an alignment this reader knows");
                return inherited;
        }
    }

    /// <summary>The model's own word for an alignment, for a report a person can read.</summary>
    private static string Word(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => "centred",
        TextAlignment.Right => "right-aligned",
        _ => "left-aligned",
    };

    /// <summary>
    /// A `flowRoot`'s paragraphs, built into the runs and the newlines a <see cref="TextItem"/> holds.
    ///
    /// **The paragraph is the file's and the line is the model's.** A `flowPara` therefore ends with a newline in
    /// the block's text - the model's own spelling of "this is a new line" - so the paragraphs come back in order,
    /// an empty one keeps a blank line, and a `flowSpan` inside one is a run of it with its own styling. Nothing
    /// else is positional: the width the block wraps at is the region's, and where each line falls inside it is the
    /// layout's business, exactly as it is in the file.
    ///
    /// A piece of text is a run while its style and its paint are unchanged, so a `flowSpan` that states neither is
    /// part of the run it sits in rather than a run of its own - which is what keeps an edit to the paragraph as a
    /// whole an edit to one thing.
    /// </summary>
    private sealed class FlowRuns
    {
        private readonly Context _context;
        private readonly List<Piece> _pieces = new();
        private readonly StringBuilder _current = new();

        private SvgTextStyle? _style;
        private PresentationStyle? _paint;
        private bool _atStart = true;
        private bool _lastWasSpace;

        public FlowRuns(Context context) => _context = context;

        private sealed record Piece(SvgTextStyle Style, PresentationStyle Paint, string Text);

        /// <summary>
        /// Adds text to the piece in hand, collapsing white space the way SVG says.
        ///
        /// The state machine runs over the **whole paragraph** and not per text node, exactly as it does for `text`:
        /// `a <flowSpan>b</flowSpan> c` is one string with one space either side of the span, and a reader that
        /// trimmed each node on its own would either glue the words together or leave two spaces where the file has
        /// one.
        /// </summary>
        public void Append(string value, PresentationStyle paint, SvgTextStyle style)
        {
            foreach (char c in value)
            {
                if (style.PreserveSpace)
                {
                    Open(paint, style).Append(c);
                    _lastWasSpace = char.IsWhiteSpace(c);
                    if (!_lastWasSpace)
                    {
                        _atStart = false;
                    }

                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (_atStart || _lastWasSpace)
                    {
                        continue;
                    }

                    Open(paint, style).Append(' ');
                    _lastWasSpace = true;
                    continue;
                }

                Open(paint, style).Append(c);
                _lastWasSpace = false;
                _atStart = false;
            }
        }

        /// <summary>
        /// Ends the paragraph in hand.
        ///
        /// The break is written **before** the next paragraph's text rather than after this one's, so a `flowPara`
        /// with nothing in it is a blank line rather than nothing at all - which is what the file drew.
        /// </summary>
        public void Break(PresentationStyle paint, SvgTextStyle style)
        {
            Trim();

            // The break joins the piece in hand rather than opening one of its own: the newline belongs to the
            // paragraph that ended, and a piece of nothing but a newline is a run the file has not got. Only a
            // paragraph with nothing before it needs the style handed to it.
            if (_style is null)
            {
                _style = style;
                _paint = paint;
            }

            _current.Append('\n');
            _atStart = true;
            _lastWasSpace = false;
        }

        /// <summary>
        /// The block's runs, and whether the paragraphs held any characters at all.
        ///
        /// The paint is resolved after the runs exist because a gradient is normalised against the block's own box,
        /// and the box is the runs - the same order, and the same flattening to a midpoint, the `text` reader uses.
        /// </summary>
        public bool Build(TextItem item, Context context)
        {
            Close();

            var painted = new List<(TextRun Run, ColorRgb Fill, string? Gradient)>();
            bool wrote = false;

            foreach (Piece piece in _pieces)
            {
                if (piece.Text.Length == 0)
                {
                    continue;
                }

                wrote = true;

                // A run is filled: text the file only strokes, or does not paint at all, is not drawn. Reported,
                // and the rest of the paragraph still is - one unpainted span does not take its neighbours with it.
                if (!Paintable(piece.Paint, context))
                {
                    continue;
                }

                var run = new TextRun
                {
                    Text = piece.Text,
                    FontFamily = piece.Style.FontFamily,
                    FontSize = piece.Style.FontSize,
                    Bold = piece.Style.Bold,
                    Italic = piece.Style.Italic,
                    LetterSpacing = piece.Style.LetterSpacing,
                    WordSpacing = piece.Style.WordSpacing,
                    FontStretch = piece.Style.FontStretch,
                    FontVariant = piece.Style.FontVariant,
                };

                item.Runs.Add(run);
                painted.Add((run, piece.Paint.Fill.Color, piece.Paint.FillGradientId));
            }

            var gradients = new Dictionary<string, ColorRgb>(StringComparer.Ordinal);
            for (int i = 0; i < painted.Count; i++)
            {
                (TextRun run, ColorRgb fill, string? gradient) = painted[i];
                ColorRgb colour = gradient is { Length: > 0 } id
                    ? Flattened(id, item, gradients, context, fill)
                    : fill;

                if (i == 0)
                {
                    // The block's colour is its first piece's, which is where the `text` reader takes it from too.
                    item.Color = colour;
                }

                run.Color = colour == item.Color ? null : colour;
            }

            return wrote;
        }

        /// <summary>
        /// A gradient fill as the one colour the model can set a run in, resolved against the block's own box.
        ///
        /// Resolved once per id: two runs painted by the same paint server are the same colour, and the report is
        /// about the paint server rather than about each run that names it.
        /// </summary>
        private static ColorRgb Flattened(
            string id, TextItem item, Dictionary<string, ColorRgb> resolved, Context context, ColorRgb fallback)
        {
            if (resolved.TryGetValue(id, out ColorRgb known))
            {
                return known;
            }

            if (context.Gradients.Resolve(id, item.LocalBounds()) is { } gradient)
            {
                ColorRgb colour = gradient.Sample(0.5).Color;
                resolved[id] = colour;
                context.Warnings.Add(
                    $"text is filled with the gradient '{id}', and the model fills text with one colour: " +
                    "the gradient's midpoint stands in for it");
                return colour;
            }

            context.Warnings.Add($"no paint server called '{id}' for this text");
            return fallback;
        }

        private StringBuilder Open(PresentationStyle paint, SvgTextStyle style)
        {
            if (_style is not null && (!Equals(_style, style) || !SameFill(_paint!, paint)))
            {
                Close();
            }

            _style = style;
            _paint = paint;
            return _current;
        }

        /// <summary>
        /// Whether two pieces are painted the same, which is what lets one run carry both.
        ///
        /// The rule is the `text` reader's: a change of colour, or of the paint server a colour is resolved from, is
        /// a different run - because a run holds one colour and nothing on it says which server painted it.
        /// </summary>
        private static bool SameFill(PresentationStyle left, PresentationStyle right)
            => Equals(left.Fill, right.Fill) && left.FillGradientId == right.FillGradientId;

        /// <summary>Ends the piece in hand, keeping it only if it holds characters.</summary>
        private void Close()
        {
            if (_current.Length > 0 && _style is not null && _paint is not null)
            {
                _pieces.Add(new Piece(_style, _paint, _current.ToString()));
            }

            _current.Clear();
        }

        /// <summary>
        /// Drops the white space at the end of the piece in hand.
        ///
        /// Trailing white space is not content: left in, it becomes a run of spaces or an advance past the last
        /// glyph - and at a paragraph break it would indent the line that follows. Leading space never got in at
        /// all; see <see cref="Append"/>. It is kept where the file asked for white space to be kept.
        /// </summary>
        private void Trim()
        {
            if (_style is not { PreserveSpace: false } || _current.Length == 0)
            {
                return;
            }

            int end = _current.Length;
            while (end > 0 && _current[end - 1] == ' ')
            {
                end--;
            }

            _current.Length = end;
        }
    }
}
