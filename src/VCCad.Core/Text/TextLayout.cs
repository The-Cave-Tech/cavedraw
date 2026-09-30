using VCCad.Core.Model;

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
/// included, not any one face's.
/// </summary>
public sealed record TextLine(int Start, int Length, double Top, double Ascent, double Descent, double Width)
{
    /// <summary>Where the glyphs sit; everything on the line shares it.</summary>
    public double Baseline => Top + Ascent;

    /// <summary>How tall the line box is.</summary>
    public double Height => Ascent + Descent;

    /// <summary>Where the line box ends, which is the next line's top.</summary>
    public double Bottom => Top + Height;

    /// <summary>The character after the last one on the line.</summary>
    public int End => Start + Length;
}

/// <summary>
/// A text block laid out: every line, every run's place on its line, and where each caret position
/// falls.
///
/// This is the one layout the program uses. Bounds, drawing, the caret, the selection highlight and
/// hit-testing all read it, so they cannot disagree - which they did while the model measured lines
/// one way, the canvas drew them another way and the caret came out of a third.
/// </summary>
public sealed record TextLayout(
    IReadOnlyList<TextLine> Lines,
    IReadOnlyList<TextRunBox> Runs,
    IReadOnlyList<double> CaretX,
    IReadOnlyList<int> CaretLine,
    double Width,
    double Height)
{
    /// <summary>An empty block: one empty line, no runs, one caret position.</summary>
    public static TextLayout Empty { get; } = new(
        new[] { new TextLine(0, 0, 0, 0, 0, 0) },
        Array.Empty<TextRunBox>(),
        new[] { 0.0 },
        new[] { 0 },
        0,
        0);

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
            double glyphs = lineAscent + lineDescent;
            double half = (glyphs * spacing) - glyphs;
            lineAscent += half / 2;
            lineDescent += half / 2;

            lines.Add(new TextLine(range.Start, end - range.Start, top, lineAscent, lineDescent, lineWidths[li]));

            // Caret positions across the line, alignment included. The caret after the last
            // character belongs to this line; the next line claims the one after that.
            double x = Align(text.Alignment, blockWidth, lineWidths[li]);
            for (int i = range.Start; i <= end && i <= n; i++)
            {
                caretX[i] = x;
                caretLine[i] = li;
                if (i < end)
                {
                    x += widths[i];
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

        return new TextLayout(lines, RunBoxes(text, lines, caretX, widths, n), caretX, caretLine, blockWidth, top);
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
    /// </summary>
    public static double RunTop(TextRun run, TextLine line) => line.Baseline - TextMeasurement.Ascent(run);

    /// <summary>
    /// Each run's segments, one per line it appears on.
    ///
    /// A run is drawn from a starting x on a line's baseline, so a run that is broken across lines -
    /// by a newline in its own text, or by wrapping inside a frame - is several segments rather than
    /// one box drawn twice.
    /// </summary>
    private static List<TextRunBox> RunBoxes(
        TextItem text, List<TextLine> lines, double[] caretX, List<double> widths, int n)
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

                double w = 0;
                for (int i = start; i < end && i < n; i++)
                {
                    w += widths[i];
                }

                boxes.Add(new TextRunBox(ri, start, end - start, li, caretX[start], w));
            }

            runStart = runEnd;
        }

        return boxes;
    }
}
