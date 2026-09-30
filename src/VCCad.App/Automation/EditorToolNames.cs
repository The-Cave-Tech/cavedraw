using VCCad.App.ViewModels;
using VCCad.Core.Model;

namespace VCCad.App.Automation;

/// <summary>
/// Turning a tool's name into a tool.
///
/// The nine shapes are tools - they are what a person picks from the compound button - so a driver can set
/// them by name, and the same name is what `tool.get` reports back. Keeping the resolution in one place is
/// what stops `tool.set` and `tool.get` drifting apart: a name that sets but does not get, or the reverse,
/// is a state a driver can put the editor into and never read.
/// </summary>
public static class EditorToolNames
{
    /// <summary>
    /// The tool a name refers to. A shape name means the shape tool with that shape; anything else must be
    /// an <see cref="EditorTool"/> name.
    /// </summary>
    public static bool TryResolve(string name, out EditorTool tool, out ShapeKind? shape)
    {
        shape = ShapeLibrary.All
            .Cast<ShapeKind?>()
            .FirstOrDefault(k => string.Equals(ShapeLibrary.Name(k!.Value), name, StringComparison.OrdinalIgnoreCase));

        if (shape is not null)
        {
            tool = EditorTool.Shape;
            return true;
        }

        if (Enum.TryParse(name, ignoreCase: true, out EditorTool parsed))
        {
            tool = parsed;
            return true;
        }

        tool = default;
        return false;
    }

    /// <summary>Every name that can be set: the tools, then the nine shapes they include.</summary>
    public static IReadOnlyList<string> AllNames
        => Enum.GetNames<EditorTool>()
            .Select(n => n.ToLowerInvariant())
            .Concat(ShapeLibrary.All.Select(ShapeLibrary.Name))
            .ToArray();

    /// <summary>How a name is reported back, so what is set is what is read.</summary>
    public static string NameOf(EditorTool tool, ShapeKind currentShape)
        => tool == EditorTool.Shape ? ShapeLibrary.Name(currentShape) : tool.ToString().ToLowerInvariant();
}
