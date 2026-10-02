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