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