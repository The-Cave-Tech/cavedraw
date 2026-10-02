using VCCad.Core.Model;

namespace VCCad.Pdf;

/// <summary>
/// PDF blend-mode support. A blend is the graphics state's <c>/BM</c>, so each mode a document states becomes an
/// <c>ExtGState</c> carrying it, and the paint switches to that state with <c>gs</c> before it is drawn.
///
/// **A blend is composited, not stored.** The value travels in the sidecar and is written by the SVG export, and
/// none of that changes a page: what changes a page is a <c>/BM</c> in a state the content stream actually enters.
/// So the states are allocated **lazily**, from <see cref="NameFor"/>, which is called by the painter at the point
/// it writes the switch - a mode nothing paints costs no object, and a document that states no blend allocates
/// nothing and is byte for byte what it was before this existed.
///
/// The names are the ones ISO 32000-1 Table 136 gives, which happen to be the model's own member names for these
/// modes - PDF spells them without the hyphens CSS uses (<c>/ColorDodge</c>, not <c>color-dodge</c>). They are
/// written from an explicit switch rather than from <c>ToString</c>, so renaming an enum member cannot silently
/// start writing a blend mode no reader knows.
/// </summary>
internal sealed class PdfBlendStates
{
    private readonly PdfAssembler _assembler;
    private readonly Dictionary<BlendMode, string> _names = new();
    private readonly List<(string Name, int Object)> _objects = new();

    public PdfBlendStates(PdfAssembler assembler) => _assembler = assembler;

    /// <summary>The <c>/ExtGState</c> entries, in allocation order.</summary>
    public IReadOnlyList<(string Name, int Object)> Entries => _objects;

    /// <summary>
    /// The resource name for a blend mode, or null when it states none.
    ///
    /// Null for <see cref="BlendMode.Normal"/> as well as for an absent one: Normal is PDF's own default, so a
    /// state for it would be a switch that changes nothing - and writing one would be writing a member the
    /// document does not state.
    /// </summary>
    public string? NameFor(BlendMode? mode)
    {
        if (mode is null or BlendMode.Normal)
        {
            return null;
        }

        if (_names.TryGetValue(mode.Value, out string? existing))
        {
            return existing;
        }

        int obj = _assembler.Allocate();
        _assembler.SetBody(obj, $"<< /Type /ExtGState /BM /{PdfModeName(mode.Value)} >>");

        string name = $"/BM{_objects.Count + 1}";
        _names[mode.Value] = name;
        _objects.Add((name, obj));
        return name;
    }

    /// <summary>The <c>gs</c> operator that enters this mode's state, or null when there is nothing to enter.</summary>
    public string? Gs(BlendMode? mode)
        => NameFor(mode) is { } name ? $"{name} gs" : null;

    /// <summary>The blend mode's name in PDF's own table.</summary>
    private static string PdfModeName(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => "Multiply",
        BlendMode.Screen => "Screen",
        BlendMode.Darken => "Darken",
        BlendMode.Lighten => "Lighten",
        BlendMode.Overlay => "Overlay",
        BlendMode.ColorDodge => "ColorDodge",
        BlendMode.ColorBurn => "ColorBurn",
        BlendMode.HardLight => "HardLight",
        BlendMode.SoftLight => "SoftLight",
        BlendMode.Difference => "Difference",
        BlendMode.Exclusion => "Exclusion",
        BlendMode.Hue => "Hue",
        BlendMode.Saturation => "Saturation",
        BlendMode.Color => "Color",
        BlendMode.Luminosity => "Luminosity",
        _ => "Normal",
    };
}
