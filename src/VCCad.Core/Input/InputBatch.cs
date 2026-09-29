using System.Text.Json;
using System.Text.Json.Serialization;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Core.Serialization;

namespace VCCad.Core.Input;

/// <summary>What a replay did, so a caller can report the timing it honoured.</summary>
/// <param name="Delivered">How many events reached the sink.</param>
/// <param name="Duration">The batch's own duration, whether or not it was waited out.</param>
/// <param name="Timing">Whether the replay waited.</param>
public sealed record InputReplayResult(int Delivered, TimeSpan Duration, InputTiming Timing);

/// <summary>
/// A gesture: the document it happened on and the events that made it, in order, each with a
/// delta in milliseconds from the one before.
///
/// This is the selection fixture's file grown to cover every kind of input, not a second format.
/// The top-level shape is the same (<c>Document</c>, <c>Events</c>, <c>FocusedArtboard</c>,
/// <c>Expected</c>) and the first five members of each event are the same, so a fixture saved by
/// one reader loads in the other. What is new is that an event may also carry a delta, a button,
/// modifiers, a device, pressure and tilt, a key, text and a pointer id — and that the batch can
/// be replayed against a clock with no window anywhere.
/// </summary>
public sealed class InputBatch
{
    /// <summary>Format version for the shared gesture file.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The file format version this batch was written with.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>A human name for the gesture, when it has one.</summary>
    public string? Name { get; init; }

    /// <summary>The events, in the order they happened.</summary>
    public required IReadOnlyList<InputEvent> Events { get; init; }

    /// <summary>The document the gesture happened on, when it is known.</summary>
    public CadDocument? Document { get; init; }

    /// <summary>What the artboard focus was when the gesture began.</summary>
    public string? FocusedArtboard { get; init; }

    /// <summary>What the gesture should select, for a batch whose answer is known.</summary>
    public IReadOnlyList<string> Expected { get; init; } = Array.Empty<string>();

    /// <summary>Every fault in the batch, in event order. Empty means it is playable.</summary>
    public IReadOnlyList<InputBatchError> Errors()
    {
        var errors = new List<InputBatchError>();

        for (int i = 0; i < Events.Count; i++)
        {
            Validate(Events[i], i, errors);
        }

        return errors;
    }

    /// <summary>
    /// Throws when the batch is malformed, naming the offending index.
    ///
    /// This runs before the first event is delivered, so a batch that is wrong at event 40 has
    /// changed nothing by the time the caller hears about it.
    /// </summary>
    public void Validate()
    {
        IReadOnlyList<InputBatchError> errors = Errors();

        if (errors.Count > 0)
        {
            throw new InputBatchException(errors);
        }
    }

    /// <summary>
    /// Replays the batch through <paramref name="sink"/>, honouring the deltas against a clock.
    ///
    /// The sink is the running application's own input path — the one that was recording when a
    /// person did it — so a batch and a hand are the same input, not two implementations of it.
    /// <see cref="InputTiming.AsFastAsPossible"/> keeps the events and their deltas but does not
    /// wait, which is how one format serves both a realistic recording and a fast test run.
    /// </summary>
    public InputReplayResult Replay(
        IInputSink sink, InputTiming timing = InputTiming.RealTime, IInputClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        // Whole-batch first: nothing is delivered from a batch that is going to be rejected.
        Validate();

        IInputClock replayClock = clock ?? new SystemInputClock();
        TimeSpan target = TimeSpan.Zero;

        for (int i = 0; i < Events.Count; i++)
        {
            InputEvent input = Events[i];
            target += TimeSpan.FromMilliseconds(input.DeltaMs);

            if (timing == InputTiming.RealTime)
            {
                TimeSpan remaining = target - replayClock.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    replayClock.Wait(remaining);
                }
            }

            sink.Send(input);
        }

