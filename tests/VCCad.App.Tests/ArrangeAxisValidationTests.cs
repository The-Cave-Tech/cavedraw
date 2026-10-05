using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// An arrangement acts along the axis the caller named (issue #244).
///
/// `arrange.align` and `arrange.distribute` read their axis through one helper that asked only
/// `string.Equals(axis, "vertical")` and treated **everything else** as horizontal. So `axis:"sideways"` was
/// accepted, answered `"axis":"Horizontal"`, and aligned the objects along an axis nobody had asked for - a
/// destructive edit reported in a way that reads as confirmation. The `edge` member of the same call had always
/// refused its unknown values by name, which is the rule this brings to the other member.
///
/// The synonyms a person reaches for are accepted rather than refused: `x` is horizontal and `y` is vertical,
/// both unambiguously, so a typed axis works instead of quietly meaning something else.
/// </summary>
public class ArrangeAxisValidationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static AutomationContext Host() => new() { ViewModel = new EditorViewModel() };

    /// <summary>Three shapes at different heights and different lefts, all selected.</summary>
    private static AutomationContext ThreeShapes()
    {
        AutomationContext context = Host();
        double[][] places =
        [
            [120.0, 140.0],
            [340.0, 260.0],
            [560.0, 200.0],
        ];

        foreach (double[] place in places)
        {
            EditorOperations.Invoke(context, "object.create", Params(new
            {
                type = "rectangle",
                x = place[0],
                y = place[1],
                width = 90.0,
                height = 60.0,
            }));
        }

        JsonElement find = Invoke(context, "object.find", new { });
        Guid[] ids = [.. find.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("itemId").GetGuid())];
        EditorOperations.Invoke(context, "selection.set", Params(new { itemIds = ids }));
        return context;
    }

    private static JsonElement Invoke(AutomationContext context, string operation, object parameters)
        => JsonSerializer.SerializeToElement(EditorOperations.Invoke(context, operation, Params(parameters)));

    /// <summary>Every object's top-left, so a test can say whether anything moved and how far.</summary>
    private static (double X, double Y)[] Positions(AutomationContext context)
        => [.. Invoke(context, "object.find", new { }).GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("x").GetDouble(), i.GetProperty("y").GetDouble()))];

    private static Exception? Failure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void AnUnknownAxisIsRefusedByNameAndNothingMoves()
    {
        AutomationContext context = ThreeShapes();
        (double X, double Y)[] before = Positions(context);

        Exception? error = Failure(() =>
            EditorOperations.Invoke(context, "arrange.align", Params(new { axis = "sideways", edge = "start" })));

        Assert.NotNull(error);
        Assert.Contains("sideways", error!.Message);
        Assert.Contains("horizontal|vertical", error.Message);
        Assert.Equal(before, Positions(context));
    }

    [Fact]
    public void AMissingAxisIsRefusedForTheSameReason()
    {
        AutomationContext context = ThreeShapes();

        Exception? error = Failure(() =>
            EditorOperations.Invoke(context, "arrange.align", Params(new { edge = "start" })));

        Assert.NotNull(error);
        Assert.Contains("axis", error!.Message);
    }

    [Fact]
    public void HorizontalAndVerticalDoWhatTheySay()
    {
        AutomationContext vertical = ThreeShapes();
        Invoke(vertical, "arrange.align", new { axis = "vertical", edge = "start" });
        double[] tops = [.. Positions(vertical).Select(p => Math.Round(p.Y, 3))];
        Assert.Single(tops.Distinct());

        AutomationContext horizontal = ThreeShapes();
        Invoke(horizontal, "arrange.align", new { axis = "horizontal", edge = "start" });
        double[] lefts = [.. Positions(horizontal).Select(p => Math.Round(p.X, 3))];
        Assert.Single(lefts.Distinct());
    }

    /// <summary>`y` is vertical and `x` is horizontal, deliberately - not silently mapped to one of them.</summary>
    [Fact]
    public void TheSynonymsAPersonTypesMeanWhatTheySay()
    {
        AutomationContext y = ThreeShapes();
        Invoke(y, "arrange.align", new { axis = "y", edge = "start" });
        Assert.Single(Positions(y).Select(p => Math.Round(p.Y, 3)).Distinct());

        AutomationContext x = ThreeShapes();
        Invoke(x, "arrange.align", new { axis = "x", edge = "start" });
        Assert.Single(Positions(x).Select(p => Math.Round(p.X, 3)).Distinct());
    }

    /// <summary>The member that was already refused stays refused, and distribute reads the same helper.</summary>
    [Fact]
    public void TheEdgeIsStillRefusedAndDistributeValidatesItsAxis()
    {
        AutomationContext context = ThreeShapes();

        Exception? edge = Failure(() =>
            EditorOperations.Invoke(context, "arrange.align", Params(new { axis = "horizontal", edge = "sideways" })));
        Assert.NotNull(edge);
        Assert.Contains("sideways", edge!.Message);

        Exception? axis = Failure(() =>
            EditorOperations.Invoke(context, "arrange.distribute", Params(new { axis = "sideways" })));
        Assert.NotNull(axis);
        Assert.Contains("horizontal|vertical", axis!.Message);
    }
}
