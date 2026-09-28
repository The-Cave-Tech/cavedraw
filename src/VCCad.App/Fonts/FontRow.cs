using Avalonia;
using Avalonia.Media;

namespace VCCad.App.Fonts;

/// <summary>What a row of the chooser's list is.</summary>
public enum FontRowKind
{
    /// <summary>A family, which may open to its faces.</summary>
    Family,

    /// <summary>One face of the family above it.</summary>
    Face,
}

/// <summary>
/// One row of the font chooser.
///
/// A family and its faces are the same list rather than a tree, because that is how the
/// chooser behaves: opening a family pushes its faces in underneath and closes them again,
/// and the list stays one scrollable column. It also makes the whole thing readable by a
/// driver, which a nested control would not be.
///
/// The display values are worked out here rather than by converters in the template. A binding
/// that resolves to nothing draws nothing and says nothing - a lesson this session has already
/// paid for once with a theme brush that did not exist - so the row carries ready values.
/// </summary>
public sealed record FontRow
{
    public required FontRowKind Kind { get; init; }

    /// <summary>The family to apply, or to open.</summary>
    public required string Family { get; init; }

    /// <summary>What the row prints.</summary>
    public required string Label { get; init; }

    /// <summary>The family to draw the row in, or null when nothing can draw it.</summary>
    public FontFamily? Face { get; init; }

    /// <summary>The face's style for a face row, empty for a family row.</summary>
    public string Style { get; init; } = string.Empty;

    public FontWeight Weight { get; init; } = FontWeight.Normal;

    public FontStyle Slant { get; init; } = FontStyle.Normal;

    /// <summary>0 for a family, 1 for one of its faces.</summary>
    public int Depth { get; init; }

    /// <summary>Whether the row can be opened - a family with more than one face.</summary>
    public bool Expandable { get; init; }

    /// <summary>Whether it is open.</summary>
    public bool Expanded { get; init; }

    public int FaceCount { get; init; }

    /// <summary>Whether the machine can draw this row in its own face.</summary>
    public bool Drawable { get; init; } = true;

    /// <summary>Whether a person has starred the family.</summary>
    public bool Favourite { get; init; }

    /// <summary>The triangle, pointing the way the row will go.</summary>
    public string Chevron => !Expandable ? string.Empty : Expanded ? "\u25be" : "\u25b8";

    /// <summary>Left padding for the row's depth, so a face sits under its family.</summary>
    public Thickness Indent => new(Depth * 14, 0, 0, 0);

    /// <summary>A starred heart is solid, an unstarred one faint.</summary>
    public double HeartOpacity => Favourite ? 1.0 : 0.25;
}
