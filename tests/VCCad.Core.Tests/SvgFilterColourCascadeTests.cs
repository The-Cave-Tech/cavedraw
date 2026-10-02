using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **A filter primitive's colour is read through the cascade, not straight off the attribute.**
///
/// `flood-color` and `lighting-color` are presentation attributes: a rule in a `<style>` binds them exactly as
/// writing them on the element does. `SvgFilters` read both with `element.Attribute(...)`, and passed `null` as the
/// stylesheet to the helper that resolves `currentColor` - so a colour stated in a rule was not seen at all, and
/// the keyword resolved against SVG's initial value rather than the `color` in force.
///
/// The assertion is an **equality between two imported primitives**, because that is what the defect is: the same
/// colour stated two legal ways must produce the same model. A test that only checked the rule-declared one would
/// pass on a reader that got both wrong the same way.
/// </summary>
public class SvgFilterColourCascadeTests
{
    private const string Head = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"400\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    /// <summary>An feFlood whose colour is written on the element, beside one whose colour comes from a rule.</summary>
    private const string TwoFloods =
        "<style>#styled { flood-color: #ff0000; }</style>" +
        "<filter id=\"f\">" +
        "<feFlood flood-color=\"#ff0000\"/>" +
        "<feFlood id=\"styled\"/>" +
        "</filter>";

    private static ColorRgb[] Floods(string body)
    {
        SvgImportResult result = Read(body);
        Assert.Single(result.Document.Filters);
        FilterSpec filter = result.Document.Filters[0];
        return filter.Primitives.Select(p => p.FloodColor ?? ColorRgb.Black).ToArray();
    }

    /// <summary>
    /// **The acceptance.** The primitive whose colour came from a `<style>` rule holds the same colour as the one
    /// whose colour was written on the element. Against the old read the rule-declared primitive was black.
    /// </summary>
    [Fact]
    public void AFloodColourDeclaredInAStyleRuleMatchesOneWrittenOnTheElement()
    {
        ColorRgb[] floods = Floods(TwoFloods);

        Assert.Equal(2, floods.Length);
        Assert.Equal(floods[0], floods[1]);
        Assert.Equal(1.0, floods[1].R, 9);
        Assert.Equal(0.0, floods[1].G, 9);
        Assert.Equal(0.0, floods[1].B, 9);
    }

    /// <summary>
    /// **And a `currentColor` primitive resolves to the `color` in force.** The `color` is stated on a `<g>` around
    /// the filter, so this also proves the ancestor walk is the one the cascade supplies rather than a null sheet.
    /// </summary>
    [Fact]
    public void AFloodColourOfCurrentColorResolvesToTheColourInForce()
    {
        ColorRgb[] floods = Floods(
            "<g color=\"#0000ff\"><filter id=\"f\"><feFlood flood-color=\"currentColor\"/></filter></g>");

        Assert.Single(floods);
        Assert.Equal(0.0, floods[0].R, 9);
        Assert.Equal(0.0, floods[0].G, 9);
        Assert.Equal(1.0, floods[0].B, 9);
    }

    /// <summary>
    /// **The same two faults on `lighting-color`.** It was read off the attribute with a null sheet as well, so a
    /// rule-declared colour was invisible. The primitive only exists when the build can read its light source, which
    /// is why each one carries an `feDistantLight` - the reader refuses the other two rather than approximating.
    /// </summary>
    [Fact]
    public void ALightingColourDeclaredInAStyleRuleMatchesOneWrittenOnTheElement()
    {
        SvgImportResult result = Read(
            "<style>#styled { lighting-color: #00ff00; }</style>" +
            "<filter id=\"f\">" +
            "<feDiffuseLighting lighting-color=\"#00ff00\" surfaceScale=\"1\">" +
            "<feDistantLight azimuth=\"0\" elevation=\"45\"/></feDiffuseLighting>" +
            "<feDiffuseLighting id=\"styled\" surfaceScale=\"1\">" +
            "<feDistantLight azimuth=\"0\" elevation=\"45\"/></feDiffuseLighting>" +
            "</filter>");

        FilterSpec filter = result.Document.Filters[0];
        Assert.Equal(2, filter.Primitives.Count);

        ColorRgb stated = filter.Primitives[0].LightingColor ?? ColorRgb.White;
        ColorRgb inAStyle = filter.Primitives[1].LightingColor ?? ColorRgb.White;

        Assert.Equal(stated, inAStyle);
        Assert.Equal(0.0, inAStyle.R, 9);
        Assert.Equal(1.0, inAStyle.G, 9);
    }
}
