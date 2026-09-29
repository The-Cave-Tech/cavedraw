using System.Globalization;
using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Pdf;

/// <summary>
/// A gradient painted with a PDF shading, and the coordinate-space matrix the content
/// stream must set before <c>sh</c>.
///
/// <see cref="Matrix"/> is null when the shading's own <c>/Coords</c> are already in the
/// current user space (linear). A radial shading is always a circle in its own space, so
/// an elliptical radial carries a matrix that turns the unit circle into the object's
/// ellipse - which is exactly how PDF does an anisotropic radial.
/// </summary>
internal readonly record struct ShadingPaint(string ResourceName, string? Matrix);

/// <summary>
/// PDF shadings for gradient fills.
///
/// The ramp is built the way PDF expects: adjacent stop pairs become exponential
/// interpolation functions (<c>FunctionType 2</c>), one per pair, stitched together by a
/// <c>FunctionType 3</c> whose <c>/Bounds</c> are the interior stop positions. A two-stop
/// gradient is therefore one pair (a bare type 2), and three stops are two sub-functions,
/// not three. A stop's <c>Midpoint</c> becomes the sub-function's exponent, which is the
/// same power curve the model samples with.
///
/// Linear geometry is an axial shading (<c>ShadingType 2</c>); radial is a
/// <c>ShadingType 3</c>. Each usage gets its own shading object because the geometry is
/// per-item; the ramp function is per-usage too, which keeps the code simple at the cost
/// of some duplication in the file.
///
/// <para>
/// Known gaps, reported through <paramref name="notes"/> rather than silently dropped:
/// per-stop <c>Opacity</c> is not expressible in a plain shading function, so only the
/// stop colours are painted; <c>Reflect</c> and <c>Repeat</c> need the function extended
/// over a wider domain, which this pass does not do, so they are exported with PDF's only
/// real extend - Pad - and said so; freeform and conical gradients have no native PDF
/// shading and fall back to the fill's flat colour.
/// </para>
/// </summary>
internal sealed class PdfShadingObjects
{
    private readonly PdfAssembler _assembler;
    private readonly List<string> _notes;
    private readonly List<(string Name, int Object)> _shadings = new();
    private int _next;

    public PdfShadingObjects(PdfAssembler assembler, List<string> notes)
    {
        _assembler = assembler;
        _notes = notes;
    }

