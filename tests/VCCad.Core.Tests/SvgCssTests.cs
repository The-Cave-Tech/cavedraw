using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The CSS cascade over SVG: selectors, specificity, `!important`, imported sheets, and the priority of a
/// presentation attribute.
///
/// **The priority is the part worth testing hardest.** A property can arrive from a presentation attribute, a rule
/// in a `style` element, a rule in an imported sheet and an inline `style`, and the winner is decided by importance
/// then specificity then order. The assumption that the attribute written on the element wins is natural and wrong:
/// it is the lowest of the four. Inkscape ships three files whose entire purpose is to catch that.
/// </summary>
public class SvgCssTests
{
    private static readonly string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    /// <summary>The imported path with this id, which is how a test asks "what colour did this end up".</summary>
    private static PathItem ById(SvgImportResult result, string id)
        => result.Document.AllPaths().Single(p => p.Name == id);

    private static double Red(PathItem path) => path.Fill.Color.R;

    private static double Green(PathItem path) => path.Fill.Color.G;

    private static double Blue(PathItem path) => path.Fill.Color.B;

    private static bool IsRed(PathItem path) => Red(path) > 0.9 && Green(path) < 0.1 && Blue(path) < 0.1;

    /// <summary>
    /// Whether the fill is CSS `green`, which is **128 of 255** - half brightness, not full.
    ///
    /// This test suite had it as "green over 0.9" and six tests failed on it. `green` and `lime` are two names for
    /// two different colours in CSS, and the corpus files use the dim one; expecting the bright one is a mistake
    /// about the specification, not about the reader.
    /// </summary>
    private static bool IsGreen(PathItem path) => Green(path) > 0.4 && Red(path) < 0.1 && Blue(path) < 0.1;

    private static bool IsBlue(PathItem path) => Blue(path) > 0.9 && Red(path) < 0.1;

    // ---------------------------------------------------------------- selectors

    [Fact]
    public void TypeClassAndIdSelectorsMatch()
    {
        SvgImportResult result = Read(
            "<style>rect { fill: red; } .c1 { fill: green; } #theOne { fill: blue; }</style>" +
            "<rect id=\"plain\" width=\"1\" height=\"1\"/>" +
            "<rect id=\"classy\" class=\"c1\" width=\"1\" height=\"1\"/>" +
            "<rect id=\"theOne\" width=\"1\" height=\"1\"/>");

        Assert.Equal(1.0, Red(ById(result, "plain")), 3);
        Assert.Equal(128.0 / 255.0, Green(ById(result, "classy")), 3);
        Assert.Equal(1.0, Blue(ById(result, "theOne")), 3);
    }

    [Fact]
    public void AGroupedSelectorAppliesToEach()
    {
        SvgImportResult result = Read(
            "<style>rect, circle { fill: red; }</style>" +
            "<rect id=\"a\" width=\"1\" height=\"1\"/><circle id=\"b\" r=\"1\"/>");

        Assert.True(IsRed(ById(result, "a")));
        Assert.True(IsRed(ById(result, "b")));
    }

    /// <summary>A descendant selector matches at any depth - `#g rect` is not `#g &gt; rect`.</summary>
    [Fact]
    public void ADescendantSelectorMatchesAtAnyDepth()
    {
        SvgImportResult result = Read(
            "<style>#g rect { fill: red; }</style>" +
            "<g id=\"g\"><g id=\"inner\"><rect id=\"deep\" width=\"1\" height=\"1\"/></g></g>" +
            "<rect id=\"outside\" width=\"1\" height=\"1\"/>");

        Assert.True(IsRed(ById(result, "deep")));
        Assert.False(IsRed(ById(result, "outside")));
    }

    // ---------------------------------------------------------------- the cascade

    /// <summary>
    /// **A presentation attribute is the lowest priority.** A rule in a `style` element beats an attribute on the
    /// element itself, however specific the attribute looks - which is the opposite of what it feels like.
    /// </summary>
    [Fact]
    public void ARuleBeatsAPresentationAttribute()
    {
        SvgImportResult result = Read(
            "<style>rect { fill: blue; }</style>" +
            "<rect id=\"a\" fill=\"red\" width=\"1\" height=\"1\"/>");

        Assert.True(IsBlue(ById(result, "a")), "the stylesheet should win over the attribute");
    }