        return new InputReplayResult(Events.Count, target, timing);
    }

    /// <summary>
    /// Replays the batch headlessly and says what it selected.
    ///
    /// A document plus a list of events in, a result out: no window, no control, no timer. This
    /// is the selection fixture's <c>Play</c> carried over to the wider event list, so a gesture
    /// recorded as an input batch can be turned into a regression test.
    /// </summary>
    public SelectionResult Play(Artboard? focused = null)
    {
        CadDocument document = Document
            ?? throw new InvalidOperationException(
                "This batch carries no document; use Play(document).");

        return Play(document, focused);
    }

    /// <summary>Replays the batch's pointer events against an explicit document.</summary>
    public SelectionResult Play(CadDocument document, Artboard? focused = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        Artboard? board = focused;

        if (board is null && FocusedArtboard is not null)
        {
            board = document.Artboards.FirstOrDefault(a => a.Name == FocusedArtboard);
        }

        IEnumerable<SelectEvent> events = Events
            .Select(e => e.ToSelectEvent())
            .Where(e => e is not null)
            .Select(e => e!);

        return SelectionEngine.Play(document, events, board);
    }

    /// <summary>
    /// Writes the batch to the shared gesture file.
    ///
    /// The payload keeps the selection fixture's property names, so the file can be read back by
    /// either reader: one file format for a gesture, whatever produced it.
    /// </summary>
    public void Save(string path)
    {
        var payload = new
        {
            Version,
            Name,
            Document = Document is null
                ? (JsonElement?)null
                : JsonSerializer.Deserialize<JsonElement>(
                    System.Text.Encoding.UTF8.GetString(
                        VccadDocumentSerializer.SerializeToBytes(Document))),
            FocusedArtboard,
            Events,
            Expected,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
    }

    /// <summary>Reads a batch back, including one the selection fixture wrote.</summary>
    public static InputBatch Load(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = json.RootElement;

        CadDocument? document = root.TryGetProperty("Document", out JsonElement doc) &&
                                doc.ValueKind == JsonValueKind.Object
            ? VccadDocumentSerializer.Deserialize(
                System.Text.Encoding.UTF8.GetBytes(doc.GetRawText()))
            : null;

        IReadOnlyList<InputEvent> events = root.TryGetProperty("Events", out JsonElement listed)
            ? listed.Deserialize<List<InputEvent>>() ?? new List<InputEvent>()
            : new List<InputEvent>();

        return new InputBatch
        {
            Version = root.TryGetProperty("Version", out JsonElement version) &&
                      version.TryGetInt32(out int parsed)
                ? parsed
                : CurrentVersion,
            Name = root.TryGetProperty("Name", out JsonElement name) &&
                   name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null,
            Document = document,
            FocusedArtboard = root.TryGetProperty("FocusedArtboard", out JsonElement focus) &&
                              focus.ValueKind == JsonValueKind.String
                ? focus.GetString()
                : null,
            Events = events,
            Expected = root.TryGetProperty("Expected", out JsonElement expected)
                ? expected.Deserialize<List<string>>() ?? new List<string>()
                : new List<string>(),
        };
    }

    /// <summary>Widens a selection fixture into a batch, so an existing gesture replays here.</summary>
    public static InputBatch FromSelectionFixture(SelectionFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return new InputBatch
        {
            Name = "selection fixture",
            Document = fixture.Document,
            FocusedArtboard = fixture.FocusedArtboard,
            Events = fixture.Events.Select(InputEvent.FromRecorded).ToList(),
            Expected = fixture.Expected,
        };
    }

    /// <summary>The events as the selection fixture's own record, for the pointer kinds that fit.</summary>
    public IReadOnlyList<RecordedEvent> ToRecordedEvents() =>
        Events.Select(e => new RecordedEvent(e.Kind, e.X, e.Y, e.Extend, e.Modifier)).ToList();

    private static void Validate(InputEvent input, int index, List<InputBatchError> errors)
    {
        void Bad(string message) => errors.Add(new InputBatchError(index, message));

        if (string.IsNullOrWhiteSpace(input.Kind))
        {
            Bad("the kind is missing");
            return;
        }

        if (!InputKinds.All.Contains(input.Kind))
        {
            Bad($"unknown kind '{input.Kind}'");
            return;
        }

        if (!double.IsFinite(input.DeltaMs) || input.DeltaMs < 0)
        {
            Bad($"delta {input.DeltaMs} ms is not a non-negative amount of time");
        }

        if (InputKinds.HasPosition(input.Kind) &&
            (!double.IsFinite(input.X) || !double.IsFinite(input.Y)))
        {
            Bad($"position ({input.X},{input.Y}) is not finite");
        }

        if (input.Kind == InputKinds.Wheel && input.WheelDelta is { } wheel &&
            !double.IsFinite(wheel))
        {
            Bad($"wheel delta {wheel} is not finite");
        }

        if (InputKinds.IsPen(input.Kind))
        {
            if (input.Pressure is { } pressure &&
                (!double.IsFinite(pressure) || pressure < 0 || pressure > 1))
            {
                Bad($"pressure {pressure} is outside 0..1");
            }

            if (input.TiltX is { } tiltX && !double.IsFinite(tiltX))
            {
                Bad($"tilt x {tiltX} is not finite");
            }

            if (input.TiltY is { } tiltY && !double.IsFinite(tiltY))
            {
                Bad($"tilt y {tiltY} is not finite");
            }
        }

        if ((InputKinds.IsPen(input.Kind) || InputKinds.IsTouch(input.Kind)) &&
            input.PointerId is { } pointerId && pointerId < 0)
        {
            Bad($"pointer id {pointerId} is negative");
        }

        if (InputKinds.IsKey(input.Kind) && string.IsNullOrWhiteSpace(input.Key))
        {
            Bad("a keyboard event needs a key");
        }

        if (input.Kind == InputKinds.Text && string.IsNullOrEmpty(input.Text))
        {
            Bad("a text event needs text");
        }

        if (input.Button is not null && !InputEventNames.TryParseButton(input.Button, out _))
        {
            Bad($"unknown button '{input.Button}'");
        }

        if (input.Modifiers is not null && !InputEventNames.TryParseModifiers(input.Modifiers, out _))
        {
            Bad($"unknown modifiers '{input.Modifiers}'");
        }

        if (input.Device is not null && !InputEventNames.TryParseDevice(input.Device, out _))
        {
            Bad($"unknown device '{input.Device}'");
        }
    }
}
