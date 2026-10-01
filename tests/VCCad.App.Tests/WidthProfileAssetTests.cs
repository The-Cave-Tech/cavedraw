using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Width profiles as reusable assets: create, list, rename, delete, apply, and edit a single point.
///
/// The assertion that matters throughout is that editing the **asset** moves the strokes that name it. That is
/// the difference between an asset and a copy, and it is what makes a rename anything other than a cosmetic
/// change: a stroke refers to a profile by name, so a rename that left the strokes behind would leave them
/// naming a profile the document no longer has.
/// </summary>
public class WidthProfileAssetTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, CadDocument Document, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 0)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, vm.Document, path);
    }

    private static object[] TwoPoints()
        => new object[]
        {
            new { position = 0.0, left = 16, right = 16 },
            new { position = 1.0, left = 2, right = 2, interpolation = "cubic" },
        };

    [Fact]
    public void AProfileCanBeCreatedListedAndApplied()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() }));

        string list = JsonSerializer.Serialize(EditorOperations.Invoke(context, "profile.list", default));
        Assert.Contains("Brush 4", list, StringComparison.Ordinal);
        Assert.Contains("\"left\":16", list, StringComparison.Ordinal);

        // Creating it does not apply it: an asset sits in the document until something uses it.
        Assert.False(path.Stroke.HasWidthProfile);

        EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Brush 4" }));

        Assert.True(path.Stroke.HasWidthProfile);
        Assert.Equal(16.0, path.Stroke.WidthProfile!.Points[0].LeftWidth, 3);
        Assert.Equal(WidthInterpolation.Cubic, path.Stroke.WidthProfile.Points[1].Interpolation);
    }

    [Fact]
    public void TwoProfilesCannotShareAName()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() }));

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() })));

        Assert.Contains("already a profile", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(document.WidthProfiles);
    }

    /// <summary>
    /// **The test that says a profile is an asset.** Renaming it moves the strokes that named it, so the stroke
    /// still points at something real and looks the same afterwards.
    /// </summary>
    [Fact]
    public void RenamingAProfileMovesTheStrokesThatUseIt()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() }));
        EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Brush 4" }));
        Assert.Equal("Brush 4", path.Stroke.WidthProfile!.Name);

        EditorOperations.Invoke(context, "profile.rename", Params(new { from = "Brush 4", to = "Brush 6" }));

        Assert.Equal("Brush 6", document.WidthProfiles.Single().Name);
        Assert.Equal("Brush 6", path.Stroke.WidthProfile!.Name);

        // And nothing is left behind: the stroke names a profile the document actually has.
        Assert.NotNull(document.FindProfile(path.Stroke.WidthProfile.Name));
    }

    [Fact]
    public void UndoingARenameRestoresBothTheAssetAndTheStroke()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() }));
        EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Brush 4" }));
        EditorOperations.Invoke(context, "profile.rename", Params(new { from = "Brush 4", to = "Brush 6" }));

        context.ViewModel.ActiveSession.Undo();

        Assert.Equal("Brush 4", document.WidthProfiles.Single().Name);
        Assert.Equal("Brush 4", path.Stroke.WidthProfile!.Name);
    }

    /// <summary>Deleting the asset clears it from the strokes, which keep their own widths.</summary>
    [Fact]
    public void DeletingAProfileClearsItFromTheStrokesAndKeepsTheirWidth()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() }));
        EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Brush 4" }));

        EditorOperations.Invoke(context, "profile.delete", Params(new { name = "Brush 4" }));

        Assert.Empty(document.WidthProfiles);
        Assert.Null(path.Stroke.WidthProfile);
        Assert.Equal(8.0, path.Stroke.Width, 3);
    }

    /// <summary>Editing a point of the asset reaches the strokes, which is what "reusable" has to mean.</summary>
    [Fact]
    public void EditingAPointReachesTheStrokesThatUseTheProfile()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() }));
        EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Brush 4" }));

        EditorOperations.Invoke(context, "profile.setPoint",
            Params(new { name = "Brush 4", index = 0, left = 30, position = 0.2 }));

        WidthPoint point = document.WidthProfiles.Single().Points[0];
        Assert.Equal(30.0, point.LeftWidth, 3);
        Assert.Equal(0.2, point.Position, 6);

        // The right width was not given, so it keeps what it had rather than being reset.
        Assert.Equal(16.0, point.RightWidth, 3);
        Assert.Equal(30.0, path.Stroke.WidthProfile!.Points[0].LeftWidth, 3);
    }

    [Fact]
    public void APointCanBeRemovedButNotTheLastOne()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() }));

        EditorOperations.Invoke(context, "profile.removePoint", Params(new { name = "Brush 4", index = 0 }));
        Assert.Single(document.WidthProfiles.Single().Points);

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "profile.removePoint", Params(new { name = "Brush 4", index = 0 })));

        Assert.Contains("delete it instead", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(document.WidthProfiles.Single().Points);
    }

    [Fact]
    public void AnUnknownProfileIsRefusedRatherThanIgnored()
    {
        (AutomationContext context, _, _) = Host();

        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Nothing" })));
        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "profile.rename", Params(new { from = "Nothing", to = "Something" })));
        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "profile.delete", Params(new { name = "Nothing" })));
    }

    /// <summary>
    /// **A reference to an asset the document does not have is reported, not silently defaulted.**
    ///
    /// A stroke holds its profile as a value, so it keeps drawing after the asset is gone - which is exactly why
    /// this needs saying out loud. Silently drawing it at its own width would make a lost asset look like a design
    /// decision, and nobody would know to look.
    /// </summary>
    [Fact]
    public void AStrokeNamingAMissingProfileIsReported()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() }));
        EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Brush 4" }));

        Assert.Empty(JsonSerializer.Deserialize<JsonElement[]>(
            JsonSerializer.Serialize(EditorOperations.Invoke(context, "profile.missing", default)))!);

        // Reach past the operation to make the reference dangle, which is the state a merged or hand-edited
        // document arrives in: the stroke still names a profile the library no longer has.
        document.RemoveWidthProfile("Brush 4");

        JsonElement[] missing = JsonSerializer.Deserialize<JsonElement[]>(
            JsonSerializer.Serialize(EditorOperations.Invoke(context, "profile.missing", default)))!;

        JsonElement report = Assert.Single(missing);
        Assert.Equal("Brush 4", report.GetProperty("profile").GetString());
        Assert.Equal(path.Id.ToString(), report.GetProperty("itemId").GetString());
    }

    /// <summary>The library is part of the document, so it has to survive a round trip.</summary>
    [Fact]
    public void TheLibrarySurvivesSaveAndReload()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Brush 4", points = TwoPoints() }));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        WidthProfileSpec profile = Assert.Single(reloaded.WidthProfiles);
        Assert.Equal("Brush 4", profile.Name);
        Assert.Equal(2, profile.Points.Count);
        Assert.Equal(16.0, profile.Points[0].LeftWidth, 3);
        Assert.Equal(WidthInterpolation.Cubic, profile.Points[1].Interpolation);
    }
}
