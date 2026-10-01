using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `text.create` can be aimed at an artboard.
///
/// It put its text on whichever page was active and offered no way to say which, while `object.create` takes a
/// `layerId` - so a scene drawn on a second page got its words on the first, and found out afterwards. Text is
/// exactly the thing a scene needs more than one of, on more than one page.
/// </summary>
public class TextCreateLayerTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, Layer Second) Host()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };
        EditorOperations.Invoke(context, "artboard.add", Params(new { name = "Second" }));
        return (context, context.Document.Artboards[1].Layers[0]);
    }

    private static IEnumerable<TextItem> TextsIn(Layer layer)
        => layer.Children.OfType<TextItem>();

    [Fact]
    public void TextGoesOnTheLayerItWasGiven()
    {
        (AutomationContext context, Layer second) = Host();

        EditorOperations.Invoke(context, "text.create",
            Params(new { x = 100, y = 120, text = "42", fontSize = 48, layerId = second.Id }));

        TextItem placed = Assert.Single(TextsIn(second));
        Assert.Equal("42", placed.PlainText);

        // And not on the page that was active, which is where it used to land.
        Layer first = context.Document.Artboards[0].Layers[0];
        Assert.Empty(TextsIn(first));
    }

    /// <summary>
    /// The coordinates are the document's, and the move re-bases them onto the page - the same thing
    /// `object.create` does with its layerId, so the two create operations place an object identically.
    /// </summary>
    [Fact]
    public void TheCoordinatesAreTheDocuments()
    {
        (AutomationContext context, Layer second) = Host();
        double pageLeft = context.Document.Artboards[1].Bounds.Left;

        EditorOperations.Invoke(context, "text.create",
            Params(new { x = pageLeft + 40, y = 90, text = "DON'T PANIC", layerId = second.Id }));

        TextItem placed = Assert.Single(TextsIn(second));
        Assert.Equal(40.0, placed.Origin.X, 3);
        Assert.Equal(90.0, placed.Origin.Y, 3);
    }

    /// <summary>With no layerId it behaves as it always did: the active artboard.</summary>
    [Fact]
    public void WithNoLayerItGoesOnTheActivePage()
    {
        (AutomationContext context, Layer second) = Host();

        EditorOperations.Invoke(context, "text.create", Params(new { x = 10, y = 10, text = "here" }));

        Assert.Empty(TextsIn(second));
        Assert.Single(TextsIn(context.Document.Artboards[0].Layers[0]));
    }

    /// <summary>A layer that does not exist is refused rather than quietly ignored.</summary>
    [Fact]
    public void AnUnknownLayerIsRefused()
    {
        (AutomationContext context, _) = Host();

        Assert.Throws<EditorOperationException>(() => EditorOperations.Invoke(
            context, "text.create",
            Params(new { x = 1, y = 1, text = "nowhere", layerId = Guid.NewGuid() })));
    }
}
