using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A document that omits a run's font family reads as the default (issue #213).
///
/// `TextRun.FontFamily` defaults to a real name, so a run built in code always has one. The way a **null** arrived
/// was the serializer: `TextRunDto` declares the member non-nullable, and System.Text.Json leaves it null when the
/// JSON has no such member. That null reached the render path, where a blank name is what terminated the process
/// from a paint pass - the crash this issue is about.
///
/// The fix is at the deserialisation boundary rather than in the model, and the reason is worth keeping: `TextRun`
/// is also what a PDF import produces, where a run legitimately carries no family and the supply chain chooses the
/// face and reports the substitution through `fonts.list`. Coalescing a blank name to a particular family at the
/// model would change what an imported document draws and what the status bar says about it. Absent-means-default
/// is the rule the rest of this serializer already follows, so it is the right place for this one too.
/// </summary>
public class TextRunFamilyDefaultTests
{
    private static CadDocument WithText()
    {
        // The factory the application itself uses for a new document: an A4 landscape page and one layer.
        CadDocument document = CadDocument.CreateDefault();
        Artboard board = document.Artboards[0];

        var item = new TextItem { Name = "words" };
        item.Origin = new Point2D(board.X + 20.0, board.Y + 40.0);
        item.Runs.Add(new TextRun { Text = "hello", FontFamily = TextItem.DefaultFontFamily, FontSize = 18.0 });
        board.Layers[0].AddItem(item);

        return document;
    }

    /// <summary>The family member removed, as a file written by something that did not know about it would be.</summary>
    private static string WithoutFamily(CadDocument document)
        => Regex.Replace(
            VccadDocumentSerializer.Serialize(document),
            "\"FontFamily\"\\s*:\\s*\"[^\"]*\"\\s*,?",
            string.Empty);

    [Fact]
    public void ARunWhoseFamilyIsAbsentReadsAsTheDefault()
    {
        string json = WithoutFamily(WithText());
        Assert.DoesNotContain("\"FontFamily\"", json);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(json);
        TextRun run = reloaded.AllItems().OfType<TextItem>().Single().Runs.Single();

        Assert.False(string.IsNullOrWhiteSpace(run.FontFamily));
        Assert.Equal(TextItem.DefaultFontFamily, run.FontFamily);
    }

    /// <summary>
    /// A document that names nothing on the run still round-trips, and the family it reads as is the one it writes
    /// back - so opening a file that omitted the member does not change what the file says about itself twice over.
    /// </summary>
    [Fact]
    public void TheDefaultSurvivesTheNextRoundTrip()
    {
        string first = WithoutFamily(WithText());
        string second = VccadDocumentSerializer.Serialize(VccadDocumentSerializer.Deserialize(first));

        Assert.Equal(second, VccadDocumentSerializer.Serialize(VccadDocumentSerializer.Deserialize(second)));
        Assert.Contains(TextItem.DefaultFontFamily, second);
    }

    /// <summary>A member that is present and blank is the same case as one that is absent.</summary>
    [Fact]
    public void ABlankFamilyReadsAsTheDefaultToo()
    {
        string json = VccadDocumentSerializer.Serialize(WithText())
            .Replace($"\"FontFamily\":\"{TextItem.DefaultFontFamily}\"", "\"FontFamily\":\"\"");

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(json);
        TextRun run = reloaded.AllItems().OfType<TextItem>().Single().Runs.Single();

        Assert.Equal(TextItem.DefaultFontFamily, run.FontFamily);
    }
}
