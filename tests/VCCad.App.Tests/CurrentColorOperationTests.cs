using System.Text;
using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// SVG's `currentColor` through the operation registry (issue #135).
///
/// The reader resolves the keyword and the model records that it did; these tests drive the **operations**, because
/// a readout nobody can reach is not a capability and because the honouring step - a rebuild of an instance - is
/// only reachable through <c>instance.refresh</c>. The assertions are the channel values the drawing comes out with
/// and the numbers the readout reports, never that a step ran.
/// </summary>
public class CurrentColorOperationTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "width=\"400\" height=\"400\" viewBox=\"0 0 400 400\">";

    /// <summary>A definition that states no colour of its own and follows the one its use site establishes.</summary>
    private const string FollowsTheColour =
        "<defs><rect id=\"box\" width=\"10\" height=\"10\" fill=\"currentColor\"/></defs>" +
        "<use href=\"#box\" color=\"#ff0000\"/>";

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static AutomationContext Host() => new() { ViewModel = new EditorViewModel() };

    private static CadDocument Import(AutomationContext context, string body)
    {
        EditorOperations.Invoke(context, "document.importSvg",
            Params(new { svgBase64 = Base64(Head + body + "</svg>") }));
        return context.Document;
    }

    private static PathItem FirstShape(ArtGroup instance)
    {
        IEnumerable<LayerItem> Deep(IEnumerable<LayerItem> items)
        {
            foreach (LayerItem item in items)
            {
                yield return item;

                if (item is ArtGroup group)
                {
                    foreach (LayerItem nested in Deep(group.Children))
                    {
                        yield return nested;
                    }
                }
            }
        }

        return Deep(instance.Children).OfType<PathItem>().First();
    }

    private static JsonElement Invoke(AutomationContext context, string op)
        => JsonSerializer.SerializeToElement(EditorOperations.Invoke(context, op, default));

    /// <summary>
    /// **The acceptance.** A definition whose paint follows the colour, one use that establishes red, and a refresh
    /// through the registry: the shape is still red afterwards. The definition was read into the library under
    /// SVG's initial values, so its `currentColor` resolved to black there - against the old behaviour the shape is
    /// `0,0,0` after the rebuild, which is the loss the issue records.
    /// </summary>
    [Fact]
    public void TheRefreshOperationKeepsTheColourADefinitionFollows()
    {
        AutomationContext context = Host();
        CadDocument document = Import(context, FollowsTheColour);

        ArtGroup instance = Assert.Single(document.AllGroups().Where(group => group.SourceId == "box"));
        PathItem shape = FirstShape(instance);
        Assert.Equal(1.0, shape.Fill.Color.R, 6);
        Assert.Equal(0.0, shape.Fill.Color.G, 6);
        Assert.Equal(0.0, shape.Fill.Color.B, 6);

        JsonElement refreshed = Invoke(context, "instance.refresh");
        Assert.Equal(1, refreshed.GetProperty("refreshed").GetInt32());

        // The drawing, after the rebuild: the colour the use site stated, not the definition's initial black.
        PathItem after = FirstShape(Assert.Single(document.AllGroups().Where(g => g.SourceId == "box")));
        Assert.Equal(1.0, after.Fill.Color.R, 6);
        Assert.Equal(0.0, after.Fill.Color.G, 6);
        Assert.Equal(0.0, after.Fill.Color.B, 6);
    }

    /// <summary>
    /// **The readout says which paint follows the colour and what it resolved to**, for a fill and for a stroke.
    /// Both are reported, because the flag lives on the paint: a caller that asked the item alone could not tell
    /// which member moves when the colour does.
    /// </summary>
    [Fact]
    public void TheReadoutNamesEveryPaintThatFollowsTheColour()
    {
        AutomationContext context = Host();
        Import(
            context,
            "<g color=\"#3366cc\">" +
            "<path d=\"M 0 0 L 10 0\" fill=\"currentColor\" stroke=\"currentColor\" stroke-width=\"2\"/>" +
            "<rect id=\"square\" x=\"0\" y=\"0\" width=\"5\" height=\"5\" fill=\"#ff0000\"/></g>");

        JsonElement readout = Invoke(context, "paint.currentColor");

        JsonElement[] entries = readout.EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);

        JsonElement fill = Assert.Single(entries, entry => entry.GetProperty("member").GetString() == "fill");
        Assert.Equal(0.2, fill.GetProperty("r").GetDouble(), 6);
        Assert.Equal(0.4, fill.GetProperty("g").GetDouble(), 6);
        Assert.Equal(0.8, fill.GetProperty("b").GetDouble(), 6);

        JsonElement stroke = Assert.Single(entries, entry => entry.GetProperty("member").GetString() == "stroke");
        Assert.Equal(0.2, stroke.GetProperty("r").GetDouble(), 6);
        Assert.Equal(0.8, stroke.GetProperty("b").GetDouble(), 6);

        // The shape that stated its own colour is not in the list: the readout is about the keyword, not about
        // every paint in the document.
        Assert.DoesNotContain(entries, entry => entry.GetProperty("name").GetString() == "square");
    }

    /// <summary>
    /// **A document that never names the keyword reports nothing**, which is what makes this a readout rather than
    /// a rewrite - and what a driver comparing two documents can rely on.
    /// </summary>
    [Fact]
    public void ADocumentWithoutTheKeywordReportsNothing()
    {
        AutomationContext context = Host();
        Import(context, "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"#ff0000\"/>");

        Assert.Empty(Invoke(context, "paint.currentColor").EnumerateArray());
    }

    /// <summary>
    /// **The colour the use site established is readable**, which is the half a caller would otherwise have to
    /// reconstruct from the drawing: `presentation.color` is the `color` a `currentColor` inside the definition
    /// stands for, and it is what the rebuild applies.
    /// </summary>
    [Fact]
    public void ThePresentationReadoutReportsTheColorTheUseSiteStated()
    {
        AutomationContext context = Host();
        Import(context, FollowsTheColour);

        JsonElement entry = Assert.Single(Invoke(context, "instance.presentation").EnumerateArray());
        JsonElement presentation = entry.GetProperty("presentation");

        JsonElement colour = presentation.GetProperty("color");
        Assert.Equal(1.0, colour.GetProperty("r").GetDouble(), 6);
        Assert.Equal(0.0, colour.GetProperty("g").GetDouble(), 6);
        Assert.Equal(0.0, colour.GetProperty("b").GetDouble(), 6);
    }

    /// <summary>
    /// **Both operations are in the catalog**, which is how a person finds them in the Operations tab and how a
    /// driver discovers them. Neither takes a parameter, and the catalog has to say so rather than describe a scope
    /// the operation would ignore.
    /// </summary>
    [Theory]
    [InlineData("paint.currentColor")]
    [InlineData("instance.presentation")]
    [InlineData("instance.refresh")]
    public void TheCatalogNamesTheOperationWithNoScope(string op)
    {
        JsonElement catalog = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(Host(), "app.operations", default));

        JsonElement entry = Assert.Single(
            catalog.EnumerateArray(), operation => operation.GetProperty("op").GetString() == op);

        Assert.Equal(string.Empty, entry.GetProperty("parameters").GetString());
    }
}
