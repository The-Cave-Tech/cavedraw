using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// `use` and `symbol`: an instance of a definition rather than a copy of it.
///
/// The corpus uses `use` 56 times and `symbol` 14, more than any element except `rect` and `text`, so this is not a
/// corner of the format - it is how Inkscape files are built. The assertions are on where the instance's geometry
/// lands, because an instance in the wrong place is the failure that looks like a broken transform.
/// </summary>
public class SvgUseTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static Point2D FirstAnchor(SvgImportResult result)
        => result.Document.AllPaths().First().SubPaths[0].Nodes[0].Anchor;

    /// <summary>
    /// A node's coordinate as the artboard sees it, every enclosing group's transform composed in.
    ///
    /// The model keeps an instance's placement on the instance group - that is the frame an edit moves - so the
    /// only way to ask "where is the picture" is to compose the ancestors in. Asserting the group's transform
    /// alone would pass for an instance whose definition was read at the wrong scale.
    /// </summary>
    private static Point2D At(PathItem path, int node)
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

        return transform.Transform(path.SubPaths[0].Nodes[node].Anchor);
    }

    // ---------------------------------------------------------------- the basics

    [Fact]
    public void AUseDrawsItsTargetAtItsOwnPosition()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></defs>" +
            "<use xlink:href=\"#box\" x=\"50\" y=\"30\"/>");

        // The definition is not drawn where it is defined, and the instance is drawn at 50,30.
        PathItem path = Assert.Single(result.Document.AllPaths());
        Assert.Equal(new Point2D(0, 0), path.SubPaths[0].Nodes[0].Anchor);

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);
        Assert.Equal("box", instance.SourceId);

        // The position is in the file's own units, which is the space the instance's transform lives in - the group
        // above the whole file is what carries them into the model's points.
        Assert.Equal(new Point2D(50, 30), instance.Transform.Transform(new Point2D(0, 0)));
    }

    /// <summary>The instance's own transform composes with its x and y, in that order.</summary>
    [Fact]
    public void AUseComposesItsTransformWithItsPosition()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use xlink:href=\"#box\" x=\"10\" y=\"0\" transform=\"scale(2,2)\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);

        // x is ten to the right of a doubling, so the origin is at 10 and one unit is two.
        Assert.Equal(10.0, instance.Transform.Transform(new Point2D(0, 0)).X, 6);
        Assert.Equal(12.0, instance.Transform.Transform(new Point2D(1, 0)).X, 6);
    }

    /// <summary>
    /// **A `use` puts the same geometry at its offset** - it adds a placement, it does not restate the shape.
    ///
    /// The numbers are the definition's own inside the instance, and the offset is what the instance group
    /// carries; on the artboard the shape is therefore the definition translated by the use's x and y through the
    /// file's unit conversion. A reader that inlined the definition *without* the offset, or that applied the
    /// offset twice, differs from this by the whole distance.
    /// </summary>
    [Fact]
    public void AUsePutsTheSameGeometryAtItsOffset()
    {
        Point2D[] alone = Read("<rect id=\"box\" width=\"10\" height=\"20\"/>")
            .Document.AllPaths().Single().SubPaths[0].Nodes.Select(node => node.Anchor).ToArray();

        SvgImportResult placed = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"20\"/></defs><use href=\"#box\" x=\"50\" y=\"30\"/>");

        PathItem path = Assert.Single(placed.Document.AllPaths());
        ArtGroup instance = placed.Document.AllGroups().Single(g => g.SourceId is not null);

        // The shape's own numbers are the definition's, unchanged...
        Assert.Equal(alone, path.SubPaths[0].Nodes.Select(node => node.Anchor).ToArray());
        // ...the instance is a bare translation by the use's x and y...
        Assert.Equal(50.0, instance.Transform.Transform(new Point2D(0, 0)).X, 9);
        Assert.Equal(30.0, instance.Transform.Transform(new Point2D(0, 0)).Y, 9);
        // ...and on the artboard that is the definition at (50,30) user units, which is 0.75 pt each.
        Assert.Equal(37.5, At(path, 0).X, 9);
        Assert.Equal(22.5, At(path, 0).Y, 9);
        Assert.Equal(45.0, At(path, 2).X, 9);
        Assert.Equal(37.5, At(path, 2).Y, 9);
    }

    /// <summary>
    /// **The instance's placement composes with the referenced element's own transform rather than replacing it.**
    ///
    /// A definition is entitled to say where it sits - `transform="translate(5,5)"` on the element itself - and
    /// the `use` says where the instance goes. Dropping either one moves the picture, and dropping the target's
    /// own is the easy mistake: the shape's transform is baked into its points while the instance carries a group
    /// transform, so the two halves are read by different code.
    /// </summary>
    [Fact]
    public void AUseComposesWithTheTargetsOwnTransform()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\" transform=\"translate(5,5)\"/></defs>" +
            "<use href=\"#box\" x=\"50\" y=\"30\"/>");

        PathItem path = Assert.Single(result.Document.AllPaths());

        // (50+5, 30+5) user units at 0.75 pt each - the use's offset and the target's own translate, both present.
        Assert.Equal(41.25, At(path, 0).X, 9);
        Assert.Equal(26.25, At(path, 0).Y, 9);
        Assert.Equal(48.75, At(path, 2).X, 9);
        Assert.Equal(33.75, At(path, 2).Y, 9);
    }

    /// <summary>**An instance is a link, not a copy**: the group carries the id it came from.</summary>
    [Fact]
    public void AnInstanceRemembersWhatItIsAnInstanceOf()
    {
        SvgImportResult result = Read(
            "<defs><g id=\"widget\"><rect width=\"5\" height=\"5\"/></g></defs>" +
            "<use href=\"#widget\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);
        Assert.Equal("widget", instance.SourceId);
    }

    /// <summary>The SVG 2 bare `href` works as well as `xlink:href`, because Inkscape writes the latter and newer
    /// files write either. The test above uses one and this one the other.</summary>
    [Fact]
    public void BothReferenceFormsResolve()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"4\" height=\"4\"/></defs>" +
            "<use xlink:href=\"#box\"/><use href=\"#box\"/>");

        Assert.Equal(2, result.Document.AllGroups().Count(g => g.SourceId is not null));
        Assert.Empty(result.Missing);
    }

    // ---------------------------------------------------------------- symbols

    /// <summary>
    /// **A symbol is sized by the use that draws it**, and its view box is what the size means.
    /// </summary>
    [Fact]
    public void ASymbolIsSizedByItsUse()
    {
        SvgImportResult result = Read(
            "<defs><symbol id=\"icon\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></symbol></defs>" +
            "<use xlink:href=\"#icon\" x=\"0\" y=\"0\" width=\"50\" height=\"50\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);
        Assert.Equal("icon", instance.SourceId);

        // The symbol is ten units across and is drawn fifty wide, so one unit is five.
        Assert.Equal(50.0, instance.Transform.Transform(new Point2D(10, 10)).X, 6);
    }

    /// <summary>
    /// SVG 2 lets the **symbol** carry its own width and height as geometry properties, and the corpus has a file
    /// whose whole point is that. A `use` that gives no size falls back to them.
    /// </summary>
    [Fact]
    public void ASymbolsOwnGeometryPropertiesSizeIt()
    {
        SvgImportResult result = Read(
            "<defs><symbol id=\"icon\" viewBox=\"0 0 10 10\" width=\"20\" height=\"20\">" +
            "<rect width=\"10\" height=\"10\"/></symbol></defs>" +
            "<use xlink:href=\"#icon\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);

        // Twenty over ten is two, so one symbol unit is two user units.
        Assert.Equal(20.0, instance.Transform.Transform(new Point2D(10, 0)).X, 6);
    }

    /// <summary>A symbol with no size anywhere is drawn at its view box size, which is what a viewer does.</summary>
    [Fact]
    public void ASymbolWithNoSizeKeepsItsViewBoxSize()
    {
        SvgImportResult result = Read(
            "<defs><symbol id=\"icon\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></symbol></defs>" +
            "<use xlink:href=\"#icon\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);
        Assert.Equal(10.0, instance.Transform.Transform(new Point2D(10, 0)).X, 6);
    }

    /// <summary>
    /// **A `symbol` nobody uses draws nothing.** It is a definition, and the repository rule is "no objects the
    /// file does not draw": a `symbol` in the tree is not artwork, it is somewhere for a `use` to point. A reader
    /// that drew it where it is defined would put an extra copy on the page that no viewer shows - and it would
    /// look like a stray shape rather than a parser fault.
    /// </summary>
    [Fact]
    public void ASymbolNobodyUsesDrawsNothing()
    {
        SvgImportResult result = Read(
            "<defs><symbol id=\"icon\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></symbol></defs>");

        Assert.Empty(result.Document.AllPaths());
        Assert.DoesNotContain(result.Document.AllGroups(), group => group.SourceId is not null);
        Assert.Empty(result.Missing);
    }

    /// <summary>
    /// **A `use` inside a `symbol`** is resolved when the symbol is used, in the symbol's own frame - the nested
    /// case that a reader resolving one level deep gets wrong. Here the symbol is drawn fifty wide over a ten-unit
    /// view box, so the five-times scale applies to the inner instance as well as to the shape.
    /// </summary>
    [Fact]
    public void AUseInsideASymbolIsDrawnWhereTheSymbolIsUsed()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/>"
            + "<symbol id=\"s\" viewBox=\"0 0 10 10\"><use href=\"#box\"/></symbol></defs>"
            + "<use href=\"#s\" width=\"50\" height=\"50\"/>");

        PathItem path = Assert.Single(result.Document.AllPaths());

        // The inner instance is five times the shape, and the outer instance a further 0.75 pt per user unit.
        Assert.Equal(37.5, At(path, 2).X, 9);
        Assert.Equal(37.5, At(path, 2).Y, 9);
        Assert.Equal(2, result.Document.AllGroups().Count(g => g.SourceId is not null));
    }

    // ---------------------------------------------------------------- width and height mean different things

    /// <summary>
    /// **`width` and `height` on a `use` of a plain shape have no effect**, and that is the specification being
    /// honoured rather than a value being dropped: SVG 2 applies them only to the viewport a `use` establishes for
    /// a `symbol` or an `svg`, so a plain shape is drawn at its own size. Treating them as a scale would be the
    /// substitution - a size the file gave a meaning it does not have.
    /// </summary>
    [Fact]
    public void AWidthOnAUseOfAPlainShapeHasNoEffect()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>"
            + "<use href=\"#box\" width=\"50\" height=\"50\"/>");

        PathItem path = Assert.Single(result.Document.AllPaths());

        // Ten user units across, not fifty: 7.5 pt, and nothing reported because nothing was lost.
        Assert.Equal(7.5, At(path, 2).X, 9);
        Assert.Equal(7.5, At(path, 2).Y, 9);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// **A `use` of an `svg` is sized by the use's own `width` and `height`.** SVG 2 sizes that viewport from the
    /// use and fits the target's view box into it, which is exactly the arithmetic a nested `svg` already uses one
    /// level in - so the value is honoured rather than reported.
    /// </summary>
    [Fact]
    public void AUseOfAnSvgIsSizedByTheUsesOwnWidthAndHeight()
    {
        SvgImportResult result = Read(
            "<defs><svg id=\"port\" width=\"10\" height=\"10\" viewBox=\"0 0 10 10\">" +
            "<rect width=\"10\" height=\"10\"/></svg></defs>" +
            "<use href=\"#port\" x=\"0\" y=\"0\" width=\"50\" height=\"50\"/>");

        PathItem path = Assert.Single(result.Document.AllPaths());

        // The box is ten units square and the use draws it fifty wide, so one unit is five: 50 user units of
        // artboard is 37.5 pt.
        Assert.Equal(37.5, At(path, 2).X, 9);
        Assert.Equal(37.5, At(path, 2).Y, 9);

        // Honoured, so there is nothing left to report.
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// **`width` and `height` on a `use` of an `svg` state the port, and are honoured.**
    ///
    /// This case used to assert the opposite - that the attributes were *reported* as something the reader could
    /// not honour - and it changed meaning when the nested-`svg` reader learned to take the port from the `use`
    /// (issue #117). What it pins now is the half that is easy to get wrong once the size is honoured: an `svg`
    /// target with **no view box** has no fit, so the port is re-established at the use's size while the content
    /// stays in its own user units - twenty over ten stretches nothing, and a reader that treated the size as a
    /// scale would draw this five times too big.
    /// </summary>
    [Fact]
    public void AWidthOnAUseOfAnSvgEstablishesThePort()
    {
        SvgImportResult result = Read(
            "<defs><svg id=\"port\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg></defs>"
            + "<use href=\"#port\" width=\"50\" height=\"50\"/>");

        PathItem path = Assert.Single(result.Document.AllPaths());

        // Ten user units across, not fifty: 7.5 pt, because a port with no view box scales nothing.
        Assert.Equal(7.5, At(path, 2).X, 9);
        Assert.Equal(7.5, At(path, 2).Y, 9);

        // Honoured, so there is nothing left to report.
        Assert.Empty(result.Warnings);
    }

    // ---------------------------------------------------------------- nesting

    [Fact]
    public void AUseCanReferToAnotherUse()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"4\" height=\"4\"/>"
            + "<use id=\"first\" xlink:href=\"#box\" x=\"7\"/></defs>"
            + "<use xlink:href=\"#first\" x=\"100\"/>");

        // Two instance groups: the outer one, and the one nested inside it for the first use. (A third group wraps
        // the whole file, carrying its units into the model's, which is why this counts instances rather than groups.)
        Assert.Equal(2, result.Document.AllGroups().Count(g => g.SourceId is not null));
        Assert.Equal("first", result.Document.AllGroups().First(g => g.SourceId is not null).SourceId);
    }

    // ---------------------------------------------------------------- what is not there

    /// <summary>
    /// **A missing target is reported, not silently dropped.** A file that refers to something it does not contain
    /// is a file the person needs to know about; an instance that quietly vanishes looks like a rendering fault
    /// nobody can explain.
    /// </summary>
    [Fact]
    public void AMissingTargetIsReported()
    {
        SvgImportResult result = Read("<use xlink:href=\"#nothing\"/>");

        Assert.Empty(result.Document.AllPaths());
        Assert.Equal("nothing", Assert.Single(result.Missing));
    }

    /// <summary>A reference that leads back to itself is reported rather than followed, because following it is a
    /// stack overflow rather than a drawing.</summary>
    [Fact]
    public void ACircularReferenceIsReportedRatherThanFollowed()
    {
        SvgImportResult result = Read(
            "<defs><g id=\"a\"><use xlink:href=\"#a\"/></g></defs>" +
            "<use xlink:href=\"#a\"/>");

        Assert.Contains(result.Missing, id => id.Contains("circular", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- the round trip

    /// <summary>
    /// **The link survives the writer.** The model has no separate definition object to point a real `use` at - an
    /// instance group holds the definition's content - so the writer inlines that content and states the id it came
    /// from in `data-source`. Reading that back is what makes the round trip return an instance rather than a plain
    /// copy: before this, the file said `use`, the model held an instance, and reopening the file silently produced
    /// a copy with the link gone and nothing reported, which is exactly the substitution the round trip forbids.
    /// </summary>
    [Fact]
    public void AnInstanceSurvivesTheRoundTripThroughTheWriter()
    {
        SvgImportResult imported = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs><use href=\"#box\" x=\"50\" y=\"30\"/>");

        string svg = SvgWriter.Write(imported.Document);

        // The copy the writer had to write says what it is a copy of...
        Assert.Contains("data-source=\"box\"", svg, StringComparison.Ordinal);

        // ...and re-reading it gives the instance back, in the same place.
        SvgImportResult again = SvgReader.Read(svg);
        ArtGroup instance = Assert.Single(
            again.Document.AllGroups(), group => group.SourceId is not null);
        Assert.Equal("box", instance.SourceId);
        Assert.Empty(again.Missing);

        PathItem path = Assert.Single(again.Document.AllPaths());
        Assert.Equal(37.5, At(path, 0).X, 9);
        Assert.Equal(22.5, At(path, 0).Y, 9);
    }

    // ---------------------------------------------------------------- another document

    /// <summary>
    /// **A `use` that names another document is resolved when the reader has that document.**
    ///
    /// `test-use.svg` in the corpus is exactly this - `xlink:href="test-use-ref.svg#root"`. Before this it
    /// imported as **zero objects**: the reference was reported as missing under its raw href, so the whole
    /// drawing vanished behind a line nobody could act on. The file sits beside the one that names it, so the
    /// reader opens it the way it opens a raster beside a document and reads the element with **that document's**
    /// own stylesheet and assets rather than this one's - `.green` is defined in the other file, so a fill that
    /// arrives green is the proof the other file's stylesheet was the one in force.
    /// </summary>
    [Fact]
    public void AUseOfAnotherDocumentsElementIsResolved()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vccad-use-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "ref.svg"),
                "<svg xmlns=\"http://www.w3.org/2000/svg\" id=\"root\" width=\"100\" height=\"100\" " +
                "viewBox=\"0 0 100 100\"><style>.green { fill: #00ff00; }</style>" +
                "<rect class=\"green\" x=\"10\" y=\"10\" width=\"20\" height=\"20\"/></svg>");

            string main = Path.Combine(directory, "main.svg");
            File.WriteAllText(
                main,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
                "<use xlink:href=\"ref.svg#root\"/></svg>");

            SvgImportResult result = SvgReader.ReadFile(main);

            Assert.Empty(result.Missing);
            Assert.True(result.Objects > 0, "the referenced document's content is drawn");

            PathItem path = Assert.Single(result.Document.AllPaths());
            Assert.Equal(7.5, At(path, 0).X, 9);
            Assert.Equal(7.5, At(path, 0).Y, 9);

            // Green from the *other* document's stylesheet: the importing file defines no `.green`, so a reader
            // that read the target against its own sheet would draw this black.
            Assert.True(path.Fill.IsVisible);
            Assert.Equal(0.0, path.Fill.Color.R, 3);
            Assert.Equal(1.0, path.Fill.Color.G, 3);

            ArtGroup instance = Assert.Single(result.Document.AllGroups(), g => g.SourceId is not null);
            Assert.Equal("root", instance.SourceId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A reference to another document that **cannot** be resolved is reported with the reason, not as the bare
    /// href: a person reading `other.svg#root` in a report has to be able to tell a missing file from a missing
    /// element, or the report sends them looking in the wrong place.
    /// </summary>
    [Fact]
    public void AnExternalReferenceWithNoFileIsReportedWithTheReason()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vccad-use-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string main = Path.Combine(directory, "main.svg");
            File.WriteAllText(
                main,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                "width=\"100\" height=\"100\"><use xlink:href=\"absent.svg#root\"/></svg>");

            SvgImportResult result = SvgReader.ReadFile(main);

            string report = Assert.Single(result.Missing);
            Assert.Contains("absent.svg#root", report, StringComparison.Ordinal);
            Assert.Contains("no file", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A reference to another document with no directory to resolve it against is a gap, not an id.</summary>
    [Fact]
    public void AnExternalReferenceWithNoBaseDirectoryIsReportedWithTheReason()
    {
        SvgImportResult result = Read("<use xlink:href=\"other.svg#root\"/>");

        string report = Assert.Single(result.Missing);
        Assert.Contains("other.svg#root", report, StringComparison.Ordinal);
        Assert.Contains("no directory", report, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the corpus

    /// <summary>
    /// **The corpus's own cross-document `use` resolves.** `test-use.svg` and `test-use-ref.svg` are two real
    /// Inkscape files, the first a two-line document whose only content is a `use` of the second - the exact case
    /// this issue was still open for.
    ///
    /// The counts are asserted rather than "something arrived", because the failure this replaces was **zero
    /// objects** behind one reported href: a reader that resolved the file but read none of it would satisfy an
    /// `Any()` and still draw nothing. The referenced file holds six `rect`s, two `path`s and three `use`s of its
    /// own, all of which travel into the instance.
    /// </summary>
    [Fact]
    public void TheCorpusCrossDocumentUseResolvesItsReference()
    {
        string? file = CorpusFile("test-use.svg");
        if (file is null)
        {
            return;
        }

        SvgImportResult result = SvgReader.ReadFile(file);

        Assert.Empty(result.Missing);

        // One `use` in test-use.svg, three in the document it refers to.
        Assert.Equal(4, result.ByElement.GetValueOrDefault("use"));
        Assert.Equal(6, result.ByElement.GetValueOrDefault("rect"));
        Assert.Equal(8, result.Document.AllPaths().Count());
        Assert.Equal(4, result.Document.AllGroups().Count(group => group.SourceId is not null));

        Assert.Contains(result.Document.AllGroups(), group => group.SourceId == "root");
    }

    /// <summary>
    /// **The real file behind the sizing gap.** `symbol-svg2-geometry-properties.svg` is Inkscape's own test for
    /// sizing a `use` over both a `symbol` and an `svg`, and the `svg` half used to be reported by name as
    /// something this reader could not honour. It is honoured now, so the report must be gone: a reader that still
    /// emitted it would be claiming a gap it no longer has, which is its own kind of wrong answer.
    /// </summary>
    [Fact]
    public void TheCorpusSizesAUseOfAnSvgTarget()
    {
        string? file = CorpusFile("symbol-svg2-geometry-properties.svg");
        if (file is null)
        {
            return;
        }

        SvgImportResult result = SvgReader.ReadFile(file);

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("cannot honour", StringComparison.Ordinal));
        Assert.Empty(result.Missing);
        Assert.True(result.Document.AllPaths().Any(), "the file's own drawing is imported");
    }

    /// <summary>
    /// The corpus files that exist for this feature import, and produce instances rather than nothing. `use` is
    /// used 56 times across the suite, so a reader that dropped it would still import every file and draw the wrong
    /// picture - which is why this counts objects rather than checking for exceptions.
    /// </summary>
    [Fact]
    public void TheCorpusUseFilesProduceInstances()
    {
        string? directory = SvgCorpusDirectory();
        if (directory is null)
        {
            return;
        }

        string[] files = Directory.GetFiles(directory, "*.svg", SearchOption.AllDirectories)
            .Where(path =>
            {
                string text = File.ReadAllText(path);
                return text.Contains("<use", StringComparison.Ordinal);
            })
            .ToArray();

        if (files.Length == 0)
        {
            return;
        }

        int instances = 0;
        foreach (string file in files)
        {
            SvgImportResult result = SvgReader.ReadFile(file);
            instances += result.Document.AllGroups().Count(g => g.SourceId is not null);
        }

        Assert.True(files.Length > 0, "the corpus has files that use `use`");
        Assert.True(instances > 0, $"those files produced instances: {instances} from {files.Length} files");
    }

    /// <summary>A named file in the Inkscape corpus, or null when the corpus is not on this machine.</summary>
    private static string? CorpusFile(string name)
    {
        string? directory = SvgCorpusDirectory();
        if (directory is null)
        {
            return null;
        }

        string path = Path.Combine(directory, name);
        return File.Exists(path) ? path : null;
    }

    private static string? SvgCorpusDirectory()
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        foreach (string directory in Directory.GetDirectories(cache, "inkscape*"))
        {
            foreach (string candidate in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (Directory.GetFiles(candidate, "*.svg").Length > 0)
                    {
                        return candidate;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Unreadable, and not needed.
                }
            }
        }

        return null;
    }
}