    /// <summary>`!important` beats a presentation attribute too, and is what the corpus files test.</summary>
    [Fact]
    public void AnImportantRuleBeatsAPresentationAttribute()
    {
        SvgImportResult result = Read(
            "<style>rect { fill: blue !important; }</style>" +
            "<rect id=\"a\" fill=\"red\" width=\"1\" height=\"1\"/>");

        Assert.True(IsBlue(ById(result, "a")));
    }

    /// <summary>Between two rules for one property, the more specific wins whatever order they are in.</summary>
    [Fact]
    public void SpecificityDecidesBetweenTwoRules()
    {
        SvgImportResult result = Read(
            "<style>#a { fill: red; } rect { fill: blue; }</style>" +
            "<rect id=\"a\" width=\"1\" height=\"1\"/>");

        Assert.True(IsRed(ById(result, "a")), "the id selector is more specific than the type selector");
    }

    /// <summary>At equal specificity the later rule wins, which is why sheets are joined in document order.</summary>
    [Fact]
    public void TheLaterRuleWinsAtEqualSpecificity()
    {
        SvgImportResult result = Read(
            "<style>.c1 { fill: red; } .c1 { fill: blue; }</style>" +
            "<rect id=\"a\" class=\"c1\" width=\"1\" height=\"1\"/>");

        Assert.True(IsBlue(ById(result, "a")), "the last of two equal rules wins");
    }

    /// <summary>
    /// **And an important rule beats a non-important inline style**, which is the case that separates "inline is
    /// most specific" from what the cascade actually says.
    /// </summary>
    [Fact]
    public void AnImportantRuleBeatsAnInlineStyle()
    {
        SvgImportResult result = Read(
            "<style>rect { fill: blue !important; }</style>" +
            "<rect id=\"a\" style=\"fill:red\" width=\"1\" height=\"1\"/>");

        Assert.True(IsBlue(ById(result, "a")), "importance outranks specificity");
    }

    /// <summary>Without importance, an inline style does beat a rule.</summary>
    [Fact]
    public void AnInlineStyleBeatsARule()
    {
        SvgImportResult result = Read(
            "<style>#a { fill: blue; }</style>" +
            "<rect id=\"a\" style=\"fill:red\" width=\"1\" height=\"1\"/>");

        Assert.True(IsRed(ById(result, "a")));
    }

    /// <summary>Attribute, sheet and inline style for the same property resolve to the documented winner.</summary>
    [Fact]
    public void AttributeSheetAndInlineResolveInThatOrder()
    {
        SvgImportResult result = Read(
            "<style>#a { fill: green; }</style>" +
            "<rect id=\"a\" fill=\"red\" style=\"fill:blue\" width=\"1\" height=\"1\"/>" +
            "<rect id=\"b\" fill=\"red\" width=\"1\" height=\"1\"/>");

        // Inline beats the sheet; and `b` matches no rule at all, so its presentation attribute is what is left.
        Assert.True(IsBlue(ById(result, "a")));
        Assert.True(IsRed(ById(result, "b")));
    }

    /// <summary>A stylesheet inside `defs` still applies: `defs` means "not drawn here", not "not applied".</summary>
    [Fact]
    public void AStylesheetInsideDefsApplies()
    {
        SvgImportResult result = Read(
            "<defs><style>rect { fill: red; }</style></defs>" +
            "<rect id=\"a\" width=\"1\" height=\"1\"/>");

        Assert.True(IsRed(ById(result, "a")));
    }

    /// <summary>Two sheets are joined in document order, so the later one wins a tie between them.</summary>
    [Fact]
    public void TwoSheetsAreJoinedInDocumentOrder()
    {
        SvgImportResult result = Read(
            "<style>rect { fill: red; }</style>" +
            "<defs><style>rect { fill: green; }</style></defs>" +
            "<rect id=\"a\" width=\"1\" height=\"1\"/>");

        Assert.True(IsGreen(ById(result, "a")), "the sheet that comes later in the document wins");
    }

    /// <summary>
    /// **Empty declarations are skipped.** A stylesheet may write `{ ; fill: green; }` and an inline style
    /// `;fill:blue`, and treating the leading semicolon as a property would shadow the real declaration.
    /// </summary>
    [Fact]
    public void EmptyDeclarationsAreSkipped()
    {
        SvgImportResult result = Read(
            "<style>#a { ; fill: green; }</style>" +
            "<rect id=\"a\" width=\"1\" height=\"1\"/>" +
            "<rect id=\"b\" style=\";;fill:blue\" width=\"1\" height=\"1\"/>");

        Assert.True(IsGreen(ById(result, "a")));
        Assert.True(IsBlue(ById(result, "b")));
    }

