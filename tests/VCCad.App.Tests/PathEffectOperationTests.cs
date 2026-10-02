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

    /// <summary>An eight-segment open path in the file's own units, so the powerstroke's knots translate exactly.</summary>
    private const string Line = "M 0,0 L 10,0 L 20,0 L 30,0 L 40,0 L 50,0 L 60,0 L 70,0 L 80,0";

    /// <summary>
    /// Inkscape's own powerstroke element, written the way Inkscape writes one: the effect's name, its id and the
    /// <c>lpeversion</c> first, then the parameters in the tool's own order. The fixture style - and the element -
    /// is copied from <c>VCCad.Core.Tests.SvgPathEffectTests</c>, which pins the reader's half of issue #155; this
    /// file pins the half a driver sees.
    /// </summary>
    private const string PowerStrokeEffect =
        "<inkscape:path-effect effect=\"powerstroke\" id=\"path-effect1\" lpeversion=\"1.4\" " +
        "is_visible=\"true\" offset_points=\"0,2 | 3,5 | 8,1\" not_jump=\"false\" sort_points=\"true\" " +
        "interpolator_type=\"Linear\" start_linecap_type=\"zerowidth\" linejoin_type=\"extrp_arc\" " +
        "miter_limit=\"4\" scale_width=\"1\" end_linecap_type=\"zerowidth\" />";

    /// <summary>
    /// The other entry in the same library, which no path in the file refers to - a named effect a person has not
    /// applied yet. It is what <c>CadDocument.ForeignPathEffects</c> was added for (issue #155), and it is the one
    /// thing in <c>document.metadata</c>'s result a driver could not previously see.
    /// </summary>
    private const string UnreferencedEffect =
        "<inkscape:path-effect effect=\"powerstroke\" id=\"path-effect2\" lpeversion=\"1.4\" " +
        "is_visible=\"true\" offset_points=\"0,1 | 4,6 | 8,2\" not_jump=\"false\" sort_points=\"true\" " +
        "interpolator_type=\"Linear\" start_linecap_type=\"zerowidth\" linejoin_type=\"extrp_arc\" " +
        "miter_limit=\"4\" scale_width=\"1\" end_linecap_type=\"zerowidth\" />";

    /// <summary>
    /// A file shaped like Inkscape's: the effect library in <c>defs</c>, and the path the first effect was applied
    /// to carrying the reference. <paramref name="effectElements"/> is what the library holds, so the two cases
    /// below differ in exactly one thing - whether the unreferenced entry is in the file at all.
    ///
    /// The namespace is declared on the root and there is no root-level foreign element, so the file's own
    /// <c>SvgExtras</c> count is zero. That is worth stating because it makes the two counts in the summary
    /// distinguishable: a reply that swapped them would fail both assertions rather than pass on a fixture where
    /// the two happened to be equal.
    /// </summary>
    private static string InkscapeFile(string effectElements)
        => "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
           "xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
           "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
           "<defs>" + effectElements + "</defs>" +
           "<path id=\"p1\" style=\"fill:none;stroke:#000000;stroke-width:1\" d=\"" + Line + "\" " +
           "inkscape:original-d=\"" + Line + "\" inkscape:path-effect=\"#path-effect1\" />" +
           "</svg>";

    /// <summary>Imports an SVG through the registry and returns the <c>document.metadata</c> reply as JSON.</summary>
    private static JsonElement MetadataAfterImport(AutomationContext context, string svg)
    {
        EditorOperations.Invoke(context, "document.importSvg", Params(new { svgBase64 = Base64(svg) }));
        return JsonSerializer.SerializeToElement(EditorOperations.Invoke(context, "document.metadata", default));
    }

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
    /// **The honouring step, through the registry.** An imported effect is converted once, and the conversion is
    /// only correct for the path it was translated against - a powerstroke stores its knots as a segment index over
    /// the whole path. Edit the path and <c>pathEffect.refresh</c> re-derives the profile from it, as one undo step,
    /// which is the half issue #180 says was missing.
    /// </summary>
    [Fact]
    public void RefreshingReDerivesTheProfileFromTheEditedPath()
    {
        var vm = new EditorViewModel();
        var context = new AutomationContext { ViewModel = vm };

        // Knot at segment index 1 of a two-segment path, so the conversion puts it at 1/2.
        const string Inkscape =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
            "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
            "<defs><inkscape:path-effect effect=\"powerstroke\" id=\"path-effect1\" lpeversion=\"1.4\" " +
            "offset_points=\"1,3\" interpolator_type=\"Linear\" linejoin_type=\"extrp_arc\" " +
            "start_linecap_type=\"zerowidth\" end_linecap_type=\"zerowidth\" miter_limit=\"4\" scale_width=\"1\" /></defs>" +
            "<path id=\"p1\" style=\"fill:none;stroke:#000000\" d=\"M 0,0 L 10,0 L 20,0\" " +
            "inkscape:original-d=\"M 0,0 L 10,0 L 20,0\" inkscape:path-effect=\"#path-effect1\" /></svg>";

        EditorOperations.Invoke(context, "document.importSvg", Params(new { svgBase64 = Base64(Inkscape) }));

        PathItem path = context.Document.AllPaths().Single();
        Assert.Equal(0.5, path.Stroke.WidthProfile!.Points[0].Position, 9);

        // The edit: the same path, extended from two segments to four.
        SubPath sub = path.SubPaths[0];
        sub.Nodes.Add(new PathNode(new Point2D(30, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(40, 0)));
        path.GeometryChanged();

        string json = JsonSerializer.Serialize(
            EditorOperations.Invoke(context, "pathEffect.refresh", default));

        Assert.Contains("\"refreshed\":1", json, StringComparison.Ordinal);
        Assert.Equal(0.25, path.Stroke.WidthProfile!.Points[0].Position, 9);

        // One undo step, restoring the converted result rather than deriving it again.
        vm.ActiveSession.Undo();
        Assert.Equal(0.5, path.Stroke.WidthProfile!.Points[0].Position, 9);
    }

    /// <summary>
    /// **The live effect itself is legible to a driver, not only its conversion.** `pathEffect.list` names the
    /// reference by id; `pathEffect.get` reports the description stored on the path - the effect's name and every
    /// parameter the file wrote - and what that description derives to against the path as it stands now, which is
    /// what `pathEffect.refresh` re-derives from.
    /// </summary>
    [Fact]
    public void TheLiveEffectIsReadWithItsDescriptionAndItsDerivation()
    {
        var vm = new EditorViewModel();
        var context = new AutomationContext { ViewModel = vm };

        const string Inkscape =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
            "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
            "<defs><inkscape:path-effect effect=\"powerstroke\" id=\"path-effect1\" lpeversion=\"1.4\" " +
            "offset_points=\"1,3\" interpolator_type=\"Linear\" linejoin_type=\"extrp_arc\" " +
            "start_linecap_type=\"zerowidth\" end_linecap_type=\"zerowidth\" miter_limit=\"4\" scale_width=\"1\" /></defs>" +
            "<path id=\"p1\" style=\"fill:none;stroke:#000000\" d=\"M 0,0 L 10,0 L 20,0\" " +
            "inkscape:original-d=\"M 0,0 L 10,0 L 20,0\" inkscape:path-effect=\"#path-effect1\" /></svg>";

        EditorOperations.Invoke(context, "document.importSvg", Params(new { svgBase64 = Base64(Inkscape) }));
        PathItem path = context.Document.AllPaths().Single();
        vm.SelectObject(path);

        JsonElement item = Assert.Single(JsonSerializer
            .SerializeToElement(EditorOperations.Invoke(context, "pathEffect.get", default))
            .GetProperty("items")
            .EnumerateArray());

        Assert.Equal("powerstroke", item.GetProperty("effect").GetProperty("effect").GetString());
        Assert.Equal("path-effect1", item.GetProperty("effect").GetProperty("id").GetString());
        Assert.Equal(
            "1,3",
            item.GetProperty("effect").GetProperty("parameters").GetProperty("offset_points").GetString());

        // And the derivation, against the path as it stands: two segments, so the knot is at 1/2.
        Assert.Equal(2, item.GetProperty("segments").GetInt32());
        Assert.True(item.GetProperty("supported").GetBoolean());

        JsonElement point = Assert.Single(item.GetProperty("profile").GetProperty("points").EnumerateArray());
        Assert.Equal(0.5, point.GetProperty("position").GetDouble(), 9);
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
    /// **An SVG with a live path effect the reader could not read says so.** The reader keeps the element from
    /// <c>defs</c> and translates a powerstroke, so a path carrying a reference is not by itself unread; what is
    /// reported is an effect that produced no profile. This fixture is that case by refusal rather than by absence:
    /// its knots sit on a **one-segment** path, so the translation is refused and the stroke keeps the file's own
    /// frozen geometry. The report names the effect by the id the file used rather than looking like a drawing that
    /// never had one.
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

    /// <summary>An import's reply as JSON, so a test reads it the way a driver receives it.</summary>
    private static JsonElement Import(AutomationContext context, string svg)
        => JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.importSvg", Params(new { svgBase64 = Base64(svg) })));

    /// <summary>An import reply's warnings, as the text a person reads and a driver greps.</summary>
    private static string[] Warnings(JsonElement result)
        => result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();

    /// <summary>The phrase the kept-effects warning always carries, so a test can find it without pinning the prose.</summary>
    private const string KeptPhrase = "no path refers to";

    /// <summary>
    /// **The documentary reply names the kept effect, not only its number.**
    ///
    /// Issue #163's first half. `document.metadata` has reported `foreignPathEffects` as a count since #155, and a
    /// count answers "did something survive" rather than "what": the state is kept, written back into `defs` and
    /// carried in the sidecar, and a driver that asks about it is handed `1`. Its siblings on the same reply are
    /// richer - `namespaces` gives prefix/uri pairs and `selection` gives per-item names - so the kept entries are
    /// named the same way, each by the `id` the file gave it and the `effect` name the element carries.
    ///
    /// The two halves of the library are asserted separately: the kept entry is named, and the **referenced** one is
    /// not in this list, because it travels on the path that points at it and is already identifiable through
    /// `pathEffect.list`. A list that named both would send a driver looking for a definition with no user that the
    /// document does not have.
    /// </summary>
    [Fact]
    public void MetadataNamesTheKeptPathEffect()
    {
        (AutomationContext context, _) = Host();

        JsonElement metadata = MetadataAfterImport(
            context, InkscapeFile(PowerStrokeEffect + UnreferencedEffect));

        JsonElement kept = Assert.Single(metadata.GetProperty("foreignPathEffectNames").EnumerateArray());
        Assert.Equal("path-effect2", kept.GetProperty("id").GetString());
        Assert.Equal("powerstroke", kept.GetProperty("effect").GetString());

        // The count it sits beside is unchanged, so a reply that reported one and not the other still fails.
        Assert.Equal(1, metadata.GetProperty("foreignPathEffects").GetInt32());
    }

    /// <summary>
    /// **The import reply says the document now carries an unreferenced effect.**
    ///
    /// Issue #163's second half. Before this, an import that left a foreign definition in the document said nothing
    /// about it: `livePathEffects` covers the opposite case - an effect a path names that this build could not read
    /// - so a person comparing the file with the drawing had nothing, and a driver had to make a later
    /// `document.metadata` call to find out. Reported on the reply's existing warning surface, which is where the
    /// person and the driver both look, and it names the entry rather than only counting it.
    ///
    /// The referenced effect is deliberately **not** named by this warning: it was translated and travels on the
    /// path, and announcing it as kept would be the same confusion the unread report already guards against.
    /// </summary>
    [Fact]
    public void ImportingAFileWithAnUnreferencedEffectReportsItInTheWarnings()
    {
        (AutomationContext context, _) = Host();

        JsonElement result = Import(context, InkscapeFile(PowerStrokeEffect + UnreferencedEffect));
        string[] warnings = Warnings(result);

        Assert.Contains(warnings, warning =>
            warning.Contains(KeptPhrase, StringComparison.Ordinal) &&
            warning.Contains("path-effect2", StringComparison.Ordinal) &&
            warning.Contains("powerstroke", StringComparison.Ordinal));

        Assert.DoesNotContain(warnings, warning =>
            warning.Contains(KeptPhrase, StringComparison.Ordinal) &&
            warning.Contains("path-effect1", StringComparison.Ordinal));

        // And the opposite case is still reported as its own, not merged into this one.
        Assert.Empty(result.GetProperty("livePathEffects").EnumerateArray());
    }

    /// <summary>
    /// **An effect whose own name cannot be read is named by its id alone - the name is not invented.**
    ///
    /// The rule this campaign has paid for repeatedly (#140, #143, #144, #150, #151, #152, #155, #158, #160,
    /// #162): a report says what it can read and never fills a gap with a plausible value. An element the reader
    /// keeps can carry an `id` and no `effect` attribute at all, so both surfaces report the `id` and leave the
    /// effect name out rather than inventing one from the id or defaulting it to something that reads like a fact.
    /// The entry still appears, so the count and the list still agree.
    /// </summary>
    [Fact]
    public void AKeptEffectWhoseNameCannotBeReadIsReportedByIdAlone()
    {
        const string NoEffectAttribute =
            "<inkscape:path-effect id=\"path-effect3\" lpeversion=\"1.4\" is_visible=\"true\" />";

        (AutomationContext context, _) = Host();

        JsonElement result = Import(context, InkscapeFile(NoEffectAttribute));

        JsonElement kept = Assert.Single(
            JsonSerializer.SerializeToElement(EditorOperations.Invoke(context, "document.metadata", default))
                .GetProperty("foreignPathEffectNames").EnumerateArray());

        Assert.Equal("path-effect3", kept.GetProperty("id").GetString());
        Assert.Null(kept.GetProperty("effect").GetString());

        // The warning names the id, which is the whole of what the element says it is.
        Assert.Contains(Warnings(result), warning =>
            warning.Contains(KeptPhrase, StringComparison.Ordinal) &&
            warning.Contains("path-effect3", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A kept definition the reply cannot name at all is still reported, with neither half invented.**
    ///
    /// The reader only keeps elements it found by `id`, so an import always names what it keeps - but the stored
    /// text is carried verbatim in the sidecar, and a document can therefore hold a definition with no `id`, or one
    /// whose stored text does not even parse (an `ns0:` prefix the text never declared). Both are reported as an
    /// entry of the list with the half that could not be read left **null**, so the length of the list still equals
    /// the count beside it and a driver can see that something is there without being told a name that is not.
    ///
    /// The count is asserted too: a reply that dropped the unnameable entry would quietly make the two fields
    /// disagree, which is exactly the sort of half-answer this issue is about.
    /// </summary>
    [Fact]
    public void AKeptEffectThatCannotBeNamedIsNotInvented()
    {
        (AutomationContext context, _) = Host();

        context.Document.SetForeignPathEffects(new[]
        {
            // An element with an effect name and no id, and one whose prefix is declared nowhere, so it cannot be
            // read as XML at all. The second is the namespace surprise the rule names.
            "<inkscape:path-effect effect=\"bend_path\" lpeversion=\"1.4\" " +
            "xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" />",
            "<ns0:path-effect effect=\"powerstroke\" id=\"path-effect9\" />",
        });

        JsonElement metadata = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.metadata", default));

        JsonElement[] kept = metadata.GetProperty("foreignPathEffectNames").EnumerateArray().ToArray();
        Assert.Equal(2, kept.Length);

        Assert.Null(kept[0].GetProperty("id").GetString());
        Assert.Equal("bend_path", kept[0].GetProperty("effect").GetString());

        Assert.Null(kept[1].GetProperty("id").GetString());
        Assert.Null(kept[1].GetProperty("effect").GetString());

        Assert.Equal(2, metadata.GetProperty("foreignPathEffects").GetInt32());
    }

    /// <summary>
    /// **A file with no unreferenced effect says nothing about one, in either reply.**
    ///
    /// The negative half of both surfaces, and the reason they stay usable: a document that keeps no foreign
    /// definition must leave `foreignPathEffectNames` empty and must not raise the import warning, so "nothing was
    /// said" means "there is nothing" rather than "nobody is looking". The file here declares its namespace on the
    /// root and carries no path-effect at all, which is the plainest file there is.
    /// </summary>
    [Fact]
    public void AFileWithNoForeignPathEffectReportsNothingInEitherReply()
    {
        (AutomationContext context, _) = Host();

        JsonElement result = Import(
            context,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">" +
            "<path d=\"M0 0 L5 5\"/></svg>");

        Assert.DoesNotContain(Warnings(result), warning =>
            warning.Contains(KeptPhrase, StringComparison.Ordinal));

        JsonElement metadata = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.metadata", default));

        Assert.Empty(metadata.GetProperty("foreignPathEffectNames").EnumerateArray());
        Assert.Equal(0, metadata.GetProperty("foreignPathEffects").GetInt32());
    }

    /// <summary>
    /// **The file half of the import says it too, and the two replies agree.**
    ///
    /// `document.importSvg` and `document.importSvgFile` are separate handlers, so the report is asserted on both
    /// rather than assumed to have travelled: a driver that opens a file by path is told exactly what a driver that
    /// handed the bytes over is told. The metadata reply is read afterwards as well, so the import's warning and
    /// the documentary list cannot drift apart - they are two surfaces of one fact.
    /// </summary>
    [Fact]
    public void ImportingAnSvgFileWithAnUnreferencedEffectReportsItInTheWarnings()
    {
        (AutomationContext context, _) = Host();
        string path = Path.Combine(Path.GetTempPath(), $"vccad-kept-effect-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, InkscapeFile(PowerStrokeEffect + UnreferencedEffect));

        try
        {
            JsonElement result = JsonSerializer.SerializeToElement(
                EditorOperations.Invoke(context, "document.importSvgFile", Params(new { path })));

            Assert.Contains(Warnings(result), warning =>
                warning.Contains(KeptPhrase, StringComparison.Ordinal) &&
                warning.Contains("path-effect2", StringComparison.Ordinal));

            JsonElement kept = Assert.Single(
                JsonSerializer.SerializeToElement(EditorOperations.Invoke(context, "document.metadata", default))
                    .GetProperty("foreignPathEffectNames").EnumerateArray());

            Assert.Equal("path-effect2", kept.GetProperty("id").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Base64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
    /// <summary>
    /// **A powerstroke the reader translated is not reported as unread.** This is the half the first draft of that
    /// report got wrong: naming every path that carried a reference meant an applied effect was announced as
    /// ignored - a person seeing it applied while a driver is told it was not, which is the failure this family is
    /// about. The knots here lie inside an eight-segment path, so the translation succeeds and nothing is reported.
    /// </summary>
    [Fact]
    public void ATranslatedPowerStrokeIsNotReportedAsUnread()
    {
        const string Inkscape =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
            "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
            "<defs><inkscape:path-effect effect=\"powerstroke\" id=\"path-effect1\" lpeversion=\"1.4\" " +
            "offset_points=\"0,2 | 3,5 | 8,1\" interpolator_type=\"Linear\" linejoin_type=\"extrp_arc\" " +
            "start_linecap_type=\"zerowidth\" end_linecap_type=\"zerowidth\" miter_limit=\"4\" scale_width=\"1\" /></defs>" +
            "<path id=\"p1\" style=\"fill:none;stroke:#000000\" " +
            "d=\"M 0,0 L 10,0 L 20,0 L 30,0 L 40,0 L 50,0 L 60,0 L 70,0 L 80,0\" " +
            "inkscape:original-d=\"M 0,0 L 80,0\" inkscape:path-effect=\"#path-effect1\" /></svg>";

        (AutomationContext context, _) = Host();
        JsonElement result = JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            context, "document.importSvg", Params(new { svgBase64 = Base64(Inkscape) })));

        PathItem path = context.Document.AllPaths().Single();
        Assert.True(path.Stroke.HasWidthProfile, "the effect was translated, so the stroke carries a profile");
        Assert.Empty(result.GetProperty("livePathEffects").EnumerateArray());
        string[] warnings = result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();
        Assert.DoesNotContain(warnings, warning => warning.Contains("path-effect1", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The unreferenced effect is not only kept, it is counted where a driver can read it.**
    ///
    /// The document model has held it since issue #155, and <c>document.metadata</c> is the only operation that
    /// reports what the file carried and the model has no meaning for - so before this count was in its reply, the
    /// member kept a person's library and an API driver had no way to learn it was there. A capability the person
    /// has and the driver does not is the design defect the registry exists to prevent, and this is the assertion
    /// that catches it coming back.
    ///
    /// The two counts are asserted separately and they differ, which is deliberate: one unreferenced effect and no
    /// root-level extra, so a reply that reported one member's value in the other's field fails here rather than
    /// passing by coincidence.
    /// </summary>
    [Fact]
    public void MetadataReportsTheUnreferencedPathEffectCount()
    {
        (AutomationContext context, _) = Host();

        JsonElement metadata = MetadataAfterImport(
            context, InkscapeFile(PowerStrokeEffect + UnreferencedEffect));

        // The referenced half travels on the path that points at it, so exactly the other one is kept.
        Assert.Equal(1, metadata.GetProperty("foreignPathEffects").GetInt32());

        // The file's own truth: this fixture declares its namespace on the root and carries no root-level foreign
        // element, so there is nothing in the extras list beside the effect.
        Assert.Equal(0, metadata.GetProperty("extras").GetInt32());
    }

    /// <summary>
    /// **A library where everything is referred to reports nothing kept**, which is what keeps the count usable:
    /// if a referenced effect were also counted, a driver would be told to look for content the file does not have
    /// and would find none.
    ///
    /// The path is asserted to carry the translated profile as well, so the zero means "there was nothing left
    /// over" rather than "the import read no effect at all" - a reader that dropped the whole library would also
    /// report a cheerful zero.
    /// </summary>
    [Fact]
    public void MetadataReportsNoUnreferencedPathEffectWhenEveryEffectIsReferenced()
    {
        (AutomationContext context, _) = Host();

        JsonElement metadata = MetadataAfterImport(context, InkscapeFile(PowerStrokeEffect));

        Assert.Equal(0, metadata.GetProperty("foreignPathEffects").GetInt32());
        Assert.Equal(0, metadata.GetProperty("extras").GetInt32());

        // And the list beside the count names nothing, so a driver is not sent looking for an entry that is not
        // there - the negative half of issue #163's naming.
        Assert.Empty(metadata.GetProperty("foreignPathEffectNames").EnumerateArray());

        Assert.True(context.Document.AllPaths().Single().Stroke.HasWidthProfile,
            "the referenced effect was translated, so nothing left over is not nothing read");
    }
}