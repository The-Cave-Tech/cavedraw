using VCCad.Core.Model;

namespace VCCad.Core.Raster;

/// <summary>
/// The buffers a filter reads that no primitive produces: the shape's fill, its stroke, and what is behind it.
///
/// SVG names five inputs a filter may read without a step producing them - `SourceGraphic`, `SourceAlpha`,
/// `BackgroundImage`, `FillPaint` and `StrokePaint` - and only the first two are derivable from the shape's
/// rendering. The other three are facts the **renderer** holds: a shape's fill is not recoverable from the pixels of
/// its fill and stroke painted together, and what is behind an object is not in its own picture at all. So they are
/// passed in here rather than guessed at, and a graph that reads one the caller did not supply is **named** by the
/// engine (<see cref="FilterEngine.UnsuppliedSourceInputs"/>) instead of quietly reading as transparent.
///
/// `FillPaint` and `StrokePaint` are the shape drawn in **its own fill and stroke only**, so a graph that reads one
/// sees the colour and coverage the file asked for. `BackgroundImage` is the picture behind the object, already
/// composited, over the same region - which a canvas that renders the object alone cannot produce, and which a
/// caller must therefore either supply or report.
/// </summary>
public sealed class FilterSources
{
    /// <summary>
    /// Nothing supplied: the three optional inputs read as transparent black, which is what SVG itself directs when
    /// a viewer has no backdrop, and which the engine names in
    /// <see cref="FilterEngine.UnsuppliedSourceInputs"/> so the caller can report it.
    /// </summary>
    public static FilterSources None { get; } = new();

    /// <summary>The shape drawn in its fill only, over the region. Null when the caller has no such picture.</summary>
    public FilterBuffer? FillPaint { get; init; }

    /// <summary>The shape drawn in its stroke only, over the region. Null when the caller has no such picture.</summary>
    public FilterBuffer? StrokePaint { get; init; }

    /// <summary>What is behind the object, over the region. Null when the caller has no such picture.</summary>
    public FilterBuffer? BackgroundImage { get; init; }

    /// <summary>The buffer a source input name refers to, or null when this caller does not supply it.</summary>
    public FilterBuffer? For(string name) => name switch
    {
        "FillPaint" => FillPaint,
        "StrokePaint" => StrokePaint,
        "BackgroundImage" => BackgroundImage,
        _ => null,
    };
}
