namespace VCCad.Core.Model;

/// <summary>
/// What a selection's strokes agree on, and what they do not.
///
/// A panel that edits a selection has to show **one** value for each member, and a selection is not obliged to agree
/// with itself. Showing the first path's width as though it were everyone's is the defect the issue names: a person
/// types a number, believes it describes what they have selected, and it does not. So each member is either the
/// common value or explicitly **mixed**, and the caller decides whether that is a blank field, a dash, or a warning.
///
/// "Mixed" is computed over the paths that **have** a stroke. A selection of three paths where one has no stroke at
/// all is a gap in the selection, not a disagreement about what a stroke's width is - but a selection where the
/// strokes disagree on the width is a real "mixed".
/// </summary>
public sealed record StrokeSummary(
    int Strokes,
    int Paths,
    double? Width,
    bool WidthMixed,
    StrokeCap? Cap,
    bool CapMixed,
    StrokeJoin? Join,
    bool JoinMixed,
    double? MiterLimit,
    bool MiterMixed,
    StrokeAlignment? Alignment,
    bool AlignmentMixed)
{
    /// <summary>Whether the selection has nothing to describe - no selected path carries a stroke.</summary>
    public bool IsEmpty => Strokes == 0;

    /// <summary>Whether any member disagrees across the selection.</summary>
    public bool IsMixed => WidthMixed || CapMixed || JoinMixed || MiterMixed || AlignmentMixed;

    /// <summary>
    /// Summarises the strokes at <paramref name="index"/> across the paths, counting from the bottom.
    ///
    /// A path whose stack is shorter than the index is skipped rather than treated as disagreeing: it has no stroke
    /// there, which is a different thing from having one that differs.
    /// </summary>
    public static StrokeSummary Of(IEnumerable<PathItem> paths, int index)
    {
        var strokes = new List<StrokeSpec>();
        int pathCount = 0;

        foreach (PathItem path in paths)
        {
            pathCount++;
            if (index >= 0 && index < path.Strokes.Count && path.Strokes[index].HasVisibleOutline)
            {
                strokes.Add(path.Strokes[index]);
            }
        }

        if (strokes.Count == 0)
        {
            return new StrokeSummary(0, pathCount, null, false, null, false, null, false, null, false, null, false);
        }

        bool widthMixed = !strokes.All(s => Math.Abs(s.Width - strokes[0].Width) < 1e-9);
        bool capMixed = !strokes.All(s => s.Cap == strokes[0].Cap);
        bool joinMixed = !strokes.All(s => s.Join == strokes[0].Join);
        bool miterMixed = !strokes.All(s => Math.Abs(s.MiterLimit - strokes[0].MiterLimit) < 1e-9);
        bool alignmentMixed = !strokes.All(s => s.Alignment == strokes[0].Alignment);

        return new StrokeSummary(
            strokes.Count,
            pathCount,
            widthMixed ? null : strokes[0].Width,
            widthMixed,
            capMixed ? null : strokes[0].Cap,
            capMixed,
            joinMixed ? null : strokes[0].Join,
            joinMixed,
            miterMixed ? null : strokes[0].MiterLimit,
            miterMixed,
            alignmentMixed ? null : strokes[0].Alignment,
            alignmentMixed);
    }
}
