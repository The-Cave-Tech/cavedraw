using System.Globalization;
using System.Text;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>
/// SVG `text` and the runs inside it.
///
/// **A run is what the file says and a block is what the model can hold.** SVG places every character itself; the
/// model places a block at one origin and lays its runs out in sequence. So a piece of the file becomes a run in the
/// block it continues, and starts a new block when it cannot:
///
/// /// <list type="bullet">
/// <item>A piece on the same baseline, in the same colour, starting where the model's own layout would have put it -
/// or positioned, in which case the room the file left is kept as the advance of the run before it - joins the
/// block as another run.</item>
/// <item>A piece on another baseline, or anchored differently, starts a block of its own: the baseline and the
/// anchor are the model's shape.</item>
/// <item>A piece in another colour is another **run** of the same block, not another block. A run holds its own
/// colour and the painter and the exporter both read it, so the file's one `text` element with two coloured
/// `tspan`s is one object with two colours - which is also what keeps an edit to the string as a whole an edit to
/// one thing. It used to start a block per colour, because nothing painted a run's own; see #161.</item>
/// </list>
///
/// **Nothing is measured into the document that the file did not say, except the advance the model needs.** A gap
/// between two runs is stored as the room the file left (`GapAfter`) plus the width the model's own measurer gives
/// the run - the same measurer the layout, the caret and the bounds use - so the gap is the gap whatever face the
/// machine ends up drawing it with.
///
/// **A value the model cannot hold is reported.** `textLength`, per-character positions, `text-decoration`, a stroked
/// run, a gradient fill: each is named in the import's warnings rather than quietly flattened, because a line that
/// lost its stroke and a string placed a character at a time both look deliberate on the page. The properties #147
/// gave the model - the two spacings, the face's requested width and variant - are no longer in that list; they are
/// resolved onto the run.
/// </summary>
public static partial class SvgReader
{
    /// <summary>One piece of the file's text, positioned, ready to become a run.</summary>
    private sealed class TextChunk
    {
        public required SvgTextStyle Style { get; init; }

        public required PresentationStyle Paint { get; init; }

        public StringBuilder Text { get; } = new();

        /// <summary>Where the file puts this piece's first glyph, in the file's own user units.</summary>
        public double X { get; set; }

        /// <summary>The baseline this piece sits on, in the file's own user units.</summary>
        public double Y { get; set; }

        /// <summary>How wide the face the file names sets this piece, through the model's own measurer.</summary>
        public double Width { get; set; }

        /// <summary>
        /// Whether this piece carries on from the one before it rather than starting somewhere of its own.
        ///
        /// Same face, same paint, same baseline, and starting exactly where that piece ended - which is what makes
        /// two pieces either side of a `<tspan>` one run rather than two halves of one. The style comparison is the
        /// whole of what a run states, so a `tspan` that changes the tracking, the width or the variant is a
        /// different run and not a continuation of the one before it.
        /// </summary>
        public bool Continues(TextChunk previous) =>
            Equals(previous.Style, Style) &&
            Equals(previous.Paint.Fill, Paint.Fill) &&
            previous.Paint.FillGradientId == Paint.FillGradientId &&
            Math.Abs(Y - previous.Y) < 1e-9 &&
            Math.Abs(X - (previous.X + previous.Width)) < 1e-9;
    }

    /// <summary>A block under construction: the item, the piece it started at, and the pieces it holds.</summary>
    private sealed record TextBlock(TextItem Item, TextChunk First, List<TextChunk> Chunks, string? Gradient);

    private static void ReadTextElement(XElement element, Context context, PresentationStyle style, SvgTextStyle text)
    {
        var walker = new TextWalker(context);
        List<TextBlock> blocks = BuildTextBlocks(walker.Walk(element, style, text), context);
        if (blocks.Count == 0)
        {
            return;
        }

        AffineTransform own = Transform(element.Attribute("transform")?.Value);
        if (blocks.Count == 1 && IsIdentity(own))
        {
            // One block and no transform: the text element needs no container of its own, and inventing one would
            // be a group the file has not got.
            CaptureForeign(element, blocks[0].Item);
            context.Add(blocks[0].Item);
        }
        else
        {
            // Otherwise the text element is a container in the model, because it is one in the file: either it
            // carries a transform a block cannot hold (a scale, a skew, a turn about some other point), or it has
            // several baselines that belong together. Keeping the transform on the group is what makes it exact
            // rather than decomposed into a rotation and a hope.
            var group = new ArtGroup
            {
                Name = element.Attribute("id")?.Value ?? string.Empty,
                Transform = own,
            };

            foreach (TextBlock block in blocks)
            {
                group.AddItem(block.Item);
            }

            CaptureForeign(element, group);
            context.Add(group);
        }

        context.Counts["text"] = context.Counts.GetValueOrDefault("text") + 1;
    }

