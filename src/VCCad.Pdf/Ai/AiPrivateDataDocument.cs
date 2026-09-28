using VCCad.Core.Model;

namespace VCCad.Pdf.Ai;

/// <summary>
/// A decoded Illustrator private-data payload plus its structured view.
///
/// This is the type <see cref="AiPrivateDataExtractor"/> returns and the parser and
/// writer operate on. The payload text is held verbatim; the element list, the
/// directive list and the layer tree are all projections that are computed lazily,
/// because a single AI24 fixture decodes to 2–5 MB of PostScript (tens of thousands
/// of lines) and most callers only need the text.
/// </summary>
public sealed class AiPrivateDataDocument
{
    private readonly Lazy<IReadOnlyList<AiPayloadElement>> _elements;
    private readonly Lazy<IReadOnlyList<AiDirective>> _directives;
    private readonly Lazy<IReadOnlyList<AiLayer>> _layers;
    private readonly Lazy<AiPrivateData> _privateData;

    private AiPrivateDataDocument(string text, AiPrivateDataFormat format, int declaredBlockCount)
    {
        Text = text;
        Format = format;
        DeclaredBlockCount = declaredBlockCount;
        _elements = new Lazy<IReadOnlyList<AiPayloadElement>>(
            () => AiPayloadParser.Parse(Text), LazyThreadSafetyMode.ExecutionAndPublication);
        _directives = new Lazy<IReadOnlyList<AiDirective>>(
            () => Elements.OfType<AiDirective>().ToList(), LazyThreadSafetyMode.ExecutionAndPublication);
        _layers = new Lazy<IReadOnlyList<AiLayer>>(BuildLayerTree, LazyThreadSafetyMode.ExecutionAndPublication);
        _privateData = new Lazy<AiPrivateData>(
            () => new AiPrivateData(Text, Format), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Decoded payload text (a Latin-1 projection of the payload bytes).</summary>
    public string Text { get; }

    /// <summary>Container format the payload was decoded from.</summary>
    public AiPrivateDataFormat Format { get; }

    /// <summary>
    /// Value of the container's <c>/NumBlock</c> entry, or 0 when the file did not
    /// carry one (Illustrator omits it in some single-block files). Purely
    /// informational: block order comes from the <c>/AIPrivateData&lt;n&gt;</c>
    /// numeric suffixes, never from this count.
    /// </summary>
    public int DeclaredBlockCount { get; }

    /// <summary>Every payload element, in source order (parsed lazily on first use).</summary>
    public IReadOnlyList<AiPayloadElement> Elements => _elements.Value;

    /// <summary>The <c>%</c>-prefixed header/directive elements, in source order.</summary>
    public IReadOnlyList<AiDirective> Directives => _directives.Value;

    /// <summary>
    /// Top-level Illustrator layers; sublayers hang off <see cref="AiLayer.Children"/>.
    /// Empty for payloads that contain no <c>%AI5_BeginLayer</c> region.
    /// </summary>
    public IReadOnlyList<AiLayer> Layers => _layers.Value;

    /// <summary>The document-model payload (decoded text + format) for this document.</summary>
    public AiPrivateData PrivateData => _privateData.Value;

    /// <summary>Parses payload text into a document with the given source format.</summary>
    /// <param name="text">Decoded payload text.</param>
    /// <param name="format">Container format the text came from.</param>
    /// <param name="declaredBlockCount">Container <c>/NumBlock</c> value, if any.</param>
    public static AiPrivateDataDocument Parse(
        string text, AiPrivateDataFormat format = AiPrivateDataFormat.Unknown, int declaredBlockCount = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new AiPrivateDataDocument(text, format, declaredBlockCount);
    }

    /// <summary>Wraps a model-level payload (e.g. one restored from a sidecar).</summary>
    public static AiPrivateDataDocument FromPrivateData(AiPrivateData privateData)
    {
        ArgumentNullException.ThrowIfNull(privateData);
        return new AiPrivateDataDocument(privateData.Text, privateData.Format, 0);
    }

    /// <summary>
    /// Re-emits the payload. For an unmodified document this is byte-identical to
    /// <see cref="Text"/>; see <see cref="AiPayloadWriter"/> for why.
    /// </summary>
    public string Write() => AiPayloadWriter.Write(this);

    /// <summary>
    /// Structural comparison of two parses: same format and same element sequence
    /// (type, verbatim text and layer depth) at every position. Used by the
    /// idempotence tests to show <c>Parse(Write(Parse(text)))</c> changes nothing.
    /// </summary>
    public bool StructurallyEquals(AiPrivateDataDocument? other)
    {
        if (other is null || other.Format != Format || other.Elements.Count != Elements.Count)
        {
            return false;
        }

        for (int i = 0; i < Elements.Count; i++)
        {
            AiPayloadElement left = Elements[i];
            AiPayloadElement right = other.Elements[i];
            if (left.GetType() != right.GetType() ||
                left.Depth != right.Depth ||
                !string.Equals(left.Raw, right.Raw, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Projects the flat element list into the nested layer tree by walking
    /// <see cref="AiLayerBegin"/>/<see cref="AiLayerEnd"/> at matching depths.
    /// </summary>
    private IReadOnlyList<AiLayer> BuildLayerTree()
    {
        var roots = new List<AiLayer>();
        var stack = new List<AiLayer>();

        foreach (AiPayloadElement element in Elements)
        {
            switch (element)
            {
                case AiLayerBegin begin:
                {
                    AiLayer? parent = stack.Count > 0 ? stack[^1] : null;
                    var layer = new AiLayer(begin, parent);
                    if (parent is null)
                    {
                        roots.Add(layer);
                    }
                    else
                    {
                        parent.MutableChildren.Add(layer);
                    }

                    stack.Add(layer);
                    break;
                }

                case AiLayerEnd:
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    break;
            }
        }

        return roots;
    }
}
