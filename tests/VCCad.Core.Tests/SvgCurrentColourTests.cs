using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// SVG's `currentColor` and the `color` property it stands for - the corner issue #135 needs before any of its
/// asset libraries can be used.
///
/// **What was wrong.** `currentColor` was not read at all: <see cref="SvgColour.Parse"/> returned null for it and
/// every caller fell back to black. A file that said `color: red` and then `fill="currentColor"` came back with a
/// black fill, which looks like a plausible drawing rather than a missing keyword - the worst kind of reader gap.
/// The idiom is the one an asset library is made of: a `symbol`, `marker` or `pattern` whose content follows the
/// colour the place that uses it establishes, which is what makes one definition reusable in several colours.
///
/// **The honouring step.** A definition is read into the library under SVG's initial values, so a `currentColor`
/// inside it resolves to black there. The instance's own copy is read through the `use` site's style and is
/// therefore right - and it is exactly the copy <see cref="RefreshInstancesCommand"/> replaces. So the colour the
/// use site stated has to be recorded on the instance and put back by the rebuild, or a symbol whose art follows
/// the current colour draws black the first time anything is refreshed. That is asserted as the **channel values
/// before and after**, never as "a refresh happened".
/// </summary>
public class SvgCurrentColourTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "width=\"400\" height=\"400\" viewBox=\"0 0 400 400\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static PathItem OnlyShape(CadDocument document) => document.AllPaths().First();

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

    private static (double R, double G, double B) Channels(ColorRgb colour) => (colour.R, colour.G, colour.B);

    // ------------------------------------------------------------------ the keyword itself

    /// <summary>
    /// **`fill="currentColor"` is the `color` in force.** `#3366cc` is 0.2, 0.4, 0.8, and those are the three
    /// numbers asserted: a reader that fell back to black - which is what this did before - gives 0, 0, 0, and one
    /// that read the parent's *fill* instead would be wrong in a way that this file cannot tell apart, so the
    /// parent states a stroke and the child a fill.
    /// </summary>
    [Fact]
    public void AFillOfCurrentColorTakesTheColourInForce()
    {
        CadDocument document = Read(
            "<g color=\"#3366cc\" stroke=\"#000000\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"currentColor\"/></g>").Document;

        PathItem shape = OnlyShape(document);
        Assert.Equal((0.2, 0.4, 0.8), Channels(shape.Fill.Color));
        Assert.True(shape.Fill.FromCurrentColor);
    }

    /// <summary>
    /// **And on a stroke.** The two are read by different methods, so a fix applied to one of them is half a fix -
    /// which is the mistake `stroke-linecap` already taught this reader once.
    /// </summary>
    [Fact]
    public void AStrokeOfCurrentColorTakesTheColourInForce()
    {
        CadDocument document = Read(
            "<g color=\"#3366cc\">" +
            "<path d=\"M 0 0 L 10 0\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/></g>").Document;

        StrokeSpec stroke = OnlyShape(document).Strokes.First(s => s.IsVisible);
        Assert.Equal((0.2, 0.4, 0.8), Channels(stroke.Color));
        Assert.True(stroke.FromCurrentColor);
    }

    /// <summary>
    /// **And on text**, which is the third consumer of the same property and the one an asset library is most
    /// likely to hold: a symbol of a label whose colour comes from the place that uses it. The run's own colour is
    /// asserted rather than the block's, because a run is where the resolved paint lands.
    /// </summary>
    [Fact]
    public void TextFilledWithCurrentColorTakesTheColourInForce()
    {
        CadDocument document = Read(
            "<g color=\"#3366cc\">" +
            "<text x=\"0\" y=\"20\" font-family=\"Face A\" font-size=\"20\" fill=\"currentColor\">red</text></g>")
            .Document;

        TextItem text = document.AllItems().OfType<TextItem>().First();
        Assert.Equal((0.2, 0.4, 0.8), Channels(text.ColourOf(text.Runs[0])));
    }

    /// <summary>
    /// **The nearest declaration wins.** `color` is an ordinary inherited property: the inner group's value is what
    /// a shape under it follows. A reader that looked only at the root, or only at the element itself, would draw
    /// the outer colour here.
    /// </summary>
    [Fact]
    public void TheNearestColourDeclarationWins()
    {
        CadDocument document = Read(
            "<g color=\"#3366cc\"><g color=\"#00ff00\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"currentColor\"/></g></g>").Document;

        Assert.Equal((0.0, 1.0, 0.0), Channels(OnlyShape(document).Fill.Color));
    }

    /// <summary>
    /// **The stylesheet decides it, through the same cascade as every other property.** An `!important` rule beats
    /// the inline style on the element itself, which is the order <see cref="SvgProperties"/> exists to state - a
    /// reader that took the inline `color` here would draw the shape blue where the file says lime.
    /// </summary>
    [Fact]
    public void AnImportantRuleDecidesTheColour()
    {
        CadDocument document = Read(
            "<style>g { color: #00ff00 !important; }</style>" +
            "<g style=\"color:#3366cc\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"currentColor\"/></g>").Document;

        Assert.Equal((0.0, 1.0, 0.0), Channels(OnlyShape(document).Fill.Color));
    }

    /// <summary>
    /// **A paint that does not follow the colour is not marked as one.** The flag is what a rebuild re-resolves, so
    /// setting it on every fill would repaint art that nobody asked to follow anything.
    /// </summary>
    [Fact]
    public void APlainPaintIsNotMarkedAsFollowingTheColour()
    {
        CadDocument document = Read(
            "<g color=\"#3366cc\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"#ff0000\"/></g>").Document;

        PathItem shape = OnlyShape(document);
        Assert.Equal((1.0, 0.0, 0.0), Channels(shape.Fill.Color));
        Assert.False(shape.Fill.FromCurrentColor);
    }

    /// <summary>
    /// **With no `color` in force, `currentColor` is SVG's initial black** - which is what an ordinary document
    /// gets, and what keeps a file that never mentions the property byte-identical.
    /// </summary>
    [Fact]
    public void WithoutAColourTheKeywordIsTheInitialBlack()
    {
        CadDocument document = Read(
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"currentColor\"/>").Document;

        PathItem shape = OnlyShape(document);
        Assert.Equal((0.0, 0.0, 0.0), Channels(shape.Fill.Color));
        Assert.True(shape.Fill.FromCurrentColor);
    }

    /// <summary>
    /// **A gradient stop follows the colour in force where the stop is.** A paint server lives in `defs` and its
    /// `currentColor` is the gradient's own inherited colour - not the referencing shape's, which is a different
    /// question and the one it is easy to answer by accident.
    /// </summary>
    [Fact]
    public void AStopColourOfCurrentColorTakesTheGradientsOwnColour()
    {
        CadDocument document = Read(
            "<defs><linearGradient id=\"ramp\" color=\"#00ff00\">" +
            "<stop offset=\"0\" stop-color=\"currentColor\"/> " +
            "<stop offset=\"1\" stop-color=\"#000000\"/></linearGradient></defs>" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"url(#ramp)\"/>").Document;

        GradientSpec gradient = Assert.IsType<GradientSpec>(OnlyShape(document).Fill.Gradient);
        Assert.Equal((0.0, 1.0, 0.0), Channels(gradient.Stops[0].Color));
    }

    // ------------------------------------------------------------------ the asset-library idiom

    /// <summary>
    /// **A definition whose paint follows the colour is drawn in the colour its `use` establishes.** This is the
    /// whole point of the keyword for the asset libraries: one `symbol`-shaped definition, several uses, each with
    /// its own colour, and the definition states no colour of its own. Both instances are asserted, and so is a
    /// third that states nothing - it stays SVG's initial black, which is what makes this about the use site.
    /// </summary>
    [Fact]
    public void AUseSitesColourReachesADefinitionThatFollowsIt()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\" fill=\"currentColor\"/></defs>" +
            "<use id=\"red\" href=\"#box\" color=\"#ff0000\"/>" +
            "<use id=\"green\" href=\"#box\" x=\"50\" color=\"#00ff00\"/>" +
            "<use id=\"plain\" href=\"#box\" x=\"100\"/>").Document;

        ArtGroup[] instances = Instances(document, "box");
        Assert.Equal(3, instances.Length);

        Assert.Equal((1.0, 0.0, 0.0), Channels(FirstShape(instances[0]).Fill.Color));
        Assert.Equal((0.0, 1.0, 0.0), Channels(FirstShape(instances[1]).Fill.Color));
        Assert.Equal((0.0, 0.0, 0.0), Channels(FirstShape(instances[2]).Fill.Color));
    }

    /// <summary>
    /// **The acceptance: the colour survives a rebuild.** Re-materialisation clones the **definition**, which was
    /// read under SVG's initial values - so its `currentColor` resolved to black there - and the instance's own
    /// copy, which carried the use site's colour, is what the rebuild throws away. Against the old behaviour both
    /// assertions after the refresh read `0,0,0`.
    ///
    /// The second instance is asserted too: the two uses establish different colours over one definition, so a fix
    /// that stamped one document-wide value would pass on the first and fail on the second.
    /// </summary>
    [Fact]
    public void ACurrentColorPaintKeepsTheUseSitesColourAcrossARefresh()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\" fill=\"currentColor\"/></defs>" +
            "<use id=\"red\" href=\"#box\" color=\"#ff0000\"/>" +
            "<use id=\"green\" href=\"#box\" x=\"50\" color=\"#00ff00\"/>").Document;

        ArtGroup[] instances = Instances(document, "box");
        Assert.Equal((1.0, 0.0, 0.0), Channels(FirstShape(instances[0]).Fill.Color));
        Assert.Equal((0.0, 1.0, 0.0), Channels(FirstShape(instances[1]).Fill.Color));

        Refresh(document);

        Assert.Equal((1.0, 0.0, 0.0), Channels(FirstShape(instances[0]).Fill.Color));
        Assert.Equal((0.0, 1.0, 0.0), Channels(FirstShape(instances[1]).Fill.Color));
    }

    /// <summary>
    /// **A nested use inherits the outer one's colour.** A definition that instantiates another is read under the
    /// initial values, so the inner instance recorded no colour - the rebuild has to carry it down, exactly as it
    /// carries the fill.
    ///
    /// **The inner use states a paint of its own and no colour**, which is what makes this about the *colour*
    /// rather than about a null presentation: an inner use that said nothing at all records nothing, and the
    /// composition returns the outer presentation without ever consulting the colour member. Saying
    /// `fill="currentColor"` gives the inner instance a presentation to compose, so the assertion fails if the
    /// colour half of the composition is missing.
    /// </summary>
    [Fact]
    public void ANestedUseInheritsTheOuterColourAcrossARefresh()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"4\" height=\"4\" fill=\"currentColor\"/>" +
            "<g id=\"first\"><use href=\"#box\" x=\"7\" fill=\"currentColor\"/></g></defs>" +
            "<use href=\"#first\" x=\"100\" color=\"#ff0000\"/>").Document;

        Assert.Equal((1.0, 0.0, 0.0), Channels(FirstShape(Instances(document, "first").Single()).Fill.Color));

        Refresh(document);

        Assert.Equal((1.0, 0.0, 0.0), Channels(FirstShape(Instances(document, "first").Single()).Fill.Color));
    }

    /// <summary>
    /// **An unstyled `use` records nothing.** SVG's initial black is not a decision somebody made, so an instance
    /// that establishes it grows no member - which is what keeps an ordinary document's bytes where they were.
    /// </summary>
    [Fact]
    public void AnUnstyledUseRecordsNoPresentation()
    {
        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\" fill=\"currentColor\"/></defs>" +
            "<use href=\"#box\"/>").Document;

        Assert.Null(Assert.Single(Instances(document, "box")).InstancePresentation);
    }

    // ------------------------------------------------------------------ both round trips

    /// <summary>
    /// **The keyword survives the SVG round trip, and so does the colour it follows.** The writer states
    /// `currentColor` where the paint follows it and the colour beside it, because writing the resolved colour
    /// alone would keep the picture and lose the fact - and the document that came back would then lose the colour
    /// the moment it was refreshed.
    /// </summary>
    [Fact]
    public void TheKeywordAndTheColourSurviveTheSvgRoundTripAndARefresh()
    {
        SvgImportResult imported = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\" fill=\"currentColor\"/></defs>" +
            "<use id=\"red\" href=\"#box\" color=\"#ff0000\"/>");

        string svg = SvgWriter.Write(imported.Document);
        Assert.Contains("currentColor", svg, StringComparison.Ordinal);

        SvgImportResult again = SvgReader.Read(svg);
        Assert.Empty(again.Missing);

        // Deterministic: writing the document that came back gives the same file, so the keyword is not a value
        // that doubles up or disappears on the second pass.
        Assert.Equal(svg, SvgWriter.Write(again.Document));

        ArtGroup instance = Assert.Single(Instances(again.Document, "box"));
        Assert.Equal((1.0, 0.0, 0.0), Channels(FirstShape(instance).Fill.Color));

        Refresh(again.Document);

        Assert.Equal(
            (1.0, 0.0, 0.0), Channels(FirstShape(Instances(again.Document, "box").Single()).Fill.Color));
    }

    /// <summary>
    /// **An ordinary document writes neither the keyword nor the property it stands for.** The absent-at-default
    /// rule, on the writer: a file that never mentions `currentColor` must not grow a `color` attribute, because
    /// that changes the bytes of every document the editor saves rather than only the ones that asked for it. The
    /// fill really is written, so the test cannot pass on a writer that emitted nothing at all.
    /// </summary>
    [Fact]
    public void AnOrdinaryDocumentWritesNeitherTheKeywordNorTheColourProperty()
    {
        SvgImportResult imported = Read(
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"#ff0000\"/>");

        string svg = SvgWriter.Write(imported.Document);

        Assert.Contains("fill=\"#ff0000\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("currentColor", svg, StringComparison.Ordinal);

        // `color="` with something before it that is not part of a hyphenated property, so `stop-color` - a
        // different property this writer also emits - does not satisfy the pattern.
        Assert.DoesNotMatch("(?<![\\w-])color=\"", svg);
    }

    /// <summary>
    /// **And the sidecar.** The flag is document state and round-trips through this repository's own format, while
    /// an ordinary document writes no member at all, which is the byte-identical rule every optional member here
    /// follows.
    ///
    /// **The ordinary document has paint in it.** An empty document has no fill for the writer to visit, so it
    /// would satisfy the rule vacuously and a member written on every real fill would go unnoticed. The second
    /// document is one ordinary filled path, and its fill is read back to prove the writer really wrote one.
    /// </summary>
    [Fact]
    public void TheFlagSurvivesTheSidecarAndAnOrdinaryDocumentWritesNoMember()
    {
        CadDocument plain = CadDocument.CreateDefault();
        Assert.DoesNotContain(
            "fromCurrentColor", VccadDocumentSerializer.Serialize(plain), StringComparison.OrdinalIgnoreCase);

        CadDocument painted = Read("<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"#ff0000\"/>").Document;
        string ordinary = VccadDocumentSerializer.Serialize(painted);
        Assert.Equal(
            1.0, VccadDocumentSerializer.Deserialize(ordinary).AllPaths().First().Fill.Color.R, 6);
        Assert.DoesNotContain("fromCurrentColor", ordinary, StringComparison.OrdinalIgnoreCase);

        CadDocument document = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\" fill=\"currentColor\"/></defs>" +
            "<use id=\"red\" href=\"#box\" color=\"#ff0000\"/>").Document;

        string json = VccadDocumentSerializer.Serialize(document);
        Assert.Contains("fromCurrentColor", json, StringComparison.OrdinalIgnoreCase);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(json);
        Assert.Equal(json, VccadDocumentSerializer.Serialize(reloaded));

        ArtGroup instance = Assert.Single(Instances(reloaded, "box"));
        Assert.Equal((1.0, 0.0, 0.0), Channels(FirstShape(instance).Fill.Color));

        Refresh(reloaded);

        Assert.Equal(
            (1.0, 0.0, 0.0), Channels(FirstShape(Instances(reloaded, "box").Single()).Fill.Color));
    }
}