    /// <summary>
    /// Groups positioned pieces into the blocks the model can hold, and places each block where the file put it.
    /// </summary>
    private static List<TextBlock> BuildTextBlocks(List<TextChunk> chunks, Context context)
    {
        var blocks = new List<TextBlock>();
        TextBlock? current = null;

        foreach (TextChunk chunk in chunks)
        {
            if (chunk.Text.Length == 0 || !Paintable(chunk.Paint, context))
            {
                continue;
            }

            if (current is null || !Holds(current, chunk))
            {
                // The block's top-left is one ascent above the baseline, which is where the model stores text and
                // where export expects to find it. The ascent is the usual Latin one rather than a measurement of
                // the face: the file declares no ascent, and the per-run `PlacedAscentEm` below is what puts every
                // baseline back exactly, whatever the face's own metrics turn out to be.
                var item = new TextItem
                {
                    Color = chunk.Paint.Fill.Color,
                    LineSpacing = chunk.Style.LineSpacing,
                    Alignment = chunk.Style.Anchor,
                    Origin = new Point2D(
                        chunk.X, chunk.Y - (TextMeasurement.TypicalAscentEm * chunk.Style.FontSize)),
                };

                current = new TextBlock(item, chunk, new List<TextChunk>(), chunk.Paint.FillGradientId);
                blocks.Add(current);
            }

            current.Chunks.Add(chunk);
            var run = new TextRun
            {
                Text = chunk.Text.ToString(),
                FontFamily = chunk.Style.FontFamily,
                FontSize = chunk.Style.FontSize,
                Bold = chunk.Style.Bold,
                Italic = chunk.Style.Italic,

                // The tracking the file asked for, as the length the model keeps. It goes on the run rather than
                // into `AdvanceWidth`, which is the whole distance to the next run: widening that to express
                // tracking stretches every glyph in the run instead of leaving room between them.
                LetterSpacing = chunk.Style.LetterSpacing,
                WordSpacing = chunk.Style.WordSpacing,

                // The face the file asked for in the words it used. Nothing selects a face by width or variant
                // yet, and the words are kept anyway, because rounding "semi-condensed" to a number and back is
                // how the width stops being the one the author chose.
                FontStretch = chunk.Style.FontStretch,
                FontVariant = chunk.Style.FontVariant,

                // The fraction of *this run's own* em that the block's top-left sits above its baseline. A line of
                // mixed sizes shares one baseline, and export puts each run's baseline back from this number - so
                // a 10pt word beside a 24pt one lands on the line rather than below it.
                PlacedAscentEm = (chunk.Y - current.Item.Origin.Y) / chunk.Style.FontSize,

                // The colour of a run that states one of its own, written only when it differs from the block's.
                // The block's colour is the first piece's, so a one-colour block grows no member it does not need
                // while a `tspan` that changes the fill - which no longer starts a block - carries its colour here,
                // and `ColourOf` answers with the file's paint for every run either way.
                Color = chunk.Paint.Fill.Color == current.Item.Color ? null : chunk.Paint.Fill.Color,
            };

            current.Item.Runs.Add(run);
        }

        foreach (TextBlock block in blocks)
        {
            Place(block, context);
        }

        return blocks;
    }

    /// <summary>
    /// Whether a piece belongs in the block in hand: one origin, one anchor and one leading serve a whole block.
    ///
    /// **The colour is deliberately not a reason to start a block any more.** A run holds its own colour, and the
    /// canvas and the PDF exporter both read <see cref="TextItem.ColourOf"/> as of #161, so a piece in another
    /// colour stays a run of the block the file wrote - which is what makes the two-coloured line one object to
    /// edit. A *gradient* still is a reason: the model fills a run with one colour, so a change of paint server is
    /// a change the block cannot hold run by run, and `Place` resolves it once for the block.
    /// </summary>
    private static bool Holds(TextBlock block, TextChunk chunk)
        => Math.Abs(chunk.Y - block.First.Y) < 1e-9 &&
           block.Item.Alignment == chunk.Style.Anchor &&
           Math.Abs(block.Item.LineSpacing - chunk.Style.LineSpacing) < 1e-9 &&
           block.First.Paint.FillGradientId == chunk.Paint.FillGradientId;

