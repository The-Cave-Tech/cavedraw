using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using VCCad.Pdf.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **A blend mode set on a stroke, or on a leaf item, reaches the page.**
///
/// Blending is a compositing capability rather than a stack one, so honouring it is not a matter of a value
/// travelling: PDF expresses it as <c>/BM</c> in an <c>ExtGState</c>, and the paint switches to that state with
/// <c>gs</c> before it is drawn. Both halves are asserted, because either one missing is the same defect - the
/// artwork is drawn <em>normally</em> and nothing in the file says otherwise.
///
/// This is the half a round-trip test cannot see. `StrokeSpec.Blend` and `LayerItem.BlendMode` are stored,
/// serialised, printed by `ModelDump` and reported by `StrokeSummary`, and a round trip of any of those is
/// perfectly correct while the step that <em>honours</em> the value never runs. So the assertion is on the
/// operators, never on the model.
/// </summary>
public class BlendModeExportTests
{
    private readonly ITestOutputHelper _output;

    public BlendModeExportTests(ITestOutputHelper output) => _output = output;

    /// <summary>A rectangle with a black fill and a red stroke, so both halves of an item have something to
    /// paint.</summary>
    private static PathItem Rect()
    {
        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(100, 100)));
        sub.Nodes.Add(new PathNode(new Point2D(300, 100)));
        sub.Nodes.Add(new PathNode(new Point2D(300, 200)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 200)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, new ColorRgb(1, 0, 0), 6, StrokeCap.Butt, StrokeJoin.Miter, 4));
        return path;
    }

    private static CadDocument Document(params LayerItem[] items)
    {
        CadDocument document = CadDocument.CreateDefault();
        foreach (LayerItem item in items)
        {
            document.Artboards[0].Layers[0].AddItem(item);
        }

        return document;
    }

    /// <summary>The drawing operators, decoded, for the switch.</summary>
    private static string Content(byte[] pdf) => PdfDrawing.Of(pdf);

    /// <summary>The file as text, for the <c>ExtGState</c> bodies, which are not inside a deflated stream.</summary>
    private static string Raw(byte[] pdf) => System.Text.Encoding.Latin1.GetString(pdf);

    /// <summary>
    /// The <c>/ExtGState</c> resources a page names, by resource name, with the object the page's dictionary
    /// points at - so a <c>/GSn gs</c> in the stream can be followed to the state it actually switches to rather
    /// than matched against the declaration by name alone.
    /// </summary>
    private static Dictionary<string, Dictionary<string, object?>> ExtGStates(byte[] pdf)
    {
        var file = new PdfFile(pdf);
        var states = new Dictionary<string, Dictionary<string, object?>>();

        foreach ((Dictionary<string, object?> page, _) in PdfDrawing.Pages(pdf))
        {
            if (file.ResolveDict(page.GetValueOrDefault("Resources")) is not { } resources ||
                file.ResolveDict(resources.GetValueOrDefault("ExtGState")) is not { } group)
            {
                continue;
            }

            foreach ((string name, object? entry) in group)
            {
                if (entry is PdfRef reference && file.GetObject(reference.Number) is Dictionary<string, object?> body)
                {
                    states[name] = body;
                }
            }
        }

        return states;
    }

    /// <summary>The blend mode a resource name actually states, or null when it states none.</summary>
    private static string? BlendOf(byte[] pdf, string name)
        => ExtGStates(pdf).TryGetValue(name, out Dictionary<string, object?>? body) &&
           body.GetValueOrDefault("BM") is PdfName mode
            ? mode.Value
            : null;

    // ------------------------------------------------------------------ a leaf item

    /// <summary>
    /// **An item's blend mode becomes an ExtGState carrying `/BM`, and the item switches to it before it paints.**
    /// </summary>
    [Fact]
    public void AnItemsBlendModeBecomesAnExtGStateTheItemSwitchesTo()
    {
        PathItem shape = Rect();
        shape.BlendMode = BlendMode.Multiply;
        byte[] pdf = PdfDocumentExporter.Export(Document(shape));
        string content = Content(pdf);

        _output.WriteLine(content);

        // The state exists and names the mode the model states.
        Match switched = Regex.Match(content, @"/(?<name>GS\d+|BM\d+) gs");
        Assert.True(switched.Success, $"the item switches to a graphics state:\n{content}");
        Assert.Equal("Multiply", BlendOf(pdf, switched.Groups["name"].Value));

        // ...and it switches before the fill and before the stroke, so both are composited rather than drawn over.
        int set = switched.Index;
        int fill = content.IndexOf("\nf\n", StringComparison.Ordinal);
        int stroke = content.IndexOf("\nS\n", StringComparison.Ordinal);
        Assert.True(fill >= 0, "the shape is filled");
        Assert.True(stroke >= 0, "the shape is stroked");
        Assert.True(set < fill, $"the blend state is set before the fill ({set} vs {fill})");
        Assert.True(set < stroke, $"the blend state is set before the stroke ({set} vs {stroke})");

        // ...and it is left behind again after the stroke, rather than staying in the graphics state.
        Assert.True(content.IndexOf("\nQ\n", stroke + 1, StringComparison.Ordinal) > stroke);
    }

    /// <summary>Every mode the model can state is written under the name PDF's own table gives it.</summary>
    [Theory]
    [InlineData(BlendMode.Multiply, "Multiply")]
    [InlineData(BlendMode.Screen, "Screen")]
    [InlineData(BlendMode.Darken, "Darken")]
    [InlineData(BlendMode.Lighten, "Lighten")]
    [InlineData(BlendMode.Overlay, "Overlay")]
    [InlineData(BlendMode.ColorDodge, "ColorDodge")]
    [InlineData(BlendMode.ColorBurn, "ColorBurn")]
    [InlineData(BlendMode.HardLight, "HardLight")]
    [InlineData(BlendMode.SoftLight, "SoftLight")]
    [InlineData(BlendMode.Difference, "Difference")]
    [InlineData(BlendMode.Exclusion, "Exclusion")]
    [InlineData(BlendMode.Hue, "Hue")]
    [InlineData(BlendMode.Saturation, "Saturation")]
    [InlineData(BlendMode.Color, "Color")]
    [InlineData(BlendMode.Luminosity, "Luminosity")]
    public void EveryBlendModeIsWrittenUnderItsPdfName(BlendMode mode, string name)
    {
        PathItem shape = Rect();
        shape.BlendMode = mode;
        byte[] pdf = PdfDocumentExporter.Export(Document(shape));

        Match switched = Regex.Match(Content(pdf), @"/(?<name>GS\d+|BM\d+) gs");
        Assert.True(switched.Success, $"{mode} switches to a graphics state");
        Assert.Equal(name, BlendOf(pdf, switched.Groups["name"].Value));
    }

    /// <summary>
    /// **A blend mode on an item is a compositing step, so it is entered and left around what the item draws** -
    /// `q` and `Q` - rather than left in the graphics state for whatever is painted next.
    /// </summary>
    [Fact]
    public void AnItemsBlendModeIsLeftBehindBeforeTheNextItem()
    {
        PathItem blended = Rect();
        blended.BlendMode = BlendMode.Screen;

        PathItem plain = Rect();
        plain.Name = "plain";
        plain.BlendMode = BlendMode.Normal;

        string content = Content(PdfDocumentExporter.Export(Document(blended, plain)));

        _output.WriteLine(content);

        // One switch for the blended item, and the plain one beside it states nothing.
        MatchCollection switches = Regex.Matches(content, @"/(GS\d+|BM\d+) gs");
        Assert.Single(switches);

        int set = switches[0].Index;
        int q = content.LastIndexOf("\nq\n", set, StringComparison.Ordinal);
        Assert.True(q >= 0, "the switch is inside a q/Q pair");
        Assert.Contains("\nQ\n", content[set..]);
    }

    // ------------------------------------------------------------------ a stroke

    /// <summary>
    /// **A stroke's blend mode is the graphics state's, set for that stroke and no other.**
    ///
    /// The stack is what makes this visible: a blend belongs to one stroke of the stack, so the state has to be
    /// entered after the stroke below it is painted and left before the one above it starts.
    /// </summary>
    [Fact]
    public void AStrokesBlendModeAppliesToThatStrokeAlone()
    {
        PathItem line = Line(
            new StrokeSpec(true, new ColorRgb(1, 0, 0), 10, StrokeCap.Butt, StrokeJoin.Miter, 4),
            new StrokeSpec(true, new ColorRgb(0, 0, 1), 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
                with { Blend = BlendMode.Multiply });

        string content = Content(PdfDocumentExporter.Export(Document(line)));
        _output.WriteLine(content);

        int red = content.IndexOf("1 0 0 RG", StringComparison.Ordinal);
        int blue = content.IndexOf("0 0 1 RG", StringComparison.Ordinal);
        Assert.True(red >= 0 && blue >= 0 && red < blue, "both strokes state their colour, bottom first");

        // The plain stroke beneath is painted, then the blended one is entered with `q`, given its state, painted
        // and given back with `Q` - so the mode belongs to that stroke and not to the stack.
        int plainPainted = content.IndexOf("\nS\n", red, StringComparison.Ordinal);
        int enters = content.LastIndexOf("\nq\n", blue, StringComparison.Ordinal);
        int switched = content.IndexOf(" gs", enters, StringComparison.Ordinal);
        int painted = content.IndexOf("\nS\n", blue, StringComparison.Ordinal);
        int leaves = content.IndexOf("\nQ\n", painted, StringComparison.Ordinal);

        Assert.True(plainPainted >= 0 && plainPainted < enters,
            $"the stroke beneath is painted before the blend is entered ({plainPainted} vs {enters})");
        Assert.True(enters >= 0 && enters < switched && switched < blue,
            $"the state is entered and set before the blended stroke's colour ({enters}, {switched}, {blue})");
        Assert.True(painted > blue && leaves > painted,
            $"the blend is left again after the blended stroke is painted ({painted}, {leaves})");

        Match name = Regex.Match(content[enters..blue], @"/(?<name>GS\d+|BM\d+) gs");
        Assert.True(name.Success, $"the blended stroke switches state before it is drawn:\n{content[enters..blue]}");
        Assert.Equal("Multiply", BlendOf(PdfDocumentExporter.Export(Document(line)), name.Groups["name"].Value));

        // The plain stroke beneath writes no state at all.
        Assert.DoesNotContain(" gs", content[..enters]);
    }

    /// <summary>The stroke beneath a blended one is painted with no blend state at all.</summary>
    [Fact]
    public void TheStrokesBeneathABlendedOneWriteNoBlendState()
    {
        PathItem line = Line(
            new StrokeSpec(true, new ColorRgb(1, 0, 0), 10, StrokeCap.Butt, StrokeJoin.Miter, 4),
            new StrokeSpec(true, new ColorRgb(0, 0, 1), 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
                with { Blend = BlendMode.Difference });

        string content = Content(PdfDocumentExporter.Export(Document(line)));

        int red = content.IndexOf("1 0 0 RG", StringComparison.Ordinal);
        Assert.DoesNotContain(" gs", content[..red]);
    }

    /// <summary>
    /// **A stroke's opacity and its blend are two states and both are stated**, because they are different
    /// questions and PDF keeps them in different keys of an ExtGState.
    /// </summary>
    [Fact]
    public void AStrokeOpacityAndAStrokeBlendAreBothSet()
    {
        PathItem line = Line(
            new StrokeSpec(true, new ColorRgb(0, 0, 1), 6, StrokeCap.Butt, StrokeJoin.Miter, 4)
            {
                Opacity = 0.5,
                Blend = BlendMode.Multiply,
            });

        byte[] pdf = PdfDocumentExporter.Export(Document(line));
        string content = Content(pdf);
        _output.WriteLine(content);

        Assert.Contains("/ca 0.5", Raw(pdf), StringComparison.Ordinal);
        Assert.Contains("/BM /Multiply", Raw(pdf), StringComparison.Ordinal);

        var names = Regex.Matches(content, @"/(?<name>GS\d+|BM\d+) gs")
            .Select(m => m.Groups["name"].Value).ToArray();
        Assert.Contains(names, name => BlendOf(pdf, name) == "Multiply");
        Assert.Contains(names, name => BlendOf(pdf, name) is null);
    }

    // ------------------------------------------------------------------ the default

    /// <summary>
    /// **An ordinary document is unchanged.** No item and no stroke states a blend, so no `/BM` and no blend
    /// switch is written - the no-member-at-its-default rule, asserted on the content stream and not only on the
    /// sidecar.
    /// </summary>
    [Fact]
    public void ADocumentWithNoBlendModeWritesNoBlendState()
    {
        byte[] pdf = PdfDocumentExporter.Export(Document(Rect()));

        Assert.DoesNotContain("/BM", Raw(pdf), StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\d gs", Content(pdf));
    }

    /// <summary>
    /// **A group's blend is not written, and the file says so by not changing.** CSS composites a group as a
    /// unit against the backdrop, which PDF expresses with an isolated transparency group - a form XObject this
    /// exporter does not emit. Blending each child against the backdrop instead would be a different picture, so
    /// the honest answer is to leave it out; `PdfExportSupport` declares it.
    /// </summary>
    [Fact]
    public void AGroupBlendModeIsNotWritten()
    {
        CadDocument plain = Document(Group(Rect()));
        CadDocument blended = Document(Group(Rect()));
        ((ArtGroup)blended.Artboards[0].Layers[0].Children[0]).BlendMode = BlendMode.Multiply;

        Assert.Equal(Content(PdfDocumentExporter.Export(plain)), Content(PdfDocumentExporter.Export(blended)));
    }

    private static ArtGroup Group(params LayerItem[] children)
    {
        var group = new ArtGroup { Name = "group" };
        foreach (LayerItem child in children)
        {
            group.AddItem(child);
        }

        return group;
    }

    private static PathItem Line(params StrokeSpec[] strokes)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(40, 400)));
        sub.Nodes.Add(new PathNode(new Point2D(300, 400)));
        path.Strokes.Clear();
        path.Strokes.AddRange(strokes);
        return path;
    }
}
