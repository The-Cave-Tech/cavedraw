using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The width-profile editor on the canvas, through the real gesture path: <c>profile.editMode</c> opens
/// the mode, a pointer grabs a grip and drags it across the stroke, and the profile - the document's
/// asset, and the stroke that names it - changes once, as one undo step.
///
/// What the mode does is measured against the model and the geometry rather than against the drawn
/// handles: a drag that moved a disc and left the profile alone would be exactly the defect these
/// tests exist to catch, so every one of them reads the width points and the outline they produce.
/// </summary>
public class WidthProfileAnnotatorCanvasTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel, AutomationContext Context) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();

        // The canvas is the automation root, exactly as it is in the running application
        // (App.axaml.cs hands the window over as inputRoot). Without it profile.editMode has no
        // canvas to open a mode on, which is a refusal rather than a silent no-op.
        var context = new AutomationContext { ViewModel = viewModel, InputRoot = () => workspace };
        return (window, workspace, viewModel, context);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A horizontal line from (100,200) to (400,200), selected, with no profile yet.</summary>
    private static PathItem Line(EditorViewModel viewModel)
    {
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        var line = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = line.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(100, 200)));
        sub.Nodes.Add(new PathNode(new Point2D(400, 200)));
        line.Stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);
        layer.AddItem(line);
        viewModel.SelectObject(line);
        return line;
    }

    /// <summary>Creates the document's "Brush" profile with even widths, and applies it to the selection.</summary>
    private static void GiveProfile(AutomationContext context, params double[] widths)
    {
        EditorOperations.Invoke(context, "profile.create", Params(new
        {
            name = "Brush",
            points = widths.Select((w, i) => new
            {
                position = i / (double)Math.Max(1, widths.Length - 1),
                left = w,
                right = w,
            }).ToArray(),
        }));

        EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Brush" }));
    }

    private static void OpenMode(AutomationContext context)
        => EditorOperations.Invoke(context, "profile.editMode", Params(new { on = true }));    /// <summary>Drags from one world point to another through real pointer events.</summary>
    private static void Drag(Window window, CanvasWorkspace workspace, Point2D from, Point2D to)
    {
        Point a = workspace.ModelToWindow(from);
        Point b = workspace.ModelToWindow(to);

        InputInjection.Press(window, a.X, a.Y, shift: false);
        InputInjection.Move(window, (a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, leftDown: true);
        InputInjection.Move(window, b.X, b.Y, leftDown: true);
        InputInjection.Release(window, b.X, b.Y);
        Settle();
    }

    [AvaloniaFact]
    public void TheModeShowsOneHandlePerProfilePointOnTheStrokesOwnEdges()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            Line(viewModel);
            GiveProfile(context, 20, 30, 20);
            OpenMode(context);

            IReadOnlyList<WidthProfileHandle> handles = workspace.WidthProfileHandles();

            // One handle per width point, and each sits at the stroke's own width there: 10, 15 and 10
            // either side of a line at y=200.
            Assert.Equal(3, handles.Count);
            Assert.Equal(new[] { 0, 1, 2 }, handles.Select(h => h.Index).ToArray());
            Assert.Equal(190.0, handles[0].Left.Point.Y, 3);
            Assert.Equal(210.0, handles[0].Right.Point.Y, 3);
            Assert.Equal(185.0, handles[1].Left.Point.Y, 3);
            Assert.Equal(215.0, handles[1].Right.Point.Y, 3);
            Assert.True(workspace.IsEditingWidthProfile, "the mode must be on");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheModeShowsNoHandlesWhenTheStrokeHasNoProfile()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            Line(viewModel);
            OpenMode(context);

            Assert.True(workspace.IsEditingWidthProfile);
            Assert.Empty(workspace.WidthProfileHandles());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DraggingAGripSetsThatPointsWidthAndLeavesTheOthersAlone()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            PathItem line = Line(viewModel);
            GiveProfile(context, 20, 20);
            OpenMode(context);

            WidthProfileHandle handle = workspace.WidthProfileHandles()[0];

            // 30 points further out on the left of the line, so that point's width becomes 60.
            Drag(window, workspace, handle.Left.Point, new Point2D(handle.Centre.X, handle.Centre.Y - 30));

            WidthProfileSpec profile = viewModel.Document.FindProfile("Brush")!;
            Assert.InRange(profile.Points[0].LeftWidth, 59.5, 60.5);

            // The other side of the same point, and the other point, are untouched.
            Assert.Equal(20.0, profile.Points[0].RightWidth, 3);
            Assert.Equal(20.0, profile.Points[1].LeftWidth, 3);
            Assert.Equal(20.0, profile.Points[1].RightWidth, 3);

            // The asset edits the stroke that names it - that is what makes a profile an asset.
            Assert.True(line.Stroke.HasWidthProfile);
            Assert.Equal(profile.Points[0].LeftWidth, line.Stroke.WidthProfile!.Points[0].LeftWidth, 6);
            Assert.Equal("Brush", line.Stroke.WidthProfile.Name);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A grip dragged across the line stops at nothing rather than turning the stroke inside out: a
    /// negative width would put the offset edge on the other side of the path, which is a different
    /// stroke and not a thinner one.
    /// </summary>
    [AvaloniaFact]
    public void DraggingAGripPastTheCentrelineClampsAtZeroRatherThanInvertingTheStroke()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            PathItem line = Line(viewModel);

            // One width point, so the stroke is the same width everywhere and the clamp's effect on the
            // outline is unambiguous - with a second point at 20 the outline legitimately tapers back
            // above the line further along, which would say nothing about an inverted edge here.
            GiveProfile(context, 20);
            OpenMode(context);

            WidthProfileHandle handle = workspace.WidthProfileHandles()[0];
            Drag(window, workspace, handle.Left.Point, new Point2D(handle.Centre.X, handle.Centre.Y + 400));

            WidthPoint point = viewModel.Document.FindProfile("Brush")!.Points[0];
            Assert.Equal(0.0, point.LeftWidth, 9);
            Assert.Equal(20.0, point.RightWidth, 3);

            // Geometry, not just the number: with nothing on the left the outline lies on the right of
            // the line. Allowing a negative width here would draw it above the line instead.
            IReadOnlyList<IReadOnlyList<Point2D>> outlines = StrokeOutlineBuilder.Outline(line, line.Stroke);
            double highest = outlines.SelectMany(loop => loop).Min(p => p.Y);
            Assert.True(
                highest >= 199.999,
                $"a clamped grip must not mirror the stroke; its outline reached y={highest} above the line at y=200");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheWholeGestureIsOneUndoStep()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            PathItem line = Line(viewModel);
            GiveProfile(context, 20, 20);
            OpenMode(context);

            WidthProfileSpec before = viewModel.Document.FindProfile("Brush")!;
            WidthProfileHandle handle = workspace.WidthProfileHandles()[0];
            Drag(window, workspace, handle.Left.Point, new Point2D(handle.Centre.X, handle.Centre.Y - 30));
            Assert.NotEqual(before, viewModel.Document.FindProfile("Brush"));

            viewModel.Undo();
            Settle();

            // One step puts back both halves of the edit - the asset and the stroke that named it -
            // rather than leaving a stroke drawn from a profile the document no longer has.
            Assert.Equal(before, viewModel.Document.FindProfile("Brush"));
            Assert.Equal(before, line.Stroke.WidthProfile);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TurningTheModeOffRemovesTheHandles()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            Line(viewModel);
            GiveProfile(context, 20, 20);
            OpenMode(context);
            Assert.NotEmpty(workspace.WidthProfileHandles());

            EditorOperations.Invoke(context, "profile.editMode", Params(new { on = false }));

            Assert.False(workspace.IsEditingWidthProfile);
            Assert.Empty(workspace.WidthProfileHandles());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A mode that traps the pointer is worse than no mode, so Escape always leaves it.</summary>
    [AvaloniaFact]
    public void EscapeLeavesTheModeWithoutChangingTheDocument()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            Line(viewModel);
            GiveProfile(context, 20, 20);
            OpenMode(context);

            WidthProfileSpec before = viewModel.Document.FindProfile("Brush")!;

            InputInjection.Key(workspace, Key.Escape, KeyModifiers.None);
            Settle();

            Assert.False(workspace.IsEditingWidthProfile);
            Assert.Equal(before, viewModel.Document.FindProfile("Brush"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **The parity test.** What a person gets by dragging a grip and what a driver gets by calling the
    /// operation are the same edit - the canvas invokes <c>profile.setPoint</c> on release, so this pins
    /// that the two routes cannot drift apart without one of them failing here.
    /// </summary>
    [AvaloniaFact]
    public void TheOperationADriverCallsDoesTheSameAsTheDrag()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        (Window otherWindow, _, EditorViewModel otherViewModel, AutomationContext otherContext) = Host();
        try
        {
            PathItem line = Line(viewModel);
            GiveProfile(context, 20, 20);
            OpenMode(context);

            PathItem otherLine = Line(otherViewModel);
            GiveProfile(otherContext, 20, 20);

            WidthProfileHandle handle = workspace.WidthProfileHandles()[0];
            Drag(window, workspace, handle.Left.Point, new Point2D(handle.Centre.X, handle.Centre.Y - 30));

            double dragged = viewModel.Document.FindProfile("Brush")!.Points[0].LeftWidth;
            Assert.InRange(dragged, 59.5, 60.5);

            // The driver calls the operation with the width the drag landed on, and gets the same model.
            EditorOperations.Invoke(otherContext, "profile.setPoint",
                Params(new { name = "Brush", index = 0, left = dragged }));

            Assert.Equal(dragged, otherViewModel.Document.FindProfile("Brush")!.Points[0].LeftWidth, 9);
            Assert.Equal(
                viewModel.Document.FindProfile("Brush"),
                otherViewModel.Document.FindProfile("Brush"));
            Assert.Equal(line.Stroke.WidthProfile, otherLine.Stroke.WidthProfile);
        }
        finally
        {
            window.Close();
            otherWindow.Close();
        }
    }

    /// <summary>
    /// A driver that cannot see has to be told where the handles are, or "edit the profile on the canvas"
    /// is a capability only someone with eyes has. The operation reports each grip's world position and
    /// the width it holds, which is what a pointer needs to aim at.
    /// </summary>
    [AvaloniaFact]
    public void TheOperationReportsWhereTheHandlesAreSoADriverCanAim()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            Line(viewModel);
            GiveProfile(context, 20, 20);
            OpenMode(context);

            JsonElement report = JsonSerializer.SerializeToElement(
                EditorOperations.Invoke(context, "profile.editMode", Params(new { on = true })));

            Assert.True(report.GetProperty("editing").GetBoolean());
            Assert.Equal("Brush", report.GetProperty("profile").GetString());

            JsonElement[] handles = report.GetProperty("handles").EnumerateArray().ToArray();
            Assert.Equal(2, handles.Length);
            Assert.Equal(190.0, handles[0].GetProperty("left").GetProperty("y").GetDouble(), 3);
            Assert.Equal(210.0, handles[0].GetProperty("right").GetProperty("y").GetDouble(), 3);
            Assert.Equal(20.0, handles[0].GetProperty("left").GetProperty("width").GetDouble(), 3);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The person's way in, without a menu: W opens the mode on the selected stroke and W again closes
    /// it - the same state <c>profile.editMode</c> sets, so the two cannot mean different modes.
    /// </summary>
    [AvaloniaFact]
    public void TheWKeyOpensAndClosesTheModeOnTheSelection()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            Line(viewModel);
            GiveProfile(context, 20, 20);
            Assert.False(workspace.IsEditingWidthProfile);

            InputInjection.Key(workspace, Key.W, KeyModifiers.None);
            Settle();

            Assert.True(workspace.IsEditingWidthProfile);
            Assert.NotEmpty(workspace.WidthProfileHandles());

            InputInjection.Key(workspace, Key.W, KeyModifiers.None);
            Settle();

            Assert.False(workspace.IsEditingWidthProfile);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A tool key leaves the mode. It swallows clicks that miss a handle, so a mode that stayed on while
    /// the toolbar said "pen" would make the pen look broken - which is the trap the mode must not be.
    /// </summary>
    [AvaloniaFact]
    public void ChoosingAToolLeavesTheMode()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            Line(viewModel);
            GiveProfile(context, 20, 20);
            OpenMode(context);

            InputInjection.Key(workspace, Key.P, KeyModifiers.None);
            Settle();

            Assert.False(workspace.IsEditingWidthProfile);
            Assert.Equal(EditorTool.Pen, viewModel.Tool);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The handles reach the pixels, which is the half of "shows a handle" that no model assertion can
    /// see: the mode is turned off and the same pixel is read again, so a mode whose handles were never
    /// painted at all - every other test here would still pass - fails on the comparison.
    /// </summary>
    [AvaloniaFact]
    public void TheHandlesAreDrawnWhereTheAnnotatorsPutThem()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            Line(viewModel);
            GiveProfile(context, 20, 20);
            OpenMode(context);
            Settle();

            Point grip = workspace.ModelToWindow(workspace.WidthProfileHandles()[1].Right.Point);
            int painted = BrightnessAt(workspace, (int)Math.Round(grip.X), (int)Math.Round(grip.Y));

            EditorOperations.Invoke(context, "profile.editMode", Params(new { on = false }));
            Settle();
            int plain = BrightnessAt(workspace, (int)Math.Round(grip.X), (int)Math.Round(grip.Y));

            Assert.True(
                painted != plain,
                $"turning the mode off must take the handles off the canvas; the pixel at the grip was {painted} either way");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The brightness (0-255) of one rendered pixel of the control.</summary>
    private static int BrightnessAt(Avalonia.Visual visual, int x, int y)
    {
        var target = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 700), new Vector(96, 96));
        target.Render(visual);

        var pixel = new byte[4];
        System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }

        return (pixel[0] + pixel[1] + pixel[2]) / 3;
    }

    /// <summary>
    /// The point of the mode: while it is on, a click that misses a grip does not clear the selection,
    /// because losing the profile being edited to a stray click is worse than a click that does nothing.
    /// </summary>
    [AvaloniaFact]
    public void AClickThatMissesAGripDoesNotLoseTheSelection()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, AutomationContext context) = Host();
        try
        {
            PathItem line = Line(viewModel);
            GiveProfile(context, 20, 20);
            OpenMode(context);

            Point empty = workspace.ModelToWindow(new Point2D(300, 400));
            InputInjection.Press(window, empty.X, empty.Y, shift: false);
            InputInjection.Release(window, empty.X, empty.Y);
            Settle();

            Assert.True(workspace.IsEditingWidthProfile);
            Assert.Same(line, viewModel.PrimarySelection);
            Assert.NotEmpty(workspace.WidthProfileHandles());
        }
        finally
        {
            window.Close();
        }
    }
}