    /// <summary>
    /// The room between the runs of a block, the anchor, and a gradient fill.
    ///
    /// The room is stored twice over and both halves are needed: `GapAfter` says how much of the advance is white
    /// space, which is what export puts back between the glyphs, and `AdvanceWidth` says how far the next run
    /// starts, which is what the layout and export advance their pens by. A run another run follows therefore
    /// carries its advance even when the two are adjacent - export moves its pen by `AdvanceWidth`, so a run
    /// without one puts everything after it in the same place.
    /// </summary>
    private static void Place(TextBlock block, Context context)
    {
        for (int i = 0; i < block.Chunks.Count; i++)
        {
            TextChunk chunk = block.Chunks[i];
            TextRun run = block.Item.Runs[i];

            if (i > 0)
            {
                TextChunk previous = block.Chunks[i - 1];
                double gap = chunk.X - (previous.X + previous.Width);
                if (Math.Abs(gap) > 1e-9)
                {
                    block.Item.Runs[i - 1].AdvanceWidth = previous.Width + gap;
                    block.Item.Runs[i - 1].GapAfter = gap;

                    if (previous.Width + gap <= 0)
                    {
                        // The file pulls this piece back past the end of the one before it, which the model can
                        // only express as a run that has an advance of its own - so the two overlap instead.
                        context.Warnings.Add(
                            "two runs of text overlap, and the model can only place one after the other");
                    }
                }
            }

            if (i < block.Chunks.Count - 1)
            {
                run.AdvanceWidth ??= chunk.Width;
            }
        }

        if (block.Gradient is { Length: > 0 } id)
        {
            // Text is filled with one colour in the model. The gradient is resolved against the block's own box,
            // which is what `objectBoundingBox` normalises against, and its midpoint stands in - the same flattened
            // colour the model uses wherever a gradient cannot be painted.
            if (context.Gradients.Resolve(id, block.Item.LocalBounds()) is { } resolved)
            {
                block.Item.Color = resolved.Sample(0.5).Color;
                context.Warnings.Add(
                    $"text is filled with the gradient '{id}', and the model fills text with one colour: " +
                    "the gradient's midpoint stands in for it");
            }
            else
            {
                context.Warnings.Add($"no paint server called '{id}' for this text");
            }
        }

        // The anchor is where the file put the *end* or the *middle* of the string, and the model's origin is the
        // left edge of the block - so the block is moved back by the width its own layout gives it. Measured
        // through the model, because that is the width the person will see and the width the caret and the bounds
        // use; a number taken from anywhere else would put the anchored text beside the anchor rather than on it.
        if (block.Item.Alignment != TextAlignment.Left)
        {
            double width = TextLayoutEngine.Compute(block.Item).Width;
            double shift = block.Item.Alignment == TextAlignment.Center ? width / 2.0 : width;
            block.Item.Origin = new Point2D(block.Item.Origin.X - shift, block.Item.Origin.Y);
        }
    }

    /// <summary>
    /// Whether the file paints this piece at all.
    ///
    /// A run in the model is *filled*: it has a colour and no stroke. So text the file only strokes, and text it
    /// paints with neither, is text the model cannot draw - and both are reported rather than turned into a run
    /// painted with something the file did not name.
    ///
    /// It reads the paint rather than the piece, so the flowed-text reader - which has no piece to give it - asks
    /// the same question and gets the same answer, and a file painted one way cannot be reported two ways.
    /// </summary>
    private static bool Paintable(PresentationStyle paint, Context context)
    {
        if (paint.Fill.IsVisible)
        {
            return true;
        }

        context.Warnings.Add(paint.Stroke.HasVisibleOutline
            ? "text is stroked and not filled, and the model fills text and never strokes it"
            : "text is painted with neither a fill nor a stroke, so nothing is drawn");
        return false;
    }

    /// <summary>
    /// Walks a text element, turning its content and its positions into pieces.
    ///
    /// The pen is the file's own: `x` and `y` are absolute, `dx` and `dy` relative, and SVG applies them in that
    /// order. Every piece is measured with the model's own measurer, so the pen after it is the pen the model's
    /// layout will use - which is what lets an absolute position become the gap between two runs instead of a width
    /// frozen from a face this machine may not have.
    /// </summary>
    private sealed class TextWalker
    {
        private readonly Context _context;
        private readonly List<TextChunk> _chunks = new();

