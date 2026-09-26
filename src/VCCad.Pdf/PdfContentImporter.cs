using System.Globalization;
using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf.Fonts;
using VCCad.Pdf.Parsing;

namespace VCCad.Pdf;

/// <summary>
/// Interprets PDF page content streams into VCCad model objects: paths (line and
/// cubic segments with fills/strokes) and text runs. Handles the graphics state
/// (q/Q/cm/w/colours), path construction and painting, Form XObjects and basic
/// text state. Coordinates are converted from PDF user space (bottom-left origin)
/// into the artboard-local top-left frame.
/// </summary>
/// <summary>A model item plus the optional-content (layer) name it was drawn under.</summary>
internal sealed record PdfImportedItem(string? Layer, LayerItem Item);

internal sealed class PdfContentImporter
{
    private readonly PdfFile _file;
    private readonly double _pageHeight;

    public PdfContentImporter(PdfFile file, double pageHeight)
    {
        _file = file;
        _pageHeight = pageHeight;
    }

    public List<PdfImportedItem> ParsePage(Dictionary<string, object?> pageDict)
    {
        var items = new List<PdfImportedItem>();
        Interpret(GetContents(pageDict), FindResources(pageDict), AffineTransform.Identity, items, 0, null);
        return items;
    }

    /// <summary>
    /// Resolves the layer name for an optional-content marked-content property
    /// (<c>/OC /MCn BDC</c>): the name is looked up in the resource
    /// <c>/Properties</c> dictionary, whose value is an OCG whose <c>/Name</c> is
    /// the human-readable layer name shown in Illustrator.
    /// </summary>
    private string? ResolveOcgName(Dictionary<string, object?> resources, object? property)
    {
        object? target = property;
        if (property is PdfName key &&
            _file.ResolveDict(resources.GetValueOrDefault("Properties")) is { } properties)
        {
            target = properties.GetValueOrDefault(key.Value);
        }

        if (_file.ResolveDict(target) is { } ocg)
        {
            if (ocg.GetValueOrDefault("Name") is string name && name.Length > 0)
            {
                return name;
            }

            if (ocg.GetValueOrDefault("OCGs") is List<object?> list && list.Count > 0 &&
                _file.ResolveDict(list[0]) is { } first &&
                first.GetValueOrDefault("Name") is string nested && nested.Length > 0)
            {
                return nested;
            }
        }

        return null;
    }

    private byte[] GetContents(Dictionary<string, object?> pageDict)
    {
        object? contents = pageDict.GetValueOrDefault("Contents");
        using var buffer = new MemoryStream();

        void Append(object? reference)
        {
            if (_file.Resolve(reference) is PdfStream stream)
            {
                byte[] data = _file.GetStreamData(stream);
                buffer.Write(data, 0, data.Length);
                buffer.WriteByte((byte)'\n');
            }
        }

        if (_file.Resolve(contents) is List<object?> list)
        {
            foreach (object? entry in list)
            {
                Append(entry);
            }
        }
        else
        {
            Append(contents);
        }

        return buffer.ToArray();
    }

    private Dictionary<string, object?> FindResources(Dictionary<string, object?> dict)
    {
        object? current = dict;
        for (int depth = 0; depth < 32 && current is not null; depth++)
        {
            if (_file.ResolveDict(current) is not { } resolved)
            {
                break;
            }

            if (_file.ResolveDict(resolved.GetValueOrDefault("Resources")) is { } resources)
            {
                return resources;
            }

            current = resolved.GetValueOrDefault("Parent");
        }

        return new Dictionary<string, object?>();
    }

    // ------------------------------------------------------------------

