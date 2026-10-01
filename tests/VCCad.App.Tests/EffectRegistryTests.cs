using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The effects registry: the declaration a panel builds its editors from and an operation validates against.
///
/// #115 names the trap this exists to close - a hand-written switch in a panel falls out of step with the engine,
/// and a person gets a control that does nothing. These assert the declaration itself: that every effect has one,
/// that each declares the parameters **its own kind** takes rather than a family's, and that the whole thing is
/// reachable through the registry.
/// </summary>
public class EffectRegistryTests
{
    /// <summary>Every kind an operation can be asked for has a declaration, because that is what validation uses.</summary>
    [Theory]
    [InlineData("zigZag")]
    [InlineData("roughen")]
    [InlineData("offsetPath")]
    [InlineData("scribble")]
    [InlineData("blur")]
    [InlineData("dropShadow")]
    [InlineData("innerGlow")]
    [InlineData("outerGlow")]
    public void EveryEffectKindHasADeclaration(string kind)
    {
        EffectDefinition definition = EffectRegistry.Find(kind)!;

        Assert.NotNull(definition);
        Assert.NotEmpty(definition.Parameters);
        Assert.NotEmpty(definition.Meaning);
    }

    [Fact]
    public void TheOutlineAndRasterFamiliesAreBothPresent()
    {
        Assert.Equal(4, EffectRegistry.All.Count(e => !e.Raster));
        Assert.Equal(4, EffectRegistry.All.Count(e => e.Raster));
    }

    /// <summary>
    /// **A kind declares what its own kind takes.** A blur has a radius and nothing else; a drop shadow has a
    /// displacement and a tint. A registry that gave every raster effect the same parameter list would be a
    /// family list wearing a kind's clothes - and the panel would offer a blur an offset box.
    /// </summary>
    [Fact]
    public void EachKindDeclaresItsOwnParameters()
    {
        Assert.Equal(new[] { "radius" }, EffectRegistry.Find("blur")!.Parameters.Select(p => p.Name).ToArray());

        Assert.Equal(
            new[] { "radius", "offsetX", "offsetY", "opacity", "tint" },
            EffectRegistry.Find("dropShadow")!.Parameters.Select(p => p.Name).ToArray());

        // A glow spreads and is tinted; it is not displaced.
        Assert.Equal(
            new[] { "radius", "opacity", "tint" },
            EffectRegistry.Find("outerGlow")!.Parameters.Select(p => p.Name).ToArray());

        // An offset path has a size and a corner, and no seed - there is nothing random about it.
        Assert.Equal(new[] { "size", "join" }, EffectRegistry.Find("offsetPath")!.Parameters.Select(p => p.Name).ToArray());
    }

    /// <summary>
    /// **Each outline effect declares the parameters the feature list names**, not a reduced set.
    ///
    /// #105 is exactly this: the effect existed and the knob did not, so the panel had nothing to build an editor
    /// from and the operation had no name to accept. Pinning the whole list here is what makes a later removal a
    /// failure rather than a control that quietly disappears.
    /// </summary>
    [Fact]
    public void TheOutlineEffectsDeclareTheParametersTheFeatureListNames()
    {
        Assert.Equal(new[] { "size", "detail", "seed" }, Names("roughen"));
        Assert.Equal(new[] { "size", "ridges", "smooth", "seed" }, Names("zigZag"));
        Assert.Equal(new[] { "size", "join" }, Names("offsetPath"));
        Assert.Equal(
            new[] { "size", "detail", "density", "overlap", "width", "curviness", "scatter", "seed" },
            Names("scribble"));

        static string[] Names(string kind)
            => EffectRegistry.Find(kind)!.Parameters.Select(p => p.Name).ToArray();
    }

    /// <summary>A parameter's sort is part of the declaration, because a panel builds a different editor for each.</summary>
    [Fact]
    public void ParametersDeclareTheirSort()
    {
        Assert.Equal(EffectParameterKind.Integer, EffectRegistry.Find("zigZag")!.Parameters.Single(p => p.Name == "seed").Kind);
        Assert.Equal(EffectParameterKind.Number, EffectRegistry.Find("blur")!.Parameters.Single(p => p.Name == "radius").Kind);
        Assert.Equal(EffectParameterKind.Color, EffectRegistry.Find("dropShadow")!.Parameters.Single(p => p.Name == "tint").Kind);
    }

    /// <summary>An effect this build does not have is not silently known, which is what validation keys off.</summary>
    [Fact]
    public void AnUnknownKindIsNotDeclared()
    {
        Assert.Null(EffectRegistry.Find("sparkle"));
        Assert.False(EffectRegistry.IsKnown("sparkle"));
        Assert.True(EffectRegistry.IsKnown("dropShadow"));
        Assert.True(EffectRegistry.IsKnown("DROPSHADOW"));
    }

    /// <summary>And the whole declaration is reachable through the registry, which is the parity half.</summary>
    [Fact]
    public void TheRegistryIsReachableAsAnOperation()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.effectParameters", default));

        Assert.Equal(8, reported.GetArrayLength());

        JsonElement dropShadow = reported.EnumerateArray()
            .Single(e => e.GetProperty("kind").GetString() == "dropShadow");

        Assert.True(dropShadow.GetProperty("raster").GetBoolean());
        Assert.Equal(5, dropShadow.GetProperty("parameters").GetArrayLength());

        JsonElement radius = dropShadow.GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "radius");
        Assert.Equal("number", radius.GetProperty("type").GetString());
        Assert.Equal(4.0, radius.GetProperty("default").GetDouble(), 6);
    }
}
