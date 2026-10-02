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

        /// <summary>
        /// Where the file puts this piece's first glyph, in the file's own user units, on the two axes the block's
        /// writing mode gives it.
        ///
        /// <see cref="Inline"/> is **along** the line - the page's x under a horizontal mode and its y under a
        /// vertical one - and is where the pen is when the piece starts. <see cref="Cross"/> is **across** the
        /// block: the page's y for a horizontal line, and the x of a vertical column's baseline. Naming them by the
        /// axis they are on rather than by the page's letters is what lets a vertical file's `y` and a horizontal
        /// file's `x` be the same thing to everything downstream.
        /// </summary>
        public double Inline { get; set; }

        /// <summary>The cross-axis position of this piece's baseline. See <see cref="Inline"/>.</summary>
        public double Cross { get; set; }

        /// <summary>
        /// **How far each of this piece's characters strays from <see cref="Cross"/>**, in the file's own units -
        /// SVG's `dy` for a horizontal block and `dx` for a vertical one, whose values are per character.
        ///
        /// `Cross` places the piece; this is what each character does relative to that. Null when the file stated a
        /// single position rather than a list, which is the common case and the one that needs no member.
        /// </summary>
        public double[]? PositionOffsets { get; set; }

        /// <summary>
        /// **How far each of this piece's characters strays from <see cref="Inline"/>, in the file's own units** -
        /// SVG's `x`/`dx` list for a horizontal block and its `y`/`dy` list for a vertical one.
        ///
        /// The along axis is the other half of <see cref="PositionOffsets"/>: a run starts where the file put it, and
        /// these are the places the file's own list gives its characters, rebased on that start. Null when the file
        /// stated a single position rather than a list.
        /// </summary>
        public double[]? PositionAlong { get; set; }

        /// <summary>How far the pen moves along the line for this piece, through the model's own measurer.</summary>
        public double Advance { get; set; }


        /// <summary>
        /// Whether the pen runs back along the line for this piece - a right-to-left run of a horizontal block.
        ///
        /// **Vertical text is never backward.** SVG's own rule for a vertical writing mode is that `y`/`dy` run
        /// along the column and `x`/`dx` across it, so a column's pen advances down the page whatever the base
        /// direction says: the direction changes which way the *columns* stack, not which way a column is read.
        /// Subtracting the advance from a vertical piece's `y` moved the block up the page by one piece, which is
        /// the same defect on the other axis.
        /// </summary>
        public bool Backward { get; init; }

        /// <summary>
        /// Whether this piece carries on from the one before it rather than starting somewhere of its own.
        ///
        /// Same face, same paint, on the same line, and starting exactly where that piece ended - which is what
        /// makes two pieces either side of a `<tspan>` one run rather than two halves of one. The style comparison
        /// is the whole of what a run states, so a `tspan` that changes the tracking, the width, the variant or the
        /// glyph orientation is a different run and not a continuation of the one before it.
        ///
        /// **Which way the pen runs depends on the direction, and only horizontally.** A right-to-left line advances
        /// backwards along its own inline axis, so the next piece starts *before* the end of this one by its advance.
        /// A vertical column advances down the page under both directions - the direction changes which way the
        /// *columns* stack, not which way the pen runs down a column.
        /// </summary>
        public bool Continues(TextChunk previous)
        {
            bool backward = previous.Backward && previous.Style.WritingMode == TextWritingMode.HorizontalTb;
            double expected = backward
                ? previous.Inline - previous.Advance
                : previous.Inline + previous.Advance;

            return Equals(previous.Style, Style) &&
                   Equals(previous.Paint.Fill, Paint.Fill) &&
                   previous.Paint.FillGradientId == Paint.FillGradientId &&
                   Math.Abs(Cross - previous.Cross) < 1e-9 &&
                   Math.Abs(Inline - expected) < 1e-9 &&

                   // **A piece that states its own per-character positions is never merged.** A list belongs to the
                   // run it came in with, and appending two pieces keeps only the first one's - so a list would be
                   // silently dropped or silently applied to characters the file said nothing about.
                   PositionOffsets is null && previous.PositionOffsets is null &&
                   PositionAlong is null && previous.PositionAlong is null;
        }
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
                //
                // **A vertical block's origin is where the file's pen is**, which is SVG's own `x` under a vertical
                // writing mode. The pen is the corner of the first glyph's box and the baseline is one ascent
                // *after* it, because a turned glyph's ascent runs in +x - so the writer's `x` is the origin itself
                // and needs no correction, the same way a horizontal block's origin needs none against `y`.
                bool vertical = chunk.Style.WritingMode != TextWritingMode.HorizontalTb;

                var item = new TextItem
                {
                    Color = chunk.Paint.Fill.Color,
                    LineSpacing = chunk.Style.LineSpacing,
                    Alignment = chunk.Style.Anchor,
                    WritingMode = chunk.Style.WritingMode,
                    Direction = chunk.Style.Direction,

                    // The piece's own position, corrected below: the model's origin is where the pen *started*, and
                    // this is where the file put the first piece's first glyph.
                    Origin = vertical
                        ? new Point2D(chunk.Cross, chunk.Inline)
                        : new Point2D(
                            chunk.Inline, chunk.Cross - (TextMeasurement.TypicalAscentEm * chunk.Style.FontSize)),
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

                // The orientation the file asked for, which only a vertical column acts on - a horizontal run is
                // upright whatever this says, so a file that states `0` on horizontal text is not re-drawn.
                FontOrientation = chunk.Style.Orientation,

                // The baseline the piece hangs from, as the fraction of an em the model keeps. It is a **run**
                // member and not a reason to start a block: SVG's own rule is that a shifted run does not grow the
                // line box, so "a superscript beside its base" is one line with two baselines, which is exactly the
                // shape the model states with a run and which laying it out as a second block would destroy.
                BaselineShift = chunk.Style.BaselineShift,

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

                // The colour of a run that states one of its own, written only when it differs from the block's.
                // The block's colour is the first piece's, so a one-colour block grows no member it does not need
                // while a `tspan` that changes the fill - which no longer starts a block - carries its colour here,
                // and `ColourOf` answers with the file's paint for every run either way.
                Color = chunk.Paint.Fill.Color == current.Item.Color ? null : chunk.Paint.Fill.Color,
            };

            // **The ascent is measured on the axis the block's baselines run on.** A horizontal block's baseline is
            // a `y` and the block's origin sits one ascent above it, so each run records the fraction of *its own*
            // em that places its baseline back on the line - which is what lets a 10pt `tspan` share a baseline
            // with a 20pt one. A vertical column's baseline is an `x` that the pen already stands on, so there is
            // nothing to record and the renderer works the ascent out from the face.
            run.PlacedAscentEm = chunk.Style.WritingMode == TextWritingMode.HorizontalTb
                ? (chunk.Cross - current.Item.Origin.Y) / chunk.Style.FontSize
                : 0.0;

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
    /// a change the block cannot hold run by run, and `Place` resolves it once for the block. A **baseline shift**
    /// is not a reason either, for the same shape of reason: the run holds it and the line box does not grow, so a
    /// superscript and its base are one line of one block (#128).
    /// </summary>
    private static bool Holds(TextBlock block, TextChunk chunk)
        => Math.Abs(chunk.Cross - block.First.Cross) < 1e-9 &&
           block.Item.WritingMode == chunk.Style.WritingMode &&
           block.Item.Direction == chunk.Style.Direction &&
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
            run.PositionOffsets = chunk.PositionOffsets;
            run.InlineOffsets = chunk.PositionAlong;

            if (i > 0)
            {
                TextChunk previous = block.Chunks[i - 1];
                double gap = chunk.Inline - (previous.Inline + previous.Advance);
                if (Math.Abs(gap) > 1e-9)
                {
                    block.Item.Runs[i - 1].AdvanceWidth = previous.Advance + gap;
                    block.Item.Runs[i - 1].GapAfter = gap;

                    if (previous.Advance + gap <= 0)
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
                run.AdvanceWidth ??= chunk.Advance;
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

        // **The anchor is where the file put the *end* or the *middle* of the string, and the model's origin is the
        // block's own corner** - so the block is moved back by the extent its own layout gives it, along the page's
        // x. Measured through the model, because that is the extent the person will see and the extent the caret
        // and the bounds use; a number taken from anywhere else would put the anchored text beside the anchor
        // rather than on it. A vertical column's extent is its columns' own width, which is the same member the
        // layout reports as its width under a vertical mode.
        //
        // **A right-to-left line runs back from its anchor**, so the same shift moves the origin the other way: the
        // file's `x` is the pen's *end* under `rtl` - the block's right edge for `start` - and the model's origin is
        // its left one. Which is why the sign is the direction's and not a constant.
        //
        // **The block's origin is already correct without a correction pass.** A right-to-left piece's own start is
        // the position `TextWalker.Flush` takes the advance off to reach, so `Origin.X` is the pen's start before
        // this runs; the earlier version of this reader added a second, layout-derived offset here, which moved the
        // block by one character's travel on the first read and by another one on every read after it.
        if (block.Item.Alignment != TextAlignment.Left)
        {
            double extent = TextLayoutEngine.Compute(block.Item).Width;
            double shift = block.Item.Alignment == TextAlignment.Center ? extent / 2.0 : extent;

            bool backward = block.Item.Direction == TextDirection.RightToLeft &&
                            block.Item.WritingMode == TextWritingMode.HorizontalTb;

            block.Item.Origin = new Point2D(
                backward ? block.Item.Origin.X + shift : block.Item.Origin.X - shift,
                block.Item.Origin.Y);
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

        private double _penInline;
        private double _penCross;

        /// <summary>
        /// **The per-character across offsets the current chunk stated, waiting for the run that carries them.**
        ///
        /// Held here rather than accumulated with the pen because it belongs to **one run**, not to the block: the
        /// pen's cross is the block's base position and keeps adding, while this is how each character deviates from
        /// it. It is cleared when a run consumes it, so a later chunk that states no list of its own does not inherit
        /// an earlier one's - which a single-`tspan` test would never catch.
        /// </summary>
        private double[]? _pendingOffsets;

        /// <summary>
        /// **The per-character along-line offsets the current chunk stated**, the across list's sibling. The file's
        /// `x`/`y` list is absolute and its `dx`/`dy` list is a running shift, so both arrive here already folded into
        /// one list of positions rebased on the piece's own start. Cleared when a run consumes it, for the same
        /// reason as the across list: a later piece that states nothing must not inherit it.
        /// </summary>
        private double[]? _pendingAlong;
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
                    last.Advance = Natural(last);
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
            // **Which of the file's two axes each of the four attributes moves depends on the writing mode.** SVG's
            // own rule: the `x`/`dx` pair is *along the line* and the `y`/`dy` pair is *across* it, so under a
            // vertical mode the pen advances down the page and the lines step sideways - which is exactly the axis
            // swap this reader does, and not a conversion of its own.
            bool vertical = style.WritingMode != TextWritingMode.HorizontalTb;
            double? inline = Position(element, vertical ? "y" : "x", vertical ? SvgAxis.Y : SvgAxis.X, style.FontSize);
            double? dinline = Position(element, vertical ? "dy" : "dx", vertical ? SvgAxis.Y : SvgAxis.X, style.FontSize);
            double? cross = Position(element, vertical ? "x" : "y", vertical ? SvgAxis.X : SvgAxis.Y, style.FontSize);

            // **The across attribute is read as a whole list or as one number, never as both.** Its entries are how
            // far each character strays from the piece's own cross, and the first entry is the first character's own
            // offset - so the single number is the *same* fact and must not be added to the pen as well. Doing both
            // moved the block down by the first value and then moved its first character down by it again: a list
            // beginning at `0` hides that, and a list beginning at anything else shows it as a run 5 units too low.
            //
            // A one-entry list is not a list. `PositionList` answers null for it, so it falls through to the single
            // read above and places the piece, leaving nothing per-character behind.
            double[]? dcrossList = PositionList(element, vertical ? "dx" : "dy", vertical ? SvgAxis.X : SvgAxis.Y, style.FontSize);
            double? dcross = dcrossList is null
                ? Position(element, vertical ? "dx" : "dy", vertical ? SvgAxis.X : SvgAxis.Y, style.FontSize)
                : null;

            if (dcrossList is not null)
            {
                _pendingOffsets = dcrossList;
            }

            // **The along pair is read as a list too, and SVG's two forms are folded into one.** `x`/`y` states where
            // each character is and `dx`/`dy` moves the pen from where the last one left it, so a file that says both
            // means the sum; the list is then rebased on the first character's place, which is where the piece
            // starts. The single values above still place the piece - and for a list they are the same fact, so
            // nothing is counted twice here: this list is *positions*, and the pen is the first of them.
            //
            // **A right-to-left line is declined and named.** Its places descend in the file's own order while the
            // pen walks the line in visual order, so applying them as they stand puts the characters in the right
            // places and gives them *negative* advances - a run whose box, caret and far edge are all wrong, which is
            // a fix that looks right glyph by glyph. Settling that frame is the work; until then the first value
            // places the run, as it did before the list was kept at all, and the file is told rather than drawn
            // mirrored.
            if (AlongOffsets(element, vertical, style.FontSize) is { Length: > 0 } along)
            {
                if (style.Direction == TextDirection.RightToLeft &&
                    style.WritingMode == TextWritingMode.HorizontalTb)
                {
                    _context.Warnings.Add(
                        "a right-to-left line states a position per character, and the model's places run along "
                        + "such a line the other way: the first value places the run and the list is not honoured");
                }
                else
                {
                    _pendingAlong = along;
                }
            }

            if (inline is not null)
            {
                _penInline = inline.Value;
            }

            if (dinline is not null)
            {
                _penInline += dinline.Value;
            }

            if (cross is not null)
            {
                _penCross = cross.Value;
            }

            if (dcross is not null)
            {
                _penCross += dcross.Value;
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
        {
            if (_current is null)
            {
                _current = new TextChunk
                {
                    Style = style,
                    Paint = paint,

                    // **A right-to-left piece's `x` is where its pen *ends*.** The pen runs back along the line, so
                    // the position the element states is the far edge of the piece it holds, and the piece's own
                    // start - what the model keeps and what the next piece continues from - is its advance short of
                    // that. Taking it back off at the merge is what lets such a line come back as one run instead of
                    // one run per character. A vertical column's pen runs *down* the page under either direction, so
                    // its piece keeps the `y` the file gave it.
                    Backward = style.Direction == TextDirection.RightToLeft &&
                               style.WritingMode == TextWritingMode.HorizontalTb,
                    Inline = _penInline,
                    Cross = _penCross,

                    // Consumed here: the list belongs to this piece, so the next one must not inherit it. A chunk
                    // that states no list of its own gets null, which is the common case.
                    PositionOffsets = _pendingOffsets,
                    PositionAlong = _pendingAlong,
                };

                _pendingOffsets = null;
                _pendingAlong = null;
            }

            return _current;
        }

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

            chunk.Advance = Natural(chunk);

            // **Merged in the frame the model stores in.** A right-to-left piece's stated `x` is the far edge of
            // the piece it holds, so it is taken back to the pen's own position first - which is where the next
            // piece continues from and what the model's origin is measured against.
            if (chunk.Backward)
            {
                chunk.Inline -= chunk.Advance;
            }

            if (_chunks.Count > 0 && chunk.Continues(_chunks[^1]))
            {
                _chunks[^1].Text.Append(chunk.Text);
                _chunks[^1].Advance = Natural(_chunks[^1]);
            }
            else
            {
                _chunks.Add(chunk);
            }

            // Where the pen is for the next piece. It is the face's own width that moves it, not a recorded advance:
            // the room a recorded advance adds is the *gap* to the next piece, which `Place` stores as the run's
            // advance and its `GapAfter`.
            _penInline = _chunks[^1].Inline + _chunks[^1].Advance;
        }

        /// <summary>
        /// **The whole list a positioning attribute states, one entry per character** - where every other attribute
        /// is answered by a single number because the model places a run as a whole.
        ///
        /// Both axes have a list now: the **across** pair (`y`/`dy`, or `x`/`dx` under a vertical mode) places each
        /// character off the baseline, and the **along** pair (`x`/`dx`, or `y`/`dy` vertically) places each
        /// character along the line. Each entry resolves through the same units a single value does, so `1em` in a
        /// list means what `1em` means on its own.
        ///
        /// A single entry is not a list: it is the number that places the piece, which the caller has already read,
        /// so null comes back and nothing per-character is recorded.
        /// </summary>
        private double[]? PositionList(XElement element, string attribute, SvgAxis axis, double fontSize)
        {
            string? value = element.Attribute(attribute)?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string[] tokens = value.Split(
                new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                return null;
            }

            var numbers = new double[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (ResolveLength(tokens[i], axis, attribute, fontSize) is not { } resolved)
                {
                    // **A list this reader cannot resolve is reported, not half-applied.** Taking the entries it
                    // understood would place some characters where the file asked and leave the rest where the face
                    // put them, which draws a string the file does not contain.
                    _context.Warnings.Add(
                        $"{attribute}=\"{value}\" has a length this reader cannot resolve, so its positions are " +
                        "not honoured");
                    return null;
                }

                numbers[i] = resolved;
            }

            return numbers;
        }

        /// <summary>
        /// The file's per-character along-line places, as offsets from where the piece starts.
        ///
        /// `x`/`y` states where each character is and `dx`/`dy` moves the pen from where the last one left it, so the
        /// two are summed per character and the list is rebased on its first entry - which is the frame the model
        /// has, because a run starts where the file put it. A delta list is therefore accumulated, which is SVG's own
        /// rule for `dx`/`dy`: each entry is a step, not a place.
        /// </summary>
        private double[]? AlongOffsets(XElement element, bool vertical, double fontSize)
        {
            SvgAxis axis = vertical ? SvgAxis.Y : SvgAxis.X;
            double[]? absolute = PositionList(element, vertical ? "y" : "x", axis, fontSize);
            double[]? delta = PositionList(element, vertical ? "dy" : "dx", axis, fontSize);
            if (absolute is null && delta is null)
            {
                return null;
            }

            int n = Math.Max(absolute?.Length ?? 0, delta?.Length ?? 0);
            var offsets = new double[n];
            double running = 0;
            double first = 0;
            for (int i = 0; i < n; i++)
            {
                if (delta is not null && i < delta.Length)
                {
                    running += delta[i];
                }

                double place = (absolute is not null && i < absolute.Length ? absolute[i] : 0.0) + running;
                if (i == 0)
                {
                    first = place;
                }

                offsets[i] = place - first;
            }

            return offsets;
        }

        /// <summary>
        /// One positioning attribute, with `em` and `ex` resolved against the size in force.
        ///
        /// In text those units mean something - the run's own size - so they are resolved here rather than through
        /// the generic length table, which has no text context and would report an assumption this reader is not
        /// making. A list of positions is read as a whole list by <see cref="PositionList"/> - on both axes now, so
        /// this reads the value that places the piece and nothing is reported about the rest: the first entry and the
        /// single number are the same fact, and the entries are kept per character.
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

            string first = value[..end];
            return ResolveLength(first, axis, attribute, fontSize);
        }

        /// <summary>
        /// One length of a positioning attribute, resolved the way a single value is: the text units first, because
        /// in text `em` and `ex` mean the run's own size, and the generic length table otherwise. Null is the generic
        /// table declining the value, which a caller reports rather than guessing at.
        /// </summary>
        private double? ResolveLength(string token, SvgAxis axis, string attribute, double fontSize)
        {
            int letters = token.Length;
            while (letters > 0 && char.IsLetter(token[letters - 1]))
            {
                letters--;
            }

            string unit = token[letters..].ToLowerInvariant();
            if (double.TryParse(token[..letters].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
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

            return _context.Length(token, axis, attribute);
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
