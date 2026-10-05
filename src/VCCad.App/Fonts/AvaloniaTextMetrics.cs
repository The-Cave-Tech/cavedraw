using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using VCCad.Core.Model;
using VCCad.Core.Text;

namespace VCCad.App.Fonts;

/// <summary>
/// Real font measurement, from Avalonia's text shaper.
///
/// This is the only place in the program that asks how wide text is. The document model
/// reaches it through <see cref="TextMeasurement.Current"/> and the canvas calls it
/// directly, so a text block's reported bounds and the box drawn round it come from the
/// same measurement and cannot drift apart.
///
/// Advance widths are cumulative prefix widths. Measuring "AV" as a whole lets the shaper
/// apply kerning between the two letters; measuring "A" and "V" separately does not, and
/// the difference is exactly the error that made the edit box miss the text inside it.
/// </summary>
public sealed class AvaloniaTextMetrics : ITextMetrics
{
    private readonly Dictionary<string, double[]> _advanceCache = new();
    private readonly Dictionary<string, (double Ascent, double Descent)> _verticalCache = new();

    public IReadOnlyList<double> Advances(TextRun run)
    {
        if (run.Text.Length == 0)
        {
            return Array.Empty<double>();
        }

        string key = Key(run);
        if (_advanceCache.TryGetValue(key, out double[]? cached))
        {
            return cached;
        }

        int n = run.Text.Length;
        var widths = new double[n];
        FormattedText? probe = null;

        try
        {
            double previous = 0;
            for (int i = 1; i <= n; i++)
            {
                probe = Build(run, run.Text[..i]);
                double width = probe.Width;
                widths[i - 1] = Math.Max(0, width - previous);
                previous = width;
            }
        }
        catch (Exception)
        {
            // A face that will not shape falls back to the estimate rather than throwing
            // out of a paint or a bounds call.
            for (int i = 0; i < n; i++)
            {
                widths[i] = VCCad.Core.Text.TextMeasurement.EstimateFor(run, run.Text[i]);
            }
        }

        // Keep the cache bounded as text is typed; a miss just re-measures.
        if (_advanceCache.Count > 8192)
        {
            _advanceCache.Clear();
        }

        _advanceCache[key] = widths;
        return widths;
    }

    public double Ascent(TextRun run) => Vertical(run).Ascent;

    public double Descent(TextRun run) => Vertical(run).Descent;

    /// <summary>
    /// Ascent and descent for the run's face, taken from the shaped text rather than a
    /// constant multiple of the font size.
    /// </summary>
    private (double Ascent, double Descent) Vertical(TextRun run)
    {
        string key = Key(run);
        if (_verticalCache.TryGetValue(key, out (double Ascent, double Descent) cached))
        {
            return cached;
        }

        // The same constants TextMeasurement uses, asked for rather than repeated: a
        // second copy of the guessing is how the numbers drift apart.
        double ascent = VCCad.Core.Text.TextMeasurement.EstimatedAscent(run);
        double descent = VCCad.Core.Text.TextMeasurement.EstimatedDescent(run);

        try
        {
            FormattedText probe = Build(run, "Hxg");
            double height = probe.Height;
            double baseline = probe.Baseline;

            if (height > 0 && baseline > 0 && baseline < height)
            {
                ascent = baseline;
                descent = height - baseline;
            }
        }
        catch (Exception)
        {
            // Keep the fallback.
        }

        _verticalCache[key] = (ascent, descent);
        return (ascent, descent);
    }

    private static FormattedText Build(TextRun run, string text)
        => new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TypefaceFor(run),
            run.FontSize,
            Brushes.Black);

    private static Typeface TypefaceFor(TextRun run)
        => new(
            // **Through the shared resolver, not the constructor.** This built a FontFamily straight from the
            // resolve step, so a run with no family threw here as it did in the paint pass - and this is reached
            // while measuring, which is on the way to drawing (issue #213).
            FontFamilyResolver.For(run),
            run.Italic ? FontStyle.Italic : FontStyle.Normal,
            run.Bold ? FontWeight.Bold : FontWeight.Normal);

    private static string Key(TextRun run)
        => string.Join(
            '\u001f',
            run.FontFamily,
            run.SourceFont ?? string.Empty,
            run.FontSize.ToString("R", CultureInfo.InvariantCulture),
            run.Bold ? "b" : "-",
            run.Italic ? "i" : "-",
            run.EmbeddedFont?.FamilyName ?? string.Empty,
            run.Text);
}
