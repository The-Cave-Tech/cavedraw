using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Inkscape's live path effects through the operation registry (issue #130), which is the only way anything gets
/// done here - the diagnostics overlay's Operations tab runs exactly what a driver posts to the endpoint.
///
/// The assertions are on the **stroke that comes out**: the width points the effect's own parameters produce, and
/// the fact that an effect this build does not implement leaves the stroke alone. An operation that answered
/// "applied" while writing nothing would pass a test that only checked the reply.
/// </summary>
public class PathEffectOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>An eight-segment open path, so the powerstroke's segment-indexed knots are known exactly.</summary>
    private static (AutomationContext Context, PathItem Path) Host(int segments = 8)
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        for (int i = 0; i <= segments; i++)
        {
            sub.Nodes.Add(new PathNode(new Point2D(i * 10, 0)));
        }

        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 1, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, path);
    }

    private static object PowerStroke() => new
    {
        effect = "powerstroke",
        id = "path-effect128881-2",
        version = "1.4",
        parameters = new Dictionary<string, string>
        {
            ["offset_points"] = "0,2 | 3,5 | 8,1",
            ["interpolator_type"] = "CubicBezierSmooth",
            ["linejoin_type"] = "extrp_arc",
            ["start_linecap_type"] = "zerowidth",
            ["end_linecap_type"] = "zerowidth",
            ["miter_limit"] = "4",
            ["scale_width"] = "1",
        },
    };

    /// <summary>
    /// **The stroke the operation writes is the effect's own widths at the effect's own positions.** The knots are
    /// stored as a segment index, so on this eight-segment path they sit at 0, 0.375 and 1 - and the widths are
    /// twice the stored offsets, because Inkscape's knot is the distance from the centreline and the stroke is
    /// the band either side of it.
    /// </summary>
    [Fact]
    public void APowerStrokeBecomesWidthPointsOnTheStroke()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "pathEffect.apply", Params(PowerStroke()));

        WidthProfileSpec profile = path.Stroke.WidthProfile!;
        Assert.Equal("path-effect128881-2", profile.Name);
        Assert.Equal(3, profile.Points.Count);
        Assert.Equal(0.0, profile.Points[0].Position, 9);
        Assert.Equal(4.0, profile.Points[0].LeftWidth, 9);
        Assert.Equal(4.0, profile.Points[0].RightWidth, 9);
        Assert.Equal(0.375, profile.Points[1].Position, 9);
        Assert.Equal(10.0, profile.Points[1].LeftWidth, 9);
        Assert.Equal(1.0, profile.Points[2].Position, 9);
        Assert.Equal(2.0, profile.Points[2].LeftWidth, 9);
        Assert.Equal(StrokeJoin.Miter, path.Stroke.Join);

        // And the profile is a document asset, so the stroke does not name one that is not there - which is what
        // the missing-profile report exists to catch.
        WidthProfileSpec stored = Assert.Single(context.ViewModel.Document.WidthProfiles);
        Assert.Equal("path-effect128881-2", stored.Name);
        Assert.Equal(10.0, stored.Points[1].LeftWidth, 9);
        Assert.Empty(context.ViewModel.Document.MissingWidthProfiles());
    }

    /// <summary>
    /// **An effect this build does not implement is reported by name and changes nothing.** Inkscape's
    /// <c>bend_path</c> is in the same corpus as the powerstrokes, and a reader that quietly dropped it would
    /// draw the path as though the effect had never been there - which is a plausible picture of the wrong thing.
    /// </summary>
    [Fact]
    public void AnUnimplementedEffectIsReportedAndChangesNothing()
    {
        (AutomationContext context, PathItem path) = Host();
        StrokeSpec before = path.Stroke;

        object result = EditorOperations.Invoke(context, "pathEffect.apply", Params(new
        {
            effect = "bend_path",
            id = "path-effect122389-7",
            parameters = new Dictionary<string, string>
            {
                ["bendpath"] = "m 1121.5,272.7 c 42.6,20.1 85.6,34.1 131.5,0",
            },
        }));

        Assert.Equal(before, path.Stroke);
        Assert.Null(path.Stroke.WidthProfile);
        Assert.Empty(context.ViewModel.Document.WidthProfiles);

        string json = JsonSerializer.Serialize(result);
        Assert.Contains("bend_path", json, StringComparison.Ordinal);
        Assert.Contains("powerstroke", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The read-only half: what the effect would become, with the refusal that names it and the note that says
    /// what was substituted - and the document untouched either way.
    /// </summary>
    [Fact]
    public void TranslatingReportsWithoutChangingTheDocument()
    {
        (AutomationContext context, PathItem path) = Host();

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(
            context,
            "pathEffect.translate",
            Params(new
            {
                effect = "powerstroke",
                id = "path-effect1",
                parameters = new Dictionary<string, string>
                {
                    ["offset_points"] = "0,2 | 8,6",
                    ["interpolator_type"] = "CentripetalCatmullRom",
                    ["linejoin_type"] = "round",
                },
            })));

        Assert.Contains("\"supported\":true", json, StringComparison.Ordinal);
        Assert.Contains("CentripetalCatmullRom", json, StringComparison.Ordinal);
        Assert.Contains("\"left\":12", json, StringComparison.Ordinal);
        Assert.Contains("\"join\":\"round\"", json, StringComparison.Ordinal);

        // Reading is not an edit: the stroke has no profile and there is no asset.
        Assert.Null(path.Stroke.WidthProfile);
        Assert.Empty(context.ViewModel.Document.WidthProfiles);
    }

    /// <summary>
    /// The live path effect a path carries is reported by the id the file referred to it by, and the path the
    /// effect was applied to is reported beside it - both of which travel with the item as the foreign attributes
    /// it carried, so a save and a re-open do not lose the reason the stroke looks like it does.
    /// </summary>
    [Fact]
    public void TheEffectAPathCarriesIsListed()
    {
        (AutomationContext context, PathItem path) = Host();
        path.ForeignAttributes["inkscape:path-effect"] = "#path-effect128881-2";
        path.ForeignAttributes["inkscape:original-d"] = "M 0,0 L 80,0";

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "pathEffect.list", default));

        Assert.Contains("\"effect\":\"path-effect128881-2\"", json, StringComparison.Ordinal);
        Assert.Contains("M 0,0 L 80,0", json, StringComparison.Ordinal);
        Assert.Contains("powerstroke", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// **One undo step.** Converting an effect writes a new asset and the strokes that refer to it, and the model
    /// refuses to hold a stroke naming a profile that is not there - so undo has to take both halves back
    /// together, or it leaves the document in a state it should not be able to reach.
    /// </summary>
    [Fact]
    public void ApplyingAnEffectUndoesAsOneStep()
    {
        (AutomationContext context, PathItem path) = Host();
        StrokeSpec before = path.Stroke;

        EditorOperations.Invoke(context, "pathEffect.apply", Params(PowerStroke()));
        Assert.True(path.Stroke.HasWidthProfile);

        context.ViewModel.ActiveSession.Undo();

        Assert.Equal(before, path.Stroke);
        Assert.Empty(context.ViewModel.Document.WidthProfiles);
    }

    /// <summary>
    /// Two paths of different length carry different knots for the same effect, so they cannot share one named
    /// asset: the second would be drawn with the first one's widths at the first one's positions.
    /// </summary>
    [Fact]
    public void TwoPathsOfDifferentLengthGetTheirOwnProfiles()
    {
        (AutomationContext context, PathItem shortPath) = Host(segments: 8);

        var longPath = new PathItem { Name = "long", Fill = FillSpec.None };
        SubPath sub = longPath.AddSubPath(closed: false);
        for (int i = 0; i <= 16; i++)
        {
            sub.Nodes.Add(new PathNode(new Point2D(i * 5, 40)));
        }

        longPath.Stroke = new StrokeSpec(true, ColorRgb.Black, 1, StrokeCap.Butt, StrokeJoin.Miter, 4);
        context.ViewModel.Document.Artboards[0].Layers[0].AddItem(longPath);
        context.ViewModel.SelectObject(shortPath);
        context.ViewModel.ToggleObjectSelection(longPath);

        EditorOperations.Invoke(context, "pathEffect.apply", Params(PowerStroke()));

        Assert.Equal(2, context.ViewModel.Document.WidthProfiles.Count);
        Assert.Equal(0.375, shortPath.Stroke.WidthProfile!.Points[1].Position, 9);
        Assert.Equal(3.0 / 16.0, longPath.Stroke.WidthProfile!.Points[1].Position, 9);
        Assert.NotEqual(shortPath.Stroke.WidthProfile.Name, longPath.Stroke.WidthProfile.Name);
        Assert.Empty(context.ViewModel.Document.MissingWidthProfiles());
    }

    /// <summary>
    /// **An SVG that came in with a live path effect on it says so.** The reader keeps the reference the path
    /// carries and the path the effect was applied to, but the element the reference points at lives in
    /// <c>defs</c> and is not kept - so the effect cannot be translated. The stroke is drawn as the file's own
    /// frozen output, which is the right picture, and the import reports the effect by the id the file used rather
    /// than looking like a drawing that never had one.
    /// </summary>
    [Fact]
    public void ImportingAnSvgWithALivePathEffectReportsIt()
    {
        const string Inkscape =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
            "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
            "<defs><inkscape:path-effect effect=\"powerstroke\" id=\"path-effect1\" lpeversion=\"1.4\" " +
            "offset_points=\"0,2 | 3,5\" linejoin_type=\"extrp_arc\" /></defs>" +
            "<path id=\"p1\" style=\"fill:none;stroke:#000000\" d=\"M 10,50 L 90,50\" " +
            "inkscape:original-d=\"M 10,50 L 90,50\" inkscape:path-effect=\"#path-effect1\" /></svg>";

        (AutomationContext context, _) = Host();
        JsonElement result = JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            context, "document.importSvg", Params(new { svgBase64 = Base64(Inkscape) })));

        JsonElement reported = Assert.Single(result.GetProperty("livePathEffects").EnumerateArray());
        Assert.Equal("path-effect1", reported.GetProperty("effect").GetString());
        Assert.Equal("M 10,50 L 90,50", reported.GetProperty("sourcePathData").GetString());

        string[] warnings = result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();
        Assert.Contains(warnings, warning => warning.Contains("path-effect1", StringComparison.Ordinal));

        // And the geometry is the file's own, untouched: the reference is data on the path, not something the
        // import rewrites.
        PathItem path = context.Document.AllPaths().Single();
        Assert.Equal("path-effect1", PathEffects.ReferenceOn(path));
        Assert.Null(path.Stroke.WidthProfile);
    }

    /// <summary>A file with no live path effect says nothing about one, which is what keeps the report usable.</summary>
    [Fact]
    public void AnSvgWithNoLivePathEffectReportsNothing()
    {
        (AutomationContext context, _) = Host();
        JsonElement result = JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            context,
            "document.importSvg",
            Params(new { svgBase64 = Base64("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">" +
                                            "<path d=\"M0 0 L5 5\"/></svg>") })));

        Assert.Empty(result.GetProperty("livePathEffects").EnumerateArray());
    }

    private static string Base64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
}
