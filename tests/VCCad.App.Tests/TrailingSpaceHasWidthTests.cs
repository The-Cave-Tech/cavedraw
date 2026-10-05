using VCCad.App.Fonts;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A space at the end of a run has a width (issue #254).
///
/// The face is measured by differencing prefixes: `advance[i] = width(text[..i+1]) - width(text[..i])`. A text API
/// trims **trailing white space**, so for a run ending in a space the two widths are equal and the difference came
/// out zero. Six of the seven runs of the Lillie page header end in a space, and the two visible consequences were
/// one defect: the caret stopped moving when it crossed one (the person's "pressing right again doesn't alter the
/// position"), and the layout, which scales each run's glyphs to the advance the file states, took the missing room
/// out of the space by stretching the run's other glyphs ("white space is compressed as you go").
///
/// This measures the advance directly, so it fails on the trimming rather than on a rendering of it.
/// </summary>
public class TrailingSpaceHasWidthTests
{
    private static TextRun Run(string text) => new()
    {
        Text = text,
        FontFamily = "Nimbus Sans",
        FontSize = 9,
    };

    [Fact]
    public void ASpaceAtTheEndOfARunHasAnAdvance()
    {
        IReadOnlyList<double> advances = new AvaloniaTextMetrics().Advances(Run("3464 "));

        Assert.Equal(5, advances.Count);
        Assert.True(advances[4] > 0.1, $"the trailing space measures {advances[4]:0.####} wide");
    }

    /// <summary>
    /// And it is the width a space actually is, not a token amount: the same as a run that is nothing but a space.
    /// </summary>
    [Fact]
    public void TheTrailingSpaceIsAsWideAsASpace()
    {
        IReadOnlyList<double> space = new AvaloniaTextMetrics().Advances(Run(" "));
        IReadOnlyList<double> word = new AvaloniaTextMetrics().Advances(Run("3464 "));

        Assert.Single(space);
        Assert.True(space[0] > 0.1, $"a lone space measures {space[0]:0.####} wide");
        Assert.Equal(space[0], word[4], 3);
    }

    /// <summary>
    /// Every character keeps a positive advance, which is what "pressing Right moves the caret" needs: an offset
    /// that advances by nothing is a keypress that does nothing on screen.
    /// </summary>
    [Fact]
    public void NoCharacterOfASpaceEndingRunIsZeroWidth()
    {
        IReadOnlyList<double> advances = new AvaloniaTextMetrics().Advances(Run("Page "));

        for (int i = 0; i < advances.Count; i++)
        {
            Assert.True(advances[i] > 0.1, $"character {i} of \"Page \" measures {advances[i]:0.####}");
        }
    }
}
