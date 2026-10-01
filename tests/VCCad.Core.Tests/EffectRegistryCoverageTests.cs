using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The registry's coverage of the model.
///
/// #115 wants a new effect to appear in the panel **by existing**. That promise only holds if every kind the model
/// can hold is declared in the registry - otherwise a kind can be created, saved, loaded and rendered while being
/// invisible to every panel and unreachable through the discovery operation. This is the invariant that makes the
/// registry worth reading, and it fails the moment someone adds an enum member and forgets to declare it.
/// </summary>
public class EffectRegistryCoverageTests
{
    private static string Name(object kind) => kind.ToString()!;

    [Fact]
    public void EveryOutlineEffectKindIsDeclared()
    {
        foreach (OutlineEffectKind kind in Enum.GetValues<OutlineEffectKind>())
        {
            EffectDefinition? definition = EffectRegistry.All.FirstOrDefault(
                d => !d.Raster && d.Kind.Equals(Name(kind), StringComparison.OrdinalIgnoreCase));

            Assert.True(definition is not null,
                $"'{Name(kind)}' is an outline effect the model can hold, but the registry does not declare it");
        }
    }

    [Fact]
    public void EveryRasterEffectKindIsDeclared()
    {
        foreach (RasterEffectKind kind in Enum.GetValues<RasterEffectKind>())
        {
            EffectDefinition? definition = EffectRegistry.All.FirstOrDefault(
                d => d.Raster && d.Kind.Equals(Name(kind), StringComparison.OrdinalIgnoreCase));

            Assert.True(definition is not null,
                $"'{Name(kind)}' is a raster effect the model can hold, but the registry does not declare it");
        }
    }

    /// <summary>And nothing is declared that the model cannot hold, which is the same invariant the other way.</summary>
    [Fact]
    public void NothingIsDeclaredThatTheModelCannotHold()
    {
        foreach (EffectDefinition definition in EffectRegistry.All)
        {
            bool known = definition.Raster
                ? Enum.GetNames<RasterEffectKind>().Any(n => n.Equals(definition.Kind, StringComparison.OrdinalIgnoreCase))
                : Enum.GetNames<OutlineEffectKind>().Any(n => n.Equals(definition.Kind, StringComparison.OrdinalIgnoreCase));

            Assert.True(known, $"the registry declares '{definition.Kind}', which no effect kind matches");
        }
    }

    /// <summary>The counts agree, so neither list has quietly grown or shrunk on its own.</summary>
    [Fact]
    public void TheFamiliesHaveTheSameSizeAsTheirEnums()
    {
        Assert.Equal(
            Enum.GetValues<OutlineEffectKind>().Length,
            EffectRegistry.All.Count(d => !d.Raster));

        Assert.Equal(
            Enum.GetValues<RasterEffectKind>().Length,
            EffectRegistry.All.Count(d => d.Raster));
    }
}
