using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Text;

/// <summary>One run's place on one line: which characters, and where the pen starts.</summary>
/// <param name="Run">Index into <see cref="TextItem.Runs"/>.</param>
/// <param name="Start">First flattened character of the segment.</param>
/// <param name="Length">How many characters of the run are on this line.</param>
/// <param name="Line">Index into <see cref="TextLayout.Lines"/>.</param>
/// <param name="X">Where the segment starts, measured from the block's own left edge.</param>
/// <param name="Width">How far the pen moves across the segment.</param>
public readonly record struct TextRunBox(int Run, int Start, int Length, int Line, double X, double Width);

/// <summary>
/// One laid-out line of a text block.
///
/// The line has ONE baseline, and every run on it puts its own baseline there: a 10pt word beside a
/// 24pt one shares the line rather than being stepped up or down by the difference in their
/// metrics. The tallest face on the line decides the line box - its ascent above the baseline and
/// its descent below - and the leading <see cref="TextItem.LineSpacing"/> asks for is split evenly
/// above and below, so the glyphs stay centred in it.
///
/// <paramref name="Ascent"/> and <paramref name="Descent"/> are therefore LINE metrics, leading
/// included, not any one face's. <paramref name="Top"/> and <paramref name="Width"/> are both
/// **along the line** - the inline axis: how far the pen has travelled down the block before this
/// line and how far the pen moves along it. <paramref name="Cross"/> is where the line sits
/// **across** the block, which is the y of a horizontal line and the x of a vertical column; a
/// vertical column's own centre line is <c>Cross + Ascent</c>.
/// </summary>
public sealed record TextLine(
    int Start, int Length, double Top, double Ascent, double Descent, double Width, double Cross = 0)
{
    /// <summary>Where the glyphs sit along the line; everything on the line shares it.</summary>
    public double Baseline => Top + Ascent;

    /// <summary>How tall the line box is across the block.</summary>
    public double Height => Ascent + Descent;

    /// <summary>Where the line box ends, which is the next line's top.</summary>
    public double Bottom => Top + Height;

    /// <summary>
    /// The pen's inline position where the line begins, which is the block's own origin on that axis.
    ///
    /// A horizontal line starts at zero - its own left edge - and a vertical column starts at the top of its box.
    /// It is what a caller subtracts to turn a glyph's own inline position into a distance from the block's origin,
    /// which is the frame the model stores in.
    /// </summary>
    public double InlineStart => Top;

    /// <summary>The character after the last one on the line.</summary>
    public int End => Start + Length;

    /// <summary>
    /// Where the pen starts along this line, measured from the line's own inline origin.
    ///
    /// Zero for every line, because a line's own inline origin *is* where its pen starts: a horizontal line begins
    /// at the left edge of its box and a vertical column at the top of its own. What differs between the modes is
    /// which page axis <see cref="Cross"/> is, and that is the caller's own projection.
    /// </summary>
    public double InlineFromCross() => 0.0;
}

/// <summary>
/// One character's place in a laid-out block.
///
/// The coordinates are the **block's own**, so a painter or a writer draws straight from them: <paramref name="X"/>
/// and <paramref name="Y"/> are where the character's pen starts, and <paramref name="Rotation"/> is the quarter
/// turn a rotated glyph in vertical text carries about that point. <paramref name="Inline"/> and
/// <paramref name="Cross"/> are the same position on the two axes the layout works in, which is what lets a test
/// - or a caller reasoning about direction rather than geometry - ask which way the pen moved without undoing the
/// projection.
/// </summary>
/// <param name="Index">Index into the block's flattened text, which is where the run and its own offset come from.</param>
/// <param name="Inline">Distance along the line from where the pen started.</param>
/// <param name="Cross">Distance across the block to the character's baseline.</param>
/// <param name="Advance">How far the pen moves after this character.</param>
/// <param name="X">The character's pen position in the block's own space.</param>
/// <param name="Y">The character's baseline in the block's own space.</param>
/// <param name="Rotation">Radians to turn the glyph about <paramref name="X"/>, <paramref name="Y"/>; zero when upright.</param>
/// <param name="Run">Which run the character belongs to.</param>
/// <param name="PieceStart">The character's offset within its own run's string.</param>
public readonly record struct GlyphBox(
    int Index, double Inline, double Cross, double Advance, double X, double Y, double Rotation,
    int Run, int PieceStart);

