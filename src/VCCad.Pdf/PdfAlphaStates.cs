using System.Globalization;

namespace VCCad.Pdf;

/// <summary>
/// PDF constant-alpha support. PDF has no per-colour alpha, so each distinct
/// alpha value becomes an ExtGState with <c>/ca</c> (fill) and <c>/CA</c> (stroke);
/// content streams switch with <c>/GSn gs</c> before painting.
/// </summary>
internal sealed class PdfAlphaStates
{
    private readonly Dictionary<int, string> _names = new();
    private readonly List<(string Name, int Object)> _objects = new();

    public bool HasTransparency { get; }

    public PdfAlphaStates(PdfAssembler assembler, IEnumerable<double> alphas)
    {
        var buckets = new SortedSet<int> { 1000 }; // always include opaque
        foreach (double a in alphas)
        {
            buckets.Add(Bucket(a));
        }

        HasTransparency = buckets.Count > 1;

        int index = 1;
        foreach (int bucket in buckets)
        {
            double value = bucket / 1000.0;
            int obj = assembler.Allocate();
            assembler.SetBody(obj,
                $"<< /Type /ExtGState /ca {PdfDocumentExporter.Num(value)} /CA {PdfDocumentExporter.Num(value)} >>");
            string name = $"/GS{index++}";
            _names[bucket] = name;
            _objects.Add((name, obj));
        }
    }

    /// <summary>Resource name for an alpha value (bucketed to 0.001).</summary>
    public string NameFor(double alpha) => _names[Bucket(alpha)];

    /// <summary>The <c>/ExtGState</c> resource dictionary entry (or empty).</summary>
    public string Dict()
    {
        if (_objects.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder("/ExtGState << ");
        foreach ((string name, int obj) in _objects)
        {
            sb.Append(name).Append(' ').Append(obj).Append(" 0 R ");
        }

        sb.Append(">> ");
        return sb.ToString();
    }

    private static int Bucket(double alpha)
        => (int)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 1000.0);
}
