using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **A character typed into a block keeps the weight the file asked for** (issue #257).
///
/// The report: typing a character into an edited block changes the font of the inserted text. The file names its faces in
/// `SourceFont` - `Helvetica-Bold`, `NPFRLV+CenturyGothic-Bold` - and a run's `Bold`/`Italic` flags are supposed to describe
/// the same fact. They do not always agree:
///
/// - the importer sets the flags from the **name** (`SourceFontName(fontName, resources)`), which is where a subset's weight
///   actually lives;
/// - but nothing re-derives them when a run is **split** by typing into the middle of it.
///
/// So a run can arrive carrying `SourceFont = "Helvetica-Bold"` with `Bold = false`, and the character typed into it is
/// inserted into a run whose flags disagree with the face the file asked for. The insertion looks like a font change because
/// it is one: the new text is drawn with the family's regular face while the text around it is bold.
///
/// These tests state the invariant that closes it: **the flags of a run must agree with the source name it carries**, however
/// the run was made - imported, split, or merged.
/// </summary>
public class TypedCharacterKeepsTheFilesWeightTests
{
    private static TextItem BlockWith(string sourceFont, bool bold)
        => new()
        {
            Runs =
            {
                new TextRun { Text = "DO NOT REPRODUCE", FontFamily = "Nimbus Sans", FontSize = 12, SourceFont = sourceFont, Bold = bold },
            },
        };

    /// <summary>The state the defect needs: the file says Bold in the name, the flag does not.</summary>
    [Fact]
    public void AFaceNamedBoldYieldsABoldFlagWhenTheRunCarriesIt()
    {
        TextItem text = BlockWith("Helvetica-Bold", bold: false);

        TextWeights.NormaliseSourceWeights(text);

        Assert.True(text.Runs[0].Bold, "a run whose source face is named -Bold must be flagged bold");
        Assert.False(text.Runs[0].Italic);
    }

    [Fact]
    public void AFaceNamedObliqueYieldsAnItalicFlag()
    {
        TextItem text = BlockWith("Helvetica-Oblique", bold: false);

        TextWeights.NormaliseSourceWeights(text);

        Assert.True(text.Runs[0].Italic);
        Assert.False(text.Runs[0].Bold);
    }

    /// <summary>A subset prefix and a case that is not the file's must not matter: the weight is in the name's words.</summary>
    [Fact]
    public void ASubsetPrefixedNameIsStillRead()
    {
        TextItem text = BlockWith("NPFRLV+CenturyGothic-Bold", bold: false);

        TextWeights.NormaliseSourceWeights(text);

        Assert.True(text.Runs[0].Bold);
    }

    [Fact]
    public void ARegularNameLeavesTheFlagsAloneAndARegularRunStaysRegular()
    {
        TextItem text = new()
        {
            Runs =
            {
                new TextRun { Text = "Jalie", FontFamily = "Nimbus Sans", FontSize = 12, SourceFont = "Arial", Bold = false },
            },
        };

        TextWeights.NormaliseSourceWeights(text);

        Assert.False(text.Runs[0].Bold);
        Assert.False(text.Runs[0].Italic);
    }

    /// <summary>
    /// The gesture from the report: type one character into the middle of a run whose name says Bold. **The character lands in
    /// a run that is still bold** - it must not be inserted into a run whose flag disagrees with the face.
    /// </summary>
    [Fact]
    public void TypingIntoTheMiddleOfABoldFaceKeepsTheNewTextBold()
    {
        TextItem text = BlockWith("Helvetica-Bold", bold: false);

        TextWeights.NormaliseSourceWeights(text);
        TextEditing.Insert(text, 5, "5");

        Assert.Equal("DO NO5T REPRODUCE", string.Concat(text.Runs.Select(r => r.Text)));

        // Whichever run the character landed in, that run is bold and still names the file's face.
        TextRun landed = TextEditing.RunAt(text, 6)!;
        Assert.True(landed.Bold, "the typed character must be drawn with the weight the file asked for");
        Assert.Equal("Helvetica-Bold", landed.SourceFont);
    }
}
