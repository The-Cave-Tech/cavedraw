using System.Linq;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **Runs are merged only when the face the file asked for matches too** (issue #257).
///
/// `FontFamily` does not describe what is drawn. `StandardFontResolver.FamilyFor` answers from `SourceFont` when a run has
/// one and from `FontFamily` when it does not - so two runs can agree on family, size, weight and slant and still be drawn in
/// completely different faces. Typing calls `Merge`, so inserting a character between two such runs joined them and kept the
/// first one's face: the text around what was typed changed font, which was the report.
/// </summary>
public class MergeKeepsFacesApartTests
{
    private static TextRun Run(string text, string? source, string family = "Nimbus Sans", double size = 9)
        => new()
        {
            Text = text,
            FontFamily = family,
            FontSize = size,
            SourceFont = source,
        };

    [Fact]
    public void RunsFromDifferentSourceFacesAreNotMerged()
    {
        var text = new TextItem();
        text.Runs.Add(Run("Jalie ", "Arial"));
        text.Runs.Add(Run("3464", "CenturyGothic-Bold"));

        TextEditing.Merge(text);

        Assert.Equal(2, text.Runs.Count);
        Assert.Equal("Arial", text.Runs[0].SourceFont);
        Assert.Equal("CenturyGothic-Bold", text.Runs[1].SourceFont);
    }

    [Fact]
    public void RunsFromTheSameSourceFaceStillMerge()
    {
        var text = new TextItem();
        text.Runs.Add(Run("Jalie ", "Arial"));
        text.Runs.Add(Run("3464 ", "Arial"));

        TextEditing.Merge(text);

        Assert.Single(text.Runs);
        Assert.Equal("Jalie 3464 ", text.Runs[0].Text);
        Assert.Equal("Arial", text.Runs[0].SourceFont);
    }

    /// <summary>
    /// The import case: a run whose source is absent must not be joined to one that has a source, because the two are drawn
    /// from different answers.
    /// </summary>
    [Fact]
    public void ARunWithASourceFaceIsNotMergedIntoOneWithout()
    {
        var text = new TextItem();
        text.Runs.Add(Run("typed", null));
        text.Runs.Add(Run(" imported", "Arial"));

        TextEditing.Merge(text);

        Assert.Equal(2, text.Runs.Count);
    }

    /// <summary>
    /// Typing into imported text is the reported case: the character lands between two runs of the same face and the block
    /// must come out of it with the same number of faces it went in with.
    /// </summary>
    [Fact]
    public void TypingBetweenTwoFacesKeepsBoth()
    {
        var text = new TextItem();
        text.Runs.Add(Run("Jalie ", "Arial"));
        text.Runs.Add(Run("3464", "CenturyGothic-Bold"));

        TextEditing.Insert(text, 6, "5");

        Assert.Equal(2, text.Runs.Count);
        Assert.Contains("5", text.Runs[0].Text + text.Runs[1].Text);
        Assert.Equal(
            new[] { "Arial", "CenturyGothic-Bold" },
            text.Runs.Select(r => r.SourceFont).ToArray());
    }
}
