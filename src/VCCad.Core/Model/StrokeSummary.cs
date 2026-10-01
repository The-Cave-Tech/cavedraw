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
    bool AlignmentMixed,
    DashPattern? Dash = null,
    bool DashMixed = false,
    WidthProfileSpec? WidthProfile = null,
    bool WidthProfileMixed = false,
    DynamicsSpec? Dynamics = null,
    bool DynamicsMixed = false)
{
    /// <summary>Whether the selection has nothing to describe - no selected path carries a stroke.</summary>
    public bool IsEmpty => Strokes == 0;

    /// <summary>Whether any member disagrees across the selection.</summary>
    public bool IsMixed => WidthMixed || CapMixed || JoinMixed || MiterMixed || AlignmentMixed
        || DashMixed || WidthProfileMixed || DynamicsMixed;

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

        // Dash, width profile and tablet dynamics are agreement too, and belong here rather than in each panel that
        // shows them: the stroke pane used to work the dash out for itself, which is a second opinion that can drift
        // from the one `style.commonStroke` reports. A profile and a dynamics spec are compared member by member
        // through the same readings the stroke itself uses - a stroke with no profile draws with none, and a missing
        // dynamics target is Off, exactly as the pane reads them.
        bool dashMixed = false;
        bool profileMixed = false;
        bool dynamicsMixed = false;

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
            alignmentMixed,
            dashMixed ? null : strokes[0].Dash,
            dashMixed,
            profileMixed ? null : Profile(strokes[0]),
            profileMixed,
            dynamicsMixed ? null : strokes[0].Dynamics,
            dynamicsMixed);
    }

    /// <summary>
    /// The profile a stroke draws with, or null when it has none.
    ///
    /// An empty profile is not a profile - the stroke is an ordinary one - so it reads as "no profile" here rather
    /// than as a value two strokes could agree on.
    /// </summary>
    private static WidthProfileSpec? Profile(StrokeSpec stroke)
        => stroke.HasWidthProfile ? stroke.WidthProfile : null;

    private static bool SameProfile(StrokeSpec a, StrokeSpec b)
    {
        WidthProfileSpec? left = Profile(a);
        WidthProfileSpec? right = Profile(b);
        return left is null ? right is null : left.Equals(right);
    }

    /// <summary>
    /// Whether two strokes record the same tablet response, target by target.
    ///
    /// Per target rather than by comparing the specs whole, because a stroke that stores no dynamics at all and one
    /// that stores every target switched off draw identically - both "never varies" - and reporting them as a
    /// disagreement would call a selection mixed over a difference nothing can see.
    /// </summary>
    private static bool SameDynamics(StrokeSpec a, StrokeSpec b)
        => Enum.GetValues<DynamicsTarget>().All(target =>
            (a.Dynamics?.For(target) ?? DynamicsTargetSpec.Off)
            == (b.Dynamics?.For(target) ?? DynamicsTargetSpec.Off));
}
