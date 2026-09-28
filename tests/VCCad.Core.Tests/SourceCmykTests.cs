using VCCad.Core.Model;
using VCCad.Core.Serialization;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The original CMYK ink values survive the model round trip.
///
/// The export does not currently paint with them, because a PDF/A-2b export must declare
/// an sRGB output intent and the sample declares none, so a viewer colours the same ink
/// values differently under each — measured as page 1 RMSE 16.3 with RGB against 20.4
/// with CMYK. They are kept anyway: losing them would make a faithful CMYK export
/// impossible later, and a document should not forget what it was painted with.
/// </summary>
public class SourceCmykTests
{
    private static CadDocument DocumentWithCmyk()
    {
        CadDocument doc = CadDocument.CreateDefault("Ink");
        Layer layer = doc.Artboards[0].Layers[0];

        var path = new PathItem { Name = "Ink path" };
        path.AddSubPath(false).Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(10, 10)));
        path.Fill = FillSpec.Solid(new ColorRgb(0.973, 0.984, 0.984));
        path.SourceFillCmyk = new[] { 0.027, 0.016, 0.016, 0.0 };
        path.SourceStrokeCmyk = new[] { 0.0, 0.0, 0.0, 1.0 };
        layer.AddItem(path);

        var text = new TextItem { Name = "Ink text", Color = new ColorRgb(0.9, 0.1, 0.1) };
        text.Runs.Add(new TextRun { Text = "x", FontFamily = "Nimbus Sans", FontSize = 12 });
        text.SourceCmyk = new[] { 0.0, 1.0, 1.0, 0.0 };
        layer.AddItem(text);

        return doc;
    }

    [Fact]
    public void InkValuesSurviveSaveAndReload()
    {
        CadDocument original = DocumentWithCmyk();

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(original));

        PathItem path = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        TextItem text = reloaded.Artboards[0].Layers[0].Children.OfType<TextItem>().Single();

        Assert.Equal(new[] { 0.027, 0.016, 0.016, 0.0 }, path.SourceFillCmyk);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 1.0 }, path.SourceStrokeCmyk);
        Assert.Equal(new[] { 0.0, 1.0, 1.0, 0.0 }, text.SourceCmyk);
    }

    [Fact]
    public void AnRgbDocumentCarriesNone()
    {
        CadDocument doc = CadDocument.CreateDefault("Plain");

        Assert.Null(VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(doc))
            .Artboards[0].Layers[0].Children.OfType<PathItem>().FirstOrDefault()?.SourceFillCmyk);
    }

    [Fact]
    public void TheDumpReportsThem()
    {
        string dump = ModelDump.Of(DocumentWithCmyk());

        // The dump is what proves a round trip is lossless, so anything the document
        // carries has to appear in it.
        Assert.Contains("fillCmyk=0.027,0.016,0.016,0", dump, StringComparison.Ordinal);
        Assert.Contains("colourCmyk=0,1,1,0", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void ACloneKeepsItsOwnCopy()
    {
        CadDocument doc = DocumentWithCmyk();
        PathItem path = doc.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        var copy = (PathItem)path.Clone();

        Assert.Equal(path.SourceFillCmyk, copy.SourceFillCmyk);
        Assert.NotSame(path.SourceFillCmyk, copy.SourceFillCmyk);
    }
}
