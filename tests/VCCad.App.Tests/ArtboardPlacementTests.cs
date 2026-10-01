using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A second page goes **beside** the first, not on top of it.
///
/// `artboard.add` placed every page at the origin, so two pages occupied the same rectangle: `view.fit` fitted
/// what it could see and the page that disappeared was the one that was there first. A person dragging a page
/// in the Pages panel would never have had to discover this.
/// </summary>
public class ArtboardPlacementTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static AutomationContext Host()
        => new() { ViewModel = new EditorViewModel() };

    [Fact]
    public void ANewPageIsPlacedClearOfTheLastOne()
    {
        AutomationContext context = Host();

        EditorOperations.Invoke(context, "artboard.add", Params(new { name = "Two" }));
        EditorOperations.Invoke(context, "artboard.add", Params(new { name = "Three" }));

        IReadOnlyList<Artboard> boards = context.Document.Artboards;
        Assert.Equal(3, boards.Count);

        for (int i = 1; i < boards.Count; i++)
        {
            Assert.True(
                boards[i].Bounds.Left >= boards[i - 1].Bounds.Right,
                $"page {i} starts at {boards[i].Bounds.Left} and page {i - 1} ends at {boards[i - 1].Bounds.Right}");
        }
    }

    /// <summary>Pages of different heights still land side by side, at the left edge of the one before.</summary>
    [Fact]
    public void AShorterPageStillClearsTheWiderOne()
    {
        AutomationContext context = Host();

        EditorOperations.Invoke(context, "artboard.add", Params(new { width = 1200, height = 400 }));
        EditorOperations.Invoke(context, "artboard.add", Params(new { width = 400, height = 1200 }));

        IReadOnlyList<Artboard> boards = context.Document.Artboards;
        Assert.True(boards[1].Bounds.Right <= boards[2].Bounds.Left);
    }

    /// <summary>An x that was asked for is still the x that is used.</summary>
    [Fact]
    public void AnExplicitPositionWins()
    {
        AutomationContext context = Host();

        EditorOperations.Invoke(context, "artboard.add", Params(new { x = 5000, y = 300, name = "Far" }));

        Artboard added = context.Document.Artboards[1];
        Assert.Equal(5000.0, added.Bounds.Left, 3);
        Assert.Equal(300.0, added.Bounds.Top, 3);
    }
}
