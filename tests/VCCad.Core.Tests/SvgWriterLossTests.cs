using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// What the SVG writer leaves out, and whether it says so.
///
/// A document can hold text (#125's import) and rasters, and the writer now emits the text. The rule this suite
/// pins is that the **file may be incomplete but the report may not be**: every item that did not reach the file is
/// named with its kind and the reason, and a document the writer wrote completely reports nothing at all - a loss
/// list that fires on everything tells a driver nothing.
///
/// The text half of this suite is **converted rather than deleted**, as the rules require. It used to pin the
/// writer's largest silent omission; it now pins the output, and the reports that stood in for the words are
/// asserted to be gone. The raster is still left out, and still named.
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
    /// The raster is still dropped from the file, and the text is not.
    ///
    /// This is the converted half of the test that established the gap: it used to assert that a document holding a
    /// text block and a raster exported neither. The text now reaches the file - the words, the `tspan` that places
    /// them - and the raster does not, which is the state #132 is half done in.
    /// </summary>
    [Fact]
    public void TheTextIsWrittenAndTheImageIsNot()
    {
        CadDocument document = DocumentWithTextAndImage();
        Assert.Equal(2, document.Artboards[0].Layers[0].Children.Count);

        string svg = SvgWriter.Write(document);

        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("<text", svg, StringComparison.Ordinal);
        Assert.Contains("<tspan", svg, StringComparison.Ordinal);
        Assert.Contains("Hello", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<image", svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// **And the loss list names exactly what is still missing.**
    ///
    /// One entry, for the raster, naming the item, its placement and the reason - the same three things a person
    /// needs to find it and a driver needs to decide what to do about it. The text block it used to name is gone
    /// from the list because it is in the file, which is the half that keeps the list worth reading: a report that
    /// fires on a document the writer wrote completely is noise.
    /// </summary>
    [Fact]
    public void TheLossListNamesTheImageAndNoLongerTheText()
    {
        SvgWriteResult result = SvgWriter.WriteResult(DocumentWithTextAndImage());

        string image = Assert.Single(result.Missing);
        Assert.StartsWith("image ", image, StringComparison.Ordinal);
        Assert.Contains("'logo'", image, StringComparison.Ordinal);
        Assert.Contains("10,20", image, StringComparison.Ordinal);
        Assert.Contains("4x3 px", image, StringComparison.Ordinal);

        // The reason is on the entry rather than only in the record's documentation: a driver reading one line of
        // JSON has to be able to tell a deliberate omission from a bug without going to the source.
        Assert.Contains("not in the file", image, StringComparison.Ordinal);
    }

    /// <summary>
    /// An item with no name is still identified, by where it sits - a report a person cannot act on is noise.
    ///
    /// This used to be asserted of a text block, which is now written and reported by nothing at all. The raster is
    /// the item the report is still about, so the check moves to it rather than being dropped with the gap.
    /// </summary>
    [Fact]
    public void AnUnnamedImageIsIdentifiedByItsPlace()
    {
        CadDocument document = DocumentWithTextAndImage();
        document.Artboards[0].Layers[0].Children.OfType<ImageItem>().Single().Name = string.Empty;

        string entry = Assert.Single(SvgWriter.WriteResult(document).Missing);

        Assert.Contains("(unnamed)", entry, StringComparison.Ordinal);
        Assert.Contains("10,20", entry, StringComparison.Ordinal);
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
    /// Text inside a group is written where the group puts it, and the group reports nothing.
    ///
    /// The walk recurses, so a reader of the file must not have to know at what depth the block sat - and a
    /// document the writer wrote completely says nothing, which used to be false here because every text block was
    /// a loss.
    /// </summary>
    [Fact]
    public void TextInsideAGroupIsWrittenTheSameWay()
    {
        CadDocument document = CadDocument.CreateDefault();
        var group = new ArtGroup { Name = "panel" };
        var text = new TextItem { Name = "Caption", Origin = new Point2D(3, 4) };
        text.Runs.Add(new TextRun { Text = "deep" });
        group.AddItem(text);
        document.Artboards[0].Layers[0].AddItem(group);

        SvgWriteResult result = SvgWriter.WriteResult(document);

        Assert.Contains("<g", result.Svg, StringComparison.Ordinal);
        Assert.Contains(">deep</tspan>", result.Svg, StringComparison.Ordinal);
        Assert.Empty(result.Missing);
    }
}
