using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **`tool.set` and its own description agree.**
///
/// The operation parsed its parameter with `Enum.TryParse` but hand-wrote the list of accepted tools in the
/// description, and the two drifted: `pencil` worked and was not listed, which reads to a driver as a missing
/// capability - it is the tool freehand drawing needs. The catalogue is how a driver learns what it can do, so a
/// description that under-reports is a defect rather than a typo.
///
/// Asserting against the **enum** would not have caught it, because `pencil` was always accepted. The description is
/// what was wrong, so the description is what is read here.
/// </summary>
public class ToolSetCatalogueTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>The tool names `tool.set`'s own description says it accepts.</summary>
    private static List<string> DocumentedTools()
    {
        string? line = EditorOperations.Catalog()
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith("- tool.set:", StringComparison.Ordinal));

        Assert.True(line is not null, "tool.set must appear in the catalogue a driver is given");

        int open = line!.IndexOf("tool:string (", StringComparison.Ordinal);
        Assert.True(open >= 0, $"tool.set must document its accepted tools; the line was: {line}");

        int start = open + "tool:string (".Length;
        int close = line.IndexOf(')', start);
        Assert.True(close > start, $"the tool list is unterminated: {line}");

        return line[start..close]
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// **The list is complete.** `pencil` is a tool the canvas handles and the operation accepts, so a driver that
    /// reads only the catalogue must find it there - that omission is this test's reason for existing.
    /// </summary>
    [Fact]
    public void TheCatalogueListsEveryToolFreehandDrawingNeeds()
    {
        List<string> documented = DocumentedTools();

        Assert.Contains("pencil", documented);
    }

    /// <summary>
    /// **And every name it lists is really accepted**, so the description cannot claim a tool the operation rejects -
    /// the drift in the other direction, which is just as misleading.
    /// </summary>
    [Fact]
    public void EveryToolTheCatalogueNamesCanBeSelected()
    {
        var viewModel = new EditorViewModel();
        var context = new AutomationContext { ViewModel = viewModel };

        foreach (string name in DocumentedTools())
        {
            object? result = EditorOperations.Invoke(context, "tool.set", Params(new { tool = name }));

            Assert.NotNull(result);
            Assert.Equal(name, viewModel.Tool.ToString().ToLowerInvariant());
        }
    }
}
