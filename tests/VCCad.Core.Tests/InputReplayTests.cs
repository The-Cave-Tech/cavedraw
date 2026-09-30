using VCCad.Core.Input;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Batched input replay: a document plus a list of events with time deltas in, a result out.
///
/// There is no window anywhere in this file. Events go to an <see cref="IInputSink"/> — the
/// place the running application puts its real input path — and the clock is a fake that records
/// what it was asked to wait, so "the timing was honoured" is an assertion and not a stopwatch
/// reading.
/// </summary>
public class InputReplayTests
{
    private const double W = 612;
    private const double H = 792;

    /// <summary>A clock that advances only when asked to wait, and remembers the asks.</summary>
    private sealed class RecordingClock : IInputClock
    {
        private TimeSpan _now;

        public List<TimeSpan> Waits { get; } = new();

        public TimeSpan Elapsed => _now;

        public void Wait(TimeSpan delay)
        {
            Waits.Add(delay);
            _now += delay;
        }
    }

    private sealed class CollectingSink : IInputSink
    {
        public List<InputEvent> Received { get; } = new();

        public void Send(InputEvent input) => Received.Add(input);
    }

    private static PathItem Box(string name, double x, double y, double size = 50)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        return path;
    }

    private static CadDocument Page(params LayerItem[] items)
    {
        var document = new CadDocument();
        Artboard page = document.AddArtboard(new Size2D(W, H), "Page 1", new Point2D(0, 0));
        Layer layer = page.AddLayer("Artwork");
        foreach (LayerItem item in items)
        {
            layer.AddItem(item);
        }

        return document;
    }

    private static InputBatch Batch(params InputEvent[] events) => new() { Events = events };

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"vccad-input-{Guid.NewGuid():N}.json");

    [Fact]
    public void AReplayDeliversEveryEventInOrder()
    {
        var sink = new CollectingSink();

        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 10, 10, 16, Button: "left"),
            new InputEvent(InputKinds.Move, 40, 20, 8),
            new InputEvent(InputKinds.Up, 40, 20, 8));

        InputReplayResult result = batch.Replay(sink, InputTiming.AsFastAsPossible, new RecordingClock());

        Assert.Equal(
            new[] { InputKinds.Down, InputKinds.Move, InputKinds.Up },
            sink.Received.Select(e => e.Kind));
        Assert.Equal(3, result.Delivered);
    }

    [Fact]
    public void RealTimeWaitsExactlyTheDeltasInOrder()
    {
        var clock = new RecordingClock();

        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 10, 10, 100),
            new InputEvent(InputKinds.Move, 40, 20, 50),
            new InputEvent(InputKinds.Up, 40, 20, 25));

        InputReplayResult result = batch.Replay(new CollectingSink(), InputTiming.RealTime, clock);

        Assert.Equal(new[] { 100d, 50d, 25d }, clock.Waits.Select(w => w.TotalMilliseconds));
        Assert.Equal(175, result.Duration.TotalMilliseconds);
    }

    [Fact]
    public void AsFastAsPossibleNeverWaitsButKeepsTheDuration()
    {
        var clock = new RecordingClock();

        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 10, 10, 100),
            new InputEvent(InputKinds.Move, 40, 20, 50),
            new InputEvent(InputKinds.Up, 40, 20, 25));

        var sink = new CollectingSink();
        InputReplayResult result = batch.Replay(sink, InputTiming.AsFastAsPossible, clock);

        Assert.Empty(clock.Waits);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        Assert.Equal(3, sink.Received.Count);
        Assert.Equal(175, result.Duration.TotalMilliseconds);
    }

    [Fact]
    public void APlayedBatchCanBeRecordedBackWithTheSameDeltas()
    {
        var clock = new RecordingClock();
        var recorder = new InputRecorder(clock);

        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 10, 10, 100, Button: "left"),
            new InputEvent(InputKinds.Move, 40, 20, 50),
            new InputEvent(InputKinds.Up, 40, 20, 25));

        batch.Replay(recorder, InputTiming.RealTime, clock);
        InputBatch recorded = recorder.Finish("recorded");

        Assert.Equal(batch.Events, recorded.Events);
        Assert.Equal(new[] { 100d, 50d, 25d }, recorded.Events.Select(e => e.DeltaMs));
    }

    [Fact]
    public void EveryEventKindSurvivesTheFormatAndReachesTheSink()
    {
        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 10, 10, 0, Button: "left", Device: "mouse"),
            new InputEvent(InputKinds.Move, 20, 20, 5, Button: "left", Device: "mouse"),
            new InputEvent(InputKinds.Up, 20, 20, 5, Button: "left", Device: "mouse"),
            new InputEvent(InputKinds.Wheel, 30, 30, 5, WheelDelta: 1.5),
            new InputEvent(InputKinds.Hover, 30, 30, 5),
            new InputEvent(InputKinds.HoverOut, 30, 30, 5),
            new InputEvent(InputKinds.Enter, 30, 30, 5),
            new InputEvent(InputKinds.Leave, 30, 30, 5),
            new InputEvent(InputKinds.KeyDown, DeltaMs: 5, Key: "A", Modifiers: "Shift"),
            new InputEvent(InputKinds.KeyUp, DeltaMs: 5, Key: "A", Modifiers: "Shift"),
            new InputEvent(InputKinds.Text, DeltaMs: 5, Text: "a"),
            new InputEvent(InputKinds.PenDown, 40, 40, 5, Device: "pen", Pressure: 0.5,
                TiltX: 10, TiltY: -4, PointerId: 3),
            new InputEvent(InputKinds.PenMove, 45, 45, 5, Device: "pen", Pressure: 0.6,
                TiltX: 11, TiltY: -4, PointerId: 3),
            new InputEvent(InputKinds.PenUp, 45, 45, 5, Device: "pen", Pressure: 0.0,
                TiltX: 11, TiltY: -4, PointerId: 3),
            new InputEvent(InputKinds.TouchDown, 50, 50, 5, Device: "touch", PointerId: 7),
            new InputEvent(InputKinds.TouchMove, 55, 55, 5, Device: "touch", PointerId: 7),
            new InputEvent(InputKinds.TouchUp, 55, 55, 5, Device: "touch", PointerId: 7));

        var sink = new CollectingSink();
        batch.Replay(sink, InputTiming.AsFastAsPossible, new RecordingClock());

        Assert.Equal(batch.Events.Select(e => e.Kind), sink.Received.Select(e => e.Kind));
        Assert.Equal(17, sink.Received.Count);
    }

    [Fact]
    public void ModifierStateReachesTheSink()
    {
        InputBatch batch = Batch(
            new InputEvent(InputKinds.KeyDown, DeltaMs: 1, Key: "A", Modifiers: "Shift, Control"),
            new InputEvent(InputKinds.Down, 10, 10, 1, Extend: true, Modifier: true));

        var sink = new CollectingSink();
        batch.Replay(sink, InputTiming.AsFastAsPossible, new RecordingClock());

        Assert.Equal(InputModifiers.Shift | InputModifiers.Control, sink.Received[0].ModifierState());
        Assert.True(sink.Received[1].Extend);
        Assert.True(sink.Received[1].Modifier);
    }

    [Fact]
    public void ButtonStateReachesTheSink()
    {
        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 10, 10, 0, Button: "right"),
            new InputEvent(InputKinds.Up, 10, 10, 0, Button: "right"));

        var sink = new CollectingSink();
        batch.Replay(sink, InputTiming.AsFastAsPossible, new RecordingClock());

        Assert.Equal(InputButton.Right, sink.Received[0].ButtonState());
        Assert.Equal(InputButton.Right, sink.Received[1].ButtonState());
    }

    [Fact]
    public void PenPressureAndTiltReachTheSink()
    {
        InputBatch batch = Batch(
            new InputEvent(InputKinds.PenMove, 12, 34, 2, Device: "pen",
                Pressure: 0.73, TiltX: 12.5, TiltY: -3.25, PointerId: 4));

        var sink = new CollectingSink();
        batch.Replay(sink, InputTiming.AsFastAsPossible, new RecordingClock());

        InputEvent pen = Assert.Single(sink.Received);
        Assert.Equal(0.73, pen.Pressure);
        Assert.Equal(12.5, pen.TiltX);
        Assert.Equal(-3.25, pen.TiltY);
        Assert.Equal(InputPointerDevice.Pen, pen.DeviceKind());
    }

    [Fact]
    public void TouchPointerIdsStayDistinguishable()
    {
        InputBatch batch = Batch(
            new InputEvent(InputKinds.TouchDown, 10, 10, 0, Device: "touch", PointerId: 1),
            new InputEvent(InputKinds.TouchDown, 20, 20, 0, Device: "touch", PointerId: 2),
            new InputEvent(InputKinds.TouchMove, 20, 30, 0, Device: "touch", PointerId: 2));

        var sink = new CollectingSink();
        batch.Replay(sink, InputTiming.AsFastAsPossible, new RecordingClock());

        Assert.Equal(new int?[] { 1, 2, 2 }, sink.Received.Select(e => e.PointerId));
        Assert.All(sink.Received, e => Assert.Equal(InputPointerDevice.Touch, e.DeviceKind()));
    }

    [Fact]
    public void AMalformedBatchNamesTheOffendingIndex()
    {
        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 10, 10, 0),
            new InputEvent(InputKinds.Move, 20, 20, 5),
            new InputEvent(InputKinds.Move, 30, 30, -5));

        InputBatchException error = Assert.Throws<InputBatchException>(batch.Validate);

        Assert.Equal(2, error.Index);
        Assert.Contains("event index 2", error.Message);
        Assert.Contains("delta", error.Message);
    }

    [Fact]
    public void ARejectedBatchDeliversNothingAtAll()
    {
        var sink = new CollectingSink();
        var clock = new RecordingClock();

        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 10, 10, 10),
            new InputEvent(InputKinds.Move, 20, 20, 10),
            new InputEvent("wiggle", 30, 30, 10));

        Assert.Throws<InputBatchException>(
            () => batch.Replay(sink, InputTiming.RealTime, clock));

        Assert.Empty(sink.Received);
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public void AnUnknownKindNamesTheOffendingIndex()
    {
        InputBatch batch = Batch(new InputEvent("wiggle", 1, 2, 0));

        InputBatchException error = Assert.Throws<InputBatchException>(batch.Validate);

        Assert.Equal(0, error.Index);
        Assert.Contains("wiggle", error.Message);
        Assert.Contains("index 0", error.Message);
    }

    [Fact]
    public void AKeyEventWithNoKeyIsMalformedAtItsOwnIndex()
    {
        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 1, 2, 0),
            new InputEvent(InputKinds.KeyDown, DeltaMs: 1));

        InputBatchException error = Assert.Throws<InputBatchException>(batch.Validate);

        Assert.Equal(1, error.Index);
        Assert.Contains("needs a key", error.Message);
    }

    [Fact]
    public void ABatchRoundTripsThroughTheSharedFileAndReplaysIdentically()
    {
        string path = TempFile();

        try
        {
            InputBatch batch = new()
            {
                Name = "a stroke",
                Document = Page(Box("target", 100, 100)),
                FocusedArtboard = "Page 1",
                Expected = new[] { "target" },
                Events = new[]
                {
                    new InputEvent(InputKinds.Down, 100, 100, 0, Button: "left"),
                    new InputEvent(InputKinds.PenMove, 150, 150, 16, Device: "pen", Pressure: 0.8),
                    new InputEvent(InputKinds.Up, 150, 150, 16),
                },
            };

            batch.Save(path);
            InputBatch loaded = InputBatch.Load(path);

            Assert.Equal(batch.Events, loaded.Events);
            Assert.Equal("a stroke", loaded.Name);
            Assert.Equal("Page 1", loaded.FocusedArtboard);
            Assert.Equal(new[] { "target" }, loaded.Expected);
            Assert.Single(loaded.Document!.Artboards);

            var original = new CollectingSink();
            var replayed = new CollectingSink();
            batch.Replay(original, InputTiming.AsFastAsPossible, new RecordingClock());
            loaded.Replay(replayed, InputTiming.AsFastAsPossible, new RecordingClock());

            Assert.Equal(original.Received, replayed.Received);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ASelectionFixtureFileLoadsAsAnInputBatchAndReplaysTheSameSelection()
    {
        string path = TempFile();

        try
        {
            var fixture = new SelectionFixture
            {
                Document = Page(Box("target", 100, 100), Box("other", 400, 400)),
                FocusedArtboard = "Page 1",
                Events = new[]
                {
                    new RecordedEvent("down", 120, 120),
                    new RecordedEvent("up", 120, 120),
                },
            };

            fixture.Save(path);

            InputBatch batch = InputBatch.Load(path);

            Assert.Equal(fixture.Selected(), batch.Play().Items.Select(i => i.Name));
            Assert.Equal(fixture.Selected(), batch.Play(batch.Document!).Items.Select(i => i.Name));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ABatchSavedInTheSharedFormatLoadsAsASelectionFixture()
    {
        string path = TempFile();

        try
        {
            InputBatch batch = new()
            {
                Document = Page(Box("target", 100, 100), Box("other", 400, 400)),
                FocusedArtboard = "Page 1",
                Events = new[]
                {
                    new InputEvent(InputKinds.Down, 102, 125, 0),
                    new InputEvent(InputKinds.Up, 102, 125, 16),
                },
            };

            batch.Save(path);

            SelectionFixture fixture = SelectionFixture.Load(path);

            Assert.Equal(new[] { "target" }, fixture.Selected());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void AReplayCanReachTheRealSinkAndTheRecorderAtOnce()
    {
        // This is how a batch that is played is also a batch that is recorded: the event goes
        // to the window and to the recorder in the same delivery.
        var clock = new RecordingClock();
        var recorder = new InputRecorder(clock);
        var sink = new CollectingSink();

        InputBatch batch = Batch(
            new InputEvent(InputKinds.Down, 10, 10, 100, Button: "left"),
            new InputEvent(InputKinds.Move, 40, 20, 50),
            new InputEvent(InputKinds.Up, 40, 20, 25));

        batch.Replay(new TeeInputSink(sink, recorder), InputTiming.RealTime, clock);

        Assert.Equal(batch.Events, sink.Received);
        Assert.Equal(batch.Events, recorder.Finish().Events);
    }

    [Fact]
    public void AHeadlessBatchPicksTheSameObjectAClickWould()
    {
        CadDocument document = Page(Box("target", 100, 100));
        InputBatch batch = new()
        {
            Document = document,
            Events = new[]
            {
                new InputEvent(InputKinds.Down, 102, 125, 0),
                new InputEvent(InputKinds.Up, 102, 125, 20),
            },
        };

        SelectionResult result = batch.Play();

        Assert.Equal("target", Assert.Single(result.Items).Name);
        Assert.Same(document.Artboards[0], result.Focused);
    }

    [Fact]
    public void APenStrokeDrivesSelectionJustLikeAMouseDrag()
    {
        CadDocument document = Page(Box("inside", 100, 100), Box("outside", 400, 400));
        InputBatch batch = new()
        {
            Document = document,
            Events = new[]
            {
                new InputEvent(InputKinds.PenDown, 50, 50, 0, Device: "pen", Pressure: 0.5),
                new InputEvent(InputKinds.PenMove, 200, 200, 10, Device: "pen", Pressure: 0.6),
                new InputEvent(InputKinds.PenUp, 200, 200, 10, Device: "pen", Pressure: 0.2),
            },
        };

        SelectionResult result = batch.Play();

        Assert.Equal(new[] { "inside" }, result.Items.Select(i => i.Name));
    }
}