        private double _penX;
        private double _penY;
        private TextChunk? _current;
        private bool _atStart = true;
        private bool _lastWasSpace;

        public TextWalker(Context context) => _context = context;

        public List<TextChunk> Walk(XElement element, PresentationStyle paint, SvgTextStyle style)
        {
            WalkElement(element, paint, style);
            Flush();

            // Trailing white space is not content: left in, it becomes a run of spaces or an advance past the last
            // glyph that the file has not got. Leading space never got in at all - see Append.
            if (_chunks.Count > 0 && !_chunks[^1].Style.PreserveSpace)
            {
                TextChunk last = _chunks[^1];
                string trimmed = last.Text.ToString().TrimEnd();
                if (trimmed.Length != last.Text.Length)
                {
                    last.Text.Clear();
                    last.Text.Append(trimmed);
                    last.Width = Natural(last);
                }

                if (last.Text.Length == 0)
                {
                    _chunks.RemoveAt(_chunks.Count - 1);
                }
            }

            return _chunks;
        }

        private void WalkElement(XElement element, PresentationStyle paint, SvgTextStyle style)
        {
            double? x = Position(element, "x", SvgAxis.X, style.FontSize);
            double? y = Position(element, "y", SvgAxis.Y, style.FontSize);
            double? dx = Position(element, "dx", SvgAxis.X, style.FontSize);
            double? dy = Position(element, "dy", SvgAxis.Y, style.FontSize);

            if (x is not null)
            {
                _penX = x.Value;
            }

            if (dx is not null)
            {
                _penX += dx.Value;
            }

            if (y is not null)
            {
                _penY = y.Value;
            }

            if (dy is not null)
            {
                _penY += dy.Value;
            }

            if (element.Attribute("textLength") is { Value.Length: > 0 } textLength)
            {
                _context.Warnings.Add(
                    $"textLength=\"{textLength.Value}\" is a measured length the model does not hold");
            }

            if (element.Attribute("lengthAdjust") is { Value.Length: > 0 } lengthAdjust)
            {
                _context.Warnings.Add(
                    $"lengthAdjust=\"{lengthAdjust.Value}\" needs a textLength the model does not hold");
            }

            if (element.Attribute("rotate") is { Value.Length: > 0 } rotate && rotate.Value.Trim() != "0")
            {
                _context.Warnings.Add(
                    $"rotate=\"{rotate.Value}\" turns individual glyphs, and the model turns a block");
            }

            foreach (XNode node in element.Nodes())
            {
                if (node is XText text)
                {
                    Append(text.Value, paint, style);
                }
                else if (node is XElement child && child.Name.Namespace == Svg)
                {
                    WalkChild(child, paint, style);
                }
            }
        }

        private void WalkChild(XElement child, PresentationStyle paint, SvgTextStyle style)
        {
            string name = child.Name.LocalName;
            if (name is "title" or "desc" or "metadata" or "style" or "script" or "defs")
            {
                // The same elements the reader does not draw elsewhere: a title inside text is metadata and not
                // content, and reporting it would be noise about a file that is doing the right thing.
                return;
            }

            if (name is "textPath" or "tref")
            {
                _context.Warnings.Add($"<{name}> is text placed by something this reader does not follow");
                return;
            }

            if (name is not ("tspan" or "a"))
            {
                _context.Warnings.Add(name);
                return;
            }

            if (child.Attribute("id") is not null || ReadForeign(child).Count > 0)
            {
                // A run carries its text and its style and nothing else, so a tspan's own name and namespaced
                // attributes have nowhere to go. Inkscape writes `sodipodi:role="line"` on every line of text,
                // which is why this is one message rather than one per attribute.
                _context.Warnings.Add("a tspan's own attributes are not kept: a run holds text and its style");
            }

            if (child.Attribute("transform") is { Value.Length: > 0 } transform && transform.Value.Trim().Length > 0)
            {
                _context.Warnings.Add("a tspan transform is not kept: the model transforms a block, not a run");
            }

            // A child element is a run of its own: the piece in hand is closed so the child's face starts a new one,
            // and closed again afterwards so the parent's text does not join it.
            Flush();

            IReadOnlyDictionary<string, (string Value, bool Important)> declarations =
                _context.Sheet.DeclarationsFor(child, child.Ancestors().ToArray());

            PresentationStyle childPaint = PresentationStyle.From(
                child, paint, declarations, _context.Viewport, _context.Warn);
            SvgTextStyle childStyle = SvgTextStyle.From(child, style, declarations, _context.Warn);

            WalkElement(child, childPaint, childStyle);
            Flush();
        }

