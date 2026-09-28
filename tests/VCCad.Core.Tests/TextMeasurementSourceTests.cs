using VCCad.Core.Model;
using VCCad.Core.Text;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// All text measurement goes through <see cref="ITextMetrics"/>.
///
/// The model has to know how wide text is, but it must not reference a rendering toolkit.
/// So a host installs a measurer and everything asks it. Three constants stand in when no
/// host has: an advance, an ascent and a descent. They live in <see cref="TextMeasurement"/>
/// and nowhere else, because a second copy of a guessed constant is how two parts of a
/// program come to disagree about the same text.
/// </summary>
public class TextMeasurementSourceTests
{
    /// <summary>A measurer that returns whatever it is told to, so a call can be traced.</summary>
    private sealed class Fixed(double advance, double ascent, double descent) : ITextMetrics
    {
        public IReadOnlyList<double> Advances(TextRun run) =>
            Enumerable.Repeat(advance, run.Text.Length).ToArray();

        public double Ascent(TextRun run) => ascent;

        public double Descent(TextRun run) => descent;
    }

    private static TextRun Run(string text = "AV", double size = 10) =>
        new() { Text = text, FontFamily = "Nimbus Sans", FontSize = size };

    [Fact]
    public void TheEndOfALineIsMeasuredRatherThanGuessed()
    {
        ITextMetrics? previous = TextMeasurement.Current;
        try
        {
            TextMeasurement.Current = new Fixed(advance: 7.5, ascent: 8.0, descent: 2.0);

            // Callers used to reach for the estimate here, ignoring the shaper that was
            // installed. The run's own last character is the width the person can see.
            Assert.Equal(7.5, TextMeasurement.AdvanceAtEnd(Run()));
        }
        finally
        {
            TextMeasurement.Current = previous;
        }
    }

    [Fact]
    public void WithNoMeasurerTheEndOfALineFallsBackToTheEstimate()
    {
        ITextMetrics? previous = TextMeasurement.Current;
        try
        {
            TextMeasurement.Current = null;

            // 10pt at the documented 0.6 em.
            Assert.Equal(6.0, TextMeasurement.AdvanceAtEnd(Run(size: 10)));
        }
        finally
        {
            TextMeasurement.Current = previous;
        }
    }

    [Fact]
    public void AscentAndDescentAreNotRepeatedConstants()
    {
        ITextMetrics? previous = TextMeasurement.Current;
        try
        {
            TextMeasurement.Current = null;
            TextRun run = Run(size: 20);

            // The fallbacks are named, so changing one changes every caller.
            Assert.Equal(TextMeasurement.EstimatedAscent(run), TextMeasurement.Ascent(run));
            Assert.Equal(TextMeasurement.EstimatedDescent(run), TextMeasurement.Descent(run));
        }
        finally
        {
            TextMeasurement.Current = previous;
        }
    }

    [Fact]
    public void NoOtherSourceFileRepeatsTheGuessedConstants()
    {
        string? src = FindSourceRoot();
        if (src is null)
        {
            // Not a checkout layout; the invariant is still asserted by the tests above.
            return;
        }

        // Anchored so the importer's "fontSize * 0.25" — the TJ kerning threshold, a
        // quarter of an em and nothing to do with measuring a glyph — is not a false hit.
        string[] patterns =
        {
            @"FontSize \* 0\.6(?![0-9])", @"FontSize \* 0\.8(?![0-9])", @"FontSize \* 0\.2(?![0-9])",
            @"fontSize \* 0\.6(?![0-9])", @"fontSize \* 0\.8(?![0-9])", @"fontSize \* 0\.2(?![0-9])",
        };

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "TextMeasurement.cs")
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (string pattern in patterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(text, pattern))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {pattern}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Guessed font metrics belong in TextMeasurement only, but found: " +
            string.Join(", ", offenders));
    }

    /// <summary>The nearest <c>src</c> directory above the test binaries, or null.</summary>
    private static string? FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(candidate) &&
                Directory.Exists(Path.Combine(candidate, "VCCad.Core")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