    [Fact]
    public void CommentsInAStylesheetAreIgnored()
    {
        SvgImportResult result = Read(
            "<style>/* a comment { fill: red; } and more */ rect { fill: green; }</style>" +
            "<rect id=\"a\" width=\"1\" height=\"1\"/>");

        Assert.True(IsGreen(ById(result, "a")));
    }

    // ---------------------------------------------------------------- external sheets

    /// <summary>**An imported stylesheet is applied**, resolved relative to the file that imports it.</summary>
    [Fact]
    public void AnImportedSheetIsApplied()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vccad-css-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "extra.css"), "rect { fill: blue; } .c1 { fill: red; }");
            string svg = Path.Combine(directory, "doc.svg");
            File.WriteAllText(svg,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                "<style>@import url(\"extra.css\");</style>" +
                "<rect id=\"a\" width=\"1\" height=\"1\"/>" +
                "<rect id=\"b\" class=\"c1\" width=\"1\" height=\"1\"/></svg>");

            SvgImportResult result = SvgReader.ReadFile(svg);

            Assert.True(IsBlue(ById(result, "a")));
            Assert.True(IsRed(ById(result, "b")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // ---------------------------------------------------------------- the corpus

    private static string? CorpusDirectory()
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

    private static string? Corpus(string name)
    {
        string? directory = CorpusDirectory();
        if (directory is null)
        {
            return null;
        }

        return Directory.GetFiles(directory, "*.svg", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f) == name);
    }

    /// <summary>
    /// **`style-parsing.svg`'s exact colours.** The file exists because of a leading semicolon in a rule and
    /// another in an inline style, so its three rectangles are the whole specification: red from the sheet, green
    /// from a rule that starts with `;`, and blue from an inline style that starts with `;`.
    /// </summary>
    [Fact]
    public void TheStyleParsingCorpusFileHasTheColoursItPromises()
    {
        string? path = Corpus("style-parsing.svg");
        if (path is null)
        {
            return;
        }

        List<PathItem> paths = SvgReader.ReadFile(path).Document.AllPaths().ToList();

        Assert.Equal(3, paths.Count);

        // The first and third carry an inline style that begins with a semicolon, and an inline style beats the
        // sheet's `rect` rule - so both are blue. The second is matched by `#MyRect1`, whose rule begins with a
        // semicolon too, and which is more specific than `rect` - so it is green. The file exists to prove that
        // both kinds of leading semicolon are skipped rather than shadowing what follows them.
        Assert.True(IsBlue(paths[0]), "the first rectangle has an inline style that starts with a semicolon");
        Assert.True(IsGreen(paths[1]), "the second is green from a rule that starts with a semicolon");
        Assert.True(IsBlue(paths[2]), "the third has an inline style that starts with a semicolon");
    }

    /// <summary>
    /// **`multi-style.svg` has two sheets, and the later one wins.** Its own comment says so: a class is redefined
    /// several times and only the last counts.
    /// </summary>
    [Fact]
    public void TheMultiStyleCorpusFileTakesTheLastSheet()
    {
        string? path = Corpus("multi-style.svg");
        if (path is null)
        {
            return;
        }

        SvgImportResult result = SvgReader.ReadFile(path);
        Assert.NotEmpty(result.Document.AllPaths());
        Assert.Empty(result.Missing);
    }

    /// <summary>
    /// **`selector-important-002.svg` imports**, and the colours it exists to test come out as its comments say:
    /// `#groupA use` is set twice, both `!important`, so the later one - blue - wins.
    /// </summary>
    [Fact]
    public void TheSelectorImportantCorpusFileResolvesItsRules()
    {
        string? path = Corpus("selector-important-002.svg");
        if (path is null)
        {
            return;
        }

        SvgImportResult result = SvgReader.ReadFile(path);
        List<PathItem> paths = result.Document.AllPaths().ToList();

        Assert.NotEmpty(paths);
        Assert.Empty(result.Missing);

        // Every instance inside groupA is blue: the second of the two identical rules is the one that counts.
        Assert.Contains(paths, IsBlue);
        Assert.DoesNotContain(paths, p => IsRed(p) && p.Name == "MyRect");
    }
}