        /// <summary>
        /// Adds text to the piece in hand, handling white space the way SVG says.
        ///
        /// Collapsing is a state machine over the **whole** element and not per text node, because that is what it
        /// is: `<text> a <tspan>b</tspan> c </text>` is one string with one leading space and one trailing space,
        /// and a reader that trimmed each node separately would glue the words together.
        /// </summary>
        private void Append(string value, PresentationStyle paint, SvgTextStyle style)
        {
            foreach (char c in value)
            {
                if (style.PreserveSpace)
                {
                    Open(paint, style).Text.Append(c);
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

                    Open(paint, style).Text.Append(' ');
                    _lastWasSpace = true;
                    continue;
                }

                Open(paint, style).Text.Append(c);
                _lastWasSpace = false;
                _atStart = false;
            }
        }

        private TextChunk Open(PresentationStyle paint, SvgTextStyle style)
            => _current ??= new TextChunk { Style = style, Paint = paint, X = _penX, Y = _penY };

        private void Flush()
        {
            if (_current is null)
            {
                return;
            }

            TextChunk chunk = _current;
            _current = null;
            if (chunk.Text.Length == 0)
            {
                return;
            }

            chunk.Width = Natural(chunk);
            if (_chunks.Count > 0 && chunk.Continues(_chunks[^1]))
            {
                _chunks[^1].Text.Append(chunk.Text);
                _chunks[^1].Width = Natural(_chunks[^1]);
            }
            else
            {
                _chunks.Add(chunk);
            }

            // Where the pen is now, which is what an absolute position on the next piece is measured against.
            _penX = _chunks[^1].X + _chunks[^1].Width;
        }

        /// <summary>
        /// One positioning attribute, with `em` and `ex` resolved against the size in force.
        ///
        /// In text those units mean something - the run's own size - so they are resolved here rather than through
        /// the generic length table, which has no text context and would report an assumption this reader is not
        /// making. A list of positions is one per character, and a run is placed as a whole: reported, and the first
        /// is used.
        /// </summary>
        private double? Position(XElement element, string attribute, SvgAxis axis, double fontSize)
        {
            string? value = element.Attribute(attribute)?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            int end = 0;
            while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] != ',')
            {
                end++;
            }

            if (Numbers(value) is { Length: > 1 })
            {
                _context.Warnings.Add(
                    $"{attribute}=\"{value}\" gives a position per character, and the model places a run as a whole");
            }

            string first = value[..end];
            int letters = first.Length;
            while (letters > 0 && char.IsLetter(first[letters - 1]))
            {
                letters--;
            }

            string unit = first[letters..].ToLowerInvariant();
            if (double.TryParse(first[..letters].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double parsed))
            {
                if (unit == "em")
                {
                    return parsed * fontSize;
                }

                if (unit == "ex")
                {
                    return parsed * fontSize / 2.0;
                }
            }

            return _context.Length(first, axis, attribute);
        }

        /// <summary>
        /// How wide the face the file names sets a piece, tracking included.
        ///
        /// The model's own measurer answers, which is the one the layout, the caret and the bounds use - so a gap
        /// measured here is the gap the model will leave. The family, size, tracking and word spacing are the
        /// file's, so what is measured is the file's layout and not this machine's idea of it, and measuring
        /// through the run's own advances is what keeps a letter-spaced piece's width the width the model will
        /// give it.
        /// </summary>
        private static double Natural(TextChunk chunk)
        {
            var run = new TextRun
            {
                Text = chunk.Text.ToString(),
                FontFamily = chunk.Style.FontFamily,
                FontSize = chunk.Style.FontSize,
                Bold = chunk.Style.Bold,
                Italic = chunk.Style.Italic,
                LetterSpacing = chunk.Style.LetterSpacing,
                WordSpacing = chunk.Style.WordSpacing,
            };

            double width = 0;
            foreach (double advance in run.Advances())
            {
                width += advance;
            }

            return width;
        }
    }
}
