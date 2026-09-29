using System.Text.Json;
using System.Text.Json.Serialization;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;

namespace VCCad.Core.Selection;

/// <summary>One recorded pointer event, as it travels in a fixture.</summary>
/// <param name="Kind">down, move or up.</param>
/// <param name="X">Document x.</param>
/// <param name="Y">Document y.</param>
/// <param name="Extend">Whether the additive modifier was held.</param>
/// <param name="Modifier">Whether the platform transform modifier was held.</param>
public sealed record RecordedEvent(
    string Kind,
    double X,
    double Y,
    bool Extend = false,
    bool Modifier = false)
{
    /// <summary>This event as the engine's own kind.</summary>
    public SelectEvent ToEvent() => Kind switch
    {
        "down" => new PointerDown(new Point2D(X, Y), Extend),
        "move" => new PointerMove(new Point2D(X, Y)),
        _ => new PointerUp(new Point2D(X, Y), Extend),
    };
}

/// <summary>
/// A gesture and the document it happened on, kept together.
///
/// The selection rules are worked out from a document and a list of events and nothing else, so
/// a run that found something interesting can be written out and replayed later with no window
/// open. That is what makes the regression suite fast enough to keep running, and what lets a
/// gesture recorded by driving the application be turned into a test rather than described in
/// a bug report.
/// </summary>
public sealed class SelectionFixture
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The document the gesture happened on.</summary>
    public required CadDocument Document { get; init; }

    /// <summary>What the pointer did, in order.</summary>
    public required IReadOnlyList<RecordedEvent> Events { get; init; }

    /// <summary>What the artboard focus was when the gesture began.</summary>
    public string? FocusedArtboard { get; init; }

    /// <summary>What the gesture selected, for a fixture whose answer is known.</summary>
    public IReadOnlyList<string> Expected { get; init; } = Array.Empty<string>();

    /// <summary>Replays the gesture.</summary>
    public SelectionResult Play()
    {
        Artboard? focused = FocusedArtboard is null
            ? null
            : Document.Artboards.FirstOrDefault(a => a.Name == FocusedArtboard);

        return SelectionEngine.Play(Document, Events.Select(e => e.ToEvent()), focused);
    }

    /// <summary>The names the replay selects, in order.</summary>
    public IReadOnlyList<string> Selected() => Play().Items.Select(i => i.Name).ToList();

    /// <summary>The names of the artboards the replay selects.</summary>
    public IReadOnlyList<string> SelectedArtboards() =>
        Play().Artboards.Select(a => a.Name).ToList();

    /// <summary>Writes the fixture to a file.</summary>
    public void Save(string path)
    {
        var payload = new
        {
            Document = JsonSerializer.Deserialize<JsonElement>(
                System.Text.Encoding.UTF8.GetString(
                    VccadDocumentSerializer.SerializeToBytes(Document))),
            Events,
            FocusedArtboard,
            Expected,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
    }

    /// <summary>Reads a fixture back.</summary>
    public static SelectionFixture Load(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = json.RootElement;

        CadDocument document = VccadDocumentSerializer.Deserialize(
            System.Text.Encoding.UTF8.GetBytes(root.GetProperty("Document").GetRawText()));

        return new SelectionFixture
        {
            Document = document,
            Events = root.TryGetProperty("Events", out JsonElement events)
                ? events.Deserialize<List<RecordedEvent>>() ?? new List<RecordedEvent>()
                : new List<RecordedEvent>(),
            FocusedArtboard = root.TryGetProperty("FocusedArtboard", out JsonElement focus) &&
                              focus.ValueKind == JsonValueKind.String
                ? focus.GetString()
                : null,
            Expected = root.TryGetProperty("Expected", out JsonElement expected)
                ? expected.Deserialize<List<string>>() ?? new List<string>()
                : new List<string>(),
        };
    }
}
