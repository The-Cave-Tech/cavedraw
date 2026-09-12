using System.Globalization;
using System.IO.Compression;
using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;

namespace VCCad.Pdf;

/// <summary>
/// Exports a <see cref="CadDocument"/> to a valid PDF 1.7 byte stream.
///
/// Model → PDF mapping rules:
/// <list type="bullet">
/// <item>Each artboard becomes one PDF page whose MediaBox equals the artboard
/// rectangle; the page's user space is the artboard's top-left-origin space with
/// the Y axis flipped (PDF user space grows upward). This flip is encoded as a
/// single <c>cm</c> matrix at the start of each content stream, so all path
/// coordinates below it are simply the model's own numbers.</item>
/// <item>Every path item maps onto native PDF path operators: straight segments
/// become <c>l</c> and cubic segments <c>c</c> — never flattened, which is why the
/// <em>rendered</em> geometry is exact (curves stay curves in PDF).</item>
/// <item>Fill and stroke, colours (DeviceRGB), stroke width, caps, joins and the
/// fill rule are all honoured. A closed contour is painted with the fill; open
/// contours are stroked only — the same rule the model applies (brief: closed
/// paths are filled).</item>
/// <item>Group transforms are <em>baked into geometry</em> before emission: this
/// avoids accumulating <c>cm</c> state and keeps every exported path self-contained.
/// Stroke widths are scaled by √|det| of the group transform as a uniform
/// approximation (PDF strokes cannot be sheared; noted for the M4 fidelity task).</item>
/// </list>
///
/// Lossless round-trip (ADR-03): the complete document model is embedded as an
/// embedded-file attachment (<c>vccad-document</c>), compressed with FlateDecode.
/// Any viewer shows the faithful artwork; only VCCad reads the attachment back.
/// </summary>
public static class PdfDocumentExporter
{
    /// <summary>Name under which the lossless model attachment is embedded.</summary>
    public const string SidecarFileName = "vccad-document";

    /// <summary>Exports the document and returns the complete PDF bytes.</summary>
    public static byte[] Export(CadDocument document)
    {
        using var buffer = new MemoryStream();
        Export(document, buffer);
        return buffer.ToArray();
    }

