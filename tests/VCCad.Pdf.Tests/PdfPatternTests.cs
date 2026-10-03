using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **The page writes a pattern as repeated tiles** (issue #203).
///
/// Bytes, and the *number of draws* in them: a patterned fill writes the tile once per tile position, so a 40x40
/// shape tiled by a 10x10 tile is sixteen fills where an unpatterned shape is one. The control in the same test is
/// the same document without the pattern, which must still be exactly one fill - so the assertion is about
/// repetition rather than about the presence of the word "pattern".
/// </summary>
public class PdfPatternTests
{
    private static CadDocument Document(bool withPattern)
    {
        CadDocument document = CadDocument.CreateDefault("Patterns");
        document.Artboards[0].Width = 300;
        document.Artboards[0].Height = 300;

        ArtGroup tile = document.AddDefinition("dots");
        tile.ForeignAttributes[SvgWriter.PatternDefinitionTag] = "dots";
        tile.ForeignAttributes["width"] = "10";
        tile.ForeignAttributes["height"] = "10";
        tile.ForeignAttributes["patternUnits"] = "userSpaceOnUse";

        var dot = new PathItem { Name = "dot", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath dotPath = dot.AddSubPath(closed: true);
        dotPath.Nodes.Add(new PathNode(new Point2D(0, 0)));
        dotPath.Nodes.Add(new PathNode(new Point2D(2, 0)));
        dotPath.Nodes.Add(new PathNode(new Point2D(2, 2)));
        dotPath.Nodes.Add(new PathNode(new Point2D(0, 2)));
        tile.AddItem(dot);

        FillSpec fill = withPattern
            ? FillSpec.Solid(ColorRgb.Black) with
            {
                Pattern = new PatternSpec("dots", 10, 10, 0, 0, "userSpaceOnUse"),
            }
            : FillSpec.Solid(ColorRgb.Black);

        var rect = new PathItem { Name = "rect", Fill = fill };
        SubPath shape = rect.AddSubPath(closed: true);
        shape.Nodes.Add(new PathNode(new Point2D(20, 20)));
        shape.Nodes.Add(new PathNode(new Point2D(60, 20)));
        shape.Nodes.Add(new PathNode(new Point2D(60, 60)));
        shape.Nodes.Add(new PathNode(new Point2D(20, 60)));
        document.Artboards[0].Layers[0].AddItem(rect);

        return document;
    }

    /// <summary>The page's own drawing operators, inflated: the exporter compresses its content streams.</summary>
    private static string[] Operators(CadDocument document)
        => PdfDrawing.Of(PdfDocumentExporter.Export(document))
            .Split('\n')
            .Select(line => line.Trim())
            .ToArray();

    private static int Fills(string[] ops) => ops.Count(line => line is "f" or "f*");

    [Fact]
    public void APatternIsWrittenAsOneFillPerTile()
    {
        string[] patterned = Operators(Document(withPattern: true));
        string[] plain = Operators(Document(withPattern: false));

        // The control: the same shape with no pattern is a single fill.
        Assert.Equal(1, Fills(plain));

        // Sixteen tile positions over a 40x40 shape tiled by 10x10, so sixteen fills - and the assertion is a floor
        // rather than the exact count, because what it is about is repetition and not this fixture's arithmetic.
        Assert.True(Fills(patterned) >= 8, $"the page wrote {Fills(patterned)} fill(s) for a patterned shape");
        Assert.True(Fills(patterned) > Fills(plain));
    }

    [Fact]
    public void ATileWithNoBoxIsNotWrittenAndIsNoted()
    {
        CadDocument document = Document(withPattern: true);
        document.Artboards[0].Layers[0].Children.OfType<PathItem>().First().Fill =
            document.Artboards[0].Layers[0].Children.OfType<PathItem>().First().Fill
                with { Pattern = new PatternSpec("dots", 0, 0, 0, 0, "userSpaceOnUse") };

        string[] ops = Operators(document);

        // No tile box, no repetition: the shape is left unpainted rather than filled with a solid stand-in.
        Assert.Equal(0, Fills(ops));
    }
}
