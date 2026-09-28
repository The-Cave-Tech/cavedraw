using VCCad.Core.Text;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Where each run of a text object sits.
///
/// Runs are pieces of a line, not lines. A PDF lays a heading out as separate pieces - "Jalie",
/// "3464", "-", "LILLIE", "-", "Page", "1/12" - each with its own move, and the importer keeps
/// them as runs of one text object. Treating a run as a line stacks them at the same x, one
/// line below the other: the page-1 heading came out printed on top of itself, which is how
/// this was reported.
///
/// The only thing that starts a new line is a newline in the text.
/// </summary>
public class RunPlacementTests
{
    private static RunMetrics Run(string text, double width, double lineHeight = 10)
        => new(text, width, lineHeight);

    [Fact]
    public void RunsOfOneLineSitSideBySide()
    {
        IReadOnlyList<RunPlacement> placed = RunLayout.Place(new[]
        {
            Run("Jalie ", 21.0), Run("3464 ", 22.5), Run("- ", 5.5), Run("LILLIE", 28.5),
        });

        // Every piece on the same baseline...
        Assert.All(placed, p => Assert.Equal(0, p.Y));

        // ...and each starting where the one before it ended.
        Assert.Equal(0, placed[0].X, 6);
        Assert.Equal(21.0, placed[1].X, 6);
        Assert.Equal(43.5, placed[2].X, 6);
        Assert.Equal(49.0, placed[3].X, 6);
    }

    [Fact]
    public void ThePenDoesNotGoBackwards()
    {
        IReadOnlyList<RunPlacement> placed = RunLayout.Place(new[]
        {
            Run("a", 10), Run("b", 10), Run("c", 10),
        });

        for (int i = 1; i < placed.Count; i++)
        {
            Assert.True(placed[i].X > placed[i - 1].X,
                $"run {i} did not advance: {placed[i - 1].X} then {placed[i].X}");
        }
    }

    [Fact]
    public void ANewlineStartsTheNextLine()
    {
        IReadOnlyList<RunPlacement> placed = RunLayout.Place(new[]
        {
            Run("first\n", 30, lineHeight: 12), Run("second", 40),
        });

        Assert.Equal(0, placed[0].Y);
        Assert.Equal(12, placed[1].Y, 6);
        Assert.Equal(0, placed[1].X, 6);
    }

    [Fact]
    public void SeveralNewlinesInOneRunAdvanceSeveralLines()
    {
        IReadOnlyList<RunPlacement> placed = RunLayout.Place(new[]
        {
            Run("a\n\n\n", 10, lineHeight: 10), Run("b", 10),
        });

        Assert.Equal(30, placed[1].Y, 6);
        Assert.Equal(0, placed[1].X, 6);
    }

    [Fact]
    public void AHeadingReadsAsOneLineAcrossItsPieces()
    {
        // The reported case, end to end: the pieces of the heading must end up left to right
        // on one baseline, not stacked.
        IReadOnlyList<RunMetrics> heading = new[]
        {
            Run("Jalie ", 21.0), Run("3464 ", 22.5), Run("- ", 5.5),
            Run("LILLIE ", 27.0), Run("- ", 5.5), Run("Page ", 23.5), Run("1/12", 23.5),
        };

        IReadOnlyList<RunPlacement> placed = RunLayout.Place(heading);

        Assert.All(placed, p => Assert.Equal(0, p.Y));
        Assert.Equal(heading.Sum(r => r.Width), placed[^1].X + placed[^1].Width, 6);
    }

    [Fact]
    public void TheBlockIsAsWideAsItsWidestLine()
    {
        double one = RunLayout.BlockWidth(new[] { Run("abc", 30), Run("de", 20) });
        Assert.Equal(50, one, 6);

        double two = RunLayout.BlockWidth(new[] { Run("abc\n", 30), Run("de", 20) });
        Assert.Equal(30, two, 6);
    }

    [Fact]
    public void ASingleRunIsPlacedAtTheOrigin()
    {
        IReadOnlyList<RunPlacement> placed = RunLayout.Place(new[] { Run("alone", 40) });

        RunPlacement only = Assert.Single(placed);
        Assert.Equal(0, only.X, 6);
        Assert.Equal(0, only.Y, 6);
        Assert.Equal(40, only.Width, 6);
    }

    [Fact]
    public void NoRunsPlaceNothing()
        => Assert.Empty(RunLayout.Place(Array.Empty<RunMetrics>()));
}
