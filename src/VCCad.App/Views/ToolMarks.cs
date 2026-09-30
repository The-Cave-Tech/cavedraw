using VCCad.App.ViewModels;

namespace VCCad.App.Views;

/// <summary>
/// The drawn glyphs for tools that do not have an icon asset.
///
/// Most tools are a PNG under `Assets/Icons`. A tool added later has none, and the tempting shortcut is to
/// reuse a neighbour's icon - which is how a tool becomes invisible, because a person scans icons and only
/// reads a tooltip once they are already looking at the right one. So a new tool gets a glyph drawn here,
/// and the glyphs live in one place where a test can see that no two tools share one.
///
/// The geometry is in a 20x20 box, drawn with a 1.6pt stroke in the toolbar.
/// </summary>
public static class ToolMarks
{
    /// <summary>The stroke the toolbar draws marks with, as a path source.</summary>
    public const string Stroke = "#E6E6EC";

    /// <summary>
    /// The glyph for a tool, or null when it uses an icon asset.
    ///
    /// Null is a real answer, not a gap: the tool has a picture of its own and inventing a glyph for it
    /// would replace a good icon with a worse one.
    /// </summary>
    public static string? Glyph(EditorTool tool) => tool switch
    {
        // A loop with a trailing tail: the freehand selection.
        EditorTool.Lasso => "M 10,2 C 15,2 18,5 18,9 C 18,14 14,17 10,17 C 5,17 2,14 2,9 C 2,5 5,2 10,2 Z M 10,17 L 13,21",

        // A right angle whose corner has been turned by an arc: the corner tool, and nothing else.
        EditorTool.Corner => "M 3,2 L 3,9 A 8,8 0 0 0 11,17 L 18,17",

        // A pencil: a body, a ferrule and a tip.
        EditorTool.Pencil => "M 3,17 L 5,12 L 14,3 L 17,6 L 8,15 Z M 5,12 L 8,15 M 14,3 L 17,6",

        _ => null,
    };
}
