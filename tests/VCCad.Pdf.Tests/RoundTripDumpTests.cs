using VCCad.Core.Model;
using VCCad.Core.Samples;
using VCCad.Core.Serialization;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Saving and reloading must return the document exactly as it was. The model dump is how
/// that is checked rather than assumed: it is exhaustive, order-stable and comparable, so
/// a lost field shows as the first line that disagrees.
///
/// It has already earned its keep. On the LILLIE pattern it found two fields going missing
/// through a round trip — the embedded font programme with its original glyph codes, and
/// the paragraph style (frame width, leading and paragraph spacing) — neither of which a
/// "does it load?" test would have noticed.
/// </summary>
public class RoundTripDumpTests
{
    private const string Lillie = "3464_LILLIE_View_A_Sides_color.pdf";

    private static string? SamplePath(string fileName)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string? candidate = SampleLibrary.Find(fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static CadDocument Reload(CadDocument document)
        => VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

    [Fact]
    public void TheRealPatternRoundTripsExactly()
    {
        string? path = SamplePath(Lillie);
        if (path is null)
        {
            return; // sample not present in this checkout: skip cleanly
        }

        CadDocument document = PdfImporter.Import(File.ReadAllBytes(path));
        string before = ModelDump.Of(document);
        string after = ModelDump.Of(Reload(document));

        Assert.True(before == after, FirstDifference(before, after));
    }

    [Fact]
    public void AnEmbeddedFontSurvivesTheRoundTrip()
    {
        string? path = SamplePath(Lillie);
        if (path is null)
        {
            return;
        }

        CadDocument document = PdfImporter.Import(File.ReadAllBytes(path));
        TextRun run = document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => Walk(l.Children))
            .OfType<TextItem>()
            .SelectMany(t => t.Runs)
            .First(r => r.EmbeddedFont is not null && r.RawCodes is { Length: > 0 });

        string programBefore = Convert.ToBase64String(run.EmbeddedFont!.Program);
        string codesBefore = run.RawCodes!;

        CadDocument reloaded = Reload(document);
        TextRun after = reloaded.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => Walk(l.Children))
            .OfType<TextItem>()
            .SelectMany(t => t.Runs)
            .First(r => ReferenceEquals(r.EmbeddedFont, null) is false &&
                        r.EmbeddedFont.FamilyName == run.EmbeddedFont.FamilyName);