/// <summary>One run's contiguous characters on one line, in the order the pen reaches them.</summary>
public readonly record struct GlyphSegment(
    int Run, int PieceStart, int Length, double X, double Y, double Width, double Rotation, IReadOnlyList<int> Indices)
{
    /// <summary>Whether this segment's characters are the ones the file wrote, in the order it wrote them.</summary>
    public bool IsLogicalOrder
    {
        get
        {
            for (int i = 1; i < Indices.Count; i++)
            {
                if (Indices[i] != Indices[i - 1] + 1)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>
/// A text block laid out: every line, every run's place on its line, and where each caret position
/// falls.
///
/// This is the one layout the program uses. Bounds, drawing, the caret, the selection highlight and
/// hit-testing all read it, so they cannot disagree - which they did while the model measured lines
/// one way, the canvas drew them another way and the caret came out of a third.
///
/// **It is mode-aware.** <see cref="Vertical"/> says whether the block runs down the page, <see cref="RightToLeft"/>
/// whether its base direction is right to left, and <see cref="Glyphs"/> gives every character's place in the
/// block's own space with the turn it carries - so the whole of "where does this glyph go" is answered here, once,
/// and the painter and the writer cannot answer it differently.
/// </summary>
public sealed record TextLayout(
    IReadOnlyList<TextLine> Lines,
    IReadOnlyList<TextRunBox> Runs,
    IReadOnlyList<double> CaretX,
    IReadOnlyList<int> CaretLine,
    double Width,
    double Height,
    IReadOnlyList<GlyphBox> Glyphs,
    IReadOnlyList<GlyphSegment> Segments,
    bool Vertical,
    bool RightToLeft,
    double CrossShift = 0)
{
    /// <summary>An empty block: one empty line, no runs, one caret position.</summary>
    public static TextLayout Empty { get; } = new(
        new[] { new TextLine(0, 0, 0, 0, 0, 0) },
        Array.Empty<TextRunBox>(),
        new[] { 0.0 },
        new[] { 0 },
        0,
        0,
        Array.Empty<GlyphBox>(),
        Array.Empty<GlyphSegment>(),
        Vertical: false,
        RightToLeft: false);

    /// <summary>
    /// Whether the runs may be drawn whole, in the order the layout lists them.
    ///
    /// True for an ordinary horizontal left-to-right block, which is nearly every block in every document - and
    /// what keeps that case a single shaped draw per run rather than one draw per character. A vertical block, or
    /// one whose characters the bidirectional algorithm reordered, is drawn from <see cref="Glyphs"/>.
    /// </summary>
    public bool Contiguous => !Vertical && !RightToLeft;

    /// <summary>Where a line box sits in the block's own space, across and along.</summary>
    public Rect2D LineBox(int line)
    {
        TextLine placed = Lines[line];
        return Vertical
            ? new Rect2D(placed.Cross - (placed.Height / 2.0), placed.Top, placed.Height, placed.Width)
            : new Rect2D(0, placed.Cross, Width, placed.Height);
    }

    /// <summary>The line a caret position sits on, clamped into range.</summary>
    public int LineOf(int caret)
        => CaretLine.Count == 0 ? 0 : CaretLine[Math.Clamp(caret, 0, CaretLine.Count - 1)];

    /// <summary>The x of a caret position, clamped into range.</summary>
    public double XOf(int caret)
        => CaretX.Count == 0 ? 0 : CaretX[Math.Clamp(caret, 0, CaretX.Count - 1)];
}

/// <summary>
/// Lays a text block out once, for everyone.
///
/// The rules:
///
/// - Lines break where <see cref="TextWrapping"/> says they break - one implementation of wrapping,
///   shared with export, so the page on screen is the page that is written.
/// - A line's ascent and descent come from the tallest face **on that line**, not from the block's
///   largest font: a block whose first line is 12pt and whose second is 36pt has two differently
///   sized lines, and measuring both as 36pt is what pushed the small line's text up the page.
/// - Leading is split above and below the glyph box, so the baseline moves down by half the leading
///   and the glyphs stay centred in their line.
/// - Every run is placed by where its **baseline** has to be, never by stacking run boxes from the
///   top. Stacking from the top is what made a line of mixed faces stair-step: each run put its own
///   ascent under the same top edge, so the taller face sat lower.
/// </summary>
public static class TextLayoutEngine
{
    /// <summary>Lays out <paramref name="text"/>, in the block's own coordinates.</summary>
    public static TextLayout Compute(TextItem text)
    {
        (string flat, List<double> widths) = TextWrapping.Flatten(text);
        List<TextWrapping.LineRange> ranges = TextWrapping.Lines(text);

        int n = flat.Length;
        bool vertical = text.WritingMode != TextWritingMode.HorizontalTb;
        bool rightToLeft = text.Direction == TextDirection.RightToLeft;

        // The visual order and the turn each character carries under it. One call answers both, so a block cannot
        // have its characters placed by one judgement and turned by another.
        int[] order = BuildVisualOrder(flat, rightToLeft, out bool reordered);
        var rotation = new double[n];
        for (int i = 0; i < n; i++)
        {
            rotation[i] = vertical ? QuarterTurnFor(text, i) : 0.0;
        }

        var ascent = new double[n];
        var descent = new double[n];
        int at = 0;
        foreach (TextRun run in text.Runs)
        {
            double a = TextMeasurement.Ascent(run);
            double d = TextMeasurement.Descent(run);
            for (int i = 0; i < run.Text.Length; i++, at++)
            {
                ascent[at] = a;
                descent[at] = d;
            }
        }

        // An empty line still needs a height, and has no face to take one from.
        double blankAscent = TextMeasurement.EstimatedAscent(text.MaxFontSize);
        double blankDescent = TextMeasurement.EstimatedDescent(text.MaxFontSize);
        double spacing = Math.Max(text.LineSpacing, 1e-6);

        var lineWidths = new double[ranges.Count];
        double widest = 0;
        for (int li = 0; li < ranges.Count; li++)
        {
            double w = 0;
            for (int i = ranges[li].Start; i < ranges[li].Start + ranges[li].Length && i < n; i++)
            {
                w += widths[i];
            }

            lineWidths[li] = w;
            widest = Math.Max(widest, w);
        }

        double blockWidth = text.FrameWidth > 0 ? text.FrameWidth : widest;

        var lines = new List<TextLine>(ranges.Count);
        var caretX = new double[n + 1];
        var caretLine = new int[n + 1];
        double top = 0;

        for (int li = 0; li < ranges.Count; li++)
        {
            TextWrapping.LineRange range = ranges[li];
            int end = Math.Min(range.Start + range.Length, n);

            double lineAscent = blankAscent;
            double lineDescent = blankDescent;
            if (end > range.Start)
            {
                lineAscent = 0;
                lineDescent = 0;
                for (int i = range.Start; i < end; i++)
                {
                    lineAscent = Math.Max(lineAscent, ascent[i]);
                    lineDescent = Math.Max(lineDescent, descent[i]);
                }
            }

            // The leading goes half above the glyph box and half below, so the baseline drops by
            // half of it rather than the whole line growing downwards from the top.
            double glyphExtent = lineAscent + lineDescent;
            double half = (glyphExtent * spacing) - glyphExtent;
            lineAscent += half / 2;
            lineDescent += half / 2;

            // **Where the line sits across the block, and where its pen starts.** A horizontal block stacks its
            // lines down the page and runs each one from its left edge. A vertical column's pen runs **down** the
            // column line the block's origin is on, and the columns sit beside it: `Cross` is that line, which is
            // the pen's own position across the block, so the glyphs of a column share one x and the ascent is not
            // a distance along the page at all.
            double cross = CrossOf(lines.Count, lines, vertical, text.WritingMode);

            lines.Add(new TextLine(range.Start, end - range.Start, top, lineAscent, lineDescent, lineWidths[li], cross));

            // Caret positions in the block's own space: the first is where the pen starts, and each following one
            // is a character's advance further along. A vertical column's pen starts where the line's own `Top`
            // does, which is what makes the caret's inline position the same number the layout reports for the
            // line's own height.
            double stable = Align(text.Alignment, blockWidth, lineWidths[li]);
            double pen = vertical ? top : stable;
            for (int i = range.Start; i <= end && i <= n; i++)
            {
                caretX[i] = pen;
                caretLine[i] = li;
                if (i < end)
                {
                    pen += widths[i];
                }
            }

            top += lineAscent + lineDescent;

            // An explicit break carries the paragraph leading with it, including a trailing one.
            if (end < n && flat[end] == '\n')
            {
                top += text.ParagraphSpacing;
            }
        }

        if (lines.Count == 0)
        {
            return TextLayout.Empty;
        }

        // A vertical block's alignment is along the cross axis and applies to the columns together: centred inside
        // the block's own cross extent, or pushed so its far edge sits on the origin. The extent is what the
        // columns add up to, less one line box - the distance from the first column's pen to the last one's.
        double crossShift = 0;
        if (vertical && text.Alignment != TextAlignment.Left)
        {
            double crossExtent = 0;
            foreach (TextLine line in lines)
            {
                crossExtent += line.Height;
            }

            crossExtent -= lines[0].Height;

            crossShift = text.Alignment == TextAlignment.Center ? -(crossExtent / 2.0) : -crossExtent;

            if (Math.Abs(crossShift) > 1e-12)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    lines[i] = lines[i] with { Cross = lines[i].Cross + crossShift };
                }
            }
        }

        List<GlyphBox> glyphs = PlaceGlyphs(
            text, widths, lines, order, rotation, vertical, ranges, caretX, blockWidth, crossShift);

        return new TextLayout(
            lines,
            RunBoxes(text, lines, caretX, widths, n, glyphs, vertical),
            caretX,
            caretLine,
            vertical ? top : blockWidth,
            vertical ? blockWidth : top,
            glyphs,
            BuildSegments(glyphs, vertical),
            vertical,
            rightToLeft || reordered,
            crossShift);
    }

    /// <summary>
    /// Where the next line sits across the block.
    ///
    /// A horizontal block stacks downwards from the origin. A vertical column stacks sideways under the mode's own
    /// direction: `vertical-rl` puts the next column to the left, `vertical-lr` to the right. SVG's own mapping puts
    /// the first column's centre line at the block's origin under both, which is why the first line is at zero
    /// either way - and why a `vertical-lr` column carries a negative coordinate, since its centre line runs down
    /// from the origin and its box extends to the left of it.
    /// </summary>
    private static double CrossOf(int count, List<TextLine> lines, bool vertical, TextWritingMode mode)
    {
        if (count == 0)
        {
            return 0;
        }

        TextLine previous = lines[^1];
        if (!vertical)
        {
            return previous.Cross + previous.Height;
        }

        double sign = mode == TextWritingMode.VerticalRl ? -1.0 : 1.0;
        return previous.Cross + (sign * previous.Height);
    }

    /// <summary>
    /// The order the pen reaches the block's characters in, and whether that is the order they were written.
    ///
    /// A block with no right-to-left character and a left-to-right base direction is already in visual order, which
    /// is every ordinary block, so the identity is returned without the algorithm running at all. A `\n` is a
    /// paragraph break as far as the ordering is concerned, and each run of characters between two breaks is
    /// ordered on its own - which is also what keeps each line's characters together, so a glyph's own index says
    /// which line it is on.
    ///
    /// **The paragraph level is the block's own direction, never a second reading of the content.** A block that
    /// states right to left runs at level 1 and one that states left to right runs at level 0, which is the answer
    /// UAX #9's P2/P3 gives *unless* a higher-level protocol states one - and the model's
    /// <see cref="TextItem.Direction"/> is that statement. Asking the content again made the answer depend on which
    /// way the model happened to be built: an `ltr` block holding a Hebrew word re-derived level 1 and laid the
    /// whole line out right to left, so a file that said `direction: ltr` was drawn the other way and the geometry
    /// no longer matched the `x` the file gave. A left-to-right block holding a right-to-left word still runs the
    /// algorithm - the island is reordered inside the line - which is what <see cref="Bidi.HasRightToLeft"/> is
    /// asked below.
    /// </summary>
    private static int[] BuildVisualOrder(string flat, bool rightToLeft, out bool reordered)
    {
        int n = flat.Length;
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }

        reordered = false;
        if (!rightToLeft && !Bidi.HasRightToLeft(flat))
        {
            return order;
        }

        // P2/P3 is answered by the block's own direction, which the reader has already derived when the file stated
        // none - so the layout and the reader cannot disagree about the paragraph level.
        int paragraph = rightToLeft ? 1 : 0;
        int start = 0;
        while (start < n)
        {
            int end = flat.IndexOf('\n', start);
            if (end < 0)
            {
                end = n;
            }

            string block = flat[start..end];
            if (block.Length > 0)
            {
                Bidi.BidiResult resolved = Bidi.Order(block, paragraph);
                for (int i = 0; i < block.Length; i++)
                {
                    order[start + i] = start + resolved.Order[i];
                    if (resolved.Order[i] != i)
                    {
                        reordered = true;
                    }
                }
            }

            start = end + 1;
        }

        return order;
    }

    /// <summary>
    /// Where every character of the block is drawn, in the block's own space.
    ///
    /// The pen walk is the same arithmetic the block has always used - a character's advance moves the pen, and
    /// where the pen is after a line is where the next line starts - but the two axes are the block's own. A
    /// vertical block's pen runs down the page, so each glyph's advance is measured down it rather than across, and
    /// its baseline is the column's own line. Each line is walked in the order the bidirectional algorithm gives
    /// it, which is what puts a right-to-left block's first character at its right edge.
    /// </summary>
    private static List<GlyphBox> PlaceGlyphs(
        TextItem text,
        List<double> widths,
        List<TextLine> lines,
        int[] order,
        double[] rotation,
        bool vertical,
        List<TextWrapping.LineRange> ranges,
        double[] caretX,
        double blockWidth,
        double crossShift)
    {
        double total = 0;
        foreach (double width in widths)
        {
            total += width;
        }

        var glyphs = new List<GlyphBox>((int)Math.Max(0, total / 4));
        int[] runOf = RunIndices(text, widths.Count);

        // How far each character's baseline is raised off its line's, in the block's own units. Resolved once from
        // the one member that states it, so the glyph walk below cannot read a run's shift and a second caller's
        // idea of it separately.
        var shifts = new double[widths.Count];
        for (int i = 0; i < shifts.Length; i++)
        {
            shifts[i] = BaselineShiftFor(text.Runs[runOf[i]]);
        }

        for (int li = 0; li < lines.Count; li++)
        {
            TextLine line = lines[li];
            if (line.Length == 0)
            {
                continue;
            }

            // Walk this line's slice of the visual order. The order covers the whole block, and a line's
            // characters are the ones the cut between two newlines holds - the ordering is done per paragraph,
            // so they are contiguous in it.
            int start = ranges[li].Start;
            int end = start + ranges[li].Length;

            // Where the pen starts along the line. Horizontally that is the line's own left edge - the alignment of
            // the block has already moved the text along, which is what `caretX` says. A vertical column starts at
            // the top of its own box and advances down it, so the pen begins at the line's `Top` and the block's
            // alignment has already moved the *column* across.
            double pen = vertical ? line.Top : caretX[start];

            // **Where the last character that took a place of its own was drawn.** A combining mark is drawn at
            // that character - the base it belongs to - and not after it: the mark takes no advance, so by the
            // time the pen reaches it the pen has already moved past the base, and drawing it there is exactly
            // the gap the file does not have. The pen itself is left alone, which the mark's zero advance does.
            double basePen = pen;

            for (int k = 0; k < order.Length; k++)
            {
                int index = order[k];
                if (index < start || index >= end)
                {
                    continue;
                }

                // **The same advance the line was measured with.** `widths` is `TextWrapping.Flatten`'s own output,
                // which already spreads a run's recorded advance over its characters - so a glyph's place on the
                // line is this number and nothing is measured a second time. Measuring again here is the same
                // arithmetic done twice, and it disagrees the moment the two calls see different faces.
                double advance = widths[index];
                bool mark = TextMeasurement.IsCombiningMark(CharAt(text, index));
                double inline = mark ? basePen : pen;
                bool turned = Math.Abs(rotation[index]) > 1e-9;

                // **The shift moves the glyph across its baseline and never moves the pen.** `baseline-shift`
                // raises a run's own baseline off the line's, so the next character starts exactly where it would
                // have and the line box is the unshifted text's - which is SVG's own rule, and what keeps a
                // superscript from pushing the lines after it down the block.
                double shift = shifts[index];

                // **The file's own per-character across offset.** It moves the glyph across its baseline and
                // nothing else - `dy` shifts a character without moving where the next one starts, which is the rule
                // the baseline shift above already follows. A list shorter than the run applies where it reaches and
                // is zero beyond: SVG repeats a short list's last value, and repeating it here would silently move
                // characters the file said nothing about.
                int within = PieceStart(text, index);
                double across = text.Runs[runOf[index]].PositionOffsets is { } offsets && within < offsets.Length
                    ? offsets[within]
                    : 0.0;

                if (vertical)
                {
                    // **The pen is the glyph's own origin and its baseline runs down the column line.** A turned
                    // glyph is drawn about that point with its ascent to the left, so the pen stands where the
                    // file's `x` did and the block's origin is that same point. An upright glyph is centred in the
                    // column instead, so its advance is the line box's own size - which is what makes a column of
                    // upright CJK run at a line's pitch. A column's baseline runs down the page, so a raised glyph
                    // sits to the right of its column line rather than above it: see `ShiftUp`.
                    // A mark takes no step either: in a column an *upright* glyph advances by the line's own pitch,
                    // so a mark that kept that step would push the column down by a whole line for nothing.
                    double step = mark ? 0.0 : turned ? advance : line.Height;
                    glyphs.Add(new GlyphBox(
                        index, inline, line.Cross, step, line.Cross + ShiftUp(vertical: true, shift) + across, inline,
                        rotation[index], runOf[index], PieceStart(text, index)));
                    pen += step;
                }
                else
                {
                    // A horizontal baseline runs right, so a raised glyph sits above the line: -y in this y-down
                    // frame. The pen, the advance and the line box are untouched.
                    glyphs.Add(new GlyphBox(
                        index, inline, line.Cross + line.Ascent, advance, inline,
                        line.Cross + line.Ascent + ShiftUp(vertical: false, shift) + across, 0.0,
                        runOf[index], PieceStart(text, index)));
                    pen += advance;
                }

                if (!mark)
                {
                    basePen = inline;
                }
            }
        }

        return glyphs;
    }

    /// <summary>Which run each flattened character belongs to.</summary>
    private static int[] RunIndices(TextItem text, int length)
    {
        var runOf = new int[length];
        int at = 0;
        for (int r = 0; r < text.Runs.Count; r++)
        {
            for (int i = 0; i < text.Runs[r].Text.Length && at < length; i++, at++)
            {
                runOf[at] = r;
            }
        }

        return runOf;
    }

    /// <summary>A flattened character's offset within its own run's string.</summary>
    private static int PieceStart(TextItem text, int index)
    {
        int at = 0;
        foreach (TextRun run in text.Runs)
        {
            if (index < at + run.Text.Length)
            {
                return index - at;
            }

            at += run.Text.Length;
        }

        return 0;
    }

    /// <summary>
    /// The quarter turn a character carries in vertical text, in radians.
    ///
    /// **Negative, which is a quarter turn clockwise in this model's space.** A `y`-down, `x`-right frame turns a
    /// point clockwise for a positive angle, and a Latin glyph in a vertical column is turned clockwise: its
    /// baseline runs down the page and its ascent points to the **left**. The block's origin is then one ascent to
    /// the right of its column's baseline, which is why the reader adds rather than subtracts an ascent to reach it.
    /// </summary>
    private static double QuarterTurnFor(TextItem text, int index)
    {
        TextRun run = RunAt(text, index);
        return run.FontOrientation switch
        {
            GlyphOrientation.Rotate => -Math.PI / 2.0,
            GlyphOrientation.Upright => 0.0,
            _ => IsUprightScript(CharAt(text, index)) ? 0.0 : -Math.PI / 2.0,
        };
    }

    private static TextRun RunAt(TextItem text, int index)
    {
        int at = 0;
        foreach (TextRun run in text.Runs)
        {
            if (index < at + run.Text.Length)
            {
                return run;
            }

            at += run.Text.Length;
        }

        return text.Runs.Count > 0 ? text.Runs[^1] : new TextRun();
    }

    private static char CharAt(TextItem text, int index)
    {
        int at = 0;
        foreach (TextRun run in text.Runs)
        {
            if (index < at + run.Text.Length)
            {
                return run.Text[index - at];
            }

            at += run.Text.Length;
        }

        return ' ';
    }

    /// <summary>
    /// How far a run's baseline is raised off its line's, in the block's own units.
    ///
    /// <see cref="TextRun.BaselineShift"/> is the run's own em fraction - the unit SVG's <c>baseline-shift</c> is
    /// resolved into on the way in - so the length is that fraction times the run's own size. Nothing else measures
    /// it, so a run whose size changes moves its shift with it, which is what a superscript set in a smaller size
    /// than its base should do.
    /// </summary>
    public static double BaselineShiftFor(TextRun run) => run.BaselineShift * run.FontSize;

    /// <summary>
    /// How far a glyph's origin moves across its baseline when the run's baseline is raised by
    /// <paramref name="shift"/>, as a signed offset on the axis a shift moves it on.
    ///
    /// A shift moves the glyph **away from its own baseline**: for a horizontal run that is straight up the page -
    /// <c>-y</c> in this y-down frame - and for a vertical column, whose baseline runs *down* the page, it is the
    /// page's <c>+x</c>. A turned Latin glyph in a column carries its up axis round with it and an upright one does
    /// not, but both point the same way here: a turned glyph's ascent is to the right of its baseline and an upright
    /// glyph's ascent is to the right of the column line. That is the whole point - the shift is off the glyph's
    /// own baseline, not off the page's.
    /// </summary>
    private static double ShiftUp(bool vertical, double shift) => vertical ? shift : -shift;

    /// <summary>
    /// Whether a script is written upright in vertical text rather than turned on its side.
    ///
    /// This is what <c>glyph-orientation-vertical: auto</c> means: CJK ideographs and kana, and the fullwidth forms
    /// that go with them, keep their own upright form; everything else - Latin, Greek, Cyrillic, digits - is turned
    /// a quarter turn clockwise. The property is read as the file wrote it, and a character this does not name is
    /// turned, which is what CSS Writing Modes says for `mixed`.
    /// </summary>
    private static bool IsUprightScript(char c) => c is
        >= '\u1100' and <= '\u11ff' or
        >= '\u2e80' and <= '\u303f' or
        >= '\u3040' and <= '\u30ff' or
        >= '\u3100' and <= '\u318f' or
        >= '\u31a0' and <= '\u31bf' or
        >= '\u3200' and <= '\u32ff' or
        >= '\u3400' and <= '\u4dbf' or
        >= '\u4e00' and <= '\u9fff' or
        >= '\ua960' and <= '\ua97f' or
        >= '\uac00' and <= '\ud7af' or
        >= '\uf900' and <= '\ufaff' or
        >= '\ufe10' and <= '\ufe1f' or
        >= '\ufe30' and <= '\ufe4f' or
        >= '\uff00' and <= '\uffef' or

        // The supplementary ideographic planes, written as the surrogate range they are: CJK extensions B to F
        // occupy U+20000..U+2FFFF, and a `char` here is one half of a surrogate pair.
        >= '\ud840' and <= '\ud87f';

    /// <summary>
    /// The runs, split into stretches the pen reaches without changing direction or turn.
    ///
    /// This is what a caller iterating for a fast path wants: a horizontal left-to-right block's line is one
    /// stretch per run, exactly the shape the drawing code has always used, while a reordered or vertical block
    /// comes out in the pieces its direction changes at - which is what a per-glyph draw needs and what lets a
    /// writer put every piece on its own `tspan`.
    /// </summary>
    private static List<GlyphSegment> BuildSegments(List<GlyphBox> glyphs, bool vertical)
    {
        var segments = new List<GlyphSegment>();

        foreach (IGrouping<int, GlyphBox> byRun in glyphs.GroupBy(g => g.Run))
        {
            foreach (IGrouping<int, GlyphBox> byPiece in byRun.GroupBy(g => g.PieceStart))
            {
                List<GlyphBox> group = byPiece.OrderBy(g => g.Inline).ToList();
                double width = 0;
                var indices = new List<int>(group.Count);
                foreach (GlyphBox glyph in group)
                {
                    width += glyph.Advance;
                    indices.Add(glyph.Index);
                }

                GlyphBox first = group[0];
                segments.Add(new GlyphSegment(
                    byRun.Key, byPiece.Key, group.Count, first.X, first.Y, width, first.Rotation, indices));
            }
        }

        return segments;
    }

    private static double Align(TextAlignment alignment, double blockWidth, double lineWidth) => alignment switch
    {
        TextAlignment.Center => (blockWidth - lineWidth) / 2,
        TextAlignment.Right => blockWidth - lineWidth,
        _ => 0,
    };

    /// <summary>
    /// Where a run's own box starts vertically, so that its baseline lands on the line's.
    ///
    /// This is the whole of "colinear baselines": a run is placed by its baseline, never by putting
    /// its top against the line's top. Two faces of different sizes share a bottom edge only if each
    /// is offset by its own ascent, which is what this returns.
    ///
    /// **A run's own <c>baseline-shift</c> is part of where its baseline is.** A superscript is a run whose baseline
    /// is raised off the line's, so the top of its box is one ascent above *that* baseline and not above the
    /// line's - which is what the canvas draws from and what the offset below answers. The line itself is
    /// unmoved: a shifted run never grows the line box.
    /// </summary>
    public static double RunTop(TextRun run, TextLine line)
        => line.Baseline - BaselineShiftFor(run) - TextMeasurement.Ascent(run);

    /// <summary>
    /// Each run's segments, one per line it appears on.
    ///
    /// A run is drawn from a starting x on a line's baseline, so a run that is broken across lines -
    /// by a newline in its own text, or by wrapping inside a frame - is several segments rather than
    /// one box drawn twice. A reordered or vertical block's characters are taken from the glyph list instead, so
    /// the box is still one box per run per line and the glyphs say where the characters inside it go.
    /// </summary>
    private static List<TextRunBox> RunBoxes(
        TextItem text, List<TextLine> lines, double[] caretX, List<double> widths, int n,
        IReadOnlyList<GlyphBox> glyphs, bool vertical)
    {
        var boxes = new List<TextRunBox>();
        int runStart = 0;

        for (int ri = 0; ri < text.Runs.Count; ri++)
        {
            int runEnd = runStart + text.Runs[ri].Text.Length;

            for (int li = 0; li < lines.Count; li++)
            {
                TextLine line = lines[li];
                int start = Math.Max(line.Start, runStart);
                int end = Math.Min(line.End, runEnd);
                if (end <= start)
                {
                    continue;
                }

                // Where the run's own characters begin along the line, measured from the line's own start - which is
                // the frame `TextRunBox.X` has always been in, and the frame the alignment shift is already applied
                // to. A reordered line's first character in logical order is not necessarily its leftmost, so this
                // is the leftmost one the pen reaches, which is the point a writer anchors the piece by.
                double x = double.MaxValue;
                double w = 0;
                foreach (GlyphBox glyph in glyphs)
                {
                    if (glyph.Run == ri && glyph.Index >= start && glyph.Index < end)
                    {
                        x = Math.Min(x, glyph.Inline);
                    }
                }

                if (x == double.MaxValue)
                {
                    x = vertical ? 0.0 : caretX[start];
                }

                for (int i = start; i < end && i < n; i++)
                {
                    w += widths[i];
                }

                boxes.Add(new TextRunBox(ri, start, end - start, li, x, w));
            }

            runStart = runEnd;
        }

        return boxes;
    }
}
