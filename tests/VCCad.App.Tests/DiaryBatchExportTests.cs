using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Input;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Exporting a session from the diary as a replayable input batch.
///
/// The diary writes for a person to read, so a position lives inside the detail text. These pin
/// that it is read back out correctly, that the gesture the recorder saw becomes the batch, and -
/// the point of the whole thing - that replaying the exported batch does the same thing again
/// through the application's own input path.
/// </summary>
public class DiaryBatchExportTests
{
    private static InteractionRecord Record(long sequence, string name, string? details)
        => new(
            sequence,
            DateTimeOffset.UnixEpoch.AddMilliseconds(sequence * 100),
            "session",
            InteractionKind.Ui,
            InteractionCategory.Pointer,
            name,
            Target: "Canvas",
            Details: details,
            Success: true,
            DurationMs: 0,
            Tags: Array.Empty<string>());

    /// <summary>The text the recorder really writes, read back into events.</summary>
    [Fact]
    public void ItReadsTheGestureOutOfWhatTheRecorderWrote()
    {
        var records = new List<InteractionRecord>
        {
            Record(1, "pointer.press", "left, modifiers -, at (120,240)"),
            Record(2, "pointer.drag.start", "from (120,240)"),
            Record(3, "pointer.drag", "to (300,360), travelled 216.3"),
            Record(4, "pointer.drop", "drag from (120,240) (Canvas) to (300,360), distance 216.3"),
            Record(5, "pointer.wheel", "delta -1.5 at (400,500) with Ctrl (zoom)"),
            Record(6, "key.down", "Control+S"),
            Record(7, "key.down", "F12 ('F12')"),
        };

        InputBatch batch = DiaryBatchExport.ToBatch(records);
        batch.Validate();

        Assert.Equal(7, batch.Events.Count);

        Assert.Equal(InputKinds.Down, batch.Events[0].Kind);
        Assert.Equal(120.0, batch.Events[0].X, 6);
        Assert.Equal(240.0, batch.Events[0].Y, 6);
        Assert.Equal("left", batch.Events[0].Button);

        Assert.Equal(InputKinds.Move, batch.Events[1].Kind);
        Assert.Equal(InputKinds.Move, batch.Events[2].Kind);
        Assert.Equal(300.0, batch.Events[2].X, 6);

        // A drop names both ends of the drag: the up belongs at the END, not at the start.
        Assert.Equal(InputKinds.Up, batch.Events[3].Kind);
        Assert.Equal(300.0, batch.Events[3].X, 6);
        Assert.Equal(360.0, batch.Events[3].Y, 6);

        // The wheel text carries its own parentheses around "(zoom)", which is not a position.
        Assert.Equal(InputKinds.Wheel, batch.Events[4].Kind);
        Assert.Equal(400.0, batch.Events[4].X, 6);
        Assert.Equal(-1.5, batch.Events[4].WheelDelta!.Value, 6);
        Assert.Equal("Control", batch.Events[4].Modifiers);

        Assert.Equal("S", batch.Events[5].Key);
        Assert.Equal("Control", batch.Events[5].Modifiers);

        // A key with no modifier keeps its name and drops the recorder's parenthesised symbol.
        Assert.Equal("F12", batch.Events[6].Key);
        Assert.Null(batch.Events[6].Modifiers);
    }

    /// <summary>Deltas come from the diary's own timestamps, so a replay keeps the rhythm.</summary>
    [Fact]
    public void TheDeltasComeFromTheDiaryTimestamps()
    {
        InputBatch batch = DiaryBatchExport.ToBatch(new[]
        {
            Record(1, "pointer.press", "left, modifiers -, at (0,0)"),
            Record(3, "pointer.release", "at (10,0), moved 10"),
        });

        Assert.Equal(0, batch.Events[0].DeltaMs);
        Assert.Equal(200, batch.Events[1].DeltaMs, 6);
    }

    /// <summary>Operations, model calls and session marks are not gestures.</summary>
    [Fact]
    public void OnlyThePersonsOwnInputBecomesEvents()
    {
        var records = new List<InteractionRecord>
        {
            Record(1, "pointer.press", "left, modifiers -, at (0,0)"),
            new(2, DateTimeOffset.UnixEpoch, "s", InteractionKind.Api, InteractionCategory.Operation,
                "object.create", null, "{}", true, 0, Array.Empty<string>()),
            new(3, DateTimeOffset.UnixEpoch, "s", InteractionKind.Llm, InteractionCategory.Model,
                "llm.chat", null, "draw", true, 0, Array.Empty<string>()),
            Record(4, "pointer.hover", "at (5,5)"),
        };

        InputBatch batch = DiaryBatchExport.ToBatch(records);

        Assert.Single(batch.Events);
        Assert.Equal(InputKinds.Down, batch.Events[0].Kind);
    }

