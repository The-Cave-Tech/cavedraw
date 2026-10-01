using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `style.commonStroke` - the driver's half of the mixed-selection report, which is the same computation a panel
/// would use.
/// </summary>
public class CommonStrokeOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static PathItem Path(double width, StrokeCap cap = StrokeCap.Butt, DashPattern? dash = null,
        WidthProfileSpec? profile = null, DynamicsSpec? dynamics = null)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, width, cap, StrokeJoin.Miter, 4,
            Dash: dash ?? DashPattern.None, WidthProfile: profile, Dynamics: dynamics));
        return path;
    }

    private static (AutomationContext Context, JsonElement Reported) With(params PathItem[] paths)
    {
        var viewModel = new EditorViewModel();
        foreach (PathItem path in paths)
        {
            viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        }

        viewModel.SelectRange(paths, additive: false);
        var context = new AutomationContext { ViewModel = viewModel };

        return (context, JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.commonStroke", default)));
    }

    [Fact]
    public void ASelectionThatAgreesReportsNoMix()
    {
        (_, JsonElement reported) = With(Path(5), Path(5));

        Assert.False(reported.GetProperty("mixed").GetBoolean());
        Assert.Equal(5.0, reported.GetProperty("width").GetDouble(), 6);
        Assert.Equal(2, reported.GetProperty("strokes").GetInt32());
    }

    /// <summary>**A disagreement is reported as mixed**, not as the first path's value.</summary>
    [Fact]
    public void ASelectionThatDisagreesReportsMixed()
    {
        (_, JsonElement reported) = With(Path(5), Path(9, StrokeCap.Round));

        Assert.True(reported.GetProperty("mixed").GetBoolean());
        Assert.True(reported.GetProperty("widthMixed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("width").ValueKind);
        Assert.True(reported.GetProperty("capMixed").GetBoolean());

        // And the members they agree on are still reported.
        Assert.False(reported.GetProperty("miterMixed").GetBoolean());
        Assert.Equal(4.0, reported.GetProperty("miterLimit").GetDouble(), 6);
    }

    [Fact]
    public void AnEmptySelectionSaysSo()
    {
        (_, JsonElement reported) = With();

        Assert.True(reported.GetProperty("empty").GetBoolean());
        Assert.Equal(0, reported.GetProperty("strokes").GetInt32());
    }

    /// <summary>
    /// **Dash, width profile and tablet dynamics are reported by the registry**, each with its own mixed flag, so a
    /// driver can learn what the stroke pane used to work out for itself. A disagreement on one of them does not
    /// blank the members the selection agrees on.
    /// </summary>
    [Fact]
    public void ADisagreeingDashProfileAndDynamicsAreReportedThroughTheRegistry()
    {
        (_, JsonElement reported) = With(
            Path(5, dash: new DashPattern(new[] { 4.0, 2.0 }), profile: WidthProfileSpec.Taper(1, 5),
                dynamics: DynamicsSpec.PressureToWidth(DynamicsPreset.Linear)),
            Path(5, dash: new DashPattern(new[] { 1.0, 1.0 }), profile: WidthProfileSpec.Constant(3),
                dynamics: DynamicsSpec.PressureToWidth(DynamicsPreset.Soft)));

        Assert.True(reported.GetProperty("dashMixed").GetBoolean());
        Assert.True(reported.GetProperty("profileMixed").GetBoolean());
        Assert.True(reported.GetProperty("dynamicsMixed").GetBoolean());
        Assert.True(reported.GetProperty("mixed").GetBoolean());

        // Mixed means no common value, not the first path's.
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("dash").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("profile").ValueKind);
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("dynamics").ValueKind);

        Assert.False(reported.GetProperty("widthMixed").GetBoolean());
        Assert.Equal(5.0, reported.GetProperty("width").GetDouble(), 6);
    }

    /// <summary>A selection that agrees reports the dash, the profile and the response themselves.</summary>
    [Fact]
    public void AnAgreeingDashProfileAndDynamicsAreReportedThroughTheRegistry()
    {
        (_, JsonElement reported) = With(
            Path(5, dash: new DashPattern(new[] { 4.0, 2.0 }), profile: WidthProfileSpec.Taper(1, 5),
                dynamics: DynamicsSpec.PressureToWidth()),
            Path(5, dash: new DashPattern(new[] { 4.0, 2.0 }), profile: WidthProfileSpec.Taper(1, 5),
                dynamics: DynamicsSpec.PressureToWidth()));

        Assert.False(reported.GetProperty("mixed").GetBoolean());
        Assert.False(reported.GetProperty("dashMixed").GetBoolean());
        Assert.False(reported.GetProperty("profileMixed").GetBoolean());
        Assert.False(reported.GetProperty("dynamicsMixed").GetBoolean());

        Assert.Equal(
            new[] { 4.0, 2.0 },
            reported.GetProperty("dash").EnumerateArray().Select(e => e.GetDouble()).ToArray());
        Assert.Equal("Taper", reported.GetProperty("profile").GetProperty("name").GetString());
        Assert.Equal("Width", reported.GetProperty("dynamics")[0].GetProperty("target").GetString());
    }
}