    /// <summary>Exports the document into <paramref name="output"/>.</summary>
    public static void Export(CadDocument document, Stream output)
    {
        var assembler = new PdfAssembler();

        // ------------------------------------------------------------------
        // Object numbering plan (contiguous; references may point forward).
        //   1  Catalog       2  Pages tree       3  Names tree
        //   4  Embedded sidecar stream   5  FileSpec
        //   6..  per artboard: page object + content stream, interleaved.
        // ------------------------------------------------------------------
        int catalogNumber = assembler.Allocate();
        int pagesNumber = assembler.Allocate();
        int namesNumber = assembler.Allocate();
        int sidecarStreamNumber = assembler.Allocate();
        int sidecarSpecNumber = assembler.Allocate();

        var pageNumbers = new int[document.Artboards.Count];
        var contentNumbers = new int[document.Artboards.Count];
        for (int i = 0; i < document.Artboards.Count; i++)
        {
            pageNumbers[i] = assembler.Allocate();
            contentNumbers[i] = assembler.Allocate();
        }

        // ------------------------------------------------------------------
        // Sidecar: lossless model JSON, zlib (RFC 1950) compressed — the PDF
        // FlateDecode filter uses exactly the zlib wrapper, so a conformant
        // reader can decompress it without custom code.
        // ------------------------------------------------------------------
        byte[] modelJson = VccadDocumentSerializer.SerializeToBytes(document);
        byte[] sidecarStream = Compress(modelJson);

        assembler.SetBody(sidecarStreamNumber, MakeStreamObject(sidecarStream));
        assembler.SetBody(
            sidecarSpecNumber,
            $"<< /Type /Filespec /F ({SidecarFileName}) /UF ({SidecarFileName}) " +
            $"/EF << /F {sidecarStreamNumber} 0 R >> >>");

        // ------------------------------------------------------------------
        // Content streams and page objects.
        // ------------------------------------------------------------------
        var kidList = new StringBuilder();
        for (int i = 0; i < document.Artboards.Count; i++)
        {
            Artboard artboard = document.Artboards[i];
            byte[] content = BuildArtboardContent(artboard);
            // Content streams are FlateDecode-filtered like the sidecar; the raw
            // operator text is compressed here before being wrapped.
            assembler.SetBody(contentNumbers[i], MakeStreamObject(Compress(content)));

            assembler.SetBody(
                pageNumbers[i],
                $"<< /Type /Page /Parent {pagesNumber} 0 R " +
                $"/MediaBox [0 0 {Num(artboard.Width)} {Num(artboard.Height)}] " +
                $"/Contents {contentNumbers[i]} 0 R " +
                $"/Resources << >> >>");

            kidList.Append(pageNumbers[i]).Append(" 0 R ");
        }

        // ------------------------------------------------------------------
        // Document trailer: catalog → pages → kids.
        // ------------------------------------------------------------------
        assembler.SetBody(
            pagesNumber,
            $"<< /Type /Pages /Kids [{kidList}] /Count {document.Artboards.Count} >>");
        assembler.SetBody(
            namesNumber,
            $"<< /EmbeddedFiles << /Names [({SidecarFileName}) {sidecarSpecNumber} 0 R] >> >>");
        assembler.SetBody(
            catalogNumber,
            $"<< /Type /Catalog /Pages {pagesNumber} 0 R /Names {namesNumber} 0 R >>");

        byte[] bytes = assembler.Serialize(catalogNumber);
        output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Renders one artboard to a content stream. See the class remarks for the
    /// mapping rules. The returned bytes are the plain content (uncompressed);
    /// callers wrap them via <see cref="MakeStreamObject"/>.
    /// </summary>
    private static byte[] BuildArtboardContent(Artboard artboard)
    {
        var ops = new List<string>();

        // Path coordinates are stored relative to the artboard's top-left, so the
        // page flip is simply  px = x ; py = Height − y  (no artboard offset).
        double e = 0;
        double f = artboard.Height;
        ops.Add($"{Num(1)} 0 0 {Num(-1)} {Num(e)} {Num(f)} cm");

        foreach (Layer layer in artboard.Layers)
        {
            if (!layer.IsVisible)
            {
                continue;
            }

            foreach (LayerItem item in layer.Children)
            {
                PaintItem(ops, item, AffineTransform.Identity, 1.0);
            }
        }

        if (ops.Count == 0)
        {
            ops.Add("% empty artboard"); // keep the stream non-empty & valid
        }

        return Encoding.UTF8.GetBytes(string.Join("\n", ops) + "\n");
    }

    /// <summary>
    /// Recursively emits an item. <paramref name="toDoc"/> maps the item's local
    /// coordinates into artboard (document) space — the composition of every
    /// ancestor group transform; <paramref name="opacity"/> is the accumulated
    /// product of ancestor opacities (reserved: emitted in a later sprint when the
    /// ExtGState task lands, see project plan M4).
    /// </summary>
    private static void PaintItem(List<string> ops, LayerItem item, AffineTransform toDoc, double opacity)
    {
        switch (item)
        {
            case PathItem path when path.IsVisible:
                PaintPath(ops, path, toDoc);
                break;

            case ArtGroup group when group.IsVisible:
                // Compose is defined as "apply argument first, then this", which is
                // exactly local→parent→doc as the walk descends.
                AffineTransform childToDoc = toDoc.Compose(group.Transform);
                foreach (LayerItem child in group.Children)
                {
                    PaintItem(ops, child, childToDoc, opacity * group.Opacity);
                }

                break;
        }
    }

    /// <summary>
    /// Emits the fill/stroke operators for one path. Contours are separated into
    /// closed (fillable) and open (stroke-only), so the renderer never fills an
    /// open path as PDF would implicitly do.
    /// </summary>
    private static void PaintPath(List<string> ops, PathItem path, AffineTransform toDoc)
    {
        // Bake geometry into artboard space: anchors and both handles per node.
        var contours = new List<Contour>();
        foreach (SubPath sub in path.SubPaths)
        {
            if (sub.Nodes.Count < 2)
            {
                continue; // a single stray node paints nothing
            }

            var contour = new Contour { IsClosed = sub.IsClosed };
            foreach (PathNode node in sub.Nodes)
            {
                contour.Start ??= toDoc.Transform(node.Anchor); // first anchor = contour start
                contour.Points.Add(toDoc.Transform(node.Anchor));
                contour.InHandles.Add(toDoc.Transform(node.InHandle));
                contour.OutHandles.Add(toDoc.Transform(node.OutHandle));
            }

            contours.Add(contour);
        }

        if (contours.Count == 0)
        {
            return;
        }

        bool anyClosed = contours.Any(c => c.IsClosed);
        bool fillVisible = path.Fill.IsVisible && anyClosed;
        bool strokeVisible = path.Stroke.HasVisibleOutline;

        if (!fillVisible && !strokeVisible)
        {
            return;
        }

        // Scaling for stroke width. The group transform was baked into geometry;
        // the width must track the same scale to look right. For an affine with
        // determinant δ, the uniform scale factor is √|δ| (exact for rotation +
        // uniform scale, a documented approximation for shears — see class remarks).
        double det = toDoc.A * toDoc.D - toDoc.C * toDoc.B;
        double strokeScale = Math.Sqrt(Math.Abs(det));
        double width = Math.Max(0.0, path.Stroke.Width * strokeScale);

        // --- Style setup ---------------------------------------------------
        if (fillVisible)
        {
            ops.Add($"{Num(path.Fill.Color.R)} {Num(path.Fill.Color.G)} {Num(path.Fill.Color.B)} rg");
        }

        if (strokeVisible)
        {
            ops.Add($"{Num(path.Stroke.Color.R)} {Num(path.Stroke.Color.G)} {Num(path.Stroke.Color.B)} RG");
            ops.Add($"{CapToPdf(path.Stroke.Cap)} J");
            ops.Add($"{JoinToPdf(path.Stroke.Join)} j");
            if (path.Stroke.Join == StrokeJoin.Miter)
            {
                ops.Add($"{Num(path.Stroke.MiterLimit)} M");
            }
        }

        var closed = contours.Where(c => c.IsClosed).ToList();
        var open = contours.Where(c => !c.IsClosed).ToList();

        // --- Fill (closed contours only) -----------------------------------
        if (fillVisible && closed.Count > 0)
        {
            WriteContours(ops, closed);
            ops.Add(path.Fill.Rule == FillRule.EvenOdd ? "f*" : "f");
        }

        if (!strokeVisible)
        {
            return;
        }

        // --- Stroke: honour Inside/Outside by clipping ---------------------
        // PDF has no stroke alignment, so an aligned stroke is drawn at double
        // width and clipped to the inside or outside of the path.
        bool aligned = path.Stroke.Alignment != StrokeAlignment.Center && closed.Count > 0;
        if (closed.Count > 0)
        {
            if (!aligned)
            {
                ops.Add($"{Num(width)} w");
                WriteContours(ops, closed);
                ops.Add("S");
            }
            else
            {
                ops.Add("q");
                if (path.Stroke.Alignment == StrokeAlignment.Inside)
                {
                    WriteContours(ops, closed);
                    ops.Add(path.Fill.Rule == FillRule.EvenOdd ? "W* n" : "W n");
                }
                else
                {
                    // Outside = everything except the path interior (even-odd).
                    ops.Add($"{Num(-10000)} {Num(-10000)} m {Num(20000)} {Num(-10000)} l " +
                            $"{Num(20000)} {Num(20000)} l {Num(-10000)} {Num(20000)} l h");
                    WriteContours(ops, closed);
                    ops.Add("W* n");
                }

                ops.Add($"{Num(width * 2)} w");
                WriteContours(ops, closed);
                ops.Add("S");
                ops.Add("Q");
            }
        }

        // --- Open contours: always centre-stroked --------------------------
        if (open.Count > 0)
        {
            ops.Add($"{Num(width)} w");
            WriteContours(ops, open);
            ops.Add("S");
        }
    }

    /// <summary>Writes the raw geometry of several contours into the current path.</summary>
    private static void WriteContours(List<string> ops, List<Contour> contours)
    {
        foreach (Contour c in contours)
        {
            ops.Add($"{Num(c.Start!.Value.X)} {Num(c.Start.Value.Y)} m");
            for (int i = 0; i < c.Points.Count - 1; i++)
            {
                Point2D start = c.Points[i];
                Point2D p1 = c.OutHandles[i];
                Point2D p2 = c.InHandles[i + 1];
                Point2D end = c.Points[i + 1];
                WriteSegment(ops, start, p1, p2, end);
            }

            if (c.IsClosed)
            {
                // Final contour segment closes back to the start node.
                Point2D start = c.Points[^1];
                Point2D p1 = c.OutHandles[^1];
                Point2D p2 = c.InHandles[0];
                Point2D end = c.Start!.Value;
                WriteSegment(ops, start, p1, p2, end);
                ops.Add("h");
            }
        }
    }

    /// <summary>
    /// Writes a single cubic as <c>c</c>, collapsing to a straight <c>l</c> when it
    /// is the degenerate straight form (control points coincident with endpoints).
    /// The model stores lines exactly that way, so the comparison is a real,
    /// reachable case — not just defensive.
    /// </summary>
    private static void WriteSegment(List<string> ops, Point2D start, Point2D p1, Point2D p2, Point2D end)
    {
        bool isLine = p1.NearlyEquals(start) && p2.NearlyEquals(end);
        if (isLine)
        {
            ops.Add($"{Num(end.X)} {Num(end.Y)} l");
        }
        else
        {
            ops.Add($"{Num(p1.X)} {Num(p1.Y)} {Num(p2.X)} {Num(p2.Y)} {Num(end.X)} {Num(end.Y)} c");
        }
    }

    /// <summary>Maps the model cap enum to the PDF linecap code.</summary>
    private static int CapToPdf(StrokeCap cap) => cap switch
    {
        StrokeCap.Butt => 0,
        StrokeCap.Round => 1,
        StrokeCap.Square => 2,
        _ => 0,
    };

    /// <summary>Maps the model join enum to the PDF linejoin code.</summary>
    private static int JoinToPdf(StrokeJoin join) => join switch
    {
        StrokeJoin.Miter => 0,
        StrokeJoin.Round => 1,
        StrokeJoin.Bevel => 2,
        _ => 0,
    };

    /// <summary>Formats a number for PDF (PDF numbers do not allow exponents).</summary>
    internal static string Num(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('E', StringComparison.OrdinalIgnoreCase)
            ? value.ToString("0.##########", CultureInfo.InvariantCulture)
            : text;
    }

    /// <summary>
    /// Wraps raw (already zlib-compressed) bytes into a PDF stream object body
    /// carrying a <c>/Filter /FlateDecode</c> entry, with the compressed length
    /// written into the dictionary.
    /// </summary>
    private static byte[] MakeStreamObject(byte[] data)
    {
        string dict = $"<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n";
        byte[] head = Encoding.ASCII.GetBytes(dict);
        byte[] tail = Encoding.ASCII.GetBytes("\nendstream");
        var result = new byte[head.Length + data.Length + tail.Length];
        Buffer.BlockCopy(head, 0, result, 0, head.Length);
        Buffer.BlockCopy(data, 0, result, head.Length, data.Length);
        Buffer.BlockCopy(tail, 0, result, head.Length + data.Length, tail.Length);
        return result;
    }

    /// <summary>zlib (RFC 1950) compression — identical framing to PDF FlateDecode.</summary>
    internal static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    /// <summary>Inflates a zlib (RFC 1950) stream produced by <see cref="Compress"/>.</summary>
    internal static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Collected geometry of one subpath in destination (page) space.</summary>
    private sealed class Contour
    {
        public bool IsClosed;
        public Point2D? Start;
        public readonly List<Point2D> Points = new();
        public readonly List<Point2D> InHandles = new();
        public readonly List<Point2D> OutHandles = new();
    }
}
