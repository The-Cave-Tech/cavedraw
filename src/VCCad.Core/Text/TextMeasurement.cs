using VCCad.Core.Model;

namespace VCCad.Core.Text;

/// <summary>
/// Real font measurement, supplied by whichever host is running.
///
/// The document model has to know how wide text is: bounds, wrapping, selection and
/// hit-testing all depend on it. But <c>VCCad.Core</c> must not reference a rendering
/// toolkit — the dependency rule is Geometry &lt;- Core &lt;- {Pdf, Api, App}, and the model
/// has to work headless.
///
/// So measurement is an interface the host installs at startup. The App installs an
/// Avalonia-backed implementation that asks the text shaper; a headless test can install
/// a fixed one. There is deliberately **one** measurement path in the running program:
/// the canvas and the model ask the same object, so the box the person sees and the box
/// the model reports cannot disagree.
/// </summary>
public interface ITextMetrics
{
    /// <summary>
    /// Advance widths, one per character of the run, in document units.
    ///
    /// These come from the shaper, so kerning and ligatures are included: the width of
    /// "AV" is not the width of "A" plus the width of "V".
    /// </summary>
    IReadOnlyList<double> Advances(TextRun run);

    /// <summary>Ascent above the baseline, in document units, for the run's face.</summary>
    double Ascent(TextRun run);

    /// <summary>Descent below the baseline, in document units, for the run's face.</summary>
    double Descent(TextRun run);
}

/// <summary>
/// The measurement source in force, plus the estimate used when no host has installed
/// one.
///
/// The estimate exists so the model is never unusable — a headless deserialise or a unit
/// test with no renderer still gets an answer. It is a fallback, not a policy: in the
/// running application <see cref="Current"/> is always set, so real metrics are used.
/// </summary>
public static class TextMeasurement
{
    private static ITextMetrics? _current;

    /// <summary>True when a real measurer is installed, rather than the fallback.</summary>
    public static bool IsReal => _current is not null;

    /// <summary>The installed measurer, or null when only the estimate is available.</summary>
    public static ITextMetrics? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>Per-character advances, real when available and estimated otherwise.</summary>
    public static IReadOnlyList<double> Advances(TextRun run)
    {
        if (_current is { } metrics && run.Text.Length > 0)
        {
            return metrics.Advances(run);
        }

        int n = run.Text.Length;
        var estimate = new double[n];
        for (int i = 0; i < n; i++)
        {
            estimate[i] = run.FontSize * 0.6;
        }

        return estimate;
    }

    /// <summary>
    /// The advance of a single character, which is what a caret position needs.
    /// </summary>
    public static double AdvanceOf(TextRun run, int index)
    {
        IReadOnlyList<double> advances = Advances(run);
        return index >= 0 && index < advances.Count ? advances[index] : run.FontSize * 0.6;
    }

    /// <summary>Ascent in document units, real when available.</summary>
    public static double Ascent(TextRun run)
        => _current?.Ascent(run) ?? run.FontSize * 0.8;

    /// <summary>Descent in document units, real when available.</summary>
    public static double Descent(TextRun run)
        => _current?.Descent(run) ?? run.FontSize * 0.2;
}
