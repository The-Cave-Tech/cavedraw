using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Editing a filter graph through the operation registry.
///
/// A filter is a document asset a driver can **change**, not only replace: the graph is the filter, so a primitive
/// is added, wired, retuned and removed where it stands. Every kind and every parameter comes from
/// <see cref="FilterPrimitiveRegistry"/>, so what is asserted here is that the operations accept what the
/// declaration says and refuse what it does not - an unknown kind, a parameter the kind has not got, a buffer
/// nothing produces, or a wiring with no answer.
/// </summary>
public class FilterEditOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (new AutomationContext { ViewModel = vm }, path);
    }

    private static FilterSpec Create(AutomationContext context, string name = "drop")
    {
        EditorOperations.Invoke(context, "filter.create", Params(new
        {
            name,
            primitives = new object[]
            {
                new { kind = "gaussianBlur", @in = "SourceAlpha", radius = 2.0, result = "soft" },
                new { kind = "offset", @in = "soft", dx = 2.0, dy = 3.0, result = "moved" },
            },
        }));

        return context.Document.FindFilter(name)!;
    }

    private static string? MessageOf(Action act)
        => Assert.Throws<EditorOperationException>(act).Message;

    // ---------------------------------------------------------------- editing the graph

    [Fact]
    public void APrimitiveIsAddedConnectedAndRemoved()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        // Added unwired: feFlood reads nothing at all, and its input stays null rather than being invented.
        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "flood",
            index = 2,
            floodColor = new[] { 0, 0, 255 },
            floodOpacity = 0.5,
            result = "ink",
        }));

        FilterSpec filter = context.Document.FindFilter("drop")!;
        Assert.Equal(3, filter.Primitives.Count);
        Assert.Equal(FilterPrimitiveKind.Flood, filter.Primitives[2].Kind);
        Assert.Null(filter.Primitives[2].Input);
        Assert.Equal(ColorRgb.FromBytes(0, 0, 255), filter.Primitives[2].FloodColor);

        EditorOperations.Invoke(context, "filter.removePrimitive", Params(new { name = "drop", index = 2 }));

        // A composite added without `in` reads SourceGraphic, and the wiring is then named rather than positional:
        // connecting it to `soft` gives it that buffer whatever the step before it produced.
        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "composite",
            in2 = "moved",
            @operator = "in",
            result = "shadow",
        }));
        Assert.Equal("SourceGraphic", context.Document.FindFilter("drop")!.Primitives[2].Input);

        EditorOperations.Invoke(context, "filter.connectPrimitive", Params(new
        {
            name = "drop",
            index = 2,
            @in = "soft",
        }));

        filter = context.Document.FindFilter("drop")!;
        Assert.Equal("soft", filter.Primitives[2].Input);
        Assert.Equal("moved", filter.Primitives[2].Input2);

        EditorOperations.Invoke(context, "filter.removePrimitive", Params(new { name = "drop", index = 2 }));
        Assert.Equal(2, context.Document.FindFilter("drop")!.Primitives.Count);
    }

    [Fact]
    public void AnInsertedPrimitiveLandsWhereTheIndexSays()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "gaussianBlur",
            index = 0,
            radius = 1.0,
        }));

        FilterSpec filter = context.Document.FindFilter("drop")!;
        Assert.Equal(3, filter.Primitives.Count);
        Assert.Equal(1.0, filter.Primitives[0].Radius, 6);
        Assert.Equal("soft", filter.Primitives[1].Result);
        Assert.Equal("moved", filter.Primitives[2].Result);
    }

    [Fact]
    public void AParameterIsChangedByTheNameTheDeclarationGivesIt()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new
        {
            name = "drop",
            index = 1,
            parameter = "dy",
            value = -4.5,
        }));

        FilterSpec filter = context.Document.FindFilter("drop")!;
        Assert.Equal(-4.5, filter.Primitives[1].Dy, 6);

        // Only the member named changes: the rest of the step is untouched.
        Assert.Equal(2.0, filter.Primitives[1].Dx, 6);
        Assert.Equal("soft", filter.Primitives[1].Input);
    }

    /// <summary>
    /// Colours go in and come out as bytes, the way every other operation takes one, so what
    /// <c>filter.list</c> reports can be sent straight back without a caller converting anything.
    /// </summary>
    [Fact]
    public void AColourIsTakenAndReportedAsBytes()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "flood",
            floodColor = new[] { 0, 128, 64 },
        }));

        FilterSpec filter = context.Document.FindFilter("drop")!;
        Assert.Equal(ColorRgb.FromBytes(0, 128, 64), filter.Primitives[2].FloodColor);

        JsonElement listed = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "filter.list", default));
        string json = listed.GetRawText();
        Assert.Contains("[0,128,64]", json, StringComparison.Ordinal);

        EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new
        {
            name = "drop",
            index = 2,
            parameter = "floodColor",
            value = new[] { 255, 0, 0 },
        }));

        Assert.Equal(ColorRgb.FromBytes(255, 0, 0), context.Document.FindFilter("drop")!.Primitives[2].FloodColor);
    }

    /// <summary>
    /// **The declaration is the panel.** Every kind it lists can be added and every parameter it declares can be
    /// set through the operations - which is what stops #133's panel being a hand-written switch that drifts from
    /// the engine.
    /// </summary>
    [Fact]
    public void EveryDeclaredKindAndParameterCanBeDrivenThroughTheOperations()
    {
        foreach (FilterPrimitiveDefinition definition in FilterPrimitiveRegistry.All)
        {
            (AutomationContext context, _) = Host();
            EditorOperations.Invoke(context, "filter.create", Params(new
            {
                name = "drop",
                primitives = new object[] { new { kind = "gaussianBlur", radius = 1.0, result = "base" } },
            }));

            var add = new Dictionary<string, object?> { ["name"] = "drop", ["kind"] = definition.Kind };
            foreach (FilterParameter parameter in definition.Required)
            {
                add[parameter.Name] = Sample(parameter);
            }

            add["index"] = 0;
            EditorOperations.Invoke(context, "filter.addPrimitive", Params(add));
            Assert.Equal(definition.ModelKind, context.Document.FindFilter("drop")!.Primitives[0].Kind);

            foreach (FilterParameter parameter in definition.Parameters)
            {
                if (parameter.Kind == FilterParameterKind.Buffer)
                {
                    continue;
                }

                EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new Dictionary<string, object?>
                {
                    ["name"] = "drop",
                    ["index"] = 0,
                    ["parameter"] = parameter.Name,
                    ["value"] = Sample(parameter),
                }));
            }

            EditorOperations.Invoke(context, "filter.removePrimitive", Params(new { name = "drop", index = 0 }));
            Assert.Equal("base", Assert.Single(context.Document.FindFilter("drop")!.Primitives).Result);
        }
    }

    /// <summary>A value the declaration allows: inside its range, and one of its choices when it has any.</summary>
    private static object Sample(FilterParameter parameter) => parameter.Kind switch
    {
        FilterParameterKind.Color => new[] { 10, 20, 30 },
        FilterParameterKind.Choice => parameter.Choices![0],
        _ => parameter.Maximum < 1.0 ? parameter.Maximum : parameter.Minimum <= 1.0 ? 1.0 : parameter.Minimum,
    };

    // ---------------------------------------------------------------- refusing what is not there

    [Fact]
    public void AnUnknownKindIsRefused()
    {
        (AutomationContext context, _) = Host();

        string? message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.addPrimitive", Params(new { name = "drop", kind = "feTurbulence", radius = 1.0 })));

        // A filter with no primitives paints nothing, so there is nothing to add to yet either.
        Assert.Contains("no filter called", message!, StringComparison.OrdinalIgnoreCase);

        Create(context);
        message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.addPrimitive", Params(new { name = "drop", kind = "feTurbulence", radius = 1.0 })));

        Assert.Contains("not a filter primitive", message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gaussianBlur", message!, StringComparison.Ordinal);
    }

    [Fact]
    public void AParameterTheKindDoesNotTakeIsRefused()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        // feFlood reads nothing, so an `in` on one is a caller who believed it did something.
        string? message = MessageOf(() => EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "flood",
            @in = "SourceAlpha",
        })));

        Assert.Contains("no parameter 'in'", message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARequiredParameterMustBeGiven()
    {
        (AutomationContext context, _) = Host();

        string? message = MessageOf(() => EditorOperations.Invoke(context, "filter.create", Params(new
        {
            name = "soft",
            primitives = new object[] { new { kind = "gaussianBlur" } },
        })));

        Assert.Contains("needs 'radius'", message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AValueTheDeclarationDoesNotAllowIsRefused()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "composite",
            @in = "soft",
            in2 = "moved",
        }));

        string? message = MessageOf(() => EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new
        {
            name = "drop",
            index = 2,
            parameter = "operator",
            value = "nonsense",
        })));

        Assert.Contains("is not one of", message!, StringComparison.OrdinalIgnoreCase);

        EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new
        {
            name = "drop",
            index = 2,
            parameter = "operator",
            value = "xor",
        }));

        Assert.Equal("xor", context.Document.FindFilter("drop")!.Primitives[2].Operator);

        // A radius below the declared minimum is the same kind of mistake as a word that is not an operator.
        message = MessageOf(() => EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new
        {
            name = "drop",
            index = 0,
            parameter = "radius",
            value = -1.0,
        })));

        Assert.Contains("at least 0", message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownParameterNameIsRefused()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string? message = MessageOf(() => EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new
        {
            name = "drop",
            index = 0,
            parameter = "colour",
            value = 1.0,
        })));

        Assert.Contains("no parameter 'colour'", message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The wiring is not a value: naming a buffer is <c>filter.connectPrimitive</c>'s job, and the refusal
    /// says so rather than setting it silently.</summary>
    [Fact]
    public void ABufferIsWiringRatherThanAValue()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string? message = MessageOf(() => EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new
        {
            name = "drop",
            index = 0,
            parameter = "in",
            value = "soft",
        })));

        Assert.Contains("connectPrimitive", message!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- graph integrity

    [Fact]
    public void ConnectingToABufferNothingProducesIsRefused()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string? message = MessageOf(() => EditorOperations.Invoke(context, "filter.connectPrimitive", Params(new
        {
            name = "drop",
            index = 0,
            @in = "ghost",
        })));

        Assert.Contains("ghost", message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anything produces", message!, StringComparison.OrdinalIgnoreCase);

        // And the filter is untouched: a refused edit is not a half-applied one.
        Assert.Equal("SourceAlpha", context.Document.FindFilter("drop")!.Primitives[0].Input);
    }

    [Fact]
    public void TwoStepsCannotClaimTheSameResult()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string? message = MessageOf(() => EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "gaussianBlur",
            radius = 1.0,
            result = "soft",
        })));

        Assert.Contains("one producer", message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWiringThatRunsInACircleIsRefused()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        // `soft` currently reads SourceAlpha; making it read `moved`, which reads `soft`, closes the loop.
        string? message = MessageOf(() => EditorOperations.Invoke(context, "filter.connectPrimitive", Params(new
        {
            name = "drop",
            index = 0,
            @in = "moved",
        })));

        Assert.Contains("circle", message!, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.Document.FindFilter("drop")!.HasCycle);
    }

    /// <summary>
    /// **A step another step reads is not removed.** Removing its producer would leave the consumer reading a
    /// buffer nothing makes - a transparent one at draw time - and the picture would change somewhere the edit
    /// does not point at.
    /// </summary>
    [Fact]
    public void RemovingAStepAnotherStepReadsIsRefused()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string? message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.removePrimitive", Params(new { name = "drop", index = 0 })));

        Assert.Contains("soft", message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing makes", message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, context.Document.FindFilter("drop")!.Primitives.Count);
    }

    [Fact]
    public void RemovingTheLastPrimitiveIsRefused()
    {
        (AutomationContext context, _) = Host();
        EditorOperations.Invoke(context, "filter.create", Params(new
        {
            name = "soft",
            primitives = new object[] { new { kind = "gaussianBlur", radius = 1.0 } },
        }));

        string? message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.removePrimitive", Params(new { name = "soft", index = 0 })));

        Assert.Contains("delete the filter instead", message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnIndexOutsideTheGraphIsRefused()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string? message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.removePrimitive", Params(new { name = "drop", index = 7 })));

        Assert.Contains("no primitive 7", message!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the region, and the asset

    [Fact]
    public void TheRegionAndTheOutputAreEditable()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        EditorOperations.Invoke(context, "filter.setRegion", Params(new
        {
            name = "drop",
            x = -0.5,
            y = -0.5,
            width = 2.0,
            height = 2.0,
            userSpace = false,
            output = "moved",
        }));

        FilterSpec filter = context.Document.FindFilter("drop")!;
        Assert.Equal(-0.5, filter.X, 6);
        Assert.Equal(2.0, filter.Width, 6);
        Assert.True(filter.ObjectBoundingBox);
        Assert.Equal("moved", filter.Output);

        // Only the members given change.
        EditorOperations.Invoke(context, "filter.setRegion", Params(new { name = "drop", userSpace = true }));

        filter = context.Document.FindFilter("drop")!;
        Assert.False(filter.ObjectBoundingBox);
        Assert.Equal(2.0, filter.Width, 6);

        string? message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.setRegion", Params(new { name = "drop", width = 0.0 })));

        Assert.Contains("no area", message!, StringComparison.OrdinalIgnoreCase);

        message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.setRegion", Params(new { name = "drop", output = "nothing" })));

        Assert.Contains("no answer", message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeletingAFilterClearsTheReferenceAndReportsIt()
    {
        (AutomationContext context, PathItem path) = Host();
        Create(context);
        EditorOperations.Invoke(context, "filter.apply", Params(new { name = "drop" }));
        Assert.Equal("drop", path.FilterId);

        JsonElement result = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "filter.delete", Params(new { name = "drop" })));

        Assert.Null(context.Document.FindFilter("drop"));
        Assert.Null(path.FilterId);

        // Nothing is left pointing at the filter that went, so the document has no missing asset to report.
        Assert.Empty(context.Document.MissingFilters());
        Assert.Contains("\"cleared\":1", result.GetRawText(), StringComparison.Ordinal);

        string? message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.delete", Params(new { name = "drop" })));
        Assert.Contains("no filter called", message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The registry is readable through the operations, so a driver can build a panel without one.</summary>
    [Fact]
    public void TheDeclarationIsReadableThroughTheOperations()
    {
        (AutomationContext context, _) = Host();

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "filter.kinds", default));

        Assert.Contains("\"element\":\"feGaussianBlur\"", json, StringComparison.Ordinal);
        Assert.Contains("\"required\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"choices\":[\"over\",\"in\",\"out\",\"atop\",\"xor\",\"arithmetic\"]", json,
            StringComparison.Ordinal);
    }
}
