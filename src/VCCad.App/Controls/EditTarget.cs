namespace VCCad.App.Controls;

/// <summary>
/// What a double-click landed on, and therefore what it opened.
///
/// A double-click means "let me work on this one", and what that is depends on the object: a text
/// block opens for typing, a path opens its geometry for node editing. Naming the outcome lets the
/// pointer handler and the registry share one rule instead of each deciding for itself.
/// </summary>
public enum EditTarget
{
    /// <summary>Nothing under the point that a double-click opens.</summary>
    None,

    /// <summary>A text block was opened for editing.</summary>
    Text,

    /// <summary>A path was selected and the node tool was armed.</summary>
    Path,
}
