using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The inspector sections the feature list names that the model can honestly support: **stroke type** (a plain
/// stroke or one carrying a width profile) and **tablet dynamics** (the response curve a stroke records).
///
/// The brush selector is deliberately absent: brushes do not exist in the model (#99-#103), so a control offering
/// them would be a placeholder that does nothing.
///
/// Every edit is asserted on the **model**, at the stroke the shared `InspectedStroke` names - the failure these
/// sections are most likely to have is a control that changes a different stroke than the pane is describing.
/// </summary>
public class StrokePaneSectionTests
{
    // ---------------------------------------------------------------- driving the pane

    private static StrokePane Pane(EditorViewModel viewModel, int inspected, params PathItem[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            if (i == 0)
            {
                viewModel.SelectObject(paths[i]);
            }
            else
            {
                viewModel.ToggleObjectSelection(paths[i]);
            }
        }

        viewModel.InspectedStroke = inspected;

        // Attached **after** the selection is made: attaching is what subscribes the pane to the session, and the
        // shared index is clamped against the selection, so both have to be in place first.
        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();
        return pane;
    }

    private static (StrokePane Pane, EditorViewModel ViewModel, PathItem First, PathItem Second) Host(
        int inspected, params StrokeSpec[] strokes)
    {
        var viewModel = new EditorViewModel();
        PathItem first = Line(viewModel, strokes);
        PathItem second = Line(viewModel, strokes);
        return (Pane(viewModel, inspected, first, second), viewModel, first, second);
    }

    private static PathItem Line(EditorViewModel viewModel, params StrokeSpec[] strokes)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        path.Strokes.AddRange(strokes);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static StrokeSpec Stroke(double width)
        => new(true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4.0);

    private static WidthProfileSpec Taper() => new("Taper", new[]
    {
        WidthPoint.Even(0, 6),
        WidthPoint.Even(1, 1, WidthInterpolation.Cubic),
    });

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static ComboBox Combo(StrokePane pane, string name)
        => pane.FindControl<ComboBox>(name) ?? throw new Xunit.Sdk.XunitException($"no combo called {name}");

    private static CheckBox Check(StrokePane pane, string name)
        => pane.FindControl<CheckBox>(name) ?? throw new Xunit.Sdk.XunitException($"no check box called {name}");

    private static TextBlock Text(StrokePane pane, string name)
        => pane.FindControl<TextBlock>(name) ?? throw new Xunit.Sdk.XunitException($"no text block called {name}");

    private static bool Visible(StrokePane pane, string name)
        => pane.FindControl<Control>(name)?.IsVisible ?? throw new Xunit.Sdk.XunitException($"no control called {name}");

    // ---------------------------------------------------------------- stroke type: plain or a width profile

    /// <summary>**The profile section appears for a stroke that has one**, and says which profile it is.</summary>
    [AvaloniaFact]
    public void TheProfileSectionAppearsForAStrokeThatHasOne()
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, Stroke(4) with { WidthProfile = Taper() });
        StrokePane pane = Pane(viewModel, 0, path);

        Assert.True(Visible(pane, "ProfileSection"));
        Assert.Contains("Taper", Text(pane, "ProfileSummary").Text);
    }

    /// <summary>**And it is not there for a plain stroke** - a profile readout over a stroke with no profile would
    /// be chrome describing something that does not exist.</summary>
    [AvaloniaFact]
    public void TheProfileSectionIsHiddenForAPlainStroke()
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, Stroke(4));
        StrokePane pane = Pane(viewModel, 0, path);

        Assert.False(Visible(pane, "ProfileSection"));
    }

    /// <summary>The readout names the width points, so the panel says what the profile actually is rather than only
    /// that there is one.</summary>
    [AvaloniaFact]
    public void TheProfileSectionNamesTheWidthPoints()
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, Stroke(4) with { WidthProfile = Taper() });
        StrokePane pane = Pane(viewModel, 0, path);

        string points = Text(pane, "ProfilePoints").Text ?? string.Empty;
        Assert.Contains("6", points);
        Assert.Contains("1", points);
        Assert.Contains("cubic", points, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **Choosing a profile gives it to the inspected stroke** and to no other stroke of the stack - the granularity
    /// the shared index exists for. `style.setWidthProfile` writes every stroke of every selected path, which is a
    /// named follow-up for the registry: the operation cannot yet reach the stroke the inspector describes.
    /// </summary>
    [AvaloniaFact]
    public void ChoosingAProfileLandsOnTheInspectedStrokeOnly()
    {
        var viewModel = new EditorViewModel();
        viewModel.Document.AddWidthProfile(Taper());

        PathItem first = Line(viewModel, Stroke(4), Stroke(8));
        PathItem second = Line(viewModel, Stroke(4), Stroke(8));
        viewModel.InspectedStroke = 1;
        StrokePane pane = Pane(viewModel, 1, first, second);

        Combo(pane, "StrokeTypeBox").SelectedItem = "Taper";
        Settle();

        Assert.Equal("Taper", first.Strokes[1].WidthProfile?.Name);
        Assert.Equal("Taper", second.Strokes[1].WidthProfile?.Name);
        Assert.Null(first.Strokes[0].WidthProfile);
        Assert.Null(second.Strokes[0].WidthProfile);
        Assert.Equal(8.0, first.Strokes[1].Width, 6);
    }

    /// <summary>Choosing "Plain" clears the profile and leaves the stroke's own width, which is what removing a
    /// profile means: the profile is a modulation of an ordinary stroke, not a replacement for it.</summary>
    [AvaloniaFact]
    public void ChoosingPlainClearsTheProfileAndKeepsTheWidth()
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, Stroke(9) with { WidthProfile = Taper() });
        StrokePane pane = Pane(viewModel, 0, path);

        Combo(pane, "StrokeTypeBox").SelectedItem = "Plain";
        Settle();

        Assert.Null(path.Strokes[0].WidthProfile);
        Assert.Equal(9.0, path.Strokes[0].Width, 6);
    }

    /// <summary>A selection that disagrees on the profile is **mixed**, not one path's profile: the type box shows
    /// no selection and says so, and the profile detail is hidden because there is no one profile to describe.</summary>
    [AvaloniaFact]
    public void TheStrokeTypeIsMixedWhenTheSelectionDisagrees()
    {
        var viewModel = new EditorViewModel();
        PathItem first = Line(viewModel, Stroke(4) with { WidthProfile = Taper() });
        PathItem second = Line(viewModel, Stroke(4));
        StrokePane pane = Pane(viewModel, 0, first, second);

        Assert.Equal(-1, Combo(pane, "StrokeTypeBox").SelectedIndex);
        Assert.Equal("mixed", Combo(pane, "StrokeTypeBox").PlaceholderText);
        Assert.False(Visible(pane, "ProfileSection"));
        Assert.Contains("profile", Text(pane, "MixedLabel").Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two paths carrying the *same* profile agree, and agreement is not reported as mixed.</summary>
    [AvaloniaFact]
    public void TheSameProfileOnEveryPathIsNotMixed()
    {
        var viewModel = new EditorViewModel();
        PathItem first = Line(viewModel, Stroke(4) with { WidthProfile = Taper() });
        PathItem second = Line(viewModel, Stroke(4) with { WidthProfile = Taper() });
        StrokePane pane = Pane(viewModel, 0, first, second);

        Assert.Equal(1, Combo(pane, "StrokeTypeBox").SelectedIndex);
        Assert.True(Visible(pane, "ProfileSection"));
    }

    // ---------------------------------------------------------------- tablet dynamics

    /// <summary>The dynamics rows describe the inspected stroke's recorded response: width dynamics on and following
    /// Soft, and every other target off.</summary>
    [AvaloniaFact]
    public void TheDynamicsRowsShowTheInspectedStrokesResponse()
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, Stroke(4) with
        {
            Dynamics = DynamicsSpec.PressureToWidth(DynamicsPreset.Soft),
        });
        StrokePane pane = Pane(viewModel, 0, path);

        Assert.True(Check(pane, "DynamicsWidthEnabled").IsChecked);
        Assert.Equal(1, Combo(pane, "DynamicsWidthCurve").SelectedIndex); // Soft
        Assert.False(Check(pane, "DynamicsOpacityEnabled").IsChecked);
        Assert.False(Check(pane, "DynamicsScatterEnabled").IsChecked);
        Assert.False(Check(pane, "DynamicsAngleEnabled").IsChecked);
        Assert.False(Check(pane, "DynamicsSmoothingEnabled").IsChecked);
    }

    /// <summary>
    /// **Turning a target on from the pane lands on the inspected stroke** and nowhere else in the stack - the
    /// granularity `style.setDynamics` does not have, reported as a named follow-up.
    /// </summary>
    [AvaloniaFact]
    public void EnablingATargetLandsOnTheInspectedStrokeOnly()
    {
        (StrokePane pane, _, PathItem first, PathItem second) = Host(1, Stroke(4), Stroke(8));

        Check(pane, "DynamicsWidthEnabled").IsChecked = true;
        Settle();

        Assert.True(first.Strokes[1].Dynamics!.For(DynamicsTarget.Width).Enabled);
        Assert.True(second.Strokes[1].Dynamics!.For(DynamicsTarget.Width).Enabled);
        Assert.False(first.Strokes[0].HasDynamics);
        Assert.Null(second.Strokes[0].Dynamics);
    }

    /// <summary>The curve a target follows is chosen here too, and the other targets keep what they had - setting
    /// width dynamics must not switch opacity dynamics off.</summary>
    [AvaloniaFact]
    public void ChoosingTheCurveLandsOnTheInspectedStrokeAndKeepsTheOtherTargets()
    {
        var viewModel = new EditorViewModel();
        var response = new DynamicsSpec(Enum.GetValues<DynamicsTarget>().Select(target => target switch
        {
            DynamicsTarget.Width => new DynamicsTargetSpec(true, DynamicsCurve.FromPreset(DynamicsPreset.Soft)),
            DynamicsTarget.Opacity => new DynamicsTargetSpec(true, DynamicsCurve.FromPreset(DynamicsPreset.Hard)),
            _ => DynamicsTargetSpec.Off,
        }));

        PathItem path = Line(viewModel, Stroke(4) with { Dynamics = response });
        StrokePane pane = Pane(viewModel, 0, path);

        Combo(pane, "DynamicsWidthCurve").SelectedIndex = 3; // Exponential
        Settle();

        DynamicsTargetSpec width = path.Strokes[0].Dynamics!.For(DynamicsTarget.Width);
        Assert.True(width.Enabled);
        Assert.Equal(DynamicsCurve.FromPreset(DynamicsPreset.Exponential), width.Curve);

        // The target that was not touched is exactly as it was.
        DynamicsTargetSpec opacity = path.Strokes[0].Dynamics!.For(DynamicsTarget.Opacity);
        Assert.True(opacity.Enabled);
        Assert.Equal(DynamicsCurve.FromPreset(DynamicsPreset.Hard), opacity.Curve);
    }

    /// <summary>Unchecking a target stores that decision without disturbing the others.</summary>
    [AvaloniaFact]
    public void DisablingATargetLandsOnTheInspectedStroke()
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, Stroke(4) with
        {
            Dynamics = DynamicsSpec.PressureToWidth(DynamicsPreset.Soft),
        });
        StrokePane pane = Pane(viewModel, 0, path);

        Check(pane, "DynamicsWidthEnabled").IsChecked = false;
        Settle();

        Assert.False(path.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);
    }

    /// <summary>A selection that disagrees on a target says **mixed** rather than showing one stroke's response: the
    /// box is indeterminate, the curve says mixed, and the readout names it.</summary>
    [AvaloniaFact]
    public void DynamicsIsMixedWhenTheSelectionDisagrees()
    {
        var viewModel = new EditorViewModel();
        PathItem first = Line(viewModel, Stroke(4) with
        {
            Dynamics = DynamicsSpec.PressureToWidth(DynamicsPreset.Soft),
        });
        PathItem second = Line(viewModel, Stroke(4));
        StrokePane pane = Pane(viewModel, 0, first, second);

        Assert.Null(Check(pane, "DynamicsWidthEnabled").IsChecked);
        Assert.Equal(-1, Combo(pane, "DynamicsWidthCurve").SelectedIndex);
        Assert.Equal("mixed", Combo(pane, "DynamicsWidthCurve").PlaceholderText);
        Assert.Contains("dynamics", Text(pane, "MixedLabel").Text, StringComparison.OrdinalIgnoreCase);

        // A target neither path responds to is not mixed: it agrees, and agrees on "off".
        Assert.False(Check(pane, "DynamicsOpacityEnabled").IsChecked);
    }

    /// <summary>The same response on every path agrees, and the row shows it.</summary>
    [AvaloniaFact]
    public void TheSameDynamicsOnEveryPathIsNotMixed()
    {
        var viewModel = new EditorViewModel();
        StrokeSpec stroked = Stroke(4) with { Dynamics = DynamicsSpec.PressureToWidth(DynamicsPreset.Hard) };
        PathItem first = Line(viewModel, stroked);
        PathItem second = Line(viewModel, stroked);
        StrokePane pane = Pane(viewModel, 0, first, second);

        Assert.True(Check(pane, "DynamicsWidthEnabled").IsChecked);
        Assert.Equal(2, Combo(pane, "DynamicsWidthCurve").SelectedIndex); // Hard
    }
}
