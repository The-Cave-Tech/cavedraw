namespace VCCad.Pdf.Ai;

/// <summary>A layer's selection colour as stored in the <c>Lb</c> operands (0–255 per channel).</summary>
public readonly record struct AiLayerColor(int Red, int Green, int Blue);

/// <summary>
/// The nested view of one <c>%AI5_BeginLayer … %AI5_EndLayer--</c> region.
///
/// Illustrator layers nest (sublayers inside layers), which the flat element list
/// cannot express directly — the tree is projected from it by
/// <see cref="AiPrivateDataDocument.Layers"/>. Editing a layer through
/// <see cref="Begin"/> yields a replacement <see cref="AiLayerBegin"/> element that
/// the writer then emits instead of the original header.
/// </summary>
public sealed class AiLayer
{
    internal AiLayer(AiLayerBegin begin, AiLayer? parent)
    {
        Begin = begin;
        Parent = parent;
        Depth = begin.Depth;
    }

    /// <summary>The parsed <c>%AI5_BeginLayer</c> header element for this layer.</summary>
    public AiLayerBegin Begin { get; }

    /// <summary>Enclosing layer, or <c>null</c> for a top-level layer.</summary>
    public AiLayer? Parent { get; }

    /// <summary>Nested sublayers, in document order.</summary>
    public IReadOnlyList<AiLayer> Children => MutableChildren;

    /// <summary>Layer name from the <c>Ln</c> statement.</summary>
    public string Name => Begin.Name;

    /// <summary>The layer's own <c>visible</c> flag; false when the layer is hidden.</summary>
    public bool IsVisible => Begin.IsVisible;

    /// <summary>The layer's own <c>visible?</c> flag (differs from <see cref="IsVisible"/>
    /// for template layers).</summary>
    public bool IsVisibleFlag => Begin.IsVisibleFlag;

    /// <summary>
    /// True only when this layer and every ancestor is visible. A hidden parent hides
    /// its sublayers regardless of the sublayers' own flags — the Illustrator
    /// behaviour called out in the format documentation.
    /// </summary>
    public bool IsEffectivelyVisible
    {
        get
        {
            for (AiLayer? layer = this; layer is not null; layer = layer.Parent)
            {
                if (!layer.IsVisible)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Selection colour index from the <c>Lb</c> operands, or −1.</summary>
    public int ColorIndex => Begin.ColorIndex;

    /// <summary>Selection colour; all components are −1 when the operands are absent.</summary>
    public AiLayerColor Color => new(Begin.Red, Begin.Green, Begin.Blue);

    /// <summary>True when the layer is shown expanded in the Layers panel.</summary>
    public bool IsExpanded => Begin.IsExpanded;

    /// <summary>Zero-based nesting depth (0 = top-level layer).</summary>
    public int Depth { get; }

    /// <summary>Flattens this layer and all descendants, parents before children.</summary>
    public IEnumerable<AiLayer> DescendantsAndSelf()
    {
        yield return this;
        foreach (AiLayer child in MutableChildren)
        {
            foreach (AiLayer layer in child.DescendantsAndSelf())
            {
                yield return layer;
            }
        }
    }

    internal List<AiLayer> MutableChildren { get; } = new();
}
