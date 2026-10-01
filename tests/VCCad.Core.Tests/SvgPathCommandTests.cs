using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A path command this build does not read.
///
/// The parser stops rather than guessing, so the path keeps everything read correctly - which is the right
/// behaviour and also a silent one: the shape draws slightly less than the file asked for and nothing about the
/// result looks like an error. So it is said, naming the command.
/// </summary>
public class SvgPathCommandTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    [Fact]
    public void AnUnreadCommandIsReported()
    {
        SvgImportResult result = Read("<path d=\"M0 0 L10 0 Q20 0 30 0 B10 10\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("does not read", StringComparison.Ordinal) &&
            warning.Contains("B", StringComparison.Ordinal));

        // And the path keeps what was read before it, rather than being thrown away.
        Assert.Single(result.Document.AllPaths());
    }

    [Fact]
    public void AnOrdinaryPathSaysNothing()
    {
        SvgImportResult result = Read(
            "<path d=\"M0 0 L10 0 C12 0 14 2 16 4 Q18 8 20 10 A5 5 0 0 1 30 20 Z\"/>");

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("does not read", StringComparison.Ordinal));
        Assert.Single(result.Document.AllPaths());
    }

    /// <summary>A command it does read says nothing, which is what keeps the list from becoming noise.</summary>
    [Fact]
    public void AnotherOrdinaryPathSaysNothing()
    {
        SvgImportResult result = Read("<path d=\"M0 0 l5 5 h5 v5 s2 2 4 4 t4 4 z\"/>");

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("does not read", StringComparison.Ordinal));
    }
}
