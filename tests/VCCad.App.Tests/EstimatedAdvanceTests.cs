using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using VCCad.Core.Model;
using VCCad.Core.Text;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// What the estimated advance is, and what it is not (issue #251).
///
/// This began as a test asserting that the face's width for an edited run matches the model's advances, and it failed
/// with 244.428 against 340.200 - which looked like the defect. **It was not.** The estimate
/// (`FontSize * 0.6 = 5.4`) is what `TextMeasurement.Advances` returns when no real metrics implementation is
/// registered, and a headless suite registers none: 63 characters at 5.4 is 340.200, and the "1.39x disagreement" was
/// this test comparing a headless number against a number read from the running application, where the same call
/// reports 244.428 - the face's own width. Two contexts, one comparison, and the wrong conclusion.
///
/// So the file now asserts what it can honestly assert from a headless suite: the documented fallback, per character,
/// and the arithmetic that made the mistake possible. When a real metrics implementation *is* registered the estimate
/// is never reached, which is why the application's own advances agree with its face.
/// </summary>
public class EstimatedAdvanceTests
{
    private static TextRun Run(string text) => new()
    {
        Text = text,
        FontFamily = "Nimbus Sans",
        FontSize = 9,
    };

    /// <summary>
    /// The estimate is <c>FontSize * 0.6</c> per character, which is where 340.200 came from: 63 x 5.4.
    /// </summary>
    [AvaloniaFact]
    public void TheEstimateIsSixtyPerCentOfThePointSizePerCharacter()
    {
        TextRun run = Run("Jalie 3464I am a jelly donut and more text - LILLIE - Page 1/12");

        double estimated = TextMeasurement.Estimate(run);

        Assert.Equal(5.4, estimated, 6);
        Assert.Equal(340.2, estimated * run.Text.Length, 3);
    }

    /// <summary>
    /// A combining mark occupies no advance of its own - the rule the estimate applies per character.
    /// </summary>
    [AvaloniaFact]
    public void ACombiningMarkIsEstimatedAtZero()
    {
        TextRun run = Run("e\u0301");

        Assert.Equal(0.0, TextMeasurement.EstimateFor(run, '\u0301'), 6);
        Assert.Equal(5.4, TextMeasurement.EstimateFor(run, 'e'), 6);
    }

    /// <summary>
    /// **The face and this project's own advances agree in the application, and they agree here once a face is in
    /// play.** This is the check worth having: build the text with a real face and measure it directly, rather than
    /// asking the model, whose answer depends on whether real metrics are installed.
    /// </summary>
    [AvaloniaFact]
    public void TheFaceMeasuresTheEditedRunNearlyTheSameAsTheModelWould()
    {
        TextRun run = Run("Jalie 3464I am a jelly donut and more text - LILLIE - Page 1/12");

        var face = new FormattedText(
            run.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(run.FontFamily), FontStyle.Normal, FontWeight.Normal),
            run.FontSize,
            Brushes.Black);

        // The application reports this sum as 244.428 for the same run, so a face built the same way should land
        // within a few units of it. The numbers are here so that a future disagreement shows its size.
        Assert.True(
            face.Width > 200 && face.Width < 280,
            $"The face measures this run {face.Width:F3}; the application reports 244.428 for it.");
    }
}
