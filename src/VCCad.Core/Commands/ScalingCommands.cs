using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// What travels with an object when it is scaled.
///
/// These are the four entries of a vector editor's "scale with object" panel, and they are one group because
/// they answer one question: when a shape gets bigger, which of the measurements attached to it get bigger
/// too? A stroke width, a corner radius and a font size are all distances or sizes in the document, and
/// leaving them behind is what makes a scaled drawing disagree with its own geometry.
///
/// Defaults are all on, which is what the editors this imitates do: a person who scales a shape and finds a
/// hairline outline where there was a drawn one has been surprised by the wrong default.
/// </summary>
public sealed class ScaleWithObject
{
    /// <summary>Stroke widths scale with the object.</summary>
    public bool LineWeights { get; set; } = true;

    /// <summary>
    /// Corner radii scale with the object.
    ///
    /// This one is satisfied by the geometry itself in this application: a rounded corner is baked into the
    /// outline when it is rounded, so scaling the outline scales the corner with it. The flag exists so the
    /// panel matches the others, and a test pins the behaviour it describes rather than leaving the claim
    /// unbacked.
    /// </summary>
    public bool ShapeCorners { get; set; } = true;

    /// <summary>Layer effect radii scale with the object. No effect carries one yet, so this does nothing.</summary>
    public bool LayerEffectRadii { get; set; }

    /// <summary>Type inside a text frame scales with the frame.</summary>
    public bool TextFrameContents { get; set; } = true;

    /// <summary>
    /// The factor a scale applies to measurements that are not geometry.
    ///
    /// The **geometric mean** of the two axis factors, which is what makes an anisotropic scale behave: a
    /// shape stretched twice as wide and left the same height has no single factor, and the mean is the one
    /// that neither doubles a stroke nor leaves it alone. A uniform scale returns that factor exactly.
    /// </summary>
    public static double MeasurementFactor(double scaleX, double scaleY)
    {
        double product = Math.Abs(scaleX * scaleY);
        return product <= 0 ? 1.0 : Math.Sqrt(product);
    }
}

/// <summary>
/// Changes a path's stroke width, keeping the rest of each stroke as it was.
///
/// **Every stroke in the stack scales, by the ratio the caller asked for.** The caller computes the new width
/// of the bottom stroke from the object's scale factor; applying that same ratio to the whole stack is what
/// keeps a highlight sitting the same distance proud of an outline when the shape it is on is resized. Scaling
/// only the bottom stroke would leave a stack growing at a different rate from its shape, which is the kind of
/// fault that is noticed late and on exactly one drawing.
/// </summary>
public sealed class SetStrokeWidthCommand : IUndoableCommand
{
    private readonly PathItem _path;
    private readonly double _before;
    private readonly double _after;

    public SetStrokeWidthCommand(PathItem path, double before, double after)
    {
        _path = path;
        _before = before;
        _after = after;
    }

    public string Description => "Scale line weight";

    public void Do() => Scale(_before <= 0 ? 1.0 : _after / _before);

    public void Undo() => Scale(_after <= 0 ? 1.0 : _before / _after);

    private void Scale(double factor)
    {
        for (int i = 0; i < _path.Strokes.Count; i++)
        {
            StrokeSpec stroke = _path.Strokes[i];
            _path.Strokes[i] = stroke with { Width = stroke.Width * factor };
        }

        _path.NotifyStrokesChanged();
    }
}

/// <summary>
/// Replaces a path's whole stroke stack, as one undo step.
///
/// Adding a stroke, removing one and reordering them are the same edit seen three ways - an ordered list
/// becoming another ordered list - so one command covers all three and undo is exact rather than a reverse
/// operation that has to be got right three times. The stack is never empty, so a caller removing the last
/// stroke replaces it with an invisible one rather than leaving a path with none.
/// </summary>
public sealed class SetStrokesCommand : IUndoableCommand
{
    private readonly PathItem _path;
    private readonly List<StrokeSpec> _before;
    private readonly List<StrokeSpec> _after;

    public SetStrokesCommand(PathItem path, IEnumerable<StrokeSpec> after, string description = "Stroke stack")
    {
        _path = path;
        _before = path.Strokes.ToList();
        _after = after.ToList();
        if (_after.Count == 0)
        {
            _after.Add(StrokeSpec.None);
        }

        Description = description;
    }

    public string Description { get; }

    public void Do() => Apply(_after);

    public void Undo() => Apply(_before);

    private void Apply(List<StrokeSpec> strokes)
    {
        _path.Strokes.Clear();
        _path.Strokes.AddRange(strokes);
        _path.NotifyStrokesChanged();
    }
}
///
/// Per-run rather than one size for the whole block: a heading and a caption in the same frame have different
/// sizes, and scaling them to a single value would flatten the block's typography instead of enlarging it.
/// </summary>
public sealed class ScaleTextFontCommand : IUndoableCommand
{
    private readonly TextItem _item;
    private readonly double[] _before;
    private readonly double _factor;

    public ScaleTextFontCommand(TextItem item, double factor)
    {
        _item = item;
        _factor = factor;
        _before = item.Runs.Select(r => r.FontSize).ToArray();
    }

    public string Description => "Scale type with the frame";

    public void Do()
    {
        for (int i = 0; i < _item.Runs.Count && i < _before.Length; i++)
        {
            _item.Runs[i].FontSize = _before[i] * _factor;
        }
    }

    public void Undo()
    {
        for (int i = 0; i < _item.Runs.Count && i < _before.Length; i++)
        {
            _item.Runs[i].FontSize = _before[i];
        }
    }
}
