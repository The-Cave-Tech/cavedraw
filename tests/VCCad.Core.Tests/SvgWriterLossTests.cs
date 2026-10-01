using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// What the SVG writer leaves out, and whether it says so.
///
/// A document can hold text (#125's import) and rasters, and the writer emits neither. The rule this suite pins is
/// that the **file may be incomplete but the report may not be**: every item that did not reach the file is named
/// with its kind and the reason, and a document the writer wrote completely reports nothing at all - a loss list
/// that fires on everything tells a driver nothing.
/// </summary>
public class SvgWriterLossTests
{
    private static CadDocument DocumentWithTextAndImage()
    {
        CadDocument document = CadDocument.CreateDefault();

        var text = new TextItem { Name = "Title", Origin = new Point2D(12.5, 34), Color = ColorRgb.Black };
        text.Runs.Add(new TextRun { Text = "Hello", FontFamily = "Nimbus Sans", FontSize = 18, Bold = true });

        var image = new ImageItem
        {
            Name = "logo",
            PixelWidth = 4,
            PixelHeight = 3,
            Samples = new byte[4 * 3 * 3],
            Placement = new Rect2D(10, 20, 40, 30),
        };

        document.Artboards[0].Layers[0].AddItem(text);
        document.Artboards[0].Layers[0].AddItem(image);
        return document;
    }

    private static CadDocument PathsOnlyDocument()
    {
        CadDocument document = CadDocument.CreateDefault();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(10, 20)));
        sub.Nodes.Add(new PathNode(new Point2D(110, 20)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4);
        document.Artboards[0].Layers[0].AddItem(path);
        return document;
    }

    /// <summary>
    /// The defect, established: the document holds a text block and a raster, and the file has neither and says
    /// nothing. Written against <see cref="SvgWriter.Write"/>, which is the whole of what a caller gets today.
    /// </summary>
    [Fact]
    public void TextAndAnImageAreDroppedFromTheFile()
    {
        CadDocument document = DocumentWithTextAndImage();
        Assert.Equal(2, document.Artboards[0].Layers[0].Children.Count);

        string svg = SvgWriter.Write(document);

        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<text", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<image", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// **And now it says so.** One entry per dropped object, naming the item, its kind and the reason - the same
    /// three things a person needs to find it and a driver needs to decide what to do about it.
    /// </summary>
    [Fact]
    public void TheLossListNamesTheTextAndTheImageItLeftOut()
    {
        SvgWriteResult result = SvgWriter.WriteResult(DocumentWithTextAndImage());

        Assert.Equal(2, result.Missing.Count);

        string text = Assert.Single(result.Missing, entry => entry.StartsWith("text ", StringComparison.Ordinal));
        Assert.Contains("'Title'", text, StringComparison.Ordinal);
        Assert.Contains("12.5,34", text, StringComparison.Ordinal);

        string image = Assert.Single(result.Missing, entry => entry.StartsWith("image ", StringComparison.Ordinal));
        Assert.Contains("'logo'", image, StringComparison.Ordinal);
        Assert.Contains("4x3 px", image, StringComparison.Ordinal);

        // The reason is on every entry rather than only in the record's documentation: a driver reading one line
        // of JSON has to be able to tell a deliberate omission from a bug without going to the source.
        Assert.All(result.Missing, entry => Assert.Contains("not in the file", entry, StringComparison.Ordinal));
    }

    /// <summary>An item with no name is still identified, by where it sits - a report a person cannot act on is noise.</summary>
    [Fact]
    public void AnUnnamedTextBlockIsIdentifiedByItsPlace()
    {
        CadDocument document = DocumentWithTextAndImage();
        document.Artboards[0].Layers[0].Children.OfType<TextItem>().Single().Name = string.Empty;

        string entry = Assert.Single(SvgWriter.WriteResult(document).Missing,
            e => e.StartsWith("text ", StringComparison.Ordinal));

        Assert.Contains("(unnamed)", entry, StringComparison.Ordinal);
        Assert.Contains("12.5,34", entry, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A document the writer can write completely reports nothing.** This is the half that keeps the list worth
    /// reading: a report that fires on every export is one a caller learns to skip.
    /// </summary>
    [Fact]
    public void APathsOnlyDocumentReportsNoLosses()
    {
        SvgWriteResult result = SvgWriter.WriteResult(PathsOnlyDocument());

        Assert.Empty(result.Missing);

        // And the report is of a document that really was written, rather than of one that failed quietly.
        Assert.Contains("<path", result.Svg, StringComparison.Ordinal);
        Assert.Equal(1, result.ByElement["path"]);
    }

    /// <summary>
    /// A text block inside a group is reported too. The walk recurses, so a reader of the report must not have to
    /// know at what depth the object sat - "the file is missing this" is the same fact wherever it was.
    /// </summary>
    [Fact]
    public void TextInsideAGroupIsReportedTheSameWay()
    {
        CadDocument document = CadDocument.CreateDefault();
        var group = new ArtGroup { Name = "panel" };
        var text = new TextItem { Name = "Caption", Origin = new Point2D(3, 4) };
        text.Runs.Add(new TextRun { Text = "deep" });
        group.AddItem(text);
        document.Artboards[0].Layers[0].AddItem(group);

        SvgWriteResult result = SvgWriter.WriteResult(document);

        Assert.Contains("<g", result.Svg, StringComparison.Ordinal);
        Assert.Contains("'Caption'", Assert.Single(result.Missing), StringComparison.Ordinal);
    }
}