    private void Interpret(byte[] content, Dictionary<string, object?> resources,
        AffineTransform ctm, List<PdfImportedItem> items, int depth, string? layer)
    {
        if (depth > 12 || content.Length == 0)
        {
            return;
        }

        // q/Q save and restore the ENTIRE graphics state (not just the CTM):
        // colour, line width, caps/joins, dash and text state. Illustrator relies
        // on this — e.g. a white fill set inside a q...Q must not leak onto later
        // text that expects the previous (black) fill.
        var stack = new Stack<(AffineTransform Ctm, double LineWidth, int LineCap, int LineJoin,
            double MiterLimit, DashPattern Dash, ColorRgb Stroke, ColorRgb Fill,
            string FontName, double FontSize, double Leading)>();
        AffineTransform current = ctm;
        double lineWidth = 1.0;
        int lineCap = 0;
        int lineJoin = 0;
        double miterLimit = 10.0;
        DashPattern dash = DashPattern.None;
        ColorRgb strokeColor = ColorRgb.Black;
        ColorRgb fillColor = ColorRgb.Black;

        AffineTransform textMatrix = AffineTransform.Identity;
        AffineTransform lineMatrix = AffineTransform.Identity;
        string fontName = string.Empty;
        double fontSize = 12;
        double leading = 0;

        var operands = new List<object?>();
        var subPaths = new List<SubPath>();
        SubPath? currentPath = null;

        // Optional-content (layer) state: BDC/BMC push, EMC pops.
        string? currentLayer = layer;
        var layerStack = new Stack<string?>();

        double Number(int index)
            => index < operands.Count ? ToDouble(operands[index]) : 0.0;

        ColorRgb Color3(int index)
            => new(Number(index), Number(index + 1), Number(index + 2));

        ColorRgb Gray(int index)
        {
            double v = Number(index);
            return new ColorRgb(v, v, v);
        }

        ColorRgb Cmyk(int index)
        {
            double c = Number(index), m = Number(index + 1), y = Number(index + 2), k = Number(index + 3);
            return new ColorRgb((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
        }

        Point2D ToModel(double x, double y)
        {
            Point2D p = current.Transform(new Point2D(x, y));
            return new Point2D(p.X, _pageHeight - p.Y);
        }

        void FlushPath(bool stroke, bool fill)
        {
            if (subPaths.Count > 0 && (stroke || fill))
            {
                var item = new PathItem { Name = "Path" };
                foreach (SubPath sp in subPaths)
                {
                    item.SubPaths.Add(sp);
                }

                item.Fill = fill ? FillSpec.Solid(fillColor) : FillSpec.None;
                item.Stroke = stroke
                    ? new StrokeSpec(true, strokeColor,
                        Math.Max(0.01, lineWidth * ScaleOf(current)), ToCap(lineCap), ToJoin(lineJoin), miterLimit,
                        StrokeAlignment.Center, dash)
                    : StrokeSpec.None;
                items.Add(new PdfImportedItem(currentLayer, item));
            }

            subPaths.Clear();
            currentPath = null;
        }

        var reader = new PdfReader(content, 0);
        while (reader.Position < content.Length)
        {
            reader.SkipWhitespace();
            if (reader.Position >= content.Length)
            {
                break;
            }

            byte c = content[reader.Position];
            if (c is (byte)'/' or (byte)'(' or (byte)'[' or (byte)'<' or (byte)'+'
                or (byte)'-' or (byte)'.' || char.IsDigit((char)c))
            {
                operands.Add(reader.ReadObject(_file));
                continue;
            }

            string op = ReadOperator(ref reader, content);
            if (op.Length == 0)
            {
                reader.Advance();
                continue;
            }

            switch (op)
            {
                case "q":
                    stack.Push((current, lineWidth, lineCap, lineJoin, miterLimit, dash,
                        strokeColor, fillColor, fontName, fontSize, leading));
                    break;
                case "Q":
                    if (stack.Count > 0)
                    {
                        (current, lineWidth, lineCap, lineJoin, miterLimit, dash,
                            strokeColor, fillColor, fontName, fontSize, leading) = stack.Pop();
                    }

                    break;
                case "cm" when operands.Count >= 6:
                    current = current.Compose(new AffineTransform(
                        Number(0), Number(1), Number(2), Number(3), Number(4), Number(5)));
                    break;
                case "w" when operands.Count >= 1:
                    lineWidth = Number(0);
                    break;
                case "J" when operands.Count >= 1:
                    lineCap = (int)Number(0);
                    break;
                case "j" when operands.Count >= 1:
                    lineJoin = (int)Number(0);
                    break;
                case "M" when operands.Count >= 1:
                    miterLimit = Math.Max(1.0, Number(0));
                    break;
                case "d" when operands.Count >= 1:
                {
                    double phase = operands.Count >= 2 ? Number(1) : 0.0;
                    if (operands[0] is List<object?> array && array.Count > 0)
                    {
                        double scale = ScaleOf(current);
                        var segs = new List<double>(array.Count);
                        foreach (object? entry in array)
                        {
                            segs.Add(Math.Max(0.0, ToDouble(entry) * scale));
                        }

                        dash = segs.Any(v => v > 0.0)
                            ? new DashPattern(segs, phase * scale)
                            : DashPattern.None;
                    }
                    else
                    {
                        dash = DashPattern.None;
                    }

                    break;
                }
                case "RG" when operands.Count >= 3:
                    strokeColor = Color3(0);
                    break;
                case "rg" when operands.Count >= 3:
                    fillColor = Color3(0);
                    break;
                case "G" when operands.Count >= 1:
                    strokeColor = Gray(0);
                    break;
                case "g" when operands.Count >= 1:
                    fillColor = Gray(0);
                    break;
                case "K" when operands.Count >= 4:
                    strokeColor = Cmyk(0);
                    break;
                case "k" when operands.Count >= 4:
                    fillColor = Cmyk(0);
                    break;
                case "m" when operands.Count >= 2:
                    currentPath = new SubPath();
                    currentPath.Nodes.Add(new PathNode(ToModel(Number(0), Number(1))));
                    subPaths.Add(currentPath);
                    break;
                case "l" when operands.Count >= 2 && currentPath is not null:
                    currentPath.Nodes.Add(new PathNode(ToModel(Number(0), Number(1))));
                    break;
                case "c" when operands.Count >= 6 && currentPath is not null:
                    AddCubic(currentPath, ToModel(Number(0), Number(1)),
                        ToModel(Number(2), Number(3)), ToModel(Number(4), Number(5)));
                    break;
                case "v" when operands.Count >= 4 && currentPath is not null:
                    AddCubic(currentPath, currentPath.Nodes[^1].Anchor,
                        ToModel(Number(0), Number(1)), ToModel(Number(2), Number(3)));
                    break;
                case "y" when operands.Count >= 4 && currentPath is not null:
                    AddCubic(currentPath, ToModel(Number(0), Number(1)),
                        ToModel(Number(2), Number(3)), ToModel(Number(2), Number(3)));
                    break;
                case "re" when operands.Count >= 4:
                    double x = Number(0), y = Number(1), w = Number(2), h = Number(3);
                    var rect = new SubPath { IsClosed = true };
                    rect.Nodes.Add(new PathNode(ToModel(x, y)));
                    rect.Nodes.Add(new PathNode(ToModel(x + w, y)));
                    rect.Nodes.Add(new PathNode(ToModel(x + w, y + h)));
                    rect.Nodes.Add(new PathNode(ToModel(x, y + h)));
                    subPaths.Add(rect);
                    break;
                case "h" when currentPath is not null:
                    currentPath.IsClosed = true;
                    break;
                case "S":
                case "s":
                    FlushPath(stroke: true, fill: false);
                    break;
                case "f":
                case "F":
                case "f*":
                    FlushPath(stroke: false, fill: true);
                    break;
                case "B":
                case "B*":
                case "b":
                case "b*":
                    FlushPath(stroke: true, fill: true);
                    break;
                case "n":
                    subPaths.Clear();
                    currentPath = null;
                    break;
                case "BDC" when operands.Count >= 2:
                    layerStack.Push(currentLayer);
                    if (operands[0] is PdfName { Value: "OC" })
                    {
                        currentLayer = ResolveOcgName(resources, operands[1]) ?? currentLayer;
                    }

                    break;
                case "BMC" when operands.Count >= 1:
                    layerStack.Push(currentLayer);
                    break;
                case "EMC":
                    if (layerStack.Count > 0)
                    {
                        currentLayer = layerStack.Pop();
                    }

                    break;
                case "BT":
                    textMatrix = AffineTransform.Identity;
                    lineMatrix = AffineTransform.Identity;
                    break;
                case "Tf" when operands.Count >= 2:
                    fontName = operands[0] is PdfName pn ? pn.Value : string.Empty;
                    fontSize = Number(1);
                    break;
                case "TL" when operands.Count >= 1:
                    leading = Number(0);
                    break;
                case "Td" when operands.Count >= 2:
                    lineMatrix = lineMatrix.Compose(AffineTransform.CreateTranslation(Number(0), Number(1)));
                    textMatrix = lineMatrix;
                    break;
                case "TD" when operands.Count >= 2:
                    leading = -Number(1);
                    lineMatrix = lineMatrix.Compose(AffineTransform.CreateTranslation(Number(0), Number(1)));
                    textMatrix = lineMatrix;
                    break;
                case "Tm" when operands.Count >= 6:
                    textMatrix = new AffineTransform(
                        Number(0), Number(1), Number(2), Number(3), Number(4), Number(5));
                    lineMatrix = textMatrix;
                    break;
                case "T*":
                    lineMatrix = lineMatrix.Compose(AffineTransform.CreateTranslation(0, -leading));
                    textMatrix = lineMatrix;
                    break;
                case "Tj" when operands.Count >= 1 && operands[0] is string text:
                    ShowText(text, resources, fontName, fontSize, current, textMatrix, fillColor, items, currentLayer);
                    break;
                case "TJ" when operands.Count >= 1 && operands[0] is List<object?> array:
                    var sb = new StringBuilder();
                    foreach (object? element in array)
                    {
                        if (element is string s)
                        {
                            sb.Append(s);
                        }
                    }

                    ShowText(sb.ToString(), resources, fontName, fontSize, current, textMatrix, fillColor, items, currentLayer);
                    break;
                case "Do" when operands.Count >= 1 && operands[0] is PdfName xname:
                    DrawXObject(resources, xname.Value, current, items, depth, currentLayer);
                    break;
            }

            operands.Clear();
        }
    }

    private void DrawXObject(Dictionary<string, object?> resources, string name,
        AffineTransform ctm, List<PdfImportedItem> items, int depth, string? layer)
    {
        if (_file.ResolveDict(resources.GetValueOrDefault("XObject")) is not { } xobjects ||
            _file.Resolve(xobjects.GetValueOrDefault(name)) is not PdfStream stream ||
            _file.ResolveDict(stream.Dict) is not { } dict ||
            dict.GetValueOrDefault("Subtype") is not PdfName { Value: "Form" })
        {
            return;
        }

        AffineTransform matrix = AffineTransform.Identity;
        if (_file.Resolve(dict.GetValueOrDefault("Matrix")) is List<object?> m && m.Count >= 6)
        {
            matrix = new AffineTransform(
                ToDouble(m[0]), ToDouble(m[1]), ToDouble(m[2]),
                ToDouble(m[3]), ToDouble(m[4]), ToDouble(m[5]));
        }

        Dictionary<string, object?> childResources =
            _file.ResolveDict(dict.GetValueOrDefault("Resources")) ?? resources;

        Interpret(_file.GetStreamData(stream), childResources, ctm.Compose(matrix), items, depth + 1, layer);
    }

    private void ShowText(string text, Dictionary<string, object?> resources, string fontName,
        double fontSize, AffineTransform ctm, AffineTransform textMatrix, ColorRgb color,
        List<PdfImportedItem> items, string? layer)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        (string family, bool bold, bool italic, double ascent) = MapFont(fontName, resources);

        // The glyph size is the Tf size scaled by the text matrix (and any enclosing
        // CTM). Illustrator typically writes "/F1 1 Tf" with the real size in Tm, so
        // ignoring the matrix scale renders everything at 1pt.
        AffineTransform matrix = ctm.Compose(textMatrix);
        double scale = Math.Sqrt((matrix.A * matrix.A) + (matrix.B * matrix.B));
        if (scale <= 0)
        {
            scale = 1.0;
        }

        double effectiveSize = fontSize * scale;
        double rotation = -Math.Atan2(matrix.B, matrix.A);

        // PDF origins sit on the baseline; the model stores the block's top-left,
        // which is one ascent up the text's *own* up axis (the matrix's second
        // column, y-flipped). Applying it straight down breaks rotated labels.
        Point2D baseline = matrix.Transform(new Point2D(0, 0));
        double ascentPoints = ascent * effectiveSize;
        double upX = matrix.C / scale;
        double upY = -matrix.D / scale;
        var item = new TextItem
        {
            Name = "Text",
            Origin = new Point2D(
                baseline.X + (ascentPoints * upX),
                (_pageHeight - baseline.Y) + (ascentPoints * upY)),
            Color = color,
            RotationRadians = rotation,
        };
        var run = new TextRun
        {
            Text = text,
            FontFamily = family,
            FontSize = effectiveSize,
            Bold = bold,
            Italic = italic,
            AdvanceWidth = MeasureAdvance(text, fontName, resources, effectiveSize),
        };
        item.Runs.Add(run);
        items.Add(new PdfImportedItem(layer, item));
    }

    /// <summary>
    /// Sums the PDF font's glyph widths for <paramref name="text"/> so the
    /// substituted font can be scaled to the original advance (keeps imported
    /// layout from reflowing under a wider fallback font). Returns null when the
    /// font exposes no simple /Widths array (e.g. CID fonts).
    /// </summary>
    private double? MeasureAdvance(string text, string fontName,
        Dictionary<string, object?> resources, double effectiveSize)
    {
        if (_file.ResolveDict(resources.GetValueOrDefault("Font")) is not { } fonts ||
            _file.ResolveDict(fonts.GetValueOrDefault(fontName)) is not { } fontDict ||
            _file.Resolve(fontDict.GetValueOrDefault("Widths")) is not List<object?> widths)
        {
            return null;
        }

        double missing = _file.ResolveNumber(fontDict.GetValueOrDefault("MissingWidth")) ?? 0;
        int first = (int)(_file.ResolveNumber(fontDict.GetValueOrDefault("FirstChar")) ?? 0);
        double total = 0;
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                continue;
            }

            int index = ch - first;
            double width = index >= 0 && index < widths.Count
                ? ToDouble(_file.Resolve(widths[index]))
                : missing;
            total += width;
        }

