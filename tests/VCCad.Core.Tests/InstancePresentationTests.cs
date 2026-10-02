using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The presentation a `use` site establishes, and the half of issue #117 that was still open: it has to **survive a
/// re-resolution**.
///
/// `ca8dc40` made an edit to a definition reach every instance, and the instance's content is rebuilt from the
/// **definition** - which the reader read under SVG's initial values. The instance's own copy is what carried the
/// use site's paint, so refreshing threw it away: measured before this work as `R=1,G=0,B=0` becoming `0,0,0` for a
/// `use fill="red"` over a definition that states no fill. These tests assert the **colour values**, before and
/// after the refresh, because "a refresh happened" is not the requirement - the drawing staying the drawing is.
/// </summary>
public class InstancePresentationTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "width=\"400\" height=\"400\" viewBox=\"0 0 400 400\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static ArtGroup[] Instances(CadDocument document, string source)
        => document.AllGroups().Where(group => group.SourceId == source).ToArray();

    private static PathItem FirstShape(ArtGroup instance)
        => Deep(instance.Children).OfType<PathItem>().First();

    private static IEnumerable<LayerItem> Deep(IEnumerable<LayerItem> items)
    {
        foreach (LayerItem item in items)
        {
            yield return item;

            if (item is ArtGroup group)
            {
                foreach (LayerItem nested in Deep(group.Children))
                {
                    yield return nested;
                }
            }
        }
    }

    private static void Refresh(CadDocument document) => new RefreshInstancesCommand(document).Do();

    /// <summary>
    /// **Text takes the use site's paint, before and after a refresh.**
    ///
    /// The repaint walk handled paths and groups and had no case for text, so a `use` stating a fill over a
    /// definition containing a `<text>` left the text at SVG's initial black while the shapes beside it took the
    /// use site's colour - a drawing that disagreed with itself, and only for text. This is the block's colour;
    /// a run's own colour is a separate member and is deliberately not touched here.
    /// </summary>
    [Fact]
    public void ATextInsideAnInstanceTakesTheUseSitesPaint()
    {
        SvgImportResult result = Read(
            "<defs><text id=\"label\" x=\"0\" y=\"10\" font-size=\"10\">hi</text></defs>" +
            "<use xlink:href=\"#label\" fill=\"#ff0000\"/>");

        TextItem text = Deep(Instances(result.Document, "label").Single().Children).OfType<TextItem>().Single();

        Assert.Equal(1.0, text.Color.R, 9);
        Assert.Equal(0.0, text.Color.G, 9);

        Refresh(result.Document);

        TextItem after = Deep(Instances(result.Document, "label").Single().Children).OfType<TextItem>().Single();

        Assert.Equal(1.0, after.Color.R, 9);
        Assert.Equal(0.0, after.Color.G, 9);
    }

    /// <summary>
    /// **A marker the reader placed survives a refresh.** `SvgMarkers` turns a marker into real artwork when the
    /// file is read - it is placed into the geometry rather than kept as a live property on the path - so the
    /// question an instance raises is whether that artwork is still there once the instance is rebuilt from the
    /// definition. A refresh is supposed to change nothing about the drawing; losing the arrow would change it.
    ///
    /// This is the residue #117 recorded but never pinned: the comment that closed its other named gaps does not
    /// cover markers, and no marker test refreshes anything.
    /// </summary>
    [Fact]
    public void AMarkerTheReaderPlacedSurvivesARefresh()
    {
        SvgImportResult result = Read(
            "<defs>" +
            "<marker id=\"arrow\" markerWidth=\"10\" markerHeight=\"10\" refX=\"5\" refY=\"5\">" +
            "<path d=\"M0,0 L10,5 L0,10 z\" fill=\"#ff0000\"/></marker>" +
            "<path id=\"line\" d=\"M0,0 L100,0\" stroke=\"#0000ff\" marker-end=\"url(#arrow)\"/>" +
            "</defs>" +
            "<use xlink:href=\"#line\"/>");

        ArtGroup instance = Instances(result.Document, "line").Single();

        // The marker became artwork at import, so it is a shape with the marker's own fill.
        Assert.Contains(
            Deep(instance.Children).OfType<PathItem>(),
            shape => shape.Fill.Color.R == 1 && shape.Fill.Color.G == 0 && shape.Fill.Color.B == 0);

        Refresh(result.Document);

        ArtGroup refreshed = Instances(result.Document, "line").Single();

        Assert.Contains(
            Deep(refreshed.Children).OfType<PathItem>(),
            shape => shape.Fill.Color.R == 1 && shape.Fill.Color.G == 0 && shape.Fill.Color.B == 0);
    }

    // ------------------------------------------------------------------ the gap

    /// <summary>
    /// **The acceptance.** A `use` states a paint, the definition does not, and the paint is still there after the
    /// instance has been rebuilt from the definition. The second instance states nothing and stays with SVG's
    /// initial black, which is what makes this a test about the use site rather than about a document-wide repaint.
    ///
    /// Against the old behaviour the two `after` assertions read `0,0,0`: materialisation cloned the definition,
    /// whose content was read under the initial values, and nothing put the use's own presentation back.
    /// </summary>
    [Fact]
    public void ARefreshKeepsThePaintTheUseSiteStated()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use id=\"red\" href=\"#box\" fill=\"red\"/>" +
            "<use id=\"plain\" href=\"#box\" x=\"50\"/>").Document;

        ArtGroup[] instances = Instances(document, "box");
        Assert.Equal(2, instances.Length);

        // The import half already worked, and is asserted so the test cannot pass on an importer that never had it.
        Assert.Equal(1.0, FirstShape(instances[0]).Fill.Color.R, 6);
        Assert.Equal(0.0, FirstShape(instances[0]).Fill.Color.G, 6);
        Assert.Equal(0.0, FirstShape(instances[0]).Fill.Color.B, 6);

        Refresh(document);

        PathItem red = FirstShape(instances[0]);
        Assert.Equal(1.0, red.Fill.Color.R, 6);
        Assert.Equal(0.0, red.Fill.Color.G, 6);
        Assert.Equal(0.0, red.Fill.Color.B, 6);

        // The stated presentation is recorded on the instance, and only there: the plain use stated nothing.
        Assert.NotNull(instances[0].InstancePresentation);
        Assert.Null(instances[1].InstancePresentation);

        // And the instance that stated nothing is still the initial black, not the black of a definition that
        // happened to be rebuilt.
        PathItem plain = FirstShape(instances[1]);
        Assert.Equal(0.0, plain.Fill.Color.R, 6);
        Assert.Equal(0.0, plain.Fill.Color.G, 6);
        Assert.Equal(0.0, plain.Fill.Color.B, 6);
    }

    /// <summary>
    /// **The corpus shape `selector-important-003.svg` is made of**: a stylesheet states the paint, with an
    /// `!important` rule that beats a type selector and a type selector that colours the use that states nothing
    /// itself. Each instance draws with its own colour, and every one of them has to come back the same from a
    /// refresh.
    /// </summary>
    [Fact]
    public void EachUseSiteKeepsItsOwnPaintAcrossARefresh()
    {
        CadDocument document = Read(
            "<style>#a { fill: red !important; } use { fill: blue; }</style>" +
            "<defs><rect id=\"MyRect\" width=\"10\" height=\"10\"/></defs>" +
            "<use id=\"a\" href=\"#MyRect\" x=\"10\"/>" +
            "<use id=\"b\" href=\"#MyRect\" x=\"60\"/>").Document;

        ArtGroup[] instances = Instances(document, "MyRect");
        Assert.Equal(2, instances.Length);

        (double R, double G, double B) Before(int index)
        {
            ColorRgb colour = FirstShape(instances[index]).Fill.Color;
            return (colour.R, colour.G, colour.B);
        }

        (double R, double G, double B) expectedA = (1.0, 0.0, 0.0);
        (double R, double G, double B) expectedB = (0.0, 0.0, 1.0);
        Assert.Equal(expectedA, Before(0));
        Assert.Equal(expectedB, Before(1));

        Refresh(document);

        Assert.Equal(expectedA, Before(0));
        Assert.Equal(expectedB, Before(1));
    }

    /// <summary>
    /// **A declaration inside the definition beats the use site.** SVG's `use` establishes the shadow tree's
    /// *inherited* style; it does not paint over a property the target states for itself. A repaint that simply
    /// stamped the use's fill on everything would get this wrong, so it is pinned here.
    /// </summary>
    [Fact]
    public void APaintTheDefinitionStatesForItselfIsNotOverriddenByTheUseSite()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\" fill=\"lime\"/></defs>" +
            "<use href=\"#box\" fill=\"red\"/>").Document;

        PathItem shape = FirstShape(Assert.Single(Instances(document, "box")));
        Assert.Equal(1.0, shape.Fill.Color.G, 6);

        Refresh(document);

        // The definition's own `fill="lime"` is still what it draws.
        Assert.Equal(0.0, FirstShape(Assert.Single(Instances(document, "box"))).Fill.Color.R, 6);
        Assert.Equal(1.0, FirstShape(Assert.Single(Instances(document, "box"))).Fill.Color.G, 6);
    }

    /// <summary>
    /// **A use inside a definition inherits the outer use's paint.** The definitions library is read under SVG's
    /// initial values, so the inner `use` records no outer cascade - the rebuilt chain has to carry it down, while
    /// still letting the inner use's own declaration win. Both halves are asserted on the innermost shape.
    /// </summary>
    [Fact]
    public void ANestedUseInheritsTheOuterUseSitesPaintAcrossARefresh()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"4\" height=\"4\"/>" +
            "<g id=\"first\"><use href=\"#box\" x=\"7\"/></g></defs>" +
            "<use href=\"#first\" x=\"100\" fill=\"red\"/>").Document;

        ArtGroup outer = Assert.Single(Instances(document, "first"));
        Assert.Equal(1.0, FirstShape(outer).Fill.Color.R, 6);

        Refresh(document);

        PathItem inner = FirstShape(Instances(document, "first").Single());
        Assert.Equal(1.0, inner.Fill.Color.R, 6);
        Assert.Equal(0.0, inner.Fill.Color.G, 6);
        Assert.Equal(0.0, inner.Fill.Color.B, 6);
    }

    /// <summary>
    /// **The stroke travels with the fill.** The recorded presentation is the use site's whole inherited style, not
    /// just the fill: a `use` that states a stroke has to keep it, width and colour, or the member is half a fix.
    /// </summary>
    [Fact]
    public void AStrokeTheUseSiteStatesSurvivesARefresh()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use href=\"#box\" fill=\"none\" stroke=\"blue\" stroke-width=\"3\"/>").Document;

        Refresh(document);

        PathItem shape = FirstShape(Assert.Single(Instances(document, "box")));
        Assert.True(shape.Stroke.IsVisible);
        Assert.Equal(0.0, shape.Stroke.Color.R, 6);
        Assert.Equal(0.0, shape.Stroke.Color.G, 6);
        Assert.Equal(1.0, shape.Stroke.Color.B, 6);
        Assert.Equal(3.0, shape.Stroke.Width, 6);
        Assert.False(shape.Fill.IsVisible);
    }

    // ------------------------------------------------------------------ both round trips

    /// <summary>
    /// **The paint survives the SVG round trip and then a refresh.** The writer states the presentation beside
    /// `data-source`, because the model has no `<use>` to state it on; the reader puts it back. Without that, a
    /// document saved and reopened would revert to the definition's paint the first time it was refreshed - the
    /// loss this issue is about, one save later.
    /// </summary>
    [Fact]
    public void TheUseSitePaintSurvivesTheSvgRoundTripAndARefresh()
    {
        SvgImportResult imported = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use href=\"#box\" fill=\"red\"/>");

        string svg = SvgWriter.Write(imported.Document);
        Assert.Contains("data-source=\"box\"", svg, StringComparison.Ordinal);

        SvgImportResult again = SvgReader.Read(svg);
        Assert.Empty(again.Missing);

        ArtGroup instance = Assert.Single(Instances(again.Document, "box"));
        Assert.NotNull(instance.InstancePresentation);

        Refresh(again.Document);

        PathItem shape = FirstShape(Instances(again.Document, "box").Single());
        Assert.Equal(1.0, shape.Fill.Color.R, 6);
        Assert.Equal(0.0, shape.Fill.Color.G, 6);
        Assert.Equal(0.0, shape.Fill.Color.B, 6);
    }

    /// <summary>
    /// **And the sidecar.** The presentation is document state: it round-trips through this repository's own format,
    /// and an ordinary document - no instances, or instances that state nothing - writes no member at all, which is
    /// the byte-identical rule every optional member here follows.
    /// </summary>
    [Fact]
    public void TheUseSitePaintSurvivesTheSidecarAndAnOrdinaryDocumentWritesNoMember()
    {
        CadDocument plain = CadDocument.CreateDefault();
        string ordinary = VccadDocumentSerializer.Serialize(plain);
        Assert.DoesNotContain("instancePresentation", ordinary, StringComparison.OrdinalIgnoreCase);

        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use href=\"#box\" fill=\"red\"/>").Document;

        string json = VccadDocumentSerializer.Serialize(document);
        Assert.Contains("instancePresentation", json, StringComparison.OrdinalIgnoreCase);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(json);
        ArtGroup instance = Assert.Single(Instances(reloaded, "box"));
        Assert.NotNull(instance.InstancePresentation);
        Assert.Equal(1.0, instance.InstancePresentation!.Fill.Color.R, 6);

        // Deterministic, and still there after a refresh on the reloaded document.
        Assert.Equal(json, VccadDocumentSerializer.Serialize(reloaded));
        Refresh(reloaded);
        Assert.Equal(1.0, FirstShape(Instances(reloaded, "box").Single()).Fill.Color.R, 6);
    }

    /// <summary>
    /// **An unstyled `use` records nothing**, so a document whose uses state no paint does not grow the member and
    /// keeps the bytes it had. SVG's initial values are not a decision somebody made.
    /// </summary>
    [Fact]
    public void AnUnstyledUseRecordsNoPresentation()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use href=\"#box\"/>").Document;

        Assert.Null(Assert.Single(Instances(document, "box")).InstancePresentation);
        Assert.DoesNotContain(
            "instancePresentation",
            VccadDocumentSerializer.Serialize(document),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Duplicating an instance keeps what its use site said, or the copy draws the definition's paint.</summary>
    [Fact]
    public void ACloneOfAnInstanceCarriesThePresentation()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use href=\"#box\" fill=\"red\"/>").Document;

        var copy = (ArtGroup)Assert.Single(Instances(document, "box")).Clone();
        Assert.NotNull(copy.InstancePresentation);
        Assert.Equal(1.0, copy.InstancePresentation!.Fill.Color.R, 6);
    }

    // ------------------------------------------------------------------ the real file

    /// <summary>
    /// **`selector-important-003.svg`, the file the issue names.** Its five `use` elements each draw with their own
    /// paint - some from the stylesheet, some from an inline style - over one definition that states none. Every
    /// one of those colours has to come back identical from a refresh; the assertion is the **channel values**, and
    /// it is deliberately an equality between the two readings rather than a table of expected colours, because
    /// what this file proves is that nothing in it moves.
    ///
    /// Skips when the Inkscape corpus is not checked out, which is the rule for every corpus-backed test here.
    /// </summary>
    [Fact]
    public void TheSelectorImportantCorpusFileKeepsEveryUseSitesPaintAcrossARefresh()
    {
        string? path = Corpus("selector-important-003.svg");
        if (path is null)
        {
            return;
        }

        SvgImportResult imported = SvgReader.ReadFile(path);
        CadDocument document = imported.Document;
        Assert.Empty(imported.Missing);
        Assert.NotNull(document.FindDefinition("MyRect"));

        ArtGroup[] instances = Instances(document, "MyRect");
        Assert.Equal(5, instances.Length);
        Assert.Empty(document.MissingDefinitions());

        ColorRgb[] before = instances.Select(instance => FirstShape(instance).Fill.Color).ToArray();

        // The file states a paint on its uses, so an all-black reading is the importer having lost it - and this
        // test would then pass for the wrong reason on an invariance that never had anything to preserve.
        Assert.Contains(before, colour => colour.R > 0.9 || colour.B > 0.9);

        Refresh(document);

        ColorRgb[] after = Instances(document, "MyRect")
            .Select(instance => FirstShape(instance).Fill.Color)
            .ToArray();

        Assert.Equal(before, after);
    }

    private static string? Corpus(string name)
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        foreach (string directory in Directory.GetDirectories(cache, "inkscape*"))
        {
            try
            {
                string? found = Directory.GetFiles(directory, name, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unreadable, and not needed.
            }
        }

        return null;
    }
}