        Assert.NotNull(after.EmbeddedFont);
        Assert.Equal(programBefore, Convert.ToBase64String(after.EmbeddedFont!.Program));
        Assert.Equal(codesBefore, after.RawCodes);
        Assert.Equal(run.EmbeddedFont.FamilyName, after.EmbeddedFont.FamilyName);
        Assert.Equal(run.EmbeddedFont.BaseFont, after.EmbeddedFont.BaseFont);
        Assert.Equal(run.EmbeddedFont.Widths, after.EmbeddedFont.Widths);
        Assert.Equal(run.EmbeddedFont.DescendantSubtype, after.EmbeddedFont.DescendantSubtype);
    }

    [Fact]
    public void ParagraphStyleSurvivesTheRoundTrip()
    {
        CadDocument document = CadDocument.CreateDefault("Style");
        var text = new TextItem
        {
            FrameWidth = 180,
            LineSpacing = 1.45,
            ParagraphSpacing = 7.5,
        };
        text.Runs.Add(new TextRun { Text = "one\ntwo", FontFamily = "Helvetica", FontSize = 11 });
        document.Artboards[0].Layers[0].AddItem(text);

        var after = (TextItem)Reload(document).Artboards[0].Layers[0].Children[0];

        Assert.Equal(180, after.FrameWidth, 6);
        Assert.Equal(1.45, after.LineSpacing, 6);
        Assert.Equal(7.5, after.ParagraphSpacing, 6);
    }

    [Fact]
    public void TheDumpIsStableAcrossTwoRuns()
    {
        CadDocument document = CadDocument.CreateDefault("Stable");
        document.Artboards[0].Layers[0].AddItem(
            PathFactory.CreateRectangle("r", new VCCad.Geometry.Rect2D(1, 2, 3, 4)));

        Assert.Equal(ModelDump.Of(document), ModelDump.Of(document));
    }

    [Fact]
    public void TheDumpNoticesAChangedValue()
    {
        CadDocument a = CadDocument.CreateDefault("A");
        CadDocument b = CadDocument.CreateDefault("A");
        var textA = new TextItem { Origin = new VCCad.Geometry.Point2D(0, 0) };
        textA.Runs.Add(new TextRun { Text = "hello", FontFamily = "Helvetica", FontSize = 10 });
        var textB = new TextItem { Origin = new VCCad.Geometry.Point2D(0.5, 0) };
        textB.Runs.Add(new TextRun { Text = "hello", FontFamily = "Helvetica", FontSize = 10 });
        a.Artboards[0].Layers[0].AddItem(textA);
        b.Artboards[0].Layers[0].AddItem(textB);

        Assert.NotEqual(ModelDump.Of(a), ModelDump.Of(b));
    }

    // ------------------------------------------------------------------
    // Filters are part of the document, so the dump has to see them (issue #154).
    //
    // Every assertion below is on a dump that changed for the reason named, built from **one** document so its ids
    // are the same on both sides: two documents with different ids differ whatever the filters do, and a test that
    // built two could pass without the dump carrying a filter at all.
    // ------------------------------------------------------------------

    private static PathItem Rectangle()
        => PathFactory.CreateRectangle("r", new VCCad.Geometry.Rect2D(10, 10, 40, 20));

    /// <summary>A document holding one filter and one item that refers to it.</summary>
    private static CadDocument WithFilter(double radius = 2.0)
    {
        CadDocument document = CadDocument.CreateDefault("Filtered");
        PathItem path = Rectangle();
        path.FilterId = "soft";
        document.Artboards[0].Layers[0].AddItem(path);
        document.AddFilter(new FilterSpec("soft", new[] { FilterPrimitive.Blur(radius, "SourceAlpha") }));
        return document;
    }

    /// <summary>
    /// **A filter added to the document changes the dump.** It could not before: the dump held no filter at all, so
    /// the whole asset was invisible to every round-trip test built on it.
    /// </summary>
    [Fact]
    public void AddingAFilterChangesTheDump()
    {
        CadDocument document = CadDocument.CreateDefault("Bare");
        document.Artboards[0].Layers[0].AddItem(Rectangle());

        string before = ModelDump.Of(document);
        document.AddFilter(new FilterSpec("soft", new[] { FilterPrimitive.Blur(2.0, "SourceAlpha") }));

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains("name=soft", after, StringComparison.Ordinal);
        Assert.DoesNotContain("name=soft", before, StringComparison.Ordinal);
    }

    /// <summary>**Editing a filter's numbers changes the dump**, so a round trip that lost a radius is caught.</summary>
    [Fact]
    public void EditingAFilterChangesTheDump()
    {
        CadDocument document = WithFilter(radius: 2.0);

        string before = ModelDump.Of(document);
        document.AddFilter(new FilterSpec("soft", new[] { FilterPrimitive.Blur(5.0, "SourceAlpha") }));

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains("radius=2", before, StringComparison.Ordinal);
        Assert.Contains("radius=5", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The graph's own wiring changes the dump, not only its numbers.** A filter is a directed graph, so a result
    /// name is what a later step reads by and two filters that agree on every radius but disagree on a name draw
    /// different pictures.
    /// </summary>
    [Fact]
    public void EditingAFiltersWiringChangesTheDump()
    {
        CadDocument document = WithFilter();

        string before = ModelDump.Of(document);
        document.AddFilter(new FilterSpec("soft", new[] { FilterPrimitive.Blur(2.0, "SourceAlpha", "blurred") }));

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains("result=-", before, StringComparison.Ordinal);
        Assert.Contains("result=blurred", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// **An item naming a filter changes the dump.** The reference is document state on the item, and a round trip
    /// that dropped it would draw the shape unfiltered with nothing in the dump to show it.
    /// </summary>
    [Fact]
    public void ReferencingAFilterChangesTheDump()
    {
        CadDocument document = CadDocument.CreateDefault("Reference");
        PathItem path = Rectangle();
        document.Artboards[0].Layers[0].AddItem(path);
        document.AddFilter(new FilterSpec("soft", new[] { FilterPrimitive.Blur(2.0, "SourceAlpha") }));

        string before = ModelDump.Of(document);
        path.FilterId = "soft";

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains("filterId=soft", after, StringComparison.Ordinal);
    }

    /// <summary>**Removing a filter changes the dump**, so a delete that silently did nothing is caught.</summary>
    [Fact]
    public void RemovingAFilterChangesTheDump()
    {
        CadDocument document = WithFilter();

        string before = ModelDump.Of(document);
        Assert.True(document.RemoveFilter("soft"));

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains("filters=1", before, StringComparison.Ordinal);
        Assert.DoesNotContain("filters=1", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The round trip the harness could not previously witness.** A filter and the item that names it survive a
    /// save and reload, asserted on the reloaded **model** - the filter's primitive and the reference - and the
    /// dumps agree. Before this change the dump comparison passed because neither side held a filter, which is the
    /// false green the issue was filed from.
    /// </summary>
    [Fact]
    public void AFilterAndItsReferenceSurviveTheRoundTrip()
    {
        CadDocument document = WithFilter(radius: 3.5);
        string before = ModelDump.Of(document);

        CadDocument reloaded = Reload(document);
        string after = ModelDump.Of(reloaded);

        Assert.True(before == after, FirstDifference(before, after));

        Assert.Equal("soft", reloaded.AllPaths().Single().FilterId);
        FilterSpec filter = Assert.Single(reloaded.Filters);
        Assert.Equal("soft", filter.Name);
        FilterPrimitive primitive = Assert.Single(filter.Primitives);
        Assert.Equal(FilterPrimitiveKind.GaussianBlur, primitive.Kind);
        Assert.Equal(3.5, primitive.Radius, 6);
        Assert.Equal("SourceAlpha", primitive.Input);
    }

    // ------------------------------------------------------------------
    // The document's remaining assets, and the per-item blend mode (issue #187).
    //
    // Same rule as the filter section above: the dump is the thing the round-trip tests compare, so a member it
    // does not print cannot fail a round trip. Every assertion is on the specific text for the member and the value
    // that was set, never merely on the two dumps being unequal - an inequality can pass for the wrong reason, which
    // is how the vacuous tests this replaces worked.
    // ------------------------------------------------------------------

    /// <summary>A named width profile with a taper, a cubic point and a one-sided point - all of them content.</summary>
    private static WidthProfileSpec TaperProfile()
        => new("Taper", new[]
        {
            WidthPoint.Even(0.0, 4.0),
            new WidthPoint(0.25, 6.0, 2.0, WidthInterpolation.Cubic),
            WidthPoint.Even(1.0, 4.0),
        });

    /// <summary>**A width profile changes the dump, and the dump names its points.** Before this the whole asset was
    /// invisible, so a save that lost a profile's taper still compared equal.</summary>
    [Fact]
    public void AWidthProfileChangesTheDump()
    {
        CadDocument document = CadDocument.CreateDefault("Bare");
        document.Artboards[0].Layers[0].AddItem(Rectangle());

        string before = ModelDump.Of(document);
        document.AddWidthProfile(TaperProfile());

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains("profile 0 name=Taper points=3", after, StringComparison.Ordinal);
        Assert.Contains("left=6 right=2 interpolation=Cubic", after, StringComparison.Ordinal);
        Assert.DoesNotContain("name=Taper", before, StringComparison.Ordinal);
    }

    /// <summary>**A brush changes the dump, and the dump names its kind and its nib.** A calligraphic brush's angle
    /// and roundness are what it draws with, so a round trip that reset them is caught here.</summary>
    [Fact]
    public void ABrushChangesTheDump()
    {
        CadDocument document = CadDocument.CreateDefault("Bare");
        document.Artboards[0].Layers[0].AddItem(Rectangle());

        string before = ModelDump.Of(document);
        document.AddBrush(BrushSpec.Calligraphic("Ink", 45.0, 0.5, 8.0));

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains(
            "brush 0 name=Ink kind=Calligraphic angle=45 roundness=0.5 diameter=8",
            after,
            StringComparison.Ordinal);
        Assert.DoesNotContain("name=Ink", before, StringComparison.Ordinal);
    }

    /// <summary>**A definition changes the dump, and its own artwork is printed inside it** - a count alone would
    /// let a definition whose content changed compare equal.</summary>
    [Fact]
    public void ADefinitionChangesTheDump()
    {
        CadDocument document = CadDocument.CreateDefault("Bare");
        document.Artboards[0].Layers[0].AddItem(Rectangle());

        string before = ModelDump.Of(document);
        ArtGroup entry = document.AddDefinition("box");
        entry.AddItem(PathFactory.CreateRectangle("entry-path", new VCCad.Geometry.Rect2D(3, 3, 6, 6)));

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains("definitions=1", after, StringComparison.Ordinal);

        string block = after[after.IndexOf("definitions=1", StringComparison.Ordinal)..];
        Assert.Contains("name=box", block, StringComparison.Ordinal);
        Assert.Contains("name=entry-path", block, StringComparison.Ordinal);
    }

    /// <summary>**A root-level foreign element changes the dump.** Verbatim XML is what keeps an Inkscape named view
    /// from being silently dropped, so the dump has to hold it.</summary>
    [Fact]
    public void AnSvgExtraChangesTheDump()
    {
        CadDocument document = CadDocument.CreateDefault("Bare");

        string before = ModelDump.Of(document);
        document.SetSvgExtras(new[] { "<sodipodi:namedview id=\"nv\"/>" });

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains("svgExtra 0 <sodipodi:namedview id=\"nv\"/>", after, StringComparison.Ordinal);
    }

    /// <summary>**An unreferenced foreign definition changes the dump**, for the same reason: nothing points at it
    /// today, and losing it is the silent rewrite issue #155 is about.</summary>
    [Fact]
    public void AForeignPathEffectChangesTheDump()
    {
        CadDocument document = CadDocument.CreateDefault("Bare");

        string before = ModelDump.Of(document);
        document.SetForeignPathEffects(new[] { "<filter id=\"unused\"/>" });

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains("foreignPathEffect 0 <filter id=\"unused\"/>", after, StringComparison.Ordinal);
    }

    /// <summary>**A declared namespace prefix changes the dump.** The prefix is what the writer gives back, so a
    /// round trip that regenerated it would be a rewrite the dump could not previously see.</summary>
    [Fact]
    public void AnSvgNamespaceChangesTheDump()
    {
        CadDocument document = CadDocument.CreateDefault("Bare");

        string before = ModelDump.Of(document);
        document.SetSvgNamespaces(new[]
        {
            new KeyValuePair<string, string>("inkscape", "http://www.inkscape.org/namespaces/inkscape"),
        });

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);
        Assert.Contains(
            "namespace inkscape=http://www.inkscape.org/namespaces/inkscape",
            after,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// **An item's blend mode changes the dump, on the item's own line, for every kind of item.**
    ///
    /// A path, a group and a text block are all printed - each kind is a different branch of the dump, so a fix that
    /// covered only the one that was easiest to test would let the others stay invisible.
    /// </summary>
    [Fact]
    public void AnItemsBlendModeChangesTheDump()
    {
        CadDocument document = CadDocument.CreateDefault("Blended");
        PathItem path = Rectangle();
        document.Artboards[0].Layers[0].AddItem(path);

        var group = new ArtGroup { Name = "g" };
        group.AddItem(PathFactory.CreateRectangle("inner", new VCCad.Geometry.Rect2D(0, 0, 5, 5)));
        document.Artboards[0].Layers[0].AddItem(group);

        var text = new TextItem { Name = "t" };
        text.Runs.Add(new TextRun { Text = "hi", FontFamily = "Helvetica", FontSize = 10 });
        document.Artboards[0].Layers[0].AddItem(text);

        string before = ModelDump.Of(document);
        Assert.DoesNotContain("blend=", before, StringComparison.Ordinal);

        path.BlendMode = BlendMode.Multiply;
        group.BlendMode = BlendMode.Overlay;
        text.BlendMode = BlendMode.Screen;

        string after = ModelDump.Of(document);
        Assert.NotEqual(before, after);

        string pathLine = LineContaining(after, "name=r");
        Assert.Contains("blend=multiply", pathLine, StringComparison.Ordinal);

        string groupLine = LineContaining(after, "name=g");
        Assert.Contains("blend=overlay", groupLine, StringComparison.Ordinal);

        string textLine = LineContaining(after, "name=t");
        Assert.Contains("blend=screen", textLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A default document dumps none of these members.** This is the other half of the remit: an ordinary
    /// document's dump has to stay byte-identical, or every existing dump comparison churns - and the text for a
    /// member that is at its default must be absent, not printed as a default value.
    /// </summary>
    [Fact]
    public void ADefaultDocumentDumpsNoneOfTheseMembers()
    {
        CadDocument document = CadDocument.CreateDefault("Ordinary");
        document.Artboards[0].Layers[0].AddItem(Rectangle());

        string dump = ModelDump.Of(document);

        Assert.DoesNotContain("widthProfiles=", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("brushes=", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("definitions=", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("svgExtras=", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("foreignPathEffects=", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("svgNamespaces=", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("blend=", dump, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The round trip the harness could not previously witness.** A width profile, a brush, a blend mode and the
    /// document's SVG baggage all survive a save and reload - asserted on the reloaded model as well as the dumps,
    /// because an equal dump only means the two agreed about what they printed.
    /// </summary>
    [Fact]
    public void TheDocumentsAssetsAndBlendModesSurviveTheRoundTrip()
    {
        CadDocument document = CadDocument.CreateDefault("Assets");
        PathItem path = Rectangle();
        path.BlendMode = BlendMode.Multiply;
        document.Artboards[0].Layers[0].AddItem(path);
        document.AddWidthProfile(TaperProfile());
        document.AddBrush(BrushSpec.Calligraphic("Ink", 45.0, 0.5, 8.0));
        document.SetSvgExtras(new[] { "<sodipodi:namedview id=\"nv\"/>" });
        document.SetForeignPathEffects(new[] { "<filter id=\"unused\"/>" });
        document.SetSvgNamespaces(new[]
        {
            new KeyValuePair<string, string>("inkscape", "http://www.inkscape.org/namespaces/inkscape"),
        });

        string before = ModelDump.Of(document);
        CadDocument reloaded = Reload(document);
        string after = ModelDump.Of(reloaded);

        Assert.True(before == after, FirstDifference(before, after));

        WidthProfileSpec profile = Assert.Single(reloaded.WidthProfiles);
        Assert.Equal("Taper", profile.Name);
        Assert.Equal(3, profile.Points.Count);
        Assert.Equal(6.0, profile.Points[1].LeftWidth, 6);
        Assert.Equal(2.0, profile.Points[1].RightWidth, 6);
        Assert.Equal(WidthInterpolation.Cubic, profile.Points[1].Interpolation);

        BrushSpec brush = Assert.Single(reloaded.Brushes);
        Assert.Equal("Ink", brush.Name);
        Assert.Equal(BrushKind.Calligraphic, brush.Kind);
        Assert.Equal(45.0, brush.AngleDegrees, 6);
        Assert.Equal(0.5, brush.Roundness, 6);
        Assert.Equal(8.0, brush.Diameter, 6);

        Assert.Equal(BlendMode.Multiply, reloaded.AllPaths().Single().BlendMode);
        Assert.Equal(new[] { "<sodipodi:namedview id=\"nv\"/>" }, reloaded.SvgExtras);
        Assert.Equal(new[] { "<filter id=\"unused\"/>" }, reloaded.ForeignPathEffects);
        Assert.Equal("http://www.inkscape.org/namespaces/inkscape", reloaded.SvgNamespaces["inkscape"]);
    }

    /// <summary>The one dump line that contains <paramref name="needle"/>, so a value can be asserted on the line
    /// the item it belongs to is actually printed on rather than anywhere in the dump.</summary>
    private static string LineContaining(string dump, string needle)
        => dump.Split('\n').Single(line => line.Contains(needle, StringComparison.Ordinal));

    /// <summary>A readable description of where two dumps first disagree.</summary>
    private static string FirstDifference(string before, string after)
    {
        string[] a = before.Split('\n');
        string[] b = after.Split('\n');

        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return $"line {i}:\n  before: {a[i]}\n  after : {b[i]}";
            }
        }

        if (a.Length != b.Length)
        {
            return $"line count {a.Length} vs {b.Length}";
        }

        return "identical";
    }
    /// <summary>
    /// Every item under these, groups included.
    ///
    /// The imported tree is nested - a page's content is a group from the file's own form XObjects, with
    /// optional-content groups inside it - so a walk that looks only at a layer's direct children no longer
    /// finds the artwork. These tests are about fonts and images, not structure, so they walk.
    /// </summary>
    private static IEnumerable<LayerItem> Walk(IEnumerable<LayerItem> items)
    {
        foreach (LayerItem item in items)
        {
            yield return item;
            if (item is ArtGroup group)
            {
                foreach (LayerItem nested in Walk(group.Children))
                {
                    yield return nested;
                }
            }
        }
    }
}
