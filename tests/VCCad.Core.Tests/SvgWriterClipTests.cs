using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A clip is structure, and the writer has to write it or say it could not.
///
/// <see cref="ClipSpec"/> is what the model records a crop as: the PDF importer gives a form's `/BBox` one, the SVG
/// reader gives a nested viewport's port one, and the canvas, selection and PDF exporter all honour them. The SVG
/// writer wrote none of them - an `ArtGroup` became a plain `&lt;g&gt;` with no `clip-path` and a nested `&lt;svg&gt;`
/// became a `&lt;g&gt;` too - so a crop survived the editor and not the save. That is the one direction of this
/// repository's silent-loss family in which a round trip through the editor *returns more than it was given*.
///
/// Asserted on the **exported bytes** first, because the file is the artifact that leaves the editor and a viewer
/// reads the file rather than the model; and then on the **re-imported model**, because a clip a viewer honours but
/// this reader loses is a crop that survives one round trip and not two.
/// </summary>
public class SvgWriterClipTests
{
    /// <summary>The two-effect figure from the report: a 50-unit port at (50,50) holding a 400-unit rect.</summary>
    private const string Figure =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
        "<svg x=\"50\" y=\"50\" width=\"50\" height=\"50\"><rect width=\"400\" height=\"400\"/></svg></svg>";

    private static SvgImportResult Imported() => SvgReader.Read(Figure);

    /// <summary>The one path's far corner as the artboard sees it, every enclosing transform composed in.</summary>
    private static Point2D FarCorner(PathItem path)
    {
        AffineTransform transform = AffineTransform.Identity;
        for (IItemContainer? container = path.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                transform = group.Transform.Compose(transform);
            }
        }

