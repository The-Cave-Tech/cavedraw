using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The half #130 left out: an imported Inkscape live path effect converted into a width profile once, at import,
/// and never re-derived when the path it was translated against changes.
///
/// #130's answer is "convert for rendering and keep the description for export", and its four tests pin exactly
/// that. This is a different question - what happens to the converted result when the input it came from is edited
/// - and the reproduction is exact. Inkscape stores a powerstroke's knots as <c>segmentIndex + t</c> over the whole
/// path, so the same knot element means different positions on paths of different segment counts. A knot at
/// segment index 1 of a two-segment path sits at 0.5; the same knot on a four-segment path sits at 0.25. The
/// profile the path holds is frozen at the count it was translated with, because nothing re-runs
/// <see cref="PathEffects.Translate"/> when the path changes.
///
/// The fix is the same shape as #117's: the description is kept as state on the path
/// (<see cref="PathItem.PathEffect"/>), and an explicit undoable step
/// (<see cref="RefreshPathEffectsCommand"/>) re-derives the stored profile from it and the path's current
/// geometry, reporting the cases it cannot honestly recompute by name.
/// </summary>
public class PathEffectLivenessTests
{
    /// <summary>A two-segment open path, so a knot at segment index 1 is at 1/2.</summary>
    private const string TwoSegments = "M 0,0 L 10,0 L 20,0";

    /// <summary>The same path extended to four segments, so the knot at index 1 is at 1/4.</summary>
    private const string FourSegments = "M 0,0 L 10,0 L 20,0 L 30,0 L 40,0";

    /// <summary>
    /// Inkscape's own powerstroke element, spelled the way the reader spells it back. Knots at segment indices 0
    /// and 1, so the second one - the reproduction - moves from 0.5 to 0.25 when the path is extended, and the
    /// drawn taper moves with it.
    /// </summary>
    private const string PowerStroke =
        "<inkscape:path-effect effect=\"powerstroke\" id=\"path-effect1\" lpeversion=\"1.4\" " +
        "is_visible=\"true\" offset_points=\"0,1 | 1,5\" not_jump=\"false\" sort_points=\"true\" " +
        "interpolator_type=\"Linear\" start_linecap_type=\"zerowidth\" linejoin_type=\"extrp_arc\" " +
        "miter_limit=\"4\" scale_width=\"1\" end_linecap_type=\"zerowidth\" />";

    /// <summary>An effect this build has no translation for, which must be reported rather than recomputed.</summary>
    private const string BendPath =
        "<inkscape:path-effect effect=\"bend_path\" id=\"path-effect1\" lpeversion=\"1.4\" " +
        "is_visible=\"true\" bendpath=\"m 10,50 c 20,0 40,20 70,0\" />";

    /// <summary>A file shaped like Inkscape's: the effect in <c>defs</c>, the path carrying the reference.</summary>
    private static string File(string pathData, string effect = PowerStroke)
        => "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
           "xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
           "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
           "<defs>" + effect + "</defs>" +
           "<path id=\"p1\" style=\"fill:none;stroke:#000000;stroke-width:1\" d=\"" + pathData + "\" " +
           "inkscape:original-d=\"" + pathData + "\" inkscape:path-effect=\"#path-effect1\" />" +
           "</svg>";

    /// <summary>
    /// The effect the file carries, read into the spec by hand, so the reproduction can name what a fresh
    /// translation of the same element gives without re-reading the file.
    /// </summary>
    private static PathEffectSpec Effect() => new(
        "powerstroke",
        "path-effect1",
        "1.4",
        new[]
        {
            new KeyValuePair<string, string>("is_visible", "true"),
            new KeyValuePair<string, string>("offset_points", "0,1 | 1,5"),
            new KeyValuePair<string, string>("not_jump", "false"),
            new KeyValuePair<string, string>("sort_points", "true"),
            new KeyValuePair<string, string>("interpolator_type", "Linear"),
            new KeyValuePair<string, string>("start_linecap_type", "zerowidth"),
            new KeyValuePair<string, string>("linejoin_type", "extrp_arc"),
            new KeyValuePair<string, string>("miter_limit", "4"),
            new KeyValuePair<string, string>("scale_width", "1"),
            new KeyValuePair<string, string>("end_linecap_type", "zerowidth"),
        });

    private static PathItem Imported(string pathData, string effect = PowerStroke)
    {
        SvgImportResult result = SvgReader.Read(File(pathData, effect));
        return Assert.Single(result.Document.AllPaths());
    }

    /// <summary>The knot at segment index 1, as the profile the path holds places it.</summary>
    private static double Knot(PathItem path)
        => path.Stroke.WidthProfile!.Points[^1].Position;