    /// <summary>The <c>/Shading</c> resource dictionary entry (or empty).</summary>
    public string Dict()
    {
        if (_shadings.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("/Shading << ");
        foreach ((string name, int obj) in _shadings)
        {
            sb.Append(name).Append(' ').Append(obj).Append(" 0 R ");
        }

        sb.Append(">> ");
        return sb.ToString();
    }

    /// <summary>
    /// Allocates a shading for <paramref name="gradient"/> on a shape whose bounding box is
    /// <paramref name="localBounds"/> in its own coordinate space, mapped to artboard space
    /// by <paramref name="toDoc"/>. Returns null when the gradient has no PDF representation
    /// (freeform, conical) after noting why.
    /// </summary>
    public ShadingPaint? NameFor(GradientSpec gradient, Rect2D localBounds, AffineTransform toDoc)
    {
        if (gradient.Kind is GradientKind.Freeform or GradientKind.Conical)
        {
            _notes.Add($"{gradient.Kind} gradient has no PDF shading; exported as its flat fill colour.");
            return null;
        }

        if (gradient.Normalised().Any(s => s.Opacity < 1.0 - 1e-9))
        {
            _notes.Add("gradient stop opacity is not exported: PDF shading functions carry no alpha (only the stop colours are painted).");
        }

        if (gradient.Spread != GradientSpread.Pad)
        {
            _notes.Add($"gradient spread {gradient.Spread} is not exported: PDF /Extend only pads, so it is exported as Pad.");
        }

        int function = BuildRampFunction(gradient.Normalised());

        double width = localBounds.Width;
        double height = localBounds.Height;
        int shading;
        string? matrix = null;

        if (gradient.Kind == GradientKind.Radial)
        {
            Point2D centre = new(
                localBounds.Left + (gradient.Center.X * width),
                localBounds.Top + (gradient.Center.Y * height));
            double radians = gradient.Rotation * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            double rx = gradient.RadiusX * width;
            double ry = gradient.RadiusY * height;

            // The object's own axes, mapped to artboard space: the shading is a unit circle
            // in its local space and this matrix turns it into the object's ellipse.
            Vector2D u = toDoc.Transform(new Vector2D(rx * cos, rx * sin));
            Vector2D v = toDoc.Transform(new Vector2D(-ry * sin, ry * cos));
            Point2D mappedCentre = toDoc.Transform(centre);

            shading = _assembler.Allocate();
            _assembler.SetBody(
                shading,
                $"<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [0 0 0 0 0 1] " +
                $"/Function {function} 0 R /Extend [true true] >>");
            matrix = $"{PdfDocumentExporter.Num(u.X)} {PdfDocumentExporter.Num(u.Y)} " +
                     $"{PdfDocumentExporter.Num(v.X)} {PdfDocumentExporter.Num(v.Y)} " +
                     $"{PdfDocumentExporter.Num(mappedCentre.X)} {PdfDocumentExporter.Num(mappedCentre.Y)}";
        }
        else
        {
            Point2D start = toDoc.Transform(new Point2D(
                localBounds.Left + (gradient.Start.X * width),
                localBounds.Top + (gradient.Start.Y * height)));
            Point2D end = toDoc.Transform(new Point2D(
                localBounds.Left + (gradient.End.X * width),
                localBounds.Top + (gradient.End.Y * height)));

            shading = _assembler.Allocate();
            _assembler.SetBody(
                shading,
                $"<< /ShadingType 2 /ColorSpace /DeviceRGB " +
                $"/Coords [{PdfDocumentExporter.Num(start.X)} {PdfDocumentExporter.Num(start.Y)} " +
                $"{PdfDocumentExporter.Num(end.X)} {PdfDocumentExporter.Num(end.Y)}] " +
                $"/Function {function} 0 R /Extend [true true] >>");
        }

        string name = $"/Sh{_next++}";
        _shadings.Add((name, shading));
        return new ShadingPaint(name.TrimStart('/'), matrix);
    }

    /// <summary>
    /// Builds the ramp as PDF functions and returns the object number of the function to
    /// reference. One exponential sub-function per adjacent stop pair is stitched by a
    /// <c>FunctionType 3</c>; the sub-function's <c>N</c> is the stop's midpoint power and
    /// its <c>C0</c>/<c>C1</c> are the pair's colours. Stops that do not sit on 0 and 1 gain
    /// a constant pad sub-function at each end so the ramp is not stretched.
    /// </summary>
    private int BuildRampFunction(IReadOnlyList<GradientStop> stops)
    {
        List<(double From, double To, ColorRgb A, ColorRgb B, double Midpoint)> segments = RampSegments(stops);

        if (segments.Count == 0)
        {
            ColorRgb colour = stops[0].Color;
            int constant = _assembler.Allocate();
            _assembler.SetBody(constant, Exponential(colour, colour, 0.5));
            return constant;
        }

        var functions = new List<int>(segments.Count);
        foreach ((double _, double _, ColorRgb a, ColorRgb b, double midpoint) in segments)
        {
            int sub = _assembler.Allocate();
            _assembler.SetBody(sub, Exponential(a, b, midpoint));
            functions.Add(sub);
        }

        if (segments.Count == 1)
        {
            return functions[0];
        }

        string functionList = string.Join(' ', functions.Select(n => $"{n} 0 R"));
        string bounds = string.Join(' ', segments.Take(segments.Count - 1)
            .Select(s => PdfDocumentExporter.Num(s.To)));
        string encode = string.Join(' ', Enumerable.Repeat("0 1", functions.Count));

        int stitched = _assembler.Allocate();
        _assembler.SetBody(
            stitched,
            $"<< /FunctionType 3 /Domain [0 1] /Functions [{functionList}] " +
            $"/Bounds [{bounds}] /Encode [{encode}] >>");
        return stitched;
    }

    /// <summary>
    /// The sub-domains of the ramp: a constant pad before the first stop when it is not at
    /// 0, one ramp per adjacent pair, and a constant pad after the last stop when it is not
    /// at 1. Equal positions were already collapsed by <see cref="GradientSpec.Normalised"/>,
    /// so no segment has zero width.
    /// </summary>
    private static List<(double From, double To, ColorRgb A, ColorRgb B, double Midpoint)> RampSegments(
        IReadOnlyList<GradientStop> stops)
    {
        var segments = new List<(double From, double To, ColorRgb A, ColorRgb B, double Midpoint)>();
        if (stops.Count == 0)
        {
            return segments;
        }

        if (stops[0].Position > 1e-9)
        {
            segments.Add((0.0, stops[0].Position, stops[0].Color, stops[0].Color, 0.5));
        }

        for (int i = 0; i + 1 < stops.Count; i++)
        {
            segments.Add((stops[i].Position, stops[i + 1].Position,
                stops[i].Color, stops[i + 1].Color, stops[i].Midpoint));
        }

        if (stops[^1].Position < 1.0 - 1e-9)
        {
            segments.Add((stops[^1].Position, 1.0, stops[^1].Color, stops[^1].Color, 0.5));
        }

        return segments;
    }

    /// <summary>
    /// One <c>FunctionType 2</c>. <paramref name="midpoint"/> is the stop's blend midpoint;
    /// the model's power curve uses <c>log(0.5)/log(midpoint)</c> as its exponent, which is
    /// exactly the exponent PDF's exponential interpolation applies.
    /// </summary>
    private static string Exponential(ColorRgb a, ColorRgb b, double midpoint)
    {
        double n = 1.0;
        double m = Math.Clamp(midpoint, 1e-6, 1.0 - 1e-6);
        if (Math.Abs(m - 0.5) > 1e-9)
        {
            n = Math.Log(0.5) / Math.Log(m);
        }

        return $"<< /FunctionType 2 /Domain [0 1] " +
               $"/C0 [{PdfDocumentExporter.Num(a.R)} {PdfDocumentExporter.Num(a.G)} {PdfDocumentExporter.Num(a.B)}] " +
               $"/C1 [{PdfDocumentExporter.Num(b.R)} {PdfDocumentExporter.Num(b.G)} {PdfDocumentExporter.Num(b.B)}] " +
               $"/N {PdfDocumentExporter.Num(n)} >>";
    }
}
