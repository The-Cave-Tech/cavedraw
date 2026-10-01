using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Blend modes: the one member of #110's list that had nowhere to live.
///
/// CSS's `mix-blend-mode` is what a file writes, so these assert the model and the **bytes**. What is deliberately
/// not claimed here is that the canvas composites it - the value is carried so an exported file composites
/// correctly in another viewer, which is what this issue is about.
/// </summary>
public class SvgBlendModeTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    [Fact]
    public void ABlendModeIsReadFromAStyle()
    {
        SvgImportResult result = Read(
            "<rect width=\"10\" height=\"10\" style=\"mix-blend-mode:multiply\"/>");

        Assert.Equal(BlendMode.Multiply, result.Document.AllPaths().Single().BlendMode);
    }

    [Fact]
    public void ABlendModeIsReadFromAStylesheet()
    {
        SvgImportResult result = Read(
            "<style>.blended { mix-blend-mode: screen; }</style>" +
            "<rect class=\"blended\" width=\"10\" height=\"10\"/>");

        Assert.Equal(BlendMode.Screen, result.Document.AllPaths().Single().BlendMode);
    }

    /// <summary>
    /// **`mix-blend-mode` does not inherit.** A group that multiplies does not make its children multiply: CSS
    /// computes the group's own result and composites that, so a reader that inherited the value would composite
    /// every child against a backdrop the file never asked for.
    /// </summary>
    [Fact]
    public void ABlendModeIsNotInherited()
    {
        SvgImportResult result = Read(
            "<g style=\"mix-blend-mode:multiply\"><rect width=\"10\" height=\"10\"/></g>");

        Assert.Equal(BlendMode.Multiply, result.Document.AllGroups().Single().BlendMode);
        Assert.Equal(BlendMode.Normal, result.Document.AllPaths().Single().BlendMode);
    }

    /// <summary>The default is `normal`, which is what an object with no blend mode has.</summary>
    [Fact]
    public void AnObjectWithNoBlendModeIsNormal()
    {
        Assert.Equal(BlendMode.Normal, Read("<rect width=\"10\" height=\"10\"/>").Document.AllPaths().Single().BlendMode);
    }

    /// <summary>An unknown mode reads as normal rather than as a mode that happens to be first in a list.</summary>
    [Fact]
    public void AnUnknownModeReadsAsNormal()
    {
        Assert.Equal(BlendMode.Normal, Read(
            "<rect width=\"10\" height=\"10\" style=\"mix-blend-mode:nonsense\"/>")
            .Document.AllPaths().Single().BlendMode);
    }

    /// <summary>And the exported bytes carry it, because another viewer is what renders it.</summary>
    [Fact]
    public void TheBlendModeIsWrittenToTheFile()
    {
        SvgImportResult result = Read("<rect width=\"10\" height=\"10\" style=\"mix-blend-mode:multiply\"/>");

        Assert.Contains("mix-blend-mode=\"multiply\"", SvgWriter.Write(result.Document), StringComparison.Ordinal);
    }

    /// <summary>
    /// A document that never set one exports **without** the attribute, so this feature cannot change the bytes of
    /// every existing document - which is what keeps the other export tests meaningful.
    /// </summary>
    [Fact]
    public void ANormalBlendModeIsNotWritten()
    {
        SvgImportResult result = Read("<rect width=\"10\" height=\"10\"/>");

        Assert.DoesNotContain("mix-blend-mode", SvgWriter.Write(result.Document), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBlendModeSurvivesTheSvgRoundTrip()
    {
        SvgImportResult back = SvgReader.Read(SvgWriter.Write(
            Read("<rect width=\"10\" height=\"10\" style=\"mix-blend-mode:color-dodge\"/>").Document));

        Assert.Equal(BlendMode.ColorDodge, back.Document.AllPaths().Single().BlendMode);
    }

    /// <summary>And this repository's own format, or a saved document would forget how it was blended.</summary>
    [Fact]
    public void TheBlendModeSurvivesTheSidecar()
    {
        SvgImportResult result = Read("<rect width=\"10\" height=\"10\" style=\"mix-blend-mode:luminosity\"/>");

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(result.Document));

        Assert.Equal(BlendMode.Luminosity, reloaded.AllPaths().Single().BlendMode);

        string plain = System.Text.Encoding.UTF8.GetString(
            VccadDocumentSerializer.SerializeToBytes(CadDocument.CreateDefault()));
        Assert.DoesNotContain("Blend", plain, StringComparison.Ordinal);
    }

    /// <summary>Every name the model can write is one it can read back, which is what makes the round trip total.</summary>
    [Fact]
    public void EveryBlendModeNameRoundTrips()
    {
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            Assert.Equal(mode, BlendModes.Parse(mode.ToSvgName()));
        }
    }
}