        return total / 1000.0 * effectiveSize;
    }

    private (string Family, bool Bold, bool Italic, double Ascent) MapFont(
        string fontName, Dictionary<string, object?> resources)
    {
        string baseFont = fontName;
        if (_file.ResolveDict(resources.GetValueOrDefault("Font")) is { } fonts &&
            _file.ResolveDict(fonts.GetValueOrDefault(fontName)) is { } fontDict &&
            fontDict.GetValueOrDefault("BaseFont") is PdfName bf)
        {
            baseFont = bf.Value;
        }

        bool bold = baseFont.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
                    baseFont.Contains("Demi", StringComparison.OrdinalIgnoreCase) ||
                    baseFont.Contains("Black", StringComparison.OrdinalIgnoreCase);
        bool italic = baseFont.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
                      baseFont.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

        string family = baseFont.Contains("Mono", StringComparison.OrdinalIgnoreCase)
            ? "DejaVu Sans Mono"
            : baseFont.Contains("Serif", StringComparison.OrdinalIgnoreCase) ||
              baseFont.Contains("Times", StringComparison.OrdinalIgnoreCase) ||
              baseFont.Contains("Garamond", StringComparison.OrdinalIgnoreCase) ||
              baseFont.Contains("Annai", StringComparison.OrdinalIgnoreCase)
                ? "DejaVu Serif"
                : "DejaVu Sans";

        // The origin lift must use the ascent of the font we will actually
        // render with (the substituted bundled font), not the source font's
        // descriptor — otherwise the baseline is misplaced.
        double ascent = 0.8;
        TrueTypeFont bundled = BundledFonts.Resolve(family, bold: false, italic: false);
        if (bundled.UnitsPerEm > 0)
        {
            ascent = (double)bundled.Ascender / bundled.UnitsPerEm;
        }

        return (family, bold, italic, ascent);
    }

    private static void AddCubic(SubPath path, Point2D c1, Point2D c2, Point2D end)
    {
        path.Nodes[^1].OutHandle = c1;
        path.Nodes.Add(new PathNode(end, c2, end));
    }

    private static string ReadOperator(ref PdfReader reader, byte[] content)
    {
        var sb = new StringBuilder();
        while (reader.Position < content.Length)
        {
            byte c = content[reader.Position];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f')
            {
                break;
            }

            if (c is (byte)'/' or (byte)'(' or (byte)'[' or (byte)'<' || char.IsDigit((char)c) ||
                c is (byte)'+' or (byte)'-' or (byte)'.')
            {
                break;
            }

            sb.Append((char)c);
            reader.Advance();
        }

        return sb.ToString();
    }

    private static StrokeCap ToCap(int value) => value switch
    {
        1 => StrokeCap.Round,
        2 => StrokeCap.Square,
        _ => StrokeCap.Butt,
    };

    private static StrokeJoin ToJoin(int value) => value switch
    {
        1 => StrokeJoin.Round,
        2 => StrokeJoin.Bevel,
        _ => StrokeJoin.Miter,
    };

    private static double ToDouble(object? value) => value switch
    {
        double d => d,
        long l => l,
        int i => i,
        _ => 0.0,
    };

    private static double ScaleOf(AffineTransform transform)
        => Math.Sqrt(Math.Abs(transform.A * transform.D - transform.C * transform.B));
}
