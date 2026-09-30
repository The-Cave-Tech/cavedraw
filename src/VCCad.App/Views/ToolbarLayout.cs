using VCCad.App.ViewModels;

namespace VCCad.App.Views;

/// <summary>
/// What the tool toolbar contains, in order.
///
/// It lives here rather than in the view so it can be **checked against the enum**: the rule for this
/// toolbar is that every tool exists on it, and a rule nobody can assert is a rule that quietly stops being
/// true. The corner and pencil tools were added to the enum and given glyphs without anyone noticing they
/// needed buttons too, which is exactly the drift this table prevents.
///
/// A separator carries no tool. The shape entry is the compound button - one slot holding the nine shapes -
/// rather than a plain tool button, which is why it is marked.
/// </summary>
public sealed record ToolbarEntry(
    EditorTool? Tool,
    string? Icon,
    string? Tip,
    bool IsSeparator = false,
    bool IsShapeFlyout = false);

/// <summary>The toolbar's contents, in the order a person sees them.</summary>
public static class ToolbarLayout
{
    /// <summary>Every entry, top to bottom.</summary>
    public static IReadOnlyList<ToolbarEntry> All { get; } = new[]
    {
        new ToolbarEntry(EditorTool.Select, "select", "Selection (V)"),
        new ToolbarEntry(EditorTool.Node, "node", "Nodes / direct selection (A)"),
        new ToolbarEntry(EditorTool.Corner, null, "Round a corner (C): drag it out"),
        new ToolbarEntry(EditorTool.Pencil, null, "Pencil (N): draw freehand"),
        new ToolbarEntry(EditorTool.Lasso, null, "Freehand selection (Q)"),
        new ToolbarEntry(EditorTool.Pen, "pen", "Pen (P)"),

        // Drawing tools below the line, selecting and editing above it.
        new ToolbarEntry(null, null, null, IsSeparator: true),

        // The nine shapes are one button. `tool.get` reports the armed shape, so the button is a tool.
        new ToolbarEntry(EditorTool.Shape, null, "Shapes (S): click to draw, hold for the rest", IsShapeFlyout: true),

        new ToolbarEntry(EditorTool.Rectangle, "rectangle", "Rectangle (M)"),
        new ToolbarEntry(EditorTool.Ellipse, "ellipse", "Ellipse (L)"),
        new ToolbarEntry(EditorTool.Artboard, "artboard", "Artboard (O)"),
        new ToolbarEntry(EditorTool.Text, "text", "Text (T)"),
    };

    /// <summary>The tools on the toolbar, separators dropped.</summary>
    public static IEnumerable<EditorTool> Tools
        => All.Where(e => e.Tool is not null).Select(e => e.Tool!.Value);
}