        return transform.Transform(path.SubPaths[0].Nodes[2].Anchor);
    }

    /// <summary>
    /// **The defect, on the exported bytes.**
    ///
    /// The editor crops the rect to the port - the model holds the clip, and
    /// <see cref="SvgNestedViewportTests.ANestedViewportClipsItsContentToItsPort"/> is that half. The file has to
    /// carry the same statement: either a `clip-path` referring to a `clipPath`, or a nested `svg` whose own port
    /// is the crop. Neither is in today's output, so the rect is written whole and the crop is gone.
    /// </summary>
    [Fact]
    public void AViewportPortReachesTheExportedBytes()
    {
        string svg = SvgWriter.Write(Imported().Document);

        bool clipped = svg.Contains("clip-path", StringComparison.Ordinal) &&
                       svg.Contains("<clipPath", StringComparison.Ordinal);
        bool nestedPort = svg.Contains("<svg", StringComparison.Ordinal) &&
                          svg[(svg.IndexOf("<svg", StringComparison.Ordinal) + 4)..]
                              .Contains("<svg", StringComparison.Ordinal);

        Assert.True(clipped || nestedPort,
            $"the file carries no clip for the viewport port\r\n{svg}");
    }

    /// <summary>
    /// **And the crop survives being read back.**
    ///
    /// One round trip is not the test: this editor's own reader is what opens the file again, so a crop the file has
    /// and the reader loses is a crop that is gone the second time the document is saved. The content still reaches
    /// 337.5 pt - a clip does not rewrite the geometry, per this repository's rule - and what the pointer can pick
    /// up is the 75 pt the port shows.
    /// </summary>
    [Fact]
    public void AViewportPortSurvivesTheRoundTrip()
    {
        CadDocument document = Imported().Document;
        CadDocument back = SvgReader.Read(SvgWriter.Write(document)).Document;

        PathItem rect = Assert.Single(back.AllPaths());

        // The geometry is not rewritten to make it fit: 400 units from the port's corner is still 300 pt of rect.
        Assert.Equal(337.5, FarCorner(rect).X, 6);
        Assert.Equal(337.5, FarCorner(rect).Y, 6);

        // And the crop is still what the pointer is measured against: the port, not the whole rect.
        Assert.True(rect.IsClipped, "the re-imported rect is cropped by no clip");

        // The port the reader recovered, in the artboard's frame. A clip is recorded in the space the item is
        // placed in - the frame the **ancestors'** transforms map into - so the clipped group's own transform is
        // deliberately not composed in, exactly as SvgNestedViewportTests.ClipOutlineInArtboard does it.
        AffineTransform toArtboard = AffineTransform.Identity;
        for (IItemContainer? container = (rect.Container as LayerItem)?.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                toArtboard = group.Transform.Compose(toArtboard);
            }
        }

        ClipSpec clip = Assert.Single(SelectionEngine.ClipsOn(rect));
        List<Point2D> port = clip.SubPaths
            .SelectMany(sub => sub.Nodes)
            .Select(node => toArtboard.Transform(node.Anchor))
            .ToList();

        Assert.Equal(37.5, port.Min(p => p.X), 6);
        Assert.Equal(37.5, port.Min(p => p.Y), 6);
        Assert.Equal(75.0, port.Max(p => p.X), 6);
        Assert.Equal(75.0, port.Max(p => p.Y), 6);
    }

    /// <summary>
    /// **A clip that is not a viewport port is written as a `clip-path`, in the frame the model records.**
    ///
    /// The PDF importer's form `/BBox` is this shape and has nothing to do with SVG viewports, so the general
    /// spelling is the one that covers it. The outline goes out as a `clipPath` in `defs` and the path refers to it
    /// by id - and the geometry is the model's own numbers, not a bounding box standing in for them, because a clip
    /// the file did not draw is exactly what this repository's "no objects the file does not draw" rule forbids.
    /// </summary>
    [Fact]
    public void AnArbitraryClipIsWrittenAsAClipPathInTheModelsFrame()
    {
        CadDocument document = CadDocument.CreateDefault();
        var path = new PathItem { Name = "cut", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath shape = path.AddSubPath(closed: true);
        shape.Nodes.Add(new PathNode(new Point2D(0, 0)));
        shape.Nodes.Add(new PathNode(new Point2D(400, 0)));
        shape.Nodes.Add(new PathNode(new Point2D(400, 400)));
        shape.Nodes.Add(new PathNode(new Point2D(0, 400)));

        // A triangle, so the file cannot be passing a rectangle off as the clip.
        var clip = new ClipSpec { Rule = FillRule.EvenOdd };
        SubPath outline = new() { IsClosed = true };
        outline.Nodes.Add(new PathNode(new Point2D(10, 20)));
        outline.Nodes.Add(new PathNode(new Point2D(100, 20)));
        outline.Nodes.Add(new PathNode(new Point2D(10, 200)));
        clip.SubPaths.Add(outline);
        path.Clips.Add(clip);

        document.Artboards[0].Layers[0].AddItem(path);

        SvgWriteResult result = SvgWriter.WriteResult(document);

        Assert.Contains("<clipPath", result.Svg, StringComparison.Ordinal);
        Assert.Contains("clip-path=\"url(#clip1)\"", result.Svg, StringComparison.Ordinal);
        Assert.Contains("clipPathUnits=\"userSpaceOnUse\"", result.Svg, StringComparison.Ordinal);

        // The outline's own numbers, and the rule the model recorded for them.
        Assert.Contains("d=\"M 10 20 L 100 20 L 10 200 L 10 20 Z\"", result.Svg, StringComparison.Ordinal);
        Assert.Contains("clip-rule=\"evenodd\"", result.Svg, StringComparison.Ordinal);

        // And it reports nothing, because it wrote everything.
        Assert.Empty(result.Missing);
    }

    /// <summary>
    /// **A clip on a group is carried into the group's own space by its transform.**
    ///
    /// SVG applies an element's `clip-path` in the user space its own `transform` establishes, while the model
    /// records a group's clip in the space the group is placed in. Writing the outline unconverted would clip the
    /// group at the wrong place - and a clip in the wrong place looks deliberate, which is what makes it worse than
    /// a missing one. A 10x10 outline at (50,50) on a group that translates by (30,40) is a 10x10 outline at (20,10)
    /// once it is stated in the group's own space.
    /// </summary>
    [Fact]
    public void AClipOnAGroupIsStatedInTheGroupsOwnSpace()
    {
        CadDocument document = CadDocument.CreateDefault();
        var group = new ArtGroup { Name = "form", Transform = AffineTransform.CreateTranslation(30, 40) };

        var path = new PathItem { Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath shape = path.AddSubPath(closed: true);
        shape.Nodes.Add(new PathNode(new Point2D(0, 0)));
        shape.Nodes.Add(new PathNode(new Point2D(400, 0)));
        shape.Nodes.Add(new PathNode(new Point2D(400, 400)));
        shape.Nodes.Add(new PathNode(new Point2D(0, 400)));
        group.AddItem(path);

        var clip = new ClipSpec();
        SubPath outline = new() { IsClosed = true };
        outline.Nodes.Add(new PathNode(new Point2D(50, 50)));
        outline.Nodes.Add(new PathNode(new Point2D(60, 50)));
        outline.Nodes.Add(new PathNode(new Point2D(60, 60)));
        outline.Nodes.Add(new PathNode(new Point2D(50, 60)));
        clip.SubPaths.Add(outline);

        // A closed rectangle with no viewport metadata would be written as a nested `svg`; the rule is what keeps
        // this one honest as a `clip-path`, so the general path is asserted through it.
        clip.Rule = FillRule.EvenOdd;
        group.Clips.Add(clip);
        document.Artboards[0].Layers[0].AddItem(group);

        SvgWriteResult result = SvgWriter.WriteResult(document);

        Assert.Contains("transform=\"matrix(1,0,0,1,30,40)\"", result.Svg, StringComparison.Ordinal);
        Assert.Contains("clip-path=\"url(#clip1)\"", result.Svg, StringComparison.Ordinal);
        Assert.Contains("d=\"M 20 10 L 30 10 L 30 20 L 20 20 L 20 10 Z\"", result.Svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A clip that cannot be written is reported, not dropped.**
    ///
    /// A group whose transform is singular - a scale of zero, which flattens everything it holds onto a line - has
    /// no local space for its outline to be stated in, so there is no honest way to write the crop. The rule this
    /// repository runs on is that what cannot be written is said rather than silently left out, and the surface is
    /// the same one a dropped text block or raster uses: <see cref="SvgWriteResult.Missing"/>, naming the item and
    /// the reason.
    /// </summary>
    [Fact]
    public void AClipThatCannotBeWrittenIsReported()
    {
        CadDocument document = CadDocument.CreateDefault();
        var group = new ArtGroup
        {
            Name = "flattened",
            Transform = AffineTransform.CreateScale(0, 0),
        };

        var path = new PathItem { Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath shape = path.AddSubPath(closed: true);
        shape.Nodes.Add(new PathNode(new Point2D(0, 0)));
        shape.Nodes.Add(new PathNode(new Point2D(10, 10)));
        group.AddItem(path);

        var clip = new ClipSpec();
        SubPath outline = new() { IsClosed = true };
        outline.Nodes.Add(new PathNode(new Point2D(1, 1)));
        outline.Nodes.Add(new PathNode(new Point2D(9, 1)));
        outline.Nodes.Add(new PathNode(new Point2D(5, 9)));
        clip.SubPaths.Add(outline);
        group.Clips.Add(clip);

        document.Artboards[0].Layers[0].AddItem(group);

        SvgWriteResult result = SvgWriter.WriteResult(document);

        string entry = Assert.Single(result.Missing);
        Assert.StartsWith("group ", entry, StringComparison.Ordinal);
        Assert.Contains("'flattened'", entry, StringComparison.Ordinal);
        Assert.Contains("clip", entry, StringComparison.Ordinal);
        Assert.Contains("not invertible", entry, StringComparison.Ordinal);

        // And the file drew the item without pretending to crop it.
        Assert.DoesNotContain("clip-path", result.Svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A document with no clips is written exactly as it was before clips were written at all.**
    ///
    /// The whole of this change has to be invisible to the documents that do not have the structure it is about:
    /// no `defs` for a clip nobody holds, no attribute on any element, and the same bytes out of the writer for the
    /// same document twice. A regression here would rewrite every file the editor saves.
    /// </summary>
    [Fact]
    public void ADocumentWithNoClipsWritesNoClipAnything()
    {
        CadDocument document = CadDocument.CreateDefault();
        var group = new ArtGroup { Name = "panel", Transform = AffineTransform.CreateTranslation(5, 6) };
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(10, 20)));
        sub.Nodes.Add(new PathNode(new Point2D(110, 20)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4);
        group.AddItem(path);
        document.Artboards[0].Layers[0].AddItem(group);

        SvgWriteResult result = SvgWriter.WriteResult(document);

        Assert.Empty(result.Missing);
        Assert.DoesNotContain("clip", result.Svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<defs", result.Svg, StringComparison.Ordinal);

        // Deterministic, and the same document writes the same file twice.
        Assert.Equal(result.Svg, SvgWriter.Write(document));
    }
}
