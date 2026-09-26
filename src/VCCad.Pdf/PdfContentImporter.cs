using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

    // Font resource object → code→Unicode map from its /ToUnicode CMap.
    private readonly Dictionary<object, Dictionary<int, string>> _toUnicodeCache = new();

    public PdfContentImporter(PdfFile file, double pageHeight)
    {
        _file = file;
        _pageHeight = pageHeight;
    }

    public List<PdfImportedItem> ParsePage(Dictionary<string, object?> pageDict)
    {
        var items = new List<PdfImportedItem>();
        byte[] content = GetContents(pageDict);
        Interpret(content, FindResources(pageDict), AffineTransform.Identity, items, 0, null);
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
            object? StrokeSpace, object? FillSpace, double StrokeAlpha, double FillAlpha,
            string FontName, double FontSize, double Leading)>();
        AffineTransform current = ctm;
        double lineWidth = 1.0;
        int lineCap = 0;
        int lineJoin = 0;
        double miterLimit = 10.0;
        DashPattern dash = DashPattern.None;
        ColorRgb strokeColor = ColorRgb.Black;
        ColorRgb fillColor = ColorRgb.Black;
        object? strokeSpace = null;
        object? fillSpace = null;
        double strokeAlpha = 1.0;
        double fillAlpha = 1.0;

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

                ColorRgb fillRgb = fillColor;
                ColorRgb penRgb = strokeColor;
                item.Fill = fill
                    ? FillSpec.Solid(new ColorRgb(fillRgb.R, fillRgb.G, fillRgb.B, fillAlpha))
                    : FillSpec.None;
                item.Stroke = stroke
                    ? new StrokeSpec(true, new ColorRgb(penRgb.R, penRgb.G, penRgb.B, strokeAlpha),
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
                        strokeColor, fillColor, strokeSpace, fillSpace, strokeAlpha, fillAlpha,
                        fontName, fontSize, leading));
                    break;
                case "Q":
                    if (stack.Count > 0)
                    {
                        (current, lineWidth, lineCap, lineJoin, miterLimit, dash,
                            strokeColor, fillColor, strokeSpace, fillSpace, strokeAlpha, fillAlpha,
                            fontName, fontSize, leading) = stack.Pop();
                    }

                    break;
                case "cm" when operands.Count >= 6:
                    current = current.Compose(new AffineTransform(
                        Number(0), Number(1), Number(2), Number(3), Number(4), Number(5)));
                    break;
                case "w" when operands.Count >= 1:
                    lineWidth = Number(0);
                    break;
                case "cs" when operands.Count >= 1:
                    fillSpace = operands[0];
                    break;
                case "CS" when operands.Count >= 1:
                    strokeSpace = operands[0];
                    break;
                case "sc":
                case "scn":
                {
                    var comps = NumericOperands(operands);
                    if (comps.Count > 0)
                    {
                        fillColor = ResolveColor(fillSpace, comps, resources);
                    }

                    break;
                }
                case "SC":
                case "SCN":
                {
                    var comps = NumericOperands(operands);
                    if (comps.Count > 0)
                    {
                        strokeColor = ResolveColor(strokeSpace, comps, resources);
                    }

                    break;
                }
                case "gs" when operands.Count >= 1 && operands[0] is PdfName gsName:
                {
                    if (_file.ResolveDict(resources.GetValueOrDefault("ExtGState")) is { } gsDict &&
                        _file.ResolveDict(gsDict.GetValueOrDefault(gsName.Value)) is { } gsState)
                    {
                        if (_file.ResolveNumber(gsState.GetValueOrDefault("ca")) is double ca)
                        {
                            fillAlpha = Math.Clamp(ca, 0.0, 1.0);
                        }

                        if (_file.ResolveNumber(gsState.GetValueOrDefault("CA")) is double caStroke)
                        {
                            strokeAlpha = Math.Clamp(caStroke, 0.0, 1.0);
                        }
                    }

                    break;
                }
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
                    strokeSpace = DeviceRgb;
                    break;
                case "rg" when operands.Count >= 3:
                    fillColor = Color3(0);
                    fillSpace = DeviceRgb;
                    break;
                case "G" when operands.Count >= 1:
                    strokeColor = Gray(0);
                    strokeSpace = DeviceGray;
                    break;
                case "g" when operands.Count >= 1:
                    fillColor = Gray(0);
                    fillSpace = DeviceGray;
                    break;
                case "K" when operands.Count >= 4:
                    strokeColor = Cmyk(0);
                    strokeSpace = DeviceCmyk;
                    break;
                case "k" when operands.Count >= 4:
                    fillColor = Cmyk(0);
                    fillSpace = DeviceCmyk;
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
                case "'" when operands.Count >= 1 && operands[0] is string sq:
                    lineMatrix = lineMatrix.Compose(AffineTransform.CreateTranslation(0, -leading));
                    textMatrix = lineMatrix;
                    ShowText(sq, resources, fontName, fontSize, current, textMatrix, fillColor, items, currentLayer);
                    break;
                case "\"" when operands.Count >= 3 && operands[2] is string dq:
                    lineMatrix = lineMatrix.Compose(AffineTransform.CreateTranslation(0, -leading));
                    textMatrix = lineMatrix;
                    ShowText(dq, resources, fontName, fontSize, current, textMatrix, fillColor, items, currentLayer);
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

        // Text operands carry glyph codes, not characters. Decode via the font's
        // /ToUnicode CMap (or fall back to Latin-1 for unencoded simple fonts).
        bool composite = IsCompositeFont(fontName, resources);
        string rawText = text;
        string decoded = DecodeText(rawText, ToUnicodeMap(fontName, resources), composite);
        if (decoded.Length == 0)
        {
            decoded = rawText;
        }

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
            Text = decoded,
            FontFamily = family,
            FontSize = effectiveSize,
            Bold = bold,
            Italic = italic,
            AdvanceWidth = MeasureAdvance(rawText, fontName, resources, effectiveSize),
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

    private Dictionary<string, object?>? FontDict(string fontName, Dictionary<string, object?> resources)
        => _file.ResolveDict(resources.GetValueOrDefault("Font")) is { } fonts
            ? _file.ResolveDict(fonts.GetValueOrDefault(fontName))
            : null;

    private bool IsCompositeFont(string fontName, Dictionary<string, object?> resources)
        => FontDict(fontName, resources)?.GetValueOrDefault("Subtype") is PdfName { Value: "Type0" };

    private Dictionary<int, string>? ToUnicodeMap(string fontName, Dictionary<string, object?> resources)
    {
        Dictionary<string, object?>? font = FontDict(fontName, resources);
        if (font is null)
        {
            return null;
        }

        if (_toUnicodeCache.TryGetValue(font, out Dictionary<int, string>? cached))
        {
            return cached;
        }

        Dictionary<int, string>? map = null;
        if (_file.Resolve(font.GetValueOrDefault("ToUnicode")) is PdfStream stream)
        {
            map = ParseToUnicode(_file.GetStreamData(stream));
        }

        _toUnicodeCache[font] = map;
        return map;
    }

    private static string DecodeText(string raw, Dictionary<int, string>? map, bool twoByte)
    {
        var sb = new StringBuilder();
        if (twoByte)
        {
            for (int i = 0; i + 1 < raw.Length; i += 2)
            {
                int code = (raw[i] << 8) | raw[i + 1];
                if (map is not null && map.TryGetValue(code, out string? mapped))
                {
                    sb.Append(mapped);
                }
                else if (code is >= 32 and < 127)
                {
                    sb.Append((char)code);
                }
            }
        }
        else
        {
            foreach (char ch in raw)
            {
                int code = ch & 0xFF;
                if (map is not null && map.TryGetValue(code, out string? mapped))
                {
                    sb.Append(mapped);
                }
                else
                {
                    sb.Append(ch);
                }
            }
        }

        return sb.ToString();
    }

    private static Dictionary<int, string> ParseToUnicode(byte[] data)
    {
        string text = Encoding.Latin1.GetString(data);
        var map = new Dictionary<int, string>();

        foreach (Match block in Regex.Matches(text, "beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
        {
            foreach (Match pair in Regex.Matches(block.Groups[1].Value, "<([0-9A-Fa-f]+)>\\s*<([0-9A-Fa-f]+)>"))
            {
                map[HexToInt(pair.Groups[1].Value)] = HexToUnicode(pair.Groups[2].Value);
            }
        }

        foreach (Match block in Regex.Matches(text, "beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
        {
            string body = block.Groups[1].Value;
            foreach (Match tri in Regex.Matches(body, "<([0-9A-Fa-f]+)>\\s*<([0-9A-Fa-f]+)>\\s*<([0-9A-Fa-f]+)>"))
            {
                int lo = HexToInt(tri.Groups[1].Value);
                int hi = HexToInt(tri.Groups[2].Value);
                int baseCode = HexToInt(tri.Groups[3].Value);
                for (int c = lo; c <= hi && c - lo < 65536; c++)
                {
                    try
                    {
                        map[c] = char.ConvertFromUtf32(baseCode + (c - lo));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        break;
                    }
                }
            }

            foreach (Match arr in Regex.Matches(body, "<([0-9A-Fa-f]+)>\\s*<([0-9A-Fa-f]+)>\\s*\\[(.*?)\\]", RegexOptions.Singleline))
            {
                int lo = HexToInt(arr.Groups[1].Value);
                MatchCollection items = Regex.Matches(arr.Groups[3].Value, "<([0-9A-Fa-f]+)>");
                for (int i = 0; i < items.Count; i++)
                {
                    map[lo + i] = HexToUnicode(items[i].Groups[1].Value);
                }
            }
        }

        return map;
    }

    private static int HexToInt(string hex)
        => int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static string HexToUnicode(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            hex = "0" + hex;
        }

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)HexToInt(hex.Substring(i * 2, 2));
        }

        return bytes.Length % 2 == 0 && bytes.Length > 0
            ? Encoding.BigEndianUnicode.GetString(bytes)
            : Encoding.Latin1.GetString(bytes);
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


    private static readonly PdfName DeviceGray = new("DeviceGray");
    private static readonly PdfName DeviceRgb = new("DeviceRGB");
    private static readonly PdfName DeviceCmyk = new("DeviceCMYK");

    private static List<double> NumericOperands(List<object?> operands)
    {
        var result = new List<double>(operands.Count);
        foreach (object? o in operands)
        {
            if (o is double d)
            {
                result.Add(d);
            }
            else if (o is long l)
            {
                result.Add(l);
            }
        }

        return result;
    }

    /// <summary>Resolves a colour space operand to a concrete space: a device
    /// name or an array whose first element names the family.</summary>
    private object? ResolveSpace(object? space, Dictionary<string, object?> resources)
    {
        if (space is PdfName name)
        {
            if (_file.ResolveDict(resources.GetValueOrDefault("ColorSpace")) is { } spaces &&
                spaces.GetValueOrDefault(name.Value) is { } mapped)
            {
                return ResolveSpace(mapped, resources);
            }

            return space;
        }

        return _file.Resolve(space);
    }

    /// <summary>Converts colour components in <paramref name="space"/> to RGB.</summary>
    private ColorRgb ResolveColor(object? space, IReadOnlyList<double> comps,
        Dictionary<string, object?> resources)
    {
        double C(int i) => i < comps.Count ? comps[i] : 0.0;

        space = ResolveSpace(space, resources);

        if (space is PdfName n)
        {
            return n.Value switch
            {
                "DeviceGray" or "G" => new ColorRgb(C(0), C(0), C(0)),
                "DeviceRGB" or "RGB" => new ColorRgb(C(0), C(1), C(2)),
                "DeviceCMYK" or "CMYK" => CmykToRgb(C(0), C(1), C(2), C(3)),
                _ => ColorRgb.Black,
            };
        }

        if (space is List<object?> arr && arr.Count > 0 && arr[0] is PdfName kind)
        {
            switch (kind.Value)
            {
                case "ICCBased":
                {
                    var profile = _file.ResolveDict(arr.Count > 1 ? arr[1] : null);
                    int count = (int)(_file.ResolveNumber(profile?.GetValueOrDefault("N")) ?? 3);
                    if (profile?.GetValueOrDefault("Alternate") is { } alternate)
                    {
                        return ResolveColor(alternate, comps, resources);
                    }

                    return count switch
                    {
                        1 => new ColorRgb(C(0), C(0), C(0)),
                        4 => CmykToRgb(C(0), C(1), C(2), C(3)),
                        _ => new ColorRgb(C(0), C(1), C(2)),
                    };
                }

                case "Indexed":
                case "I":
                {
                    object? baseSpace = ResolveSpace(arr.Count > 1 ? arr[1] : null, resources);
                    int components = ComponentCount(baseSpace, resources);
                    double[] table = LookupBytes(arr.Count > 3 ? arr[3] : null);
                    int index = Math.Clamp((int)Math.Round(C(0)), 0, Math.Max(0, table.Length / Math.Max(1, components) - 1));
                    var baseComps = new double[components];
                    for (int i = 0; i < components; i++)
                    {
                        int at = (index * components) + i;
                        baseComps[i] = at < table.Length ? table[at] : 0.0;
                    }

                    return ResolveColor(baseSpace, baseComps, resources);
                }

                case "Separation":
                {
                    double[] tinted = ApplyFunction(arr.Count > 3 ? arr[3] : null, new[] { C(0) }, resources);
                    return ResolveColor(arr.Count > 2 ? arr[2] : null, tinted, resources);
                }

                case "DeviceN":
                {
                    var names = _file.Resolve(arr.Count > 1 ? arr[1] : null) as List<object?>
                                ?? new List<object?>();
                    var inputs = new double[names.Count];
                    for (int i = 0; i < names.Count; i++)
                    {
                        inputs[i] = C(i);
                    }

                    double[] tinted = ApplyFunction(arr.Count > 3 ? arr[3] : null, inputs, resources);
                    return ResolveColor(arr.Count > 2 ? arr[2] : null, tinted, resources);
                }

                case "CalGray":
                    return new ColorRgb(C(0), C(0), C(0));
                case "CalRGB":
                    return new ColorRgb(C(0), C(1), C(2));
                case "Lab":
                    return LabToRgb(C(0), C(1), C(2));
                case "Pattern":
                    return ColorRgb.Black;
            }
        }

        // Unknown/absent space: infer from the component count (postScript default).
        return comps.Count switch
        {
            1 => new ColorRgb(C(0), C(0), C(0)),
            4 => CmykToRgb(C(0), C(1), C(2), C(3)),
            _ => new ColorRgb(C(0), C(1), C(2)),
        };
    }

    private static ColorRgb CmykToRgb(double c, double m, double y, double k)
        => new((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));

    private int ComponentCount(object? space, Dictionary<string, object?> resources)
    {
        space = ResolveSpace(space, resources);
        if (space is PdfName n)
        {
            return n.Value switch
            {
                "DeviceGray" or "G" => 1,
                "DeviceCMYK" or "CMYK" => 4,
                _ => 3,
            };
        }

        if (space is List<object?> arr && arr.Count > 0 && arr[0] is PdfName kind)
        {
            switch (kind.Value)
            {
                case "ICCBased":
                    return (int)(_file.ResolveNumber(
                        _file.ResolveDict(arr.Count > 1 ? arr[1] : null)?.GetValueOrDefault("N")) ?? 3);
                case "Separation":
                    return 1;
                case "DeviceN":
                    return (_file.Resolve(arr.Count > 1 ? arr[1] : null) as List<object?>)?.Count ?? 1;
                case "CalGray":
                    return 1;
                case "Indexed":
                case "I":
                    return 1;
                default:
                    return 3;
            }
        }

        return 3;
    }

    private double[] LookupBytes(object? lookup)
    {
        if (lookup is string text)
        {
            byte[] bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                bytes[i] = (byte)(text[i] & 0xFF);
            }

            return Array.ConvertAll(bytes, b => b / 255.0);
        }

        if (_file.Resolve(lookup) is PdfStream stream)
        {
            return Array.ConvertAll(_file.GetStreamData(stream), b => b / 255.0);
        }

        return Array.Empty<double>();
    }

    /// <summary>Evaluates a PDF function (type 2, and type 0/4 best-effort) for one
    /// input, returning its outputs. Used for Separation/DeviceN tint transforms.</summary>
    private double[] ApplyFunction(object? function, IReadOnlyList<double> inputs,
        Dictionary<string, object?> resources)
    {
        var dict = _file.ResolveDict(function);
        if (dict is null)
        {
            return inputs.ToArray();
        }

        int type = (int)(_file.ResolveNumber(dict.GetValueOrDefault("FunctionType")) ?? 4);
        double x = inputs.Count > 0 ? inputs[0] : 0.0;

        if (type == 2)
        {
            double[] c0 = ReadNumbers(dict.GetValueOrDefault("C0")) ?? new[] { 0.0 };
            double[] c1 = ReadNumbers(dict.GetValueOrDefault("C1")) ?? new[] { 1.0 };
            double exponent = _file.ResolveNumber(dict.GetValueOrDefault("N")) ?? 1.0;
            var output = new double[c0.Length];
            double t = x <= 0 ? 0 : Math.Pow(x, exponent);
            for (int i = 0; i < output.Length; i++)
            {
                double a = i < c0.Length ? c0[i] : 0.0;
                double b = i < c1.Length ? c1[i] : 1.0;
                output[i] = a + ((b - a) * t);
            }

            return output;
        }

        if (type == 0)
        {
            // Sampled function: best effort — return the first sample scaled.
            object? range = dict.GetValueOrDefault("Range");
            double[]? r = ReadNumbers(range);
            if (r is { Length: >= 2 })
            {
                return new[] { r[0] + (x * (r[1] - r[0])) };
            }
        }

        return inputs.ToArray();
    }

    private double[]? ReadNumbers(object? array)
    {
        if (_file.Resolve(array) is not List<object?> list)
        {
            return null;
        }

        var result = new double[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            result[i] = ToDouble(_file.Resolve(list[i]));
        }

        return result;
    }

    private static ColorRgb LabToRgb(double l, double a, double b)
    {
        // ISO 32000-1 default Lab range: L* in [0,100], a*,b* in [-100,100].
        double fy = (l + 16.0) / 116.0;
        double fx = fy + (a / 500.0);
        double fz = fy - (b / 200.0);

        static double F(double t) => t > 6.0 / 29.0
            ? t * t * t
            : (t - 4.0 / 29.0) * (108.0 / 841.0);

        double x = 0.9505 * F(fx);
        double y = 1.0000 * F(fy);
        double z = 1.0890 * F(fz);

        double r = (3.2406 * x) - (1.5372 * y) - (0.4986 * z);
        double g = (-0.9689 * x) + (1.8758 * y) + (0.0415 * z);
        double bl = (0.0557 * x) - (0.2040 * y) + (1.0570 * z);

        static double Gamma(double v) => v <= 0.0031308
            ? 12.92 * v
            : (1.055 * Math.Pow(Math.Max(0, v), 1.0 / 2.4)) - 0.055;

        return new ColorRgb(
            Math.Clamp(Gamma(r), 0.0, 1.0),
            Math.Clamp(Gamma(g), 0.0, 1.0),
            Math.Clamp(Gamma(bl), 0.0, 1.0));
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
