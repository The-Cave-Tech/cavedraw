using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **A right-to-left run survives the SVG round trip.**
///
/// `TextLayout.BuildSegments` says of itself that it is "what lets a writer put every piece on its own `tspan`", and
/// no writer uses it - so this asks whether that consumer was ever needed. A reordered run is the case a single
/// `tspan` is supposed to be unable to express: the visual order differs from the logical order, and a segment is
/// exactly the piece the pen reaches without changing direction.
///
/// If the round trip is stable the member is a leftover and its doc should be corrected; if it is not, an SVG
/// export defect is filed. Either way the answer is evidence rather than judgement.
/// </summary>
public class SvgRtlRoundTripTests
{
    private const string Head = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"200\">";

    private static SvgImportResult Read(string svg) => SvgReader.Read(svg);

    private static string TextOf(SvgImportResult result)
        => string.Concat(result.Document.AllItems().OfType<TextItem>().SelectMany(t => t.Runs).Select(r => r.Text));

    /// <summary>The run's text, written out and read back, is the same - the writer cannot lose the ordering.</summary>
    [Fact]
    public void AMixedDirectionRunSurvivesTheRoundTrip()
    {
        // Hebrew (RTL) with a Latin word inside it: the reordering the bidi algorithm does is what a round trip has
        // to preserve, and a Latin-only string would exercise none of it.
        SvgImportResult first = Read(Head + "<text x=\"20\" y=\"60\" font-size=\"24\">\u05e9\u05dc\u05d5\u05dd AB</text></svg>");

        string before = TextOf(first);
        Assert.Contains("AB", before, StringComparison.Ordinal);

        SvgWriteResult written = SvgWriter.WriteResult(first.Document);
        Assert.Empty(written.Missing);

        SvgImportResult second = Read(written.Svg);
        string after = TextOf(second);

        Assert.Equal(before, after);

        // The reader's own visual order must be stable too, or the second read says something different from the
        // first even when the decoded text matches.
        Assert.Equal(
            first.Document.AllItems().OfType<TextItem>().Single().Runs.Single().Text,
            second.Document.AllItems().OfType<TextItem>().Single().Runs.Single().Text);
    }
}
