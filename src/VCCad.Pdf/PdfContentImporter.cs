using System.Globalization;
using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf.Parsing;

namespace VCCad.Pdf;

/// <summary>
/// Interprets PDF page content streams into VCCad model objects: paths (line and
/// cubic segments with fills/strokes) and text runs. Handles the graphics state
/// (q/Q/cm/w/colours), path construction and painting, Form XObjects and basic
/// text state. Coordinates are converted from PDF user space (bottom-left origin)
/// into the artboard-local top-left frame.
/// </summary>
internal sealed class PdfContentImporter
{
    private readonly PdfFile _file;
    private readonly double _pageHeight;

    public PdfContentImporter(PdfFile file, double pageHeight)
    {
        _file = file;
        _pageHeight = pageHeight;
    }

    public List<LayerItem> ParsePage(Dictionary<string, object?> pageDict)
    {
        var items = new List<LayerItem>();
        Interpret(GetContents(pageDict), FindResources(pageDict), AffineTransform.Identity, items, 0);
        return items;
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
        AffineTransform ctm, List<LayerItem> items, int depth)
    {
        if (depth > 12 || content.Length == 0)
        {
            return;
        }

        var stack = new Stack<AffineTransform>();
        AffineTransform current = ctm;
        double lineWidth = 1.0;
        int lineCap = 0;
        int lineJoin = 0;
        double miterLimit = 10.0;
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
                        Math.Max(0.01, lineWidth * ScaleOf(current)), ToCap(lineCap), ToJoin(lineJoin), miterLimit)
                    : StrokeSpec.None;
                items.Add(item);
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
                    stack.Push(current);
                    break;
                case "Q":
                    if (stack.Count > 0)
                    {
                        current = stack.Pop();
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
                    ShowText(text, resources, fontName, fontSize, current, textMatrix, fillColor, items);
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

                    ShowText(sb.ToString(), resources, fontName, fontSize, current, textMatrix, fillColor, items);
                    break;
                case "Do" when operands.Count >= 1 && operands[0] is PdfName xname:
                    DrawXObject(resources, xname.Value, current, items, depth);
                    break;
            }

            operands.Clear();
        }
    }

    private void DrawXObject(Dictionary<string, object?> resources, string name,
        AffineTransform ctm, List<LayerItem> items, int depth)
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

        Interpret(_file.GetStreamData(stream), childResources, ctm.Compose(matrix), items, depth + 1);
    }

    private void ShowText(string text, Dictionary<string, object?> resources, string fontName,
        double fontSize, AffineTransform ctm, AffineTransform textMatrix, ColorRgb color,
        List<LayerItem> items)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        (string family, bool bold, bool italic) = MapFont(fontName, resources);
        Point2D origin = ctm.Compose(textMatrix).Transform(new Point2D(0, 0));
        var item = new TextItem
        {
            Name = "Text",
            Origin = new Point2D(origin.X, _pageHeight - origin.Y),
            Color = color,
        };
        item.Runs.Add(new TextRun { Text = text, FontFamily = family, FontSize = fontSize, Bold = bold, Italic = italic });
        items.Add(item);
    }

    private (string Family, bool Bold, bool Italic) MapFont(string fontName, Dictionary<string, object?> resources)
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

        return (family, bold, italic);
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
