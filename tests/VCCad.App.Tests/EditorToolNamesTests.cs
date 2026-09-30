using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Turning a tool's name into a tool - which is what `tool.set`, `tool.get` and `tool.list` all depend on.
///
/// The nine shapes are tools: they are what a person picks from the compound button, so they are names a
/// driver can set and names that come back. The failure this guards against is the two halves drifting
/// apart - a name that sets but does not get leaves a driver able to put the editor into a state it cannot
/// read.
/// </summary>
public class EditorToolNamesTests
{
    /// <summary>
    /// Every shape name resolves to the shape tool with that shape - except `rectangle`, which is also a
    /// tool, and where the **tool name wins**.
    ///
    /// That precedence is deliberate and load-bearing: if the shape won, the plain rectangle tool would be
    /// unreachable by name, and "every tool can be set and read back" is the promise this file keeps. The
    /// rectangle shape is reached by arming the shape tool and setting its kind, which is checked below.
    /// </summary>
    [Fact]
    public void EveryShapeNameResolvesToTheShapeTool()
    {
        foreach (ShapeKind kind in ShapeLibrary.All)
        {
            string name = ShapeLibrary.Name(kind);

            Assert.True(EditorToolNames.TryResolve(name, out EditorTool tool, out ShapeKind? shape),
                $"'{name}' should resolve");

            if (name == "rectangle")
            {
                Assert.Equal(EditorTool.Rectangle, tool);
                Assert.Null(shape);
                continue;
            }

            Assert.Equal(EditorTool.Shape, tool);
            Assert.Equal(kind, shape);
        }

        Assert.Equal(9, ShapeLibrary.All.Count);
    }

    /// <summary>
    /// The rectangle **shape** is still reachable, by the route the collision leaves open: arm the shape
    /// tool, then set its kind. Documented as a test so it cannot quietly become a gap.
    /// </summary>
    [Fact]
    public void TheRectangleShapeIsReachableByArmingTheShapeTool()
    {
        Assert.True(EditorToolNames.TryResolve("shape", out EditorTool tool, out ShapeKind? shape));
        Assert.Equal(EditorTool.Shape, tool);
        Assert.Null(shape);

        ShapeKind armed = ShapeKind.Rectangle;
        Assert.Equal("rectangle", EditorToolNames.NameOf(EditorTool.Shape, armed));
    }

    /// <summary>A shape name is matched however it is written - a driver should not have to guess case.</summary>
    [Theory]
    [InlineData("star", ShapeKind.Star)]
    [InlineData("STAR", ShapeKind.Star)]
    [InlineData("Rounded-rectangle", ShapeKind.RoundedRectangle)]
    [InlineData("arrow", ShapeKind.Arrow)]
    public void ShapeNamesMatchRegardlessOfCase(string name, ShapeKind expected)
    {
        Assert.True(EditorToolNames.TryResolve(name, out EditorTool tool, out ShapeKind? shape));
        Assert.Equal(EditorTool.Shape, tool);
        Assert.Equal(expected, shape);
    }

    /// <summary>Ordinary tool names still resolve, and carry no shape.</summary>
    [Theory]
    [InlineData("select", EditorTool.Select)]
    [InlineData("node", EditorTool.Node)]
    [InlineData("pencil", EditorTool.Pencil)]
    [InlineData("corner", EditorTool.Corner)]
    [InlineData("shape", EditorTool.Shape)]
    public void ToolNamesStillResolve(string name, EditorTool expected)
    {
        Assert.True(EditorToolNames.TryResolve(name, out EditorTool tool, out ShapeKind? shape));
        Assert.Equal(expected, tool);
        Assert.Null(shape);
    }

    /// <summary>An unknown name resolves to nothing rather than to a default, so it can be refused.</summary>
    [Fact]
    public void AnUnknownNameDoesNotResolve()
    {
        Assert.False(EditorToolNames.TryResolve("banana", out _, out _));
        Assert.False(EditorToolNames.TryResolve(string.Empty, out _, out _));
    }

    /// <summary>
    /// What is set is what is read: every name in `tool.list` resolves, and resolving it and asking for the
    /// name back gives the name it was set by. This is the check that keeps the two halves together.
    /// </summary>
    [Fact]
    public void EveryNameThatCanBeSetCanBeReadBack()
    {
        foreach (string name in EditorToolNames.AllNames)
        {
            // The bare name "shape" is the container: it sets the shape tool holding whatever is armed,
            // and reading it back gives that shape rather than the word "shape" - deliberately, because
            // the shape is what a person chose. Every other name round-trips exactly.
            if (name == "shape")
            {
                continue;
            }

            Assert.True(EditorToolNames.TryResolve(name, out EditorTool tool, out ShapeKind? shape),
                $"'{name}' is listed as settable but does not resolve");

            ShapeKind current = shape ?? ShapeKind.Rectangle;
            Assert.Equal(name, EditorToolNames.NameOf(tool, current));
        }
    }

    /// <summary>The shape tool reports the armed shape, not the word "shape".</summary>
    [Fact]
    public void TheShapeToolReportsTheShapeItIsHolding()
    {
        Assert.Equal("star", EditorToolNames.NameOf(EditorTool.Shape, ShapeKind.Star));
        Assert.Equal("select", EditorToolNames.NameOf(EditorTool.Select, ShapeKind.Star));
    }

    /// <summary>Setting a shape name through the editor arms that shape and puts the flyout's button on it.</summary>
    [Fact]
    public void SettingAShapeNameArmsIt()
    {
        var viewModel = new EditorViewModel();

        Assert.True(EditorToolNames.TryResolve("heart", out EditorTool tool, out ShapeKind? shape));
        viewModel.CurrentShape = shape!.Value;
        viewModel.Tool = tool;

        Assert.Equal(EditorTool.Shape, viewModel.Tool);
        Assert.Equal(ShapeKind.Heart, viewModel.CurrentShape);
        Assert.Equal("heart", EditorToolNames.NameOf(viewModel.Tool, viewModel.CurrentShape));
    }
}