    /// <summary>
    /// The whole loop: a person's drag is recorded, exported, and replayed through the same input
    /// path - and the object moves again.
    /// </summary>
    [AvaloniaFact]
    public void ASessionRecordedInTheDiaryExportsAndReplays()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "vccad-diary-batch", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);

        var diary = new InteractionLog(directory);
        diary.StartSession("batch test");

        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();

        try
        {
            Layer layer = viewModel.Document.Artboards[0].Layers[0];
            PathItem rect = PathFactory.CreateRectangle("rect", new Rect2D(100, 100, 120, 120));
            layer.AddItem(rect);
            viewModel.SelectObject(rect);
            Settle();

            // Grabbed on the rectangle's own top edge: a click picks the path, not the area its
            // fill covers. Placed 30 in from the corner, clear of the selection handles at the
            // corners and edge midpoints, which would resize rather than move.
            Point from = workspace.ModelToWindow(new Point2D(130, 100));
            Point to = workspace.ModelToWindow(new Point2D(300, 260));

            // Recorded through the real recorder, so the exported batch is built from the text a
            // session actually writes rather than from text invented for the test.
            using (UiEventRecorder.Attach(window, diary))
            {
                InputInjection.Press(window, from.X, from.Y, shift: false);
                InputInjection.Move(window, from.X + 25, from.Y + 12, leftDown: true);
                Thread.Sleep(80);
                InputInjection.Move(window, (from.X + to.X) / 2, (from.Y + to.Y) / 2, leftDown: true);
                Thread.Sleep(80);
                InputInjection.Move(window, to.X, to.Y, leftDown: true);
                Thread.Sleep(80);
                InputInjection.Release(window, to.X, to.Y);
                Settle();
            }

            double movedTo = rect.WorldBounds().X;
            Assert.True(movedTo > 150, $"the drag must have moved the object (x={movedTo})");

            InputBatch batch = DiaryBatchExport.From(diary, diary.SessionId);
            batch.Validate();

            Assert.Contains(batch.Events, e => e.Kind == InputKinds.Down);
            Assert.Contains(batch.Events, e => e.Kind == InputKinds.Move);
            Assert.Contains(batch.Events, e => e.Kind == InputKinds.Up);

            // The batch begins where the finger went down. The diary rounds a recorded position to
            // whole window pixels, so that is the resolution a replayed gesture has.
            Assert.Equal(Math.Round(from.X), batch.Events[0].X, 0);
            Assert.Equal(Math.Round(from.Y), batch.Events[0].Y, 0);

            // The same batch is a file the registry can replay.
            string path = Path.Combine(directory, "session.json");
            batch.Save(path);
            InputBatch loaded = InputBatch.Load(path);
            Assert.Equal(batch.Events.Count, loaded.Events.Count);

            // Put the object back, then replay the recorded session the way the app does.
            viewModel.Undo();
            Assert.Equal(100, rect.WorldBounds().X, 3);

            loaded.Replay(InjectionInputSink.For(window), InputTiming.AsFastAsPossible);
            Settle();

            // The same gesture, to the diary's whole-pixel resolution: the object arrives where it
            // did the first time, not necessarily to the last decimal of a sub-pixel.
            Assert.Equal(movedTo, rect.WorldBounds().X, 1);
        }
        finally
        {
            window.Close();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A test artefact left behind is not worth failing a run over.
            }
        }
    }

    /// <summary>The registry half: the operation writes a file the replay path can read.</summary>
    [AvaloniaFact]
    public void TheExportOperationWritesAReplayableFile()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "vccad-diary-export", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);

        var diary = new InteractionLog(directory);
        diary.StartSession("export test");
        diary.Record(InteractionKind.Ui, InteractionCategory.Pointer, "pointer.press",
            target: "Canvas", details: "left, modifiers -, at (10,20)");
        diary.Record(InteractionKind.Ui, InteractionCategory.Pointer, "pointer.release",
            target: "Canvas", details: "at (10,20), moved 0");

        try
        {
            var context = new AutomationContext
            {
                ViewModel = new EditorViewModel(),
                History = diary,
            };

            string path = Path.Combine(directory, "exported.json");
            EditorOperations.Invoke(context, "history.exportBatch",
                JsonSerializer.SerializeToElement(new { path }));

            Assert.True(File.Exists(path));
            InputBatch loaded = InputBatch.Load(path);
            Assert.Equal(2, loaded.Events.Count);
            Assert.Equal(InputKinds.Down, loaded.Events[0].Kind);
            Assert.Equal(10.0, loaded.Events[0].X, 6);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // As above.
            }
        }
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