    /// <summary>The edit: the same path, extended from two segments to four.</summary>
    private static void Extend(PathItem path)
    {
        SubPath sub = Assert.Single(path.SubPaths);
        sub.Nodes.Add(new PathNode(new Point2D(30, 0), new Point2D(30, 0), new Point2D(30, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(40, 0), new Point2D(40, 0), new Point2D(40, 0)));
        path.GeometryChanged();
    }

    /// <summary>The edit the other way: the same path, shortened from four segments to two.</summary>
    private static void Shorten(PathItem path)
    {
        SubPath sub = Assert.Single(path.SubPaths);
        sub.Nodes.RemoveRange(2, sub.Nodes.Count - 2);
        path.GeometryChanged();
    }

    /// <summary>The outline's y at a given path-local x, which is the half-width there for a horizontal path.</summary>
    private static double[] Edge(PathItem path, double x)
    {
        IReadOnlyList<Point2D> outline = Assert.Single(StrokeOutlineBuilder.Plan(path, path.Stroke).Outlines);
        return outline.Where(p => Math.Abs(p.X - x) < 1e-9).Select(p => p.Y).ToArray();
    }

    // ---------------------------------------------------------------- the reproduction

    /// <summary>
    /// **The reproduction.** A knot at segment index 1 of a two-segment path is converted to 0.5 at import. Extend
    /// the same path to four segments and a fresh translation of the same element puts it at 0.25, because the knot
    /// is <c>segmentIndex + t</c> over the segment count - and the stroke in the document still holds 0.5 until the
    /// honouring step runs, which is the defect.
    ///
    /// Before this class existed the last assertion read <c>Expected: 0.25, Actual: 0.5</c>, and it is the number
    /// the issue records.
    /// </summary>
    [Fact]
    public void TheStoredProfileFollowsTheSegmentCountWhenTheEffectIsRecomputed()
    {
        PathItem path = Imported(TwoSegments);

        // #130's one-time conversion, unchanged: two segments, so the knot at index 1 is at 1/2.
        Assert.Equal(0.5, Knot(path), 9);

        // The description is on the path, so there is something to re-derive from.
        Assert.Equal(Effect(), path.PathEffect);

        Extend(path);

        // The same element is a different position on the edited path, which is what "live" means.
        PathEffectTranslation fresh = PathEffects.Translate(Effect(), path, path.Stroke);
        Assert.True(fresh.IsSupported, fresh.Refusal);
        Assert.Equal(0.25, fresh.Stroke!.WidthProfile!.Points[^1].Position, 9);

        // The step that honours the effect.
        var command = new RefreshPathEffectsCommand(path.Document!);
        command.Do();

        Assert.Equal(1, command.LastResolution!.Refreshed);
        Assert.Empty(command.LastResolution.NotRefreshed);
        Assert.Equal(0.25, Knot(path), 9);

        // And what is drawn follows too: the knot is at x=10 either way, and the taper reaches its full 5-user-unit
        // half-width there once the profile is right (3 before, because the stored knot still sat at 1/2).
        Assert.Contains(Edge(path, 10.0), y => Math.Abs(y - 5.0) < 1e-9);
        Assert.Contains(Edge(path, 10.0), y => Math.Abs(y + 5.0) < 1e-9);
    }

    /// <summary>
    /// **Nothing re-derives on read, and that is the design.** The stored profile is what the canvas, the exporters,
    /// the stroke pane and <c>pathEffect.*</c> all read, and five readers deriving five answers is worse than one
    /// stored answer that is stale until the step runs - the same discipline <see cref="RefreshInstancesCommand"/>
    /// follows. This pins the boundary so a future change to derive in the outline builder has to say so here.
    /// </summary>
    [Fact]
    public void AnEditAloneDoesNotChangeTheStoredProfile()
    {
        PathItem path = Imported(TwoSegments);
        Extend(path);

        Assert.Equal(0.5, Knot(path), 9);
    }

    // ---------------------------------------------------------------- the description as state

    /// <summary>
    /// **The description travels through the sidecar, so a reopened document can still re-derive.**
    ///
    /// A save that kept only the converted widths would hand back a document whose effect can never be recomputed -
    /// the same flattening the SVG export avoids by keeping the element - so this asserts the spec itself, not the
    /// profile it happened to produce, and then proves it is live after the reopen.
    /// </summary>
    [Fact]
    public void TheLiveEffectTravelsThroughTheSidecarAndStillReDerives()
    {
        SvgImportResult imported = SvgReader.Read(File(TwoSegments));
        CadDocument document = imported.Document;

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        PathItem path = Assert.Single(reloaded.AllPaths());
        Assert.Equal(Effect(), path.PathEffect);

        Extend(path);
        new RefreshPathEffectsCommand(reloaded).Do();
        Assert.Equal(0.25, Knot(path), 9);

        // And a document without a live effect gains no member: the bytes are what they were before this existed.
        string plain = System.Text.Encoding.UTF8.GetString(
            VccadDocumentSerializer.SerializeToBytes(CadDocument.CreateDefault()));
        Assert.DoesNotContain("PathEffect", plain, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- undo

    /// <summary>
    /// **Re-deriving is one undoable step, and undo restores the converted result it replaced.**
    ///
    /// Undo has to put back the profile the document held, not re-derive: the geometry is still the edited geometry
    /// at that point, so deriving again would produce exactly the value being undone.
    /// </summary>
    [Fact]
    public void RefreshingTheEffectIsUndoable()
    {
        PathItem path = Imported(TwoSegments);
        CadDocument document = path.Document!;
        Extend(path);

        var command = new RefreshPathEffectsCommand(document);
        command.Do();
        Assert.Equal(0.25, Knot(path), 9);

        command.Undo();
        Assert.Equal(0.5, Knot(path), 9);

        // The contract says Do must be repeatable after Undo.
        command.Do();
        Assert.Equal(0.25, Knot(path), 9);
    }

    // ---------------------------------------------------------------- what is reported, not recomputed

    /// <summary>
    /// **An edit that makes the stored knot meaningless is reported by name, and the last conversion is kept.**
    ///
    /// The path was four segments and the knot sat at 3 of them; shorten it to two and the knot is past the end.
    /// Deriving a value there would mean inventing one, and blanking the profile would turn a stroke with a width
    /// into a stroke with none - so the path keeps the profile it had and the refusal names the effect, the path and
    /// the reason. This is the same rule a missing profile or a dangling `use` follows.
    /// </summary>
    [Fact]
    public void AKnotAnEditHasLeftOffThePathIsReportedAndTheLastConversionIsKept()
    {
        // Knots at segment indices 0, 1 and 3, so the last one is only placeable on a path of four or more.
        PathItem path = Imported(FourSegments, PowerStroke.Replace("0,1 | 1,5", "0,1 | 1,5 | 3,2", StringComparison.Ordinal));
        CadDocument document = path.Document!;
        Assert.Equal(0.75, Knot(path), 9);

        Shorten(path);

        var command = new RefreshPathEffectsCommand(document);
        command.Do();

        Assert.Equal(0, command.LastResolution!.Refreshed);
        string refusal = Assert.Single(command.LastResolution.NotRefreshed);
        Assert.Contains("path-effect1", refusal, StringComparison.Ordinal);
        Assert.Contains("off the path", refusal, StringComparison.Ordinal);

        // The stroke still draws what it drew before the edit: the description is still there, and so is the
        // geometry a person last saw.
        Assert.Equal(0.75, Knot(path), 9);
        Assert.NotNull(path.Stroke.WidthProfile);
    }

    /// <summary>
    /// **An effect this build does not implement is reported, not recomputed.** The reader has always refused to
    /// redraw a path whose effect it cannot translate - the path already holds the file's own geometry - and the
    /// refresh step refuses the same way, by name, instead of quietly leaving the effect out of the drawing.
    /// </summary>
    [Fact]
    public void AnEffectThisBuildDoesNotImplementIsReportedRatherThanRecomputed()
    {
        PathItem path = Imported(TwoSegments, BendPath);
        CadDocument document = path.Document!;

        // The import behaviour is #130's and is unchanged: no profile, the geometry the file drew.
        Assert.False(path.Stroke.HasWidthProfile);
        Assert.Equal(TwoSegments, PathEffects.SourcePathData(path));
        Assert.Equal("bend_path", path.PathEffect!.Effect);

        var command = new RefreshPathEffectsCommand(document);
        command.Do();

        Assert.Equal(0, command.LastResolution!.Refreshed);
        string refusal = Assert.Single(command.LastResolution.NotRefreshed);
        Assert.Contains("bend_path", refusal, StringComparison.Ordinal);
        Assert.Contains("does not implement", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A path with no live effect is not touched, and not counted.** The resolver's reach is exactly the paths
    /// that carry a description, which is what keeps an ordinary document's undo state empty.
    /// </summary>
    [Fact]
    public void APathWithNoLiveEffectIsNotCounted()
    {
        SvgImportResult result = SvgReader.Read(File(TwoSegments).Replace(
            " inkscape:path-effect=\"#path-effect1\"", string.Empty, StringComparison.Ordinal));

        PathItem path = Assert.Single(result.Document.AllPaths());
        Assert.Null(path.PathEffect);

        var command = new RefreshPathEffectsCommand(result.Document);
        command.Do();

        Assert.Equal(0, command.LastResolution!.Refreshed);
        Assert.Empty(command.LastResolution.NotRefreshed);
    }
}
