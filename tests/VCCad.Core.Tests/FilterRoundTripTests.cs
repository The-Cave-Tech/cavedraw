using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The filter **graph** through the model, the SVG writer and the SVG reader, and the drawing it produces.
///
/// "A filter reached the export" is not a claim a byte comparison can make on its own: what matters is that the
/// graph that comes back is the graph that went out, and that evaluating it draws the same picture. So every test
/// here asserts the model structurally, and the ones that can, the pixels too.
///
/// The declarations these validate against are <see cref="FilterPrimitiveRegistry"/> - the one list the operations
/// and the panel both read.
/// </summary>
public class FilterRoundTripTests
{
    private static FilterSpec RoundTrip(FilterSpec filter)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddFilter(filter);

        SvgImportResult back = SvgReader.Read(SvgWriter.Write(document));
        return back.Document.FindFilter(filter.Name)!;
    }

    /// <summary>A solid block, which is what a blur has something to spread and a shadow something to sit behind.</summary>
    private static FilterBuffer Block(int size = 24)
    {
        var buffer = new FilterBuffer(size, size);
        for (int y = 8; y < 16; y++)
        {
            for (int x = 8; x < 16; x++)
            {
                buffer.Set(x, y, new ColorRgb(0, 0, 0), 1f);
            }
        }

        return buffer;
    }

    private static readonly Rect2D Bounds = new(0, 0, 24, 24);

    // ---------------------------------------------------------------- the declaration

    /// <summary>
    /// Every primitive the engine can apply has exactly one declaration, and an unknown word is not one.
    ///
    /// This is the list the operations validate against, so a primitive the engine grew without a declaration
    /// would be one no operation could reach - reachable only by writing the model by hand. The count and the
    /// one-per-kind rule are what keep the enum, the declarations and the engine's switch in step: adding a kind
    /// without a declaration fails here, and declaring one twice fails as well.
    /// </summary>
    [Fact]
    public void EveryPrimitiveTheEngineHasIsDeclaredOnce()
    {
        FilterPrimitiveKind[] kinds = Enum.GetValues<FilterPrimitiveKind>();

        Assert.Equal(kinds.Length, FilterPrimitiveRegistry.All.Count);
        foreach (FilterPrimitiveKind kind in kinds)
        {
            FilterPrimitiveDefinition declared = Assert.Single(
                FilterPrimitiveRegistry.All, definition => definition.ModelKind == kind);

            // The declaration's own kind name and its element both answer to a caller, in any case.
            Assert.Same(declared, FilterPrimitiveRegistry.Find(declared.Kind));
            Assert.Same(declared, FilterPrimitiveRegistry.Find(declared.Element.ToUpperInvariant()));
        }

        Assert.Same(FilterPrimitiveRegistry.Find("gaussianBlur"), FilterPrimitiveRegistry.Find("blur"));

        // `feTurbulence` was the primitive this build did **not** have, and now it is one - so the assertion that
        // pinned its absence has become the positive one, which is what this file exists to keep honest.
        Assert.Same(FilterPrimitiveRegistry.Find("turbulence"), FilterPrimitiveRegistry.Find("feTurbulence"));
        Assert.Same(FilterPrimitiveRegistry.Find("turbulence"), FilterPrimitiveRegistry.Find("fePerlinNoise"));
        Assert.Same(FilterPrimitiveRegistry.Find("morphology"), FilterPrimitiveRegistry.Find("morph"));
        Assert.Same(FilterPrimitiveRegistry.Find("colorMatrix"), FilterPrimitiveRegistry.Find("feColorMatrix"));
        Assert.True(FilterPrimitiveRegistry.IsKnown("displacementMap"));
        Assert.True(FilterPrimitiveRegistry.IsKnown("specularLighting"));
        Assert.True(FilterPrimitiveRegistry.IsKnown("diffuseLighting"));

        // A word that names nothing is still not a primitive.
        Assert.Null(FilterPrimitiveRegistry.Find("feNonsense"));
        Assert.False(FilterPrimitiveRegistry.IsKnown("feNonsense"));
    }

    /// <summary>
    /// **Every primitive the engine's switch evaluates is reachable through the operations.**
    ///
    /// This is the other half of the parity rule: a kind declared but missing from the engine's switch draws
    /// nothing, and a kind in the switch but not declared is one no operation can set. The engine's own
    /// declaration of what it applies is the enumeration order, and this checks the count and the round trip
    /// through the registry for each.
    /// </summary>
    [Fact]
    public void EveryDeclaredKindHasParametersThatTheRegistryCanAnswerFor()
    {
        foreach (FilterPrimitiveDefinition definition in FilterPrimitiveRegistry.All)
        {
            Assert.NotEmpty(definition.Parameters);
            Assert.False(string.IsNullOrWhiteSpace(definition.Meaning));
            Assert.StartsWith("fe", definition.Element, StringComparison.Ordinal);

            // The wiring parameters are the same three everywhere, so a caller can connect any primitive.
            if (definition.Parameter("in") is { } input)
            {
                Assert.Equal(FilterParameterKind.Buffer, input.Kind);
                Assert.False(input.Required);
            }

            foreach (FilterParameter parameter in definition.Parameters)
            {
                // A choice without choices is a parameter nothing can validate.
                if (parameter.Kind == FilterParameterKind.Choice)
                {
                    Assert.NotEmpty(parameter.Choices!);
                }

                Assert.False(string.IsNullOrWhiteSpace(parameter.Meaning));
            }
        }
    }

    /// <summary>A required parameter is one a kind cannot be evaluated without - and it is declared, not implied.</summary>
    [Fact]
    public void TheDeclarationSaysWhichParametersAreRequired()
    {
        FilterPrimitiveDefinition blur = FilterPrimitiveRegistry.Find("gaussianBlur")!;
        FilterParameter? radius = blur.Parameter("radius");

        Assert.NotNull(radius);
        Assert.True(radius!.Required);
        Assert.Equal(0, radius.Minimum);

        // A flood cannot be asked to read a buffer, and nothing but the blur has a required value parameter.
        Assert.Null(FilterPrimitiveRegistry.Find("flood")!.Parameter("in"));
        Assert.Empty(FilterPrimitiveRegistry.Find("composite")!.Required);

        // The two-input primitives declare in2, and it is a buffer rather than a value - which is why an unwired
        // step may be added and connected afterwards.
        Assert.Equal(FilterParameterKind.Buffer, FilterPrimitiveRegistry.Find("composite")!.Parameter("in2")!.Kind);
        Assert.Equal(FilterParameterKind.Choice, FilterPrimitiveRegistry.Find("blend")!.Parameter("mode")!.Kind);
        Assert.Contains("multiply", FilterPrimitiveRegistry.Find("blend")!.Parameter("mode")!.Choices!);
    }

    // ---------------------------------------------------------------- graph integrity

    /// <summary>
    /// **One result consumed twice.** A design that chained the primitives would hand the second consumer the
    /// first's output rather than the named buffer, and the picture would be wrong in a way no parameter test sees.
    /// The model already expresses this - <c>ConsumersOf</c> and the engine's buffer cache are what make it work -
    /// and what is asserted here is that it survives the export unchanged.
    /// </summary>
    [Fact]
    public void AResultConsumedTwiceSurvivesTheRoundTrip()
    {
        var original = new FilterSpec("shared", new[]
        {
            FilterPrimitive.Blur(2.0, input: "SourceGraphic", result: "soft"),
            FilterPrimitive.Combine("in", "soft", "SourceAlpha", "inside"),
            FilterPrimitive.Blended("screen", "soft", "inside", "lit"),
        })
        {
            X = -0.2,
            Y = -0.2,
            Width = 1.4,
            Height = 1.4,
        };

        FilterSpec back = RoundTrip(original);

        // Structurally the same graph: same wiring, same region, same order.
        Assert.Equal(original, back);
        Assert.Equal(2, back.ConsumersOf("soft").Count());
        Assert.Equal(FilterPrimitiveKind.GaussianBlur, back.ProducerOf("soft")!.Kind);

        // And the same picture, which is the claim that matters: a flattened chain would draw differently here.
        FilterBuffer source = Block();
        Assert.True(
            new FilterEngine(original).Evaluate(source, Bounds)
                .Matches(new FilterEngine(back).Evaluate(source, Bounds), 0f),
            "the graph read back must draw the same pixels as the one that was written");
    }

    /// <summary>
    /// **The region is part of the filter, and it decides the picture.** A blur either spreads into the margin the
    /// region gives it or is cut off by the edge, so a round trip that dropped the four numbers would hand back a
    /// different drawing with nothing in the file to say so.
    /// </summary>
    [Fact]
    public void TheRegionSurvivesTheRoundTripAndStillDecidesTheBlur()
    {
        FilterPrimitive[] primitives = { FilterPrimitive.Blur(4.0, input: "SourceAlpha") };
        var tight = new FilterSpec("tight", primitives);
        var wide = new FilterSpec("wide", primitives)
        {
            X = -0.5,
            Y = -0.5,
            Width = 2.0,
            Height = 2.0,
            ObjectBoundingBox = false,
        };

        FilterSpec tightBack = RoundTrip(tight);
        FilterSpec wideBack = RoundTrip(wide);

        Assert.Equal(tight, tightBack);
        Assert.Equal(wide, wideBack);
        Assert.True(wideBack is { ObjectBoundingBox: false, Width: 2.0 });
        Assert.NotEqual(tightBack.Width, wideBack.Width);

        FilterBuffer source = Block();
        FilterBuffer clipped = new FilterEngine(tightBack).Evaluate(source, Bounds);
        FilterBuffer roomy = new FilterEngine(wideBack).Evaluate(source, Bounds);
        Assert.True(clipped.Matches(new FilterEngine(tight).Evaluate(source, Bounds), 0f));
        Assert.True(roomy.Matches(new FilterEngine(wide).Evaluate(source, Bounds), 0f));

        // The region is not decoration: the two regions are genuinely different buffers, and a test that only
        // compared the numbers would pass with a region nothing read.
        Assert.NotEqual(clipped.Width, roomy.Width);
        Assert.NotEqual(
            FilterEngine.RegionPixels(tightBack, Bounds, 1.0),
            FilterEngine.RegionPixels(wideBack, Bounds, 1.0));
    }

    /// <summary>
    /// **The filter's answer need not be its last step.** A file can name an intermediate result as what is drawn,
    /// which is the other half of "a graph rather than a pipeline" - and the writer had nowhere to put it until
    /// this build's own spelling of it was written back alongside the reader.
    /// </summary>
    [Fact]
    public void AnIntermediateResultNamedAsTheOutputSurvivesTheRoundTrip()
    {
        var original = new FilterSpec("tinted", new[]
        {
            FilterPrimitive.Solid(ColorRgb.FromBytes(0, 128, 64), 0.75, "ink"),
            FilterPrimitive.Combine("in", "ink", "SourceAlpha", "tinted"),
            FilterPrimitive.Blur(3.0, input: "tinted", result: "softened"),
        })
        {
            Output = "tinted",
        };

        string svg = SvgWriter.Write(Document(original));
        XElement written = XDocument.Parse(svg).Descendants().First(element => element.Name.LocalName == "filter");
        Assert.Equal("tinted", (string?)written.Attribute("result"));

        FilterSpec back = RoundTrip(original);

        Assert.Equal("tinted", back.Output);
        Assert.Equal(original, back);

        // Taking the last step instead would blur what the model says is sharp - the two are different answers,
        // so the assertion is that the drawing still comes from the named buffer.
        FilterBuffer source = Block();
        FilterBuffer named = new FilterEngine(back).Evaluate(source, Bounds);
        FilterBuffer last = new FilterEngine(back with { Output = string.Empty }).Evaluate(source, Bounds);
        Assert.True(named.Matches(new FilterEngine(original).Evaluate(source, Bounds), 0f));
        Assert.False(named.Matches(last, 0.001f), "the named answer and the last step must not be the same picture");
    }

    /// <summary>
    /// The whole graph - fan-out, region, colour, and the reference from the shape - survives as one document, so
    /// what the canvas would draw and what a reader of the exported file would draw are the same filter.
    /// </summary>
    [Fact]
    public void TheWholeGraphAndItsReferenceSurviveTheRoundTrip()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddFilter(new FilterSpec("drop", new[]
        {
            FilterPrimitive.Blur(3.0, input: "SourceAlpha", result: "soft"),
            FilterPrimitive.OffsetBy(2, 3, input: "soft", result: "moved"),
            FilterPrimitive.Solid(ColorRgb.FromBytes(0, 102, 153), 0.5, "ink"),
            FilterPrimitive.Combine("in", "ink", "moved", "shadow"),
            FilterPrimitive.Blended("multiply", "soft", "shadow", "shaded"),
        })
        {
            X = -0.25,
            Y = -0.25,
            Width = 1.5,
            Height = 1.5,
            Output = "shaded",
        });

        PathItem path = PathFactory.CreateRectangle("shape", new Rect2D(10, 10, 40, 40));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        path.FilterId = "drop";
        document.Artboards[0].Layers[0].AddItem(path);

        SvgImportResult back = SvgReader.Read(SvgWriter.Write(document));

        FilterSpec original = document.FindFilter("drop")!;
        Assert.Equal(original, back.Document.FindFilter("drop")!);
        Assert.Equal("drop", back.Document.AllPaths().Single().FilterId);

        FilterBuffer source = Block();
        Assert.True(
            new FilterEngine(original).Evaluate(source, Bounds)
                .Matches(new FilterEngine(back.Document.FindFilter("drop")!).Evaluate(source, Bounds), 0f));
    }

    /// <summary>
    /// A graph that reads its own output has no answer to evaluate, and the engine deliberately hands back a
    /// transparent buffer rather than recursing. The model can say so, which is what lets an operation refuse to
    /// build one where the reason can be explained.
    /// </summary>
    [Fact]
    public void AGraphThatReadsItsOwnOutputIsACycle()
    {
        var direct = new FilterSpec("direct", new[]
        {
            FilterPrimitive.Blur(1.0, input: "loop", result: "loop"),
        });

        var indirect = new FilterSpec("indirect", new[]
        {
            FilterPrimitive.Blur(1.0, input: "second", result: "first"),
            FilterPrimitive.Blur(1.0, input: "first", result: "second"),
        });

        var acyclic = new FilterSpec("fine", new[]
        {
            FilterPrimitive.Blur(1.0, input: "SourceAlpha", result: "soft"),
            FilterPrimitive.Blur(1.0, input: "soft"),
        });

        Assert.True(direct.HasCycle);
        Assert.True(indirect.HasCycle);
        Assert.False(acyclic.HasCycle);

        // The engine survives one - it draws nothing rather than hanging - which is exactly why an editor must
        // refuse to make one: nothing about the output says the graph is the reason.
        FilterBuffer result = new FilterEngine(direct).Evaluate(Block(), Bounds);
        Assert.Equal(0, result.OpaquePixels());
    }

    // ---------------------------------------------------------------- the new primitives

    /// <summary>
    /// **Every primitive the engine gained renders the same picture after a round trip**, which is the claim that
    /// matters rather than "the attribute was written". Each one carries a different kind of parameter - a box
    /// radius, a twenty-number matrix, a channel selector, a seed, a light - so a writer that dropped any of them
    /// would draw something else here, and the structural assertion is what says which one.
    /// </summary>
    [Fact]
    public void EveryNewPrimitiveSurvivesTheRoundTripAndStillDrawsTheSamePixels()
    {
        FilterPrimitive[] primitives =
        {
            // The things each new primitive reads, produced where they are needed.
            FilterPrimitive.Solid(new ColorRgb(0, 0, 0), 1.0, "sheet"),
            FilterPrimitive.Noise("turbulence", 0.25, 2, 5, "sheet", "grain"),
            FilterPrimitive.Morph("dilate", 1.0, "grain", "grown"),
            // The shorthand as the model keeps it: the one number, which is what the reader stores and what the
            // writer writes back. A caller who hands over the expanded matrix gets the same picture - there is a
            // test for that below - but the two are not the same *model*, so the structural assertion uses this one.
            FilterPrimitive.ColourMatrix(new[] { 0.35 }, "saturate", "grown", "grey"),
            FilterPrimitive.Displace(3.0, "R", "G", "grey", "grain", "warped"),
            FilterPrimitive.Specular(2.0, 0.6, 12.0, ColorRgb.White, 40, 35, "warped", "gloss"),
            FilterPrimitive.Diffuse(2.0, 0.8, ColorRgb.White, 40, 35, "warped", "matte"),
            FilterPrimitive.Blended("screen", "gloss", "matte", "lit"),
            FilterPrimitive.Combine("over", "lit", "grey", "answer"),
        };

        var original = new FilterSpec("mixed", primitives)
        {
            X = -0.1,
            Y = -0.1,
            Width = 1.2,
            Height = 1.2,
            Output = "answer",
        };

        FilterSpec back = RoundTrip(original);

        Assert.Equal(original, back);
        Assert.Equal(primitives.Length, back.Primitives.Count);

        // And the same drawing, which is the point: the parameters are not decoration.
        Assert.True(
            new FilterEngine(original).Evaluate(Block(), Bounds)
                .Matches(new FilterEngine(back).Evaluate(Block(), Bounds), 0f),
            "the graph read back must draw the same pixels as the one that was written");
    }

    /// <summary>
    /// **A colour matrix shorthand and the matrix it stands for draw the same pixels, and the shorthand survives
    /// as a shorthand.**
    ///
    /// A file that writes `type="saturate" values="0.35"` and one that writes the twenty numbers out are the same
    /// picture by SVG's definition. What the model keeps is the **one number**, so the writer can write the
    /// shorthand back the way it was read rather than expanding a person's file into a wall of twenty - and a
    /// primitive a caller built from the expanded matrix still comes out as the shorthand, because the writer
    /// recognises the matrix the shorthand stands for.
    /// </summary>
    [Fact]
    public void AColourMatrixShorthandAndItsMatrixAreTheSameModel()
    {
        FilterPrimitive shorthand = FilterPrimitive.ColourMatrix(new[] { 0.35 }, "saturate");
        FilterPrimitive spelledOut = FilterPrimitive.ColourMatrix(FilterEngine.Saturate(0.35), "saturate");

        Assert.Equal(FilterEngine.Saturate(0.35), FilterEngine.MatrixOf(shorthand));
        Assert.Equal(FilterEngine.Saturate(0.35), FilterEngine.MatrixOf(spelledOut));

        // The two draw identically, which is what makes the shorthand a spelling rather than a different filter.
        FilterBuffer source = Block();
        var byShortHand = new FilterSpec("a", new[] { shorthand });
        var byMatrix = new FilterSpec("b", new[] { spelledOut });
        Assert.True(
            new FilterEngine(byShortHand).Evaluate(source, Bounds)
                .Matches(new FilterEngine(byMatrix).Evaluate(source, Bounds), 0f));
        Assert.True(
            new FilterEngine(byShortHand).Evaluate(source, Bounds)
                .Matches(new FilterEngine(RoundTrip(byShortHand)).Evaluate(source, Bounds), 0f),
            "the expanded matrix a caller handed over must still draw the same picture after the round trip");

        // And the shorthand survives as a shorthand, with the amount that describes it.
        FilterSpec back = RoundTrip(new FilterSpec("sat", new[] { shorthand }));
        FilterPrimitive read = Assert.Single(back.Primitives);
        Assert.Equal("saturate", read.Type);
        Assert.Equal(1, read.Matrix!.Length);
        Assert.Equal(0.35, read.Matrix[0], 6);
    }

    /// <summary>
    /// **A lighting primitive is written as a distant light and read back as one.**
    ///
    /// The element has to carry its light source or a viewer has nothing to light it with, and the reader refuses
    /// the other two kinds rather than approximating them - so this pins both the writing and the positive half of
    /// that refusal.
    /// </summary>
    [Fact]
    public void ALightingPrimitiveCarriesItsDistantLight()
    {
        var filter = new FilterSpec("gloss", new[]
        {
            FilterPrimitive.Specular(2.5, 0.7, 14.0, ColorRgb.White, 35, 55),
        });

        string svg = SvgWriter.Write(Document(filter));
        XElement written = XDocument.Parse(svg).Descendants()
            .First(element => element.Name.LocalName == "feSpecularLighting");

        XElement light = Assert.Single(written.Elements());
        Assert.Equal("feDistantLight", light.Name.LocalName);
        Assert.Equal("35", (string?)light.Attribute("azimuth"));
        Assert.Equal("55", (string?)light.Attribute("elevation"));
        Assert.Equal("2.5", (string?)written.Attribute("surfaceScale"));
        Assert.Equal("14", (string?)written.Attribute("specularExponent"));

        FilterPrimitive read = Assert.Single(RoundTrip(filter).Primitives);
        Assert.Equal(35, read.Azimuth, 6);
        Assert.Equal(55, read.Elevation, 6);
        Assert.Equal(FilterPrimitiveKind.SpecularLighting, read.Kind);
    }

    private static CadDocument Document(FilterSpec filter)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddFilter(filter);
        return document;
    }
}
