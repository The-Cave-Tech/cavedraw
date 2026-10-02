using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// Edits the document's brush library and every stroke that refers to it, as one undo step.
///
/// A brush is referred to **by name**, so changing one is never a change to the library alone - the same rule a
/// width profile follows. Renaming a brush has to move the strokes that named the old name; deleting one has to
/// clear them; editing its parameters has to re-point the strokes that used it. Capturing both halves in the
/// same command is what keeps undo exact: undoing a rename while leaving the strokes behind would produce a
/// document whose strokes name a brush that does not exist, which is a state the model should not be able to
/// reach.
///
/// The stroke half is captured as (path, index, before, after) rather than by rewriting the stack, because a
/// stroke is a value in a list and its index is stable for the duration of the edit.
/// </summary>
public sealed class EditBrushesCommand : IUndoableCommand
{
    private readonly CadDocument _document;
    private readonly List<BrushSpec> _libraryBefore;
    private readonly List<BrushSpec> _libraryAfter;
    private readonly List<StrokeEdit> _edits;

    public EditBrushesCommand(
        CadDocument document,
        IEnumerable<BrushSpec> libraryAfter,
        IEnumerable<StrokeEdit> edits,
        string description)
    {
        _document = document;
        _libraryBefore = document.Brushes.ToList();
        _libraryAfter = libraryAfter.ToList();
        _edits = edits.ToList();
        Description = description;
    }

    /// <summary>One stroke's before and after, at a fixed place in its path's stack.</summary>
    public sealed record StrokeEdit(PathItem Path, int Index, StrokeSpec Before, StrokeSpec After);

    public string Description { get; }

    public void Do() => Apply(_libraryAfter, after: true);

    public void Undo() => Apply(_libraryBefore, after: false);

    private void Apply(List<BrushSpec> library, bool after)
    {
        _document.SetBrushes(library);

        foreach (StrokeEdit edit in _edits)
        {
            if (edit.Index >= 0 && edit.Index < edit.Path.Strokes.Count)
            {
                edit.Path.Strokes[edit.Index] = after ? edit.After : edit.Before;
                edit.Path.NotifyStrokesChanged();
            }
        }
    }
}
