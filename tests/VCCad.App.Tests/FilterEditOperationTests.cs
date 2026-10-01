using System.Globalization;
using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
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
    /// set through the operations, and the value is read back **off the model** - which is what stops #133's panel
    /// being a hand-written switch that drifts from the engine, and what makes a kind added to the registry
    /// without a build path fail here instead of being quietly accepted.
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

                object sample = Sample(parameter);
                EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new Dictionary<string, object?>
                {
                    ["name"] = "drop",
                    ["index"] = 0,
                    ["parameter"] = parameter.Name,
                    ["value"] = sample,
                }));

                // Read off the record rather than off the operation's own answer: `filter.list` echoing a value
                // back is not evidence that the model holds it.
                FilterPrimitive after = context.Document.FindFilter("drop")!.Primitives[0];
                Assert.Equal(Text(sample), Text(Read(after, parameter.Name)));
            }

            EditorOperations.Invoke(context, "filter.removePrimitive", Params(new { name = "drop", index = 0 }));
            Assert.Equal("base", Assert.Single(context.Document.FindFilter("drop")!.Primitives).Result);
        }
    }

    /// <summary>
    /// **The declaration and the operations as two lists, compared.**
    ///
    /// `filter.kinds` is only a promise about what a driver can build until every entry in it is driven through
    /// `filter.addPrimitive` and the kind that landed is read back. A declared kind with no build path - or, worse,
    /// a build path that answers with some **other** kind - fails here rather than being accepted quietly, which is
    /// what the arms that invented a `feBlend` did.
    /// </summary>
    [Fact]
    public void TheKindsTheDeclarationListsAreTheKindsTheOperationsBuild()
    {
        (AutomationContext context, _) = Host();
        EditorOperations.Invoke(context, "filter.create", Params(new
        {
            name = "drop",
            primitives = new object[] { new { kind = "gaussianBlur", radius = 1.0, result = "base" } },
        }));

        string[] declared = FilterPrimitiveRegistry.All.Select(definition => definition.Kind).ToArray();

        var built = new List<string>();
        foreach (FilterPrimitiveDefinition definition in FilterPrimitiveRegistry.All)
        {
            var add = new Dictionary<string, object?> { ["name"] = "drop", ["kind"] = definition.Kind };
            foreach (FilterParameter parameter in definition.Required)
            {
                add[parameter.Name] = Sample(parameter);
            }

            EditorOperations.Invoke(context, "filter.addPrimitive", Params(add));

            FilterPrimitive landed = context.Document.FindFilter("drop")!.Primitives[^1];
            built.Add(FilterPrimitiveRegistry.Find(landed.Kind.ToString())?.Kind ?? landed.Kind.ToString());
        }

        Assert.Equal(declared, built);
    }

    /// <summary>
    /// A parameter the request does not carry takes the **declaration's** default, not the record's own.
    ///
    /// The two are not always the same word, and one record cannot start life holding two kinds' defaults at once:
    /// a morphology added with no operator would otherwise be `operator="over"`, an operator `feMorphology` does not
    /// have and one the SVG export would then write out.
    /// </summary>
    [Fact]
    public void AStepWithNoValueForAParameterTakesTheDeclarationsDefault()
    {
        (AutomationContext context, _) = Host();
        EditorOperations.Invoke(context, "filter.create", Params(new
        {
            name = "drop",
            primitives = new object[] { new { kind = "gaussianBlur", radius = 1.0, result = "base" } },
        }));

        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "morphology",
            radius = 2.0,
        }));

        FilterPrimitive morph = context.Document.FindFilter("drop")!.Primitives[^1];
        Assert.Equal(FilterPrimitiveKind.Morphology, morph.Kind);
        Assert.Equal("erode", morph.Operator);

        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "colorMatrix",
            values = 0.35,
        }));

        FilterPrimitive matrix = context.Document.FindFilter("drop")!.Primitives[^1];
        Assert.Equal(FilterPrimitiveKind.ColorMatrix, matrix.Kind);
        Assert.Equal("matrix", matrix.Type);
    }

    /// <summary>
    /// A value the declaration allows for a parameter, and **not** the default it declares - so a value that
    /// reached the wrong member, or never reached the model at all, is caught rather than matching what was there.
    /// </summary>
    private static object Sample(FilterParameter parameter) => parameter.Kind switch
    {
        FilterParameterKind.Color => new[] { 10, 20, 30 },
        // The last word rather than the first, which is the one every kind declares as its default.
        FilterParameterKind.Choice => parameter.Choices![^1],
        // A colour matrix's `values` is the twenty numbers, which a shorthand's single amount is not.
        _ when parameter.Name.Equals("values", StringComparison.OrdinalIgnoreCase) =>
            Enumerable.Range(1, 20).Select(number => (double)number).ToArray(),
        _ => Whole(parameter),
    };

    /// <summary>
    /// A whole number the declaration allows that is not its default.
    ///
    /// Whole because two declared parameters - `numOctaves` and `seed` - are integers in the model, so a sample of
    /// 2.5 would be rounded on the way in and fail a read-back that is about the member and not the rounding.
    /// </summary>
    private static double Whole(FilterParameter parameter)
    {
        double declared = parameter.Default is { Length: > 0 } text &&
                          double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0.0;

        return declared + 1 <= parameter.Maximum ? declared + 1 : declared - 1;
    }

    /// <summary>
    /// The model member a declared parameter name stands for - the read half of the map the operation keeps as its
    /// write half, because the declaration names a parameter by the word a caller sets it with.
    /// </summary>
    private static object? Read(FilterPrimitive primitive, string parameter) => parameter switch
    {
        "radius" => primitive.Radius,
        "dx" => primitive.Dx,
        "dy" => primitive.Dy,
        "floodColor" => primitive.FloodColor is { } flood ? Bytes(flood) : null,
        "floodOpacity" => primitive.FloodOpacity,
        "operator" => primitive.Operator,
        "mode" => primitive.Mode,
        "type" => primitive.Type,
        "values" => primitive.Matrix,
        "scale" => primitive.Scale,
        "xChannel" => primitive.XChannel,
        "yChannel" => primitive.YChannel,
        "baseFrequency" => primitive.BaseFrequency,
        "numOctaves" => (double)primitive.Octaves,
        "seed" => (double)primitive.Seed,
        "surfaceScale" => primitive.SurfaceScale,
        "diffuseConstant" => primitive.DiffuseConstant,
        "specularConstant" => primitive.SpecularConstant,
        "specularExponent" => primitive.SpecularExponent,
        "lightingColor" => primitive.LightingColor is { } light ? Bytes(light) : null,
        "azimuth" => primitive.Azimuth,
        "elevation" => primitive.Elevation,
        _ => throw new InvalidOperationException($"this test has no reader for the parameter '{parameter}'"),
    };

    private static int[] Bytes(ColorRgb colour) => new[]
    {
        (int)Math.Round(Math.Clamp(colour.R, 0.0, 1.0) * 255),
        (int)Math.Round(Math.Clamp(colour.G, 0.0, 1.0) * 255),
        (int)Math.Round(Math.Clamp(colour.B, 0.0, 1.0) * 255),
    };

    /// <summary>A value as one string, so a sample and the member it should have reached compare whatever sort
    /// they are: a number, a declared word, a colour's bytes or a matrix's twenty numbers.</summary>
    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        double number => number.ToString("0.####", CultureInfo.InvariantCulture),
        int whole => whole.ToString(CultureInfo.InvariantCulture),
        int[] bytes => string.Join(",", bytes),
        double[] numbers => string.Join(",", numbers.Select(number => number.ToString("0.####", CultureInfo.InvariantCulture))),
        string text => text,
        object other => other.ToString() ?? string.Empty,
    };

    // ---------------------------------------------------------------- refusing what is not there

    [Fact]
    public void AnUnknownKindIsRefused()
    {
        (AutomationContext context, _) = Host();

        // `feTile` is a real SVG primitive this build has not got. `feTurbulence` used to stand here and cannot any
        // more: it is the element name of a kind the registry now declares, and a declaration answers to the element
        // name as well as the kind name - so it is a known kind, not an example of an unknown one.
        string? message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.addPrimitive", Params(new { name = "drop", kind = "feTile", radius = 1.0 })));

        // A filter with no primitives paints nothing, so there is nothing to add to yet either.
        Assert.Contains("no filter called", message!, StringComparison.OrdinalIgnoreCase);

        Create(context);
        message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.addPrimitive", Params(new { name = "drop", kind = "feTile", radius = 1.0 })));

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

    // ------------------------------------------ what a primitive's lengths mean, and the resolution

    /// <summary>
    /// **The two settings the model carried and no operation could reach.**
    ///
    /// `primitiveUnits` decides whether a primitive's own length is a length in the document or a fraction of the
    /// shape's box, and `filterRes` decides the pixel resolution the filter is evaluated at. Both are read and
    /// written through the operations, and both have to survive the sidecar: a setting a driver can write and not
    /// find again is not a capability.
    /// </summary>
    [Fact]
    public void PrimitiveUnitsAndFilterResolutionReadBackAndSurviveSaveAndReload()
    {
        (AutomationContext context, _) = Host();

        EditorOperations.Invoke(context, "filter.create", Params(new
        {
            name = "coarse",
            primitives = new object[]
            {
                new { kind = "gaussianBlur", @in = "SourceAlpha", radius = 0.1 },
            },
            primitiveUnits = "objectBoundingBox",
            filterRes = 12,
        }));

        // Read off the model first, so a `filter.list` that echoes the request cannot carry the test on its own.
        FilterSpec filter = context.Document.FindFilter("coarse")!;
        Assert.True(filter.PrimitiveUnitsObjectBoundingBox);
        Assert.Equal(12, filter.FilterResolutionX);
        Assert.Equal(12, filter.FilterResolutionY);

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "filter.list", default));
        Assert.Contains("\"primitiveUnits\":\"objectBoundingBox\"", json, StringComparison.Ordinal);
        Assert.Contains("\"filterRes\":[12,12]", json, StringComparison.Ordinal);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(context.Document));

        FilterSpec back = reloaded.FindFilter("coarse")!;
        Assert.Equal(filter, back);
        Assert.True(back.PrimitiveUnitsObjectBoundingBox);
        Assert.Equal(12, back.FilterResolutionY);
    }

    /// <summary>
    /// The region operation carries them too, and clears the resolution when asked: a filter that named one and
    /// should not is as much a state a driver has to be able to leave as one it has to be able to enter.
    /// </summary>
    [Fact]
    public void TheRegionOperationCarriesThePrimitiveUnitsAndTheResolution()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        FilterSpec before = context.Document.FindFilter("drop")!;
        Assert.False(before.PrimitiveUnitsObjectBoundingBox);
        Assert.Null(before.FilterResolutionX);

        EditorOperations.Invoke(context, "filter.setRegion", Params(new
        {
            name = "drop",
            primitiveUnits = "objectBoundingBox",
            filterRes = new[] { 16, 9 },
        }));

        FilterSpec filter = context.Document.FindFilter("drop")!;
        Assert.True(filter.PrimitiveUnitsObjectBoundingBox);
        Assert.Equal(16, filter.FilterResolutionX);
        Assert.Equal(9, filter.FilterResolutionY);
        Assert.Equal(before.Output, filter.Output);

        // The same member, spelled the other way round: one number is both axes, as SVG writes it.
        EditorOperations.Invoke(context, "filter.setRegion", Params(new { name = "drop", filterRes = 24 }));

        filter = context.Document.FindFilter("drop")!;
        Assert.Equal(24, filter.FilterResolutionX);
        Assert.Equal(24, filter.FilterResolutionY);

        // And null clears it, so the caller's own scale decides again rather than leaving a resolution nobody asked
        // for pinned to the filter for the rest of the document's life.
        EditorOperations.Invoke(context, "filter.setRegion", Params(new { name = "drop", filterRes = (int?)null }));

        filter = context.Document.FindFilter("drop")!;
        Assert.Null(filter.FilterResolutionX);
        Assert.Null(filter.FilterResolutionY);
        Assert.False(filter.HasFilterResolution);
    }

    /// <summary>
    /// **A resolution this build will not allocate is refused by name rather than stored.**
    ///
    /// A resolution is a request to allocate the region at that size, and the engine refuses one it cannot make - but
    /// a filter is evaluated while the canvas paints, so a model that carried one would throw inside the renderer
    /// and take the artwork with it. The refusal has to happen where the reason can be said, and the model has to be
    /// left exactly as it was, or the caller is handed back a filter that throws on the next frame.
    /// </summary>
    [Fact]
    public void AResolutionThisBuildWillNotAllocateIsRefusedByName()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string? message = MessageOf(() => EditorOperations.Invoke(context, "filter.create", Params(new
        {
            name = "huge",
            primitives = new object[]
            {
                new { kind = "gaussianBlur", @in = "SourceAlpha", radius = 2.0 },
            },
            filterRes = 100_000,
        })));

        Assert.Contains("filterRes", message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8192", message!, StringComparison.Ordinal);
        Assert.Null(context.Document.FindFilter("huge"));

        // The same bound on the way in through filter.setRegion, which must leave the existing filter alone.
        message = MessageOf(() => EditorOperations.Invoke(context, "filter.setRegion", Params(new
        {
            name = "drop",
            filterRes = new[] { 9000, 10 },
        })));

        Assert.Contains("filterRes", message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8192", message!, StringComparison.Ordinal);

        FilterSpec untouched = context.Document.FindFilter("drop")!;
        Assert.Null(untouched.FilterResolutionX);
        Assert.Null(untouched.FilterResolutionY);

        // Zero and a negative are the other end of the same rule: a filter that allocates no pixels paints nothing.
        message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.setRegion", Params(new { name = "drop", filterRes = 0 })));

        Assert.Contains("8192", message!, StringComparison.Ordinal);
        Assert.Null(context.Document.FindFilter("drop")!.FilterResolutionX);
    }

    /// <summary>
    /// A `primitiveUnits` that is not one of SVG's two is refused rather than defaulted, and a non-string is not
    /// quietly read as "absent" - both are values the caller meant, and guessing which is how a filter comes back
    /// measuring its blur in the wrong unit.
    /// </summary>
    [Fact]
    public void AnUnknownPrimitiveUnitsIsRefused()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string? message = MessageOf(() => EditorOperations.Invoke(
            context, "filter.setRegion", Params(new { name = "drop", primitiveUnits = "boundingBox" })));

        Assert.Contains("objectBoundingBox", message!, StringComparison.Ordinal);
        Assert.False(context.Document.FindFilter("drop")!.PrimitiveUnitsObjectBoundingBox);
    }
}
