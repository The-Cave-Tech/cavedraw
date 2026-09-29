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
    private readonly double _pageWidth;

    // Font resource object → code→Unicode map from its /ToUnicode CMap.
    private readonly Dictionary<object, Dictionary<int, string>> _toUnicodeCache = new();

    // Font resource object → extracted embedded programme (pass-through).
    private readonly Dictionary<object, EmbeddedFont?> _embeddedFontCache = new();
    private readonly Dictionary<Dictionary<string, object?>, Dictionary<int, string>?> _encodingCache = new();

    public PdfContentImporter(PdfFile file, double pageHeight, double pageWidth = 0,
        List<string>? notes = null)
    {
        _file = file;
        _pageHeight = pageHeight;
        _pageWidth = pageWidth;
        Notes = notes ?? new List<string>();
    }

    /// <summary>
    /// Lossy approximations this import had to make — an unsupported shading type
    /// approximated by the closest supported one, a radial inner radius the model cannot
    /// express. Reported rather than dropped silently.
    /// </summary>
    public List<string> Notes { get; }

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
            string FontName, double FontSize, double Leading, double[]? FillCmyk, double[]? StrokeCmyk,
            double CharSpacing, double WordSpacing, double HorizontalScale, double Rise)>();
        AffineTransform current = ctm;
        double lineWidth = 1.0;
        int lineCap = 0;
        int lineJoin = 0;
        double miterLimit = 10.0;
        DashPattern dash = DashPattern.None;
        ColorRgb strokeColor = ColorRgb.Black;
        ColorRgb fillColor = ColorRgb.Black;

        // The ink values behind those RGB colours, when the file painted with DeviceCMYK.
        // Kept alongside rather than recomputed later, because the conversion cannot be
        // undone — see PathItem.SourceFillCmyk.
        double[]? fillCmyk = null;
        double[]? strokeCmyk = null;
        object? strokeSpace = null;
        object? fillSpace = null;
        double strokeAlpha = 1.0;
        double fillAlpha = 1.0;

        // Text state. None of this is decoration: spacing and horizontal scale change how
        // wide the text is, and rise moves it off the baseline, so a run measured without
        // them is a run the file did not draw. They are part of the graphics state, so
        // q/Q save and restore them along with everything else.
        double charSpacing = 0.0;
        double wordSpacing = 0.0;
        double horizontalScale = 1.0;
        double rise = 0.0;

        // "W" marks the path just built as the clip rather than as paint; the clip only
        // takes effect at the next painting operator, which is usually "n".
        _pendingClip = false;

        AffineTransform textMatrix = AffineTransform.Identity;
        AffineTransform lineMatrix = AffineTransform.Identity;
        string fontName = string.Empty;
        double fontSize = 12;
        double leading = 0;

        var operands = new List<object?>();
        var subPaths = new List<SubPath>();

        /// <summary>
        /// Moves the text matrix on by the width of what was just shown, which is what a
        /// viewer does after every show operation.
        ///
        /// Without it a line drawn as several show operations — the usual shape when the
        /// font changes mid-line, for a trademark symbol or a product name — put every
        /// piece back at the line's start. The Transparency Guide's page 2 laid its legal
        /// notice out that way: 'Adobe, the Adobe logo, ' and 'Illustrator' arrived at the
        /// same origin, and reading the page back gave the two lines interleaved.
        /// </summary>
        void AdvanceTextMatrixBy(double textSpaceWidth)
        {
            if (Math.Abs(textSpaceWidth) > 1e-9)
            {
                textMatrix = textMatrix.Compose(AffineTransform.CreateTranslation(textSpaceWidth, 0));
            }
        }

        void AdvanceTextMatrix(string shown) => AdvanceTextMatrixBy(MeasureAdvance(
            shown, fontName, resources, fontSize,
            new TextState(charSpacing, wordSpacing, horizontalScale, rise),
            IsCompositeFont(fontName, resources)) ?? 0);

        void TakeClip()
        {
            if (!_pendingClip)
            {
                return;
            }

            _pendingClip = false;
            if (subPaths.Count == 0)
            {
                return;
            }

            var clip = new ClipSpec { Rule = _pendingClipRule };
            foreach (SubPath sub in subPaths)
            {
                clip.SubPaths.Add(sub.Clone());
            }

            _clips.Add(clip);
            _clipJustTaken = true;
        }

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

        /// <summary>
        /// Paints the path built so far. Geometry is stored exactly as the file draws
        /// it: a tiled PDF's overflow past the page edge is a rendering concern (the
        /// page box clips it), not something to bake into the document.
        /// </summary>
        void FlushPath(bool stroke, bool fill, FillRule rule = FillRule.NonZero)
        {
            // A path marked by "W" is the clip, not paint, so it is taken before the
            // subpaths are consumed by painting.
            TakeClip();

            if (subPaths.Count > 0 && (stroke || fill))
            {
                // A fill implicitly closes every open subpath (ISO 32000-1 §8.5.3.3);
                // a stroke leaves them open. This is not a detail: outline exporters
                // routinely write a glyph contour as "m ... c c c f" with no "h", and an
                // open figure does not fill, so the letter disappears entirely. That is
                // why the round glyphs - O, S, C, 0, c - were missing while glyphs whose
                // contours happened to carry an "h" survived.
                if (fill)
                {
                    foreach (SubPath sub in subPaths)
                    {
                        sub.IsClosed = true;
                    }
                }

                var item = new PathItem { Name = "Path" };
                foreach (SubPath sp in subPaths)
                {
                    item.SubPaths.Add(sp);
                }

                ColorRgb fillRgb = fillColor;
                ColorRgb penRgb = strokeColor;
                item.Fill = fill
                    ? FillSpec.Solid(new ColorRgb(fillRgb.R, fillRgb.G, fillRgb.B, fillAlpha)) with { Rule = rule }
                    : FillSpec.None;
                item.Stroke = stroke
                    ? new StrokeSpec(true, new ColorRgb(penRgb.R, penRgb.G, penRgb.B, strokeAlpha),
                        Math.Max(0.01, lineWidth * ScaleOf(current)), ToCap(lineCap), ToJoin(lineJoin), miterLimit,
                        StrokeAlignment.Center, dash)
                    : StrokeSpec.None;

                // The ink values the file used, kept so export can paint with the same
                // colour rather than a converted approximation of it.
                item.SourceFillCmyk = fill ? fillCmyk : null;
                item.SourceStrokeCmyk = stroke ? strokeCmyk : null;

                EndTextLine();
                Attach(item);
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

            // Consumed by the next painting operator. A cm/gs between the clip and sh is a
            // coordinate or state change rather than paint, so it keeps the flag: a radial
            // gradient emits its shaping matrix exactly there.
            bool clipJustTaken = _clipJustTaken;
            if (op is not ("cm" or "gs"))
            {
                _clipJustTaken = false;
            }

            switch (op)
            {
                case "q":
                    _clipStack.Push(new List<ClipSpec>(_clips));
                    stack.Push((current, lineWidth, lineCap, lineJoin, miterLimit, dash,
                        strokeColor, fillColor, strokeSpace, fillSpace, strokeAlpha, fillAlpha,
                        fontName, fontSize, leading, fillCmyk, strokeCmyk,
                        charSpacing, wordSpacing, horizontalScale, rise));
                    break;
                case "Q":
                    if (stack.Count > 0)
                    {
                        // Q restores the graphics state but does not end a line. A file
                        // that wraps each show operation in its own q/Q — the Transparency
                        // Guide wraps every glyph — would otherwise never merge anything,
                        // which is exactly what happened while this call was here. The
                        // baseline test is what decides whether text continues; the state
                        // stack has nothing to say about it.
                        _clips.Clear();
                        _clips.AddRange(_clipStack.Pop());
                        (current, lineWidth, lineCap, lineJoin, miterLimit, dash,
                            strokeColor, fillColor, strokeSpace, fillSpace, strokeAlpha, fillAlpha,
                            fontName, fontSize, leading, fillCmyk, strokeCmyk,
                            charSpacing, wordSpacing, horizontalScale, rise) = stack.Pop();
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

                        // This sample sets almost every colour through CS/SCN rather than
                        // k/K, so leaving the ink values alone here keeps whatever the last
                        // k/K operator left behind — which painted every line black on
                        // export. The general operator has to maintain the state too.
                        fillCmyk = InkValues(fillSpace, comps, resources);
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
                        strokeCmyk = InkValues(strokeSpace, comps, resources);
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
                    strokeCmyk = null;
                    break;
                case "rg" when operands.Count >= 3:
                    fillColor = Color3(0);
                    fillSpace = DeviceRgb;
                    fillCmyk = null;
                    break;
                case "G" when operands.Count >= 1:
                    strokeColor = Gray(0);
                    strokeSpace = DeviceGray;
                    strokeCmyk = null;
                    break;
                case "g" when operands.Count >= 1:
                    fillColor = Gray(0);
                    fillSpace = DeviceGray;
                    fillCmyk = null;
                    break;
                case "K" when operands.Count >= 4:
                    strokeColor = Cmyk(0);
                    strokeSpace = DeviceCmyk;
                    strokeCmyk = new[] { Number(0), Number(1), Number(2), Number(3) };
                    break;
                case "k" when operands.Count >= 4:
                    fillColor = Cmyk(0);
                    fillSpace = DeviceCmyk;
                    fillCmyk = new[] { Number(0), Number(1), Number(2), Number(3) };
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
                case "W":
                    // The outline just built becomes the clip. It takes effect at the next
                    // painting operator, which in practice is the "n" that follows.
                    _pendingClip = true;
                    _pendingClipRule = FillRule.NonZero;
                    break;
                case "W*":
                    _pendingClip = true;
                    _pendingClipRule = FillRule.EvenOdd;
                    break;
                case "S":
                    FlushPath(stroke: true, fill: false);
                    break;
                case "s":
                    // "close and stroke": the close applies to this stroke, unlike S.
                    foreach (SubPath open in subPaths)
                    {
                        open.IsClosed = true;
                    }

                    FlushPath(stroke: true, fill: false);
                    break;
                case "f":
                case "F":
                    FlushPath(stroke: false, fill: true, FillRule.NonZero);
                    break;
                case "f*":
                    // The star is the fill rule: even-odd, not non-zero. Treating the two
                    // alike fills in the counter of every glyph — the "6 with no hole".
                    FlushPath(stroke: false, fill: true, FillRule.EvenOdd);
                    break;
                case "B":
                case "b":
                    FlushPath(stroke: true, fill: true, FillRule.NonZero);
                    break;
                case "B*":
                case "b*":
                    FlushPath(stroke: true, fill: true, FillRule.EvenOdd);
                    break;
                case "n":
                    TakeClip();
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
                    ShowText(sq, resources, fontName, fontSize, current, textMatrix, fillColor, items, currentLayer, fillCmyk, new TextState(charSpacing, wordSpacing, horizontalScale, rise));
                    break;
                case "\"" when operands.Count >= 3 && operands[2] is string dq:
                    lineMatrix = lineMatrix.Compose(AffineTransform.CreateTranslation(0, -leading));
                    textMatrix = lineMatrix;
                    ShowText(dq, resources, fontName, fontSize, current, textMatrix, fillColor, items, currentLayer, fillCmyk, new TextState(charSpacing, wordSpacing, horizontalScale, rise));
                    break;

                // Text state. A run measured without these is a run the file did not
                // draw: spacing widens it, horizontal scale condenses or expands it, and
                // rise moves it off the baseline.
                case "Tc" when operands.Count >= 1:
                    charSpacing = Number(0);
                    break;
                case "Tw" when operands.Count >= 1:
                    wordSpacing = Number(0);
                    break;
                case "Tz" when operands.Count >= 1:
                    horizontalScale = Number(0) / 100.0;
                    break;
                case "Ts" when operands.Count >= 1:
                    rise = Number(0);
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
                    ShowText(text, resources, fontName, fontSize, current, textMatrix, fillColor, items, currentLayer, fillCmyk, new TextState(charSpacing, wordSpacing, horizontalScale, rise));
                    AdvanceTextMatrix(text);
                    break;
                case "TJ" when operands.Count >= 1 && operands[0] is List<object?> array:
                    // A TJ array interleaves strings with positioning adjustments, in
                    // thousandths of the text-space em. They carry real position: the
                    // page-number table on a pattern is drawn as [(1)-1130(2)-1118(3)],
                    // where -1130 means "move 1.13 em before the next digit". Concatenating
                    // the strings and ignoring the numbers collapses that to "123" bunched
                    // at the origin — the digits land in the wrong cell, and an extractor
                    // reads one word where the file has three.
                    //
                    // Small adjustments are *kerning* and are best left alone: splitting on
                    // them would turn every letter-spaced line into one object per glyph.
                    // A quarter of an em is far above any kerning pair and far below any
                    // deliberate positioning, so that is the line.
                    {
                        var segments = new List<(string Text, double Offset)>();
                        var buffer = new StringBuilder();
                        double pen = 0;
                        double segmentStart = 0;
                        double pendingShift = 0;
                        double threshold = fontSize * 0.25;

                        foreach (object? element in array)
                        {
                            if (element is string part)
                            {
                                if (buffer.Length == 0)
                                {
                                    segmentStart = pen;
                                }

                                buffer.Append(part);
                            }
                            else if (TryNumber(element, out double adjustment))
                            {
                                // Negative advances the pen (PDF moves forward on the page).
                                // Offsets are in text space, so they are measured with the
                                // declared Tf size, before the matrix scale.
                                pendingShift += -adjustment / 1000.0 * fontSize;

                                if (Math.Abs(pendingShift) > threshold && buffer.Length > 0)
                                {
                                    // The pen also advances by the glyphs just shown; without
                                    // that the next piece lands one advance too far left.
                                    pen += MeasureAdvance(buffer.ToString(), fontName, resources, fontSize,
                new TextState(charSpacing, wordSpacing, horizontalScale, rise)) ?? 0;
                                    pen += pendingShift;
                                    segments.Add((buffer.ToString(), segmentStart));
                                    buffer.Clear();
                                    pendingShift = 0;
                                }
                            }
                        }

                        if (buffer.Length > 0)
                        {
                            segments.Add((buffer.ToString(), segmentStart));
                        }

                        for (int s = 0; s < segments.Count; s++)
                        {
                            (string part, double offset) = segments[s];
                            AffineTransform segmentMatrix = offset == 0
                                ? textMatrix
                                : textMatrix.Compose(AffineTransform.CreateTranslation(offset, 0));

                            // How much room the file left between this piece and the next,
                            // over and above the glyphs themselves. Measured from the
                            // offsets the split already worked out, in text space, and
                            // passed as a fraction of the em because that is the unit
                            // ShowText scales by.
                            double gap = 0;
                            if (s + 1 < segments.Count)
                            {
                                double own = MeasureAdvance(part, fontName, resources, fontSize,
                                    new TextState(charSpacing, wordSpacing, horizontalScale, rise)) ?? 0;
                                gap = (segments[s + 1].Offset - offset - own) / fontSize;
                            }

                            ShowText(part, resources, fontName, fontSize, current, segmentMatrix,
                                fillColor, items, currentLayer, fillCmyk,
                                new TextState(charSpacing, wordSpacing, horizontalScale, rise),
                                gap);
                        }

                        // The whole array has now moved the pen; the next show operation
                        // continues from there, exactly as it would in a viewer.
                        double shown = 0;
                        foreach ((string part, double offset) in segments)
                        {
                            shown = offset + (MeasureAdvance(part, fontName, resources, fontSize,
                                new TextState(charSpacing, wordSpacing, horizontalScale, rise),
                                IsCompositeFont(fontName, resources)) ?? 0);
                        }

                        AdvanceTextMatrixBy(shown + pendingShift);
                    }

                    break;
                case "Do" when operands.Count >= 1 && operands[0] is PdfName xname:
                    DrawXObject(resources, xname.Value, current, items, depth, currentLayer, fillColor);
                    break;
                case "sh" when operands.Count >= 1 && operands[0] is PdfName shadingName:
                    PaintShading(resources, shadingName.Value, current, items, currentLayer,
                        clipJustTaken, fillSpace);
                    break;
            }

            operands.Clear();
        }
    }

    /// <summary>
    /// Paints a shading (<c>sh</c>) as a gradient-filled path.
    ///
    /// A shading paints the whole current clip, so the shape is not the painted path - it
    /// is the clip the file set immediately before <c>sh</c> (<c>path W n /Sh sh</c>).
    /// That clip becomes the item's geometry, which is what keeps a gradient inside its
    /// shape on the way back out. When no clip was just taken the shading covers the page,
    /// and a page rectangle is used.
    /// </summary>
    private void PaintShading(Dictionary<string, object?> resources, string name,
        AffineTransform ctm, List<PdfImportedItem> items, string? layer, bool clipIsShape,
        object? fallbackSpace)
    {
        if (_file.ResolveDict(resources.GetValueOrDefault("Shading")) is not { } shadings ||
            _file.ResolveDict(shadings.GetValueOrDefault(name)) is not { } shading)
        {
            Notes.Add($"shading /{name} was not found in the /Shading resources; the gradient was skipped.");
            return;
        }

        ClipSpec? shape = null;
        if (clipIsShape && _clips.Count > 0)
        {
            shape = _clips[^1];
            _clips.RemoveAt(_clips.Count - 1);
        }

        if (shape is null)
        {
            if (_pageWidth <= 0 || _pageHeight <= 0)
            {
                Notes.Add($"shading /{name} has no clip and the page size is unknown; the gradient was skipped.");
                return;
            }

            shape = new ClipSpec();
            shape.SubPaths.Add(PageRectangle());
        }

        Rect2D bounds = BoundsOf(shape);
        if (bounds.Width <= 1e-9 || bounds.Height <= 1e-9)
        {
            Notes.Add($"shading /{name} has zero-area bounds; the gradient was skipped.");
            return;
        }

        GradientSpec? gradient = ReadShadingGradient(shading, bounds, ctm, fallbackSpace, resources);
        if (gradient is null)
        {
            return;
        }

        var item = new PathItem { Name = "Gradient" };
        foreach (SubPath sub in shape.SubPaths)
        {
            item.SubPaths.Add(sub.Clone());
        }

        // The flattened colour is what a viewer with no shading support shows, so it is a
        // stop from the middle of the ramp rather than white.
        IReadOnlyList<GradientStop> stops = gradient.Normalised();
        item.Fill = FillSpec.WithGradient(gradient, shape.Rule, stops[stops.Count / 2].Color);
        item.Stroke = StrokeSpec.None;
        Attach(item);
        items.Add(new PdfImportedItem(layer, item));
    }

    /// <summary>
    /// Maps one shading to a <see cref="GradientSpec"/>. Axial (2) becomes linear and
    /// radial (3) becomes radial; every other shading type is approximated by the closest
    /// supported one and reported rather than dropped.
    /// </summary>
    private GradientSpec? ReadShadingGradient(Dictionary<string, object?> shading, Rect2D bounds,
        AffineTransform ctm, object? fallbackSpace, Dictionary<string, object?> resources)
    {
        int type = (int)(_file.ResolveNumber(shading.GetValueOrDefault("ShadingType")) ?? 0);
        object? space = _file.Resolve(shading.GetValueOrDefault("ColorSpace")) ?? fallbackSpace;
        object? function = shading.GetValueOrDefault("Function");

        if (type == 2)
        {
            double[]? coords = ReadNumbers(shading.GetValueOrDefault("Coords"));
            if (coords is not { Length: >= 4 })
            {
                return null;
            }

            Point2D start = ToModelPoint(ctm, coords[0], coords[1]);
            Point2D end = ToModelPoint(ctm, coords[2], coords[3]);
            List<GradientStop> stops = StopsFromFunction(function, space, resources, out (double Low, double High) domain);
            return new GradientSpec
            {
                Kind = GradientKind.Linear,
                Stops = stops,
                Spread = SpreadFromFunction(function, space, domain, resources),
                Start = NormaliseToBounds(start, bounds),
                End = NormaliseToBounds(end, bounds),
            };
        }

        if (type == 3)
        {
            double[]? coords = ReadNumbers(shading.GetValueOrDefault("Coords"));
            if (coords is not { Length: >= 6 })
            {
                return null;
            }

            // Coords are [x0 y0 r0 x1 y1 r1]. A non-zero r0 is an inner radius - a ramp
            // that starts part-way out instead of at the centre - which the model has no
            // field for, so it is reported and the ramp is taken from the centre.
            if (Math.Abs(coords[2]) > 1e-9)
            {
                Notes.Add($"radial shading has an inner radius ({PdfDocumentExporter.Num(coords[2])}); " +
                          "the model has no inner radius, so the ramp is taken from the centre out.");
            }

            Point2D centre = ToModelPoint(ctm, coords[3], coords[4]);
            Point2D xEdge = ToModelPoint(ctm, coords[3] + coords[5], coords[4]);
            Point2D yEdge = ToModelPoint(ctm, coords[3], coords[4] + coords[5]);
            double rx = Math.Sqrt(((xEdge.X - centre.X) * (xEdge.X - centre.X)) +
                                  ((xEdge.Y - centre.Y) * (xEdge.Y - centre.Y)));
            double ry = Math.Sqrt(((yEdge.X - centre.X) * (yEdge.X - centre.X)) +
                                  ((yEdge.Y - centre.Y) * (yEdge.Y - centre.Y)));

            List<GradientStop> radialStops = StopsFromFunction(function, space, resources, out (double Low, double High) radialDomain);
            return new GradientSpec
            {
                Kind = GradientKind.Radial,
                Stops = radialStops,
                Spread = SpreadFromFunction(function, space, radialDomain, resources),
                Center = NormaliseToBounds(centre, bounds),
                RadiusX = rx / bounds.Width,
                RadiusY = ry / bounds.Height,
                Rotation = Math.Atan2(xEdge.Y - centre.Y, xEdge.X - centre.X) * 180.0 / Math.PI,
            };
        }

        return ApproximateUnsupported(type, function, space, resources);
    }

    /// <summary>
    /// Reads the stops out of a shading's function. A <c>FunctionType 3</c> gives them
    /// directly - its sub-functions are the stop pairs and its <c>/Bounds</c> are the
    /// interior stop positions - and a single <c>FunctionType 2</c> is a two-stop ramp.
    /// Anything else is sampled, which is an approximation but not a dropped gradient.
    /// </summary>
    private List<GradientStop> StopsFromFunction(object? function, object? space,
        Dictionary<string, object?> resources, out (double Low, double High) domain)
    {
        domain = (0.0, 1.0);
        var dict = _file.ResolveDict(function);
        if (dict is null)
        {
            return new List<GradientStop> { new(0.0, ColorRgb.Black), new(1.0, ColorRgb.White) };
        }

        int functionType = (int)(_file.ResolveNumber(dict.GetValueOrDefault("FunctionType")) ?? 4);
        double[]? bounds = ReadNumbers(dict.GetValueOrDefault("Bounds"));
        var subs = _file.Resolve(dict.GetValueOrDefault("Functions")) as List<object?>;
        double[]? functionDomain = ReadNumbers(dict.GetValueOrDefault("Domain"));

        if (functionType == 3 && subs is { Count: > 0 } && bounds is not null)
        {
            double low = functionDomain is { Length: >= 2 } ? functionDomain[0] : 0.0;
            double high = functionDomain is { Length: >= 2 } ? functionDomain[1] : 1.0;
            domain = (low, high);

            // A widened domain is a spread extension: the ramp is one period, and the
            // sub-functions beyond it are copies. The base stops are the [0,1] period.
            if (low < -1e-9 || high > 1.0 + 1e-9)
            {
                return SampleRamp(function, space, resources, 9);
            }

            var stops = new List<GradientStop>(subs.Count + 1);
            double position = low;
            for (int i = 0; i < subs.Count; i++)
            {
                GradientStop stop = new(
                    position,
                    EvalFunctionColor(subs[i], space, 0.0, resources),
                    1.0,
                    MidpointOfFunction(_file.ResolveDict(subs[i])));
                stops.Add(stop);
                position = i < bounds.Length ? bounds[i] : high;
            }

            stops.Add(new GradientStop(high, EvalFunctionColor(subs[^1], space, 1.0, resources)));
            return stops;
        }

        if (functionType == 2)
        {
            return new List<GradientStop>
            {
                new(0.0, EvalFunctionColor(function, space, 0.0, resources), 1.0, MidpointOfFunction(dict)),
                new(1.0, EvalFunctionColor(function, space, 1.0, resources)),
            };
        }

        return SampleRamp(function, space, resources, 9);
    }

    /// <summary>
    /// Samples a ramp at even positions. Used when the function is not a stitching of
    /// exponentials (a sampled or calculator function), where there are no stop positions
    /// to read and sampling is the honest approximation.
    /// </summary>
    private List<GradientStop> SampleRamp(object? function, object? space,
        Dictionary<string, object?> resources, int samples)
    {
        var stops = new List<GradientStop>(samples);
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)(samples - 1);
            stops.Add(new GradientStop(t, EvalFunctionColor(function, space, t, resources)));
        }

        return stops;
    }

    /// <summary>
    /// Recovers the spread from an extended function domain. A domain wider than 0..1 only
    /// exists because the author extended the function for Reflect or Repeat, so probing
    /// whether the function repeats or mirrors identifies which. Neither means the
    /// extension is something else, and Pad is the safe approximation - reported.
    /// </summary>
    private GradientSpread SpreadFromFunction(object? function, object? space,
        (double Low, double High) domain, Dictionary<string, object?> resources)
    {
        if (domain.Low >= -1e-9 && domain.High <= 1.0 + 1e-9)
        {
            return GradientSpread.Pad;
        }

        bool repeat = true;
        bool reflect = true;
        for (int i = 1; i <= 9; i++)
        {
            double t = i / 10.0;
            ColorRgb baseColour = EvalFunctionColor(function, space, t, resources);
            if (!NearlyEqual(baseColour, EvalFunctionColor(function, space, t + 1.0, resources)))
            {
                repeat = false;
            }

            if (!NearlyEqual(baseColour, EvalFunctionColor(function, space, -t, resources)))
            {
                reflect = false;
            }
        }

        if (reflect)
        {
            return GradientSpread.Reflect;
        }

        if (repeat)
        {
            return GradientSpread.Repeat;
        }

        Notes.Add(
            $"shading function domain [{PdfDocumentExporter.Num(domain.Low)} " +
            $"{PdfDocumentExporter.Num(domain.High)}] extends past 0..1 but is neither a " +
            "repeat nor a reflection; spread approximated as Pad.");
        return GradientSpread.Pad;
    }

    /// <summary>
    /// An unsupported shading type (function-based, or one of the mesh types) has no
    /// <see cref="GradientSpec"/> equivalent, so it is approximated by a linear ramp of
    /// samples from its function and the approximation is reported. A mesh with no
    /// function has nothing to sample and is reported as dropped - never silently.
    /// </summary>
    private GradientSpec? ApproximateUnsupported(int type, object? function, object? space,
        Dictionary<string, object?> resources)
    {
        if (function is null)
        {
            Notes.Add($"shading type {type} is not supported and has no /Function; the gradient was dropped.");
            return null;
        }

        List<GradientStop> stops = SampleRamp(function, space, resources, 9);
        Notes.Add($"shading type {type} is not supported; approximated as a {stops.Count}-stop linear gradient.");
        return new GradientSpec
        {
            Kind = GradientKind.Linear,
            Stops = stops,
            Spread = GradientSpread.Pad,
            Start = new Point2D(0.0, 0.5),
            End = new Point2D(1.0, 0.5),
        };
    }

    /// <summary>Evaluates a shading function and converts its output through the colour space.</summary>
    private ColorRgb EvalFunctionColor(object? function, object? space, double t,
        Dictionary<string, object?> resources)
    {
        try
        {
            double[] components = ApplyFunction(function, new[] { t }, resources);
            return ResolveColor(space, components, resources);
        }
        catch (Exception)
        {
            return ColorRgb.Black;
        }
    }

    /// <summary>
    /// A stop's blend midpoint from an exponential sub-function's <c>N</c>. The model's
    /// midpoint is the power curve PDF's exponential applies, so this inverts
    /// <c>N = log(0.5)/log(midpoint)</c>.
    /// </summary>
    private double MidpointOfFunction(Dictionary<string, object?>? dict)
    {
        double n = dict is null
            ? 1.0
            : _file.ResolveNumber(dict.GetValueOrDefault("N")) ?? 1.0;
        if (!(n > 0) || Math.Abs(n - 1.0) < 1e-9 || !double.IsFinite(n))
        {
            return 0.5;
        }

        return Math.Clamp(Math.Pow(0.5, 1.0 / n), 0.0, 1.0);
    }

    private static bool NearlyEqual(ColorRgb a, ColorRgb b)
        => Math.Abs(a.R - b.R) < 0.02 && Math.Abs(a.G - b.G) < 0.02 && Math.Abs(a.B - b.B) < 0.02;

    private Point2D ToModelPoint(AffineTransform ctm, double x, double y)
    {
        Point2D p = ctm.Transform(new Point2D(x, y));
        return new Point2D(p.X, _pageHeight - p.Y);
    }

    private static Point2D NormaliseToBounds(Point2D p, Rect2D bounds)
        => new((p.X - bounds.Left) / bounds.Width, (p.Y - bounds.Top) / bounds.Height);

    private static Rect2D BoundsOf(ClipSpec clip)
    {
        Rect2D box = Rect2D.Empty;
        foreach (SubPath sub in clip.SubPaths)
        {
            box = box.Union(sub.BoundingBox());
        }

        return box;
    }

    private SubPath PageRectangle()
    {
        var rect = new SubPath { IsClosed = true };
        rect.Nodes.Add(new PathNode(new Point2D(0, 0)));
        rect.Nodes.Add(new PathNode(new Point2D(_pageWidth, 0)));
        rect.Nodes.Add(new PathNode(new Point2D(_pageWidth, _pageHeight)));
        rect.Nodes.Add(new PathNode(new Point2D(0, _pageHeight)));
        return rect;
    }

    private void DrawXObject(Dictionary<string, object?> resources, string name,
        AffineTransform ctm, List<PdfImportedItem> items, int depth, string? layer,
        ColorRgb fillColor)
    {
        if (_file.ResolveDict(resources.GetValueOrDefault("XObject")) is not { } xobjects ||
            _file.Resolve(xobjects.GetValueOrDefault(name)) is not PdfStream stream ||
            _file.ResolveDict(stream.Dict) is not { } dict)
        {
            return;
        }

        // Images are painted, not interpreted: the CTM maps the unit square onto the page.
        if (dict.GetValueOrDefault("Subtype") is PdfName { Value: "Image" })
        {
            AddImage(dict, stream, ctm, items, layer, fillColor);
            return;
        }

        if (dict.GetValueOrDefault("Subtype") is not PdfName { Value: "Form" })
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

        // What the form draws is a group, not a scattering of loose objects.
        //
        // A form XObject is the file's own grouping: it names a piece of artwork and draws it
        // as one thing. Inlining its contents into the page flattened that away, so a panel
        // that should show Layer 1 holding a group holding a clipping mask showed 227 objects
        // in one list, and nothing about the document's shape survived the import.
        //
        // The children keep page coordinates and the group's transform is identity, so this
        // changes the tree and not the picture. The exporter already composes a group's
        // transform on the way down, so both ends agree.
        // A form's /BBox clips everything it draws. It is the parent object's rectangle: the
        // file says "this artwork belongs inside this box", and a viewer that ignores it lets
        // the artwork spill. The LILLIE sample has a form whose box is one line of header
        // text, so its content is meant to be bounded by a sliver nine units tall.
        //
        // The box is given in form space, so it goes through the same matrix the content
        // does. Its corners are taken rather than a width and a height, because a rotation
        // makes it a parallelogram.
        AffineTransform toPage = ctm.Compose(matrix);
        var box = _file.Resolve(dict.GetValueOrDefault("BBox")) as List<object?>;
        ClipSpec? bounds = null;

        if (box is { Count: >= 4 })
        {
            double bx0 = ToDouble(_file.Resolve(box[0]));
            double by0 = ToDouble(_file.Resolve(box[1]));
            double bx1 = ToDouble(_file.Resolve(box[2]));
            double by1 = ToDouble(_file.Resolve(box[3]));

            bounds = new ClipSpec { Rule = FillRule.NonZero };
            var rect = new SubPath { IsClosed = true };

            foreach ((double cx, double cy) in new[]
                     {
                         (bx0, by0), (bx1, by0), (bx1, by1), (bx0, by1),
                     })
            {
                Point2D page = toPage.Transform(new Point2D(cx, cy));
                rect.Nodes.Add(new PathNode(new Point2D(page.X, _pageHeight - page.Y)));
            }

            bounds.SubPaths.Add(rect);
        }

        var drawn = new List<PdfImportedItem>();

        try
        {
            if (bounds is not null)
            {
                _clips.Add(bounds);
            }

            Interpret(_file.GetStreamData(stream), childResources, toPage, drawn, depth + 1, layer);
        }
        finally
        {
            // The box bounds this form and nothing else.
            if (bounds is not null)
            {
                _clips.Remove(bounds);
            }
        }

        if (drawn.Count == 0)
        {
            return;
        }

        // A group of one is noise: it adds a level to every page whose content is a single
        // form, and says nothing a person needs.
        //
        // And a form whose contents span optional-content layers is not one group either. The
        // layer an item belongs to is carried on the item and a group can only sit in one
        // layer, so grouping across a boundary moves every content-layer object into whichever
        // layer came first - which silently lost a layer per page of the sample on the first
        // attempt. Where the form draws into one layer, the group is that layer's.
        string? only = drawn[0].Layer;

        if (drawn.Count == 1 ||
            drawn.Any(d => !string.Equals(d.Layer, only, StringComparison.Ordinal)))
        {
            items.AddRange(drawn);
            return;
        }

        var group = new ArtGroup { Name = "Group" };
        foreach (PdfImportedItem item in drawn)
        {
            group.AddItem(item.Item);
        }

        // The box goes on the group when there is a group, because that is what it is: the
        // parent object's rectangle bounding everything the form drew. Left on each item it
        // still clips correctly, but nothing in the tree says a clipping happened - Affinity
        // shows one row for the mask, and this is that row.
        //
        // Only when it actually cuts something. A form whose box is the whole page bounds its
        // content without removing any of it, and calling that a Clipping Mask would put a
        // mask row on every page of every document that has one - which is what happened on
        // the first attempt, twelve masks for twelve pages that clip nothing.
        if (bounds is not null && Cuts(bounds, group))
        {
            group.Clips.Add(bounds);
        }

        items.Add(new PdfImportedItem(only, group));
    }

    /// <summary>Whether a clip removes any of what the group draws.</summary>
    private static bool Cuts(ClipSpec clip, ArtGroup group)
    {
        Rect2D content = group.Transform.Transform(group.BoundingBox());
        if (content.IsEmpty)
        {
            return false;
        }

        foreach (SubPath sub in clip.SubPaths)
        {
            Rect2D box = sub.BoundingBox();
            if (box.IsEmpty)
            {
                continue;
            }

            // Any part of the content outside the box is a part the box removes.
            if (content.Left < box.Left - Tolerance ||
                content.Top < box.Top - Tolerance ||
                content.Right > box.Right + Tolerance ||
                content.Bottom > box.Bottom + Tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How far outside a box still counts as inside it.</summary>
    private const double Tolerance = 0.01;

    /// <summary>
    /// The compressor an image stream is still in, when this reader has no way to decode it.
    ///
    /// Flate is opened on the way in, so its bytes really are samples and no filter is
    /// recorded. JPEG and JPEG2000 are not, so their bytes are passed through and the name
    /// of the compression travels with them.
    /// </summary>
    private string? UnopenedFilter(Dictionary<string, object?> dict)
    {
        object? filter = _file.Resolve(dict.GetValueOrDefault("Filter"));
        var names = filter switch
        {
            PdfName name => new[] { name.Value },
            List<object?> list => list.OfType<PdfName>().Select(n => n.Value).ToArray(),
            _ => Array.Empty<string>(),
        };

        foreach (string name in names)
        {
            if (name is "DCTDecode" or "JPXDecode" or "CCITTFaxDecode" or "JBIG2Decode" or "RunLengthDecode")
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Records an image mask — a one-bit stencil painted in the current fill colour.
    ///
    /// Kept as an Indexed image of two palette entries rather than as a greyscale one,
    /// because the interesting part is the colour the stencil paints in, and greyscale
    /// samples would paint black and white instead. Coverage comes from the stencil:
    /// opaque where the file says paint, transparent where it does not, so the page shows
    /// through rather than a white rectangle appearing.
    /// </summary>
    private void AddStencil(Dictionary<string, object?> dict, PdfStream stream,
        AffineTransform ctm, List<PdfImportedItem> items, string? layer, ColorRgb fillColor,
        int width, int height)
    {
        byte[] samples = _file.GetStreamData(stream);

        // Decode [0 1] paints where the sample is 0; [1 0] inverts that. The default is
        // the former, so only an explicit inversion changes the reading.
        bool inverted = false;
        if (_file.Resolve(dict.GetValueOrDefault("Decode")) is List<object?> decode &&
            decode.Count >= 2 &&
            ToDouble(_file.Resolve(decode[0])) > ToDouble(_file.Resolve(decode[1])))
        {
            inverted = true;
        }

        var coverage = new byte[width * height];
        var probe = new ImageItem
        {
            PixelWidth = width,
            PixelHeight = height,
            BitsPerComponent = 1,
            ColorSpace = ImageColorSpace.Gray,
            Samples = samples,
        };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool paint = probe.RawSampleAt(x, y) == 0;
                if (inverted)
                {
                    paint = !paint;
                }

                coverage[(y * width) + x] = paint ? (byte)255 : (byte)0;
            }
        }

        var image = new ImageItem
        {
            Name = dict.GetValueOrDefault("Name") is PdfName { Value: var n } ? n : "Stencil",
            PixelWidth = width,
            PixelHeight = height,
            BitsPerComponent = 1,
            ColorSpace = ImageColorSpace.Indexed,
            Samples = samples,

            // Entry 0 is the colour the stencil paints; entry 1 is never seen, because
            // coverage is zero wherever the sample selects it.
            Palette = new byte[]
            {
                (byte)Math.Clamp(Math.Round(fillColor.R * 255.0), 0, 255),
                (byte)Math.Clamp(Math.Round(fillColor.G * 255.0), 0, 255),
                (byte)Math.Clamp(Math.Round(fillColor.B * 255.0), 0, 255),
                0, 0, 0,
            },
            Mask = coverage,
        };

        Point2D stencil0 = ctm.Transform(new Point2D(0, 0));
        Point2D stencil1 = ctm.Transform(new Point2D(1, 1));
        image.Placement = Rect2D.FromPoints(
            new Point2D(stencil0.X, _pageHeight - stencil0.Y),
            new Point2D(stencil1.X, _pageHeight - stencil1.Y));

        EndTextLine();
        Attach(image);
        items.Add(new PdfImportedItem(layer, image));
    }

    /// <summary>
    /// Records an embedded raster image.
    ///
    /// Samples are kept decoded but otherwise untouched, in the colour space the file
    /// used, so a CMYK scan is still a CMYK scan when it is written back out. The
    /// placement comes from the CTM, which maps the image's unit square onto the page.
    /// </summary>
    private void AddImage(Dictionary<string, object?> dict, PdfStream stream,
        AffineTransform ctm, List<PdfImportedItem> items, string? layer, ColorRgb fillColor)
    {
        int width = (int)Math.Round(ToDouble(_file.Resolve(dict.GetValueOrDefault("Width"))));
        int height = (int)Math.Round(ToDouble(_file.Resolve(dict.GetValueOrDefault("Height"))));
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // A stencil has no colour space of its own: it paints the current fill colour
        // wherever its one bit says to. Treating it as a greyscale image instead gives a
        // 1-bit RGB image with three components per pixel where the file has one, which
        // is not merely wrong but unrenderable.
        if (_file.Resolve(dict.GetValueOrDefault("ImageMask")) is true)
        {
            AddStencil(dict, stream, ctm, items, layer, fillColor, width, height);
            return;
        }

        int bits = dict.TryGetValue("BitsPerComponent", out object? bpc)
            ? (int)Math.Round(ToDouble(_file.Resolve(bpc)))
            : 8;

        (ImageColorSpace space, byte[] palette) = ResolveImageColorSpace(dict);
        byte[] samples = _file.GetStreamData(stream);

        // A JPEG cannot be decoded here, so what came back is the compressed stream itself.
        // Saying so on the image is what keeps it a picture: written out as raw samples it is
        // a fifth of the bytes a 185x174 RGB image needs, and viewers make of that what they
        // will — one drew nothing where it should be and another drew a black square.
        string? compressor = UnopenedFilter(dict);

        // The declared colour space can be indirect, inherited or simply absent, and
        // getting it wrong is not subtle: CMYK samples read as RGB turn a pale magenta
        // tint into black. The byte count per pixel is unambiguous for 8-bit images, so
        // it settles the question when the declaration does not.
        if (bits == 8 && width > 0 && height > 0 && samples.Length >= width * height)
        {
            int perPixel = samples.Length / (width * height);
            space = space switch
            {
                ImageColorSpace.Rgb when perPixel == 4 => ImageColorSpace.Cmyk,
                ImageColorSpace.Rgb when perPixel == 1 => ImageColorSpace.Gray,
                ImageColorSpace.Gray when perPixel == 3 => ImageColorSpace.Rgb,
                ImageColorSpace.Gray when perPixel == 4 => ImageColorSpace.Cmyk,
                _ => space,
            };
        }

        var image = new ImageItem
        {
            Name = dict.GetValueOrDefault("Name") is PdfName { Value: var n } ? n : "Image",
            PixelWidth = width,
            PixelHeight = height,
            BitsPerComponent = bits is 1 or 2 or 4 or 8 or 16 ? bits : 8,
            ColorSpace = space,
            Palette = palette,
            Samples = samples,
            Filter = compressor,

            // Both of these change what the picture looks like and neither is optional.
            // Decode inverts or remaps the samples; a colour-key Mask makes one colour
            // transparent, which is how a logo drawn on white is placed over artwork.
            Decode = ReadNumbers(dict.GetValueOrDefault("Decode")),
            ColourKey = ReadNumbers(dict.GetValueOrDefault("Mask")),
        };

        // A soft mask is a second image; only its coverage is needed.
        if (_file.Resolve(dict.GetValueOrDefault("SMask")) is PdfStream maskStream)
        {
            byte[] mask = _file.GetStreamData(maskStream);
            image.Mask = mask;

            Dictionary<string, object?>? maskDict = _file.ResolveDict(maskStream.Dict);

            // A 1-bit or sub-byte mask has to be widened before it can be used as alpha.
            if (maskDict is not null &&
                maskDict.TryGetValue("BitsPerComponent", out object? mbpc) &&
                (int)Math.Round(ToDouble(_file.Resolve(mbpc))) is > 0 and < 8 and var maskBits)
            {
                image.Mask = ExpandMask(mask, width, height, maskBits);
            }
            else if (maskDict is not null)
            {
                // Left alone, so its compressor has to travel with it: a JPEG mask written as
                // raw grey bytes is a mask no viewer can read.
                image.MaskFilter = UnopenedFilter(maskDict);
            }
        }

        // The two diagonal corners of the unit square, mapped through the CTM into the
        // same top-left model frame the paths use.
        Point2D corner0 = ctm.Transform(new Point2D(0, 0));
        Point2D corner1 = ctm.Transform(new Point2D(1, 1));
        image.Placement = Rect2D.FromPoints(
            new Point2D(corner0.X, _pageHeight - corner0.Y),
            new Point2D(corner1.X, _pageHeight - corner1.Y));

        EndTextLine();
        Attach(image);
        items.Add(new PdfImportedItem(layer, image));
    }

    /// <summary>Widens a sub-byte soft mask to one byte per pixel.</summary>
    private static byte[] ExpandMask(byte[] packed, int width, int height, int bits)
    {
        var expanded = new byte[width * height];
        int perByte = 8 / bits;
        int rowBytes = (width + perByte - 1) / perByte;
        int max = (1 << bits) - 1;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int index = rowStart + (x / perByte);
                if (index >= packed.Length)
                {
                    return expanded;
                }

                int shift = 8 - bits * ((x % perByte) + 1);
                int value = (packed[index] >> shift) & max;
                expanded[(y * width) + x] = (byte)(value * 255 / max);
            }
        }

        return expanded;
    }

    /// <summary>The image's colour space and palette, normalised to what the model stores.</summary>
    private (ImageColorSpace Space, byte[] Palette) ResolveImageColorSpace(Dictionary<string, object?> dict)
    {
        object? raw = _file.Resolve(dict.GetValueOrDefault("ColorSpace"));

        if (raw is PdfName { Value: var name })
        {
            return name switch
            {
                "DeviceGray" or "G" => (ImageColorSpace.Gray, Array.Empty<byte>()),
                "DeviceCMYK" or "CMYK" => (ImageColorSpace.Cmyk, Array.Empty<byte>()),
                _ => (ImageColorSpace.Rgb, Array.Empty<byte>()),
            };
        }

        if (raw is List<object?> parts && parts.Count > 0 &&
            _file.Resolve(parts[0]) is PdfName { Value: "Indexed" } && parts.Count >= 4)
        {
            // [/Indexed base hival lookup] — the lookup is a string or a stream.
            object? lookup = _file.Resolve(parts[3]);
            byte[] palette = lookup switch
            {
                string literal => System.Text.Encoding.Latin1.GetBytes(literal),
                PdfStream paletteStream => _file.GetStreamData(paletteStream),
                _ => Array.Empty<byte>(),
            };

            return (ImageColorSpace.Indexed, palette);
        }

        if (raw is List<object?> array && array.Count > 0 &&
            _file.Resolve(array[0]) is PdfName { Value: "ICCBased" } && array.Count >= 2 &&
            _file.Resolve(array[1]) is PdfStream icc &&
            _file.ResolveDict(icc.Dict) is { } iccDict)
        {
            int components = iccDict.TryGetValue("N", out object? n)
                ? (int)Math.Round(ToDouble(_file.Resolve(n)))
                : 3;

            return components switch
            {
                1 => (ImageColorSpace.Gray, Array.Empty<byte>()),
                4 => (ImageColorSpace.Cmyk, Array.Empty<byte>()),
                _ => (ImageColorSpace.Rgb, Array.Empty<byte>()),
            };
        }

        return (ImageColorSpace.Rgb, Array.Empty<byte>());
    }

    private void ShowText(string text, Dictionary<string, object?> resources, string fontName,
        double fontSize, AffineTransform ctm, AffineTransform textMatrix, ColorRgb color,
        List<PdfImportedItem> items, string? layer, double[]? cmyk = null,
        TextState? state = null, double gapAfter = 0.0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

            (string family, bool bold, bool italic, double mapAscent) = MapFont(fontName, resources);
        EmbeddedFont? embedded = BuildEmbeddedFont(fontName, resources);
        // Baseline placement uses the embedded font's own ascent when available
        // (the canvas and exporter both render with that programme).
        double ascent = embedded is not null && embedded.Ascent > 0 ? embedded.Ascent / 1000.0 : mapAscent;

        // Text operands carry glyph codes, not characters. Decode via the font's
        // /ToUnicode CMap when it has one, otherwise through the font's /Encoding:
        // WinAnsi code 0x94 is a right double quote (the inches mark on a pattern),
        // and reading it as Latin-1 turns it into a control character with no glyph.
        bool composite = IsCompositeFont(fontName, resources);
        string rawText = text;
        Dictionary<int, string>? decodeMap = ToUnicodeMap(fontName, resources)
            ?? EncodingMap(fontName, resources);
        string decoded = DecodeText(rawText, decodeMap, composite);
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
        double ascentPoints = (ascent * effectiveSize) + (state?.Rise ?? 0.0);
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
            // The gap the file left before the next piece of the same line is part of how
            // wide this one is. Folding it in is what lets a letter-spaced line arrive as
            // one block: the piece starts where the file put it, ends where the next piece
            // begins, and the importer sees a line that continues rather than two fragments
            // with a hole between them. Without it a heading set at 0.2 em spacing came in
            // two glyphs at a time, and an extractor read "IN TR OD UC TI ON".
            //
            // Kept on the run as well, so export can put the room back between the glyphs
            // rather than after them.
            AdvanceWidth = MeasureAdvance(rawText, fontName, resources, effectiveSize, state, composite)
                + (gapAfter * effectiveSize),
            GapAfter = gapAfter * effectiveSize,
            // What the origin was actually lifted by, as a fraction of the size, so export
            // can put the baseline back exactly. The rise belongs in it: the placement added
            // that lift, and a run recovered without it sits low by the rise - which is how
            // the LILLIE licence block stayed 0.448pt out after the ascent itself was fixed.
            PlacedAscentEm = ascentPoints / effectiveSize,
            SourceFont = SourceFontName(fontName, resources),
            EmbeddedFont = embedded,
            RawCodes = embedded is not null ? rawText : null,
            GlyphIds = embedded is not null ? ComputeGlyphIds(embedded, decoded, rawText) : null,
        };
        item.Runs.Add(run);

        // The run is stored exactly as authored. A tiled PDF draws each label once per
        // sheet it touches and lets the page box clip it; rewriting the characters to
        // fit was tried and it butchered the labels. Clipping belongs at render time.

        // Does this fragment continue the line the last one was on? If it starts where
        // that one ended, along the same baseline and in the same colour, the two are one
        // line of text that the file happened to split, and they belong in one block.
        Point2D baselineModel = new(baseline.X, _pageHeight - baseline.Y);
        double theta = -rotation;
        Point2D right = new(Math.Cos(theta), Math.Sin(theta));
        double advance = run.AdvanceWidth ?? 0.0;
        Point2D end = new(
            baselineModel.X + (advance * right.X),
            baselineModel.Y + (advance * right.Y));

        bool continues = _lastTextValid && _lastText is { } previous
            && Math.Abs(previous.RotationRadians - rotation) < 1e-6
            && previous.Color == color
            && Math.Abs(baselineModel.X - _lastTextEnd.X) < 0.5
            && Math.Abs(baselineModel.Y - _lastTextEnd.Y) < 0.5;

        if (continues)
        {
            _lastText!.Runs.Add(run);
            _lastTextEnd = end;
            return;
        }

        Attach(item);
        items.Add(new PdfImportedItem(layer, item));
        _lastText = item;
        _lastTextEnd = end;
        _lastTextRotation = rotation;
        _lastTextColor = color;
        _lastTextValid = true;
    }

    /// <summary>
    /// The advance of text set in a composite (Type0) font.
    ///
    /// Two bytes per code in an Identity-H document, so the codes are read as pairs rather
    /// than as characters. Widths come from the descendant CIDFont's <c>/W</c>, which has
    /// two forms — a list per starting code, or a range with one width — and fall back to
    /// <c>/DW</c>, which defaults to 1000/1000 em.
    /// </summary>
    private double? MeasureCompositeAdvance(string text, Dictionary<string, object?> fontDict,
        double effectiveSize, TextState? state)
    {
        object? descendants = _file.Resolve(fontDict.GetValueOrDefault("DescendantFonts"));
        if (descendants is not List<object?> list || list.Count == 0 ||
            _file.ResolveDict(list[0]) is not { } cidFont)
        {
            return null;
        }

        double fallback = _file.ResolveNumber(cidFont.GetValueOrDefault("DW")) ?? 1000.0;
        List<object?>? w = _file.Resolve(cidFont.GetValueOrDefault("W")) as List<object?>;

        // /W is a flat array of two shapes, so it is walked once into a lookup rather than
        // scanned per code.
        var widths = new Dictionary<int, double>();
        if (w is not null)
        {
            int i = 0;
            while (i < w.Count)
            {
                int first = (int)ToDouble(_file.Resolve(w[i]));
                if (i + 1 < w.Count && _file.Resolve(w[i + 1]) is List<object?> run)
                {
                    for (int k = 0; k < run.Count; k++)
                    {
                        widths[first + k] = ToDouble(_file.Resolve(run[k]));
                    }

                    i += 2;
                    continue;
                }

                if (i + 2 < w.Count)
                {
                    int last = (int)ToDouble(_file.Resolve(w[i + 1]));
                    double width = ToDouble(_file.Resolve(w[i + 2]));
                    for (int code = first; code <= last && code - first < 65536; code++)
                    {
                        widths[code] = width;
                    }

                    i += 3;
                    continue;
                }

                break;
            }
        }

        double total = 0;
        int glyphs = 0;
        int spaces = 0;

        // Two bytes to a code. An odd trailing byte is a damaged string, not a character.
        for (int at = 0; at + 1 < text.Length; at += 2)
        {
            int code = (text[at] << 8) | text[at + 1];
            total += widths.TryGetValue(code, out double width) ? width : fallback;
            glyphs++;
            if (code == 32)
            {
                spaces++;
            }
        }

        if (glyphs == 0)
        {
            return null;
        }

        double advance = total / 1000.0 * effectiveSize;

        if (state is { } ts)
        {
            advance += (glyphs * ts.CharSpacing) + (spaces * ts.WordSpacing);
            advance *= ts.HorizontalScale;
        }

        return advance;
    }

    /// <summary>
    /// Sums the PDF font's glyph widths for <paramref name="text"/> so the
    /// substituted font can be scaled to the original advance (keeps imported
    /// layout from reflowing under a wider fallback font). Returns null when the
    /// font exposes no simple /Widths array (e.g. CID fonts).
    /// </summary>
    /// <summary>
    /// The text-state parameters that change how wide a run is or where it sits:
    /// character spacing, word spacing, horizontal scale and rise. Taken together because
    /// they travel together — a caller either has all of them from the graphics state or
    /// is measuring text that has none.
    /// </summary>
    private readonly record struct TextState(
        double CharSpacing, double WordSpacing, double HorizontalScale, double Rise);

    private double? MeasureAdvance(string text, string fontName,
        Dictionary<string, object?> resources, double effectiveSize, TextState? state = null,
        bool composite = false)
    {
        if (_file.ResolveDict(resources.GetValueOrDefault("Font")) is not { } fonts ||
            _file.ResolveDict(fonts.GetValueOrDefault(fontName)) is not { } fontDict)
        {
            return null;
        }

        // A composite font has no /Widths. Its widths live in the descendant CIDFont as
        // /W with a /DW default, and returning null for them left every run in a document
        // of CID fonts with no advance at all — so the model had no width, the text could
        // not be laid out, and one line arrived as a scatter of separately positioned
        // fragments. That is the whole of the scrambled reading order on the Transparency
        // Guide, whose fourteen fonts are all Type0.
        if (composite || _file.Resolve(fontDict.GetValueOrDefault("Widths")) is not List<object?>)
        {
            return MeasureCompositeAdvance(text, fontDict, effectiveSize, state);
        }

        if (_file.Resolve(fontDict.GetValueOrDefault("Widths")) is not List<object?> widths)
        {
            return null;
        }

        double missing = _file.ResolveNumber(fontDict.GetValueOrDefault("MissingWidth")) ?? 0;
        int first = (int)(_file.ResolveNumber(fontDict.GetValueOrDefault("FirstChar")) ?? 0);
        double total = 0;
        int glyphs = 0;
        int spaces = 0;
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
            glyphs++;
            if (ch == ' ')
            {
                spaces++;
            }
        }

        double advance = (total / 1000.0 * effectiveSize);

        if (state is { } ts)
        {
            // Word spacing applies to the single-byte code 32, and character spacing to
            // every glyph including spaces. Both are in unscaled text space, so they are
            // added after the glyph widths and before the horizontal scale, which then
            // stretches the whole line.
            advance += (glyphs * ts.CharSpacing) + (spaces * ts.WordSpacing);
            advance *= ts.HorizontalScale;
        }

        return advance;
    }

    /// <summary>
    /// Extracts the embedded font programme (FontFile/FontFile2/FontFile3) plus
    /// the metrics needed to re-emit it verbatim. Returns null when the font is
    /// not embedded (or is a composite we do not yet pass through).
    /// </summary>
    private EmbeddedFont? BuildEmbeddedFont(string fontName, Dictionary<string, object?> resources)
    {
        Dictionary<string, object?>? font = FontDict(fontName, resources);
        if (font is null)
        {
            return null;
        }

        if (_embeddedFontCache.TryGetValue(font, out EmbeddedFont? cached))
        {
            return cached;
        }

        EmbeddedFont? result = null;
        string subtype = (font.GetValueOrDefault("Subtype") as PdfName)?.Value ?? string.Empty;
        bool composite = subtype == "Type0";

        Dictionary<string, object?>? descriptor;
        Dictionary<string, object?>? descendant = null;
        if (composite)
        {
            var descendants = _file.Resolve(font.GetValueOrDefault("DescendantFonts")) as List<object?>;
            descendant = descendants is { Count: > 0 } ? _file.ResolveDict(descendants[0]) : null;
            descriptor = descendant is null
                ? null
                : _file.ResolveDict(descendant.GetValueOrDefault("FontDescriptor"));
        }
        else
        {
            descriptor = _file.ResolveDict(font.GetValueOrDefault("FontDescriptor"));
        }

        if (descriptor is not null &&
            (FindFontProgram(descriptor) is (EmbeddedFontFormat format, byte[] program)))
        {
            double[]? bbox = ReadNumbers(descriptor.GetValueOrDefault("FontBBox"));
            result = new EmbeddedFont
            {
                DescendantSubtype = (descendant?.GetValueOrDefault("Subtype") as PdfName)?.Value ?? "CIDFontType2",
                DescendantBaseFont = (descendant?.GetValueOrDefault("BaseFont") as PdfName)?.Value
                    ?? (font.GetValueOrDefault("BaseFont") as PdfName)?.Value ?? "Embedded",
                Type0Encoding = font.GetValueOrDefault("Encoding") is PdfName e0 ? e0.Value : null,
                Type0EncodingStream = _file.Resolve(font.GetValueOrDefault("Encoding")) is PdfStream es
                    ? _file.GetStreamData(es)
                    : null,
                CidSystemInfo = BuildCidSystemInfo(_file.ResolveDict(descendant?.GetValueOrDefault("CIDSystemInfo"))),
                DefaultWidth = _file.ResolveNumber(descendant?.GetValueOrDefault("DW")) ?? 1000,
                WidthsSpec = FormatPdfArray(descendant?.GetValueOrDefault("W")),
                CidToGidMapName = descendant?.GetValueOrDefault("CIDToGIDMap") is PdfName cm ? cm.Value : "Identity",
                CidToGidMapStream = _file.Resolve(descendant?.GetValueOrDefault("CIDToGIDMap")) is PdfStream cms
                    ? _file.GetStreamData(cms)
                    : null,
                Format = format,
                Program = program,
                Composite = composite,
                BaseFont = (font.GetValueOrDefault("BaseFont") as PdfName)?.Value ?? "Embedded",
                FamilyName = "VCCadEmb" + StableHash(program).ToString("X8"),
                FirstChar = (int)(_file.ResolveNumber(font.GetValueOrDefault("FirstChar")) ?? 0),
                Widths = ReadNumbers(font.GetValueOrDefault("Widths")) ?? Array.Empty<double>(),
                MissingWidth = _file.ResolveNumber(descriptor.GetValueOrDefault("MissingWidth")) ?? 0,
                ToUnicode = _file.Resolve(font.GetValueOrDefault("ToUnicode")) is PdfStream tu
                    ? _file.GetStreamData(tu)
                    : null,
                EncodingName = font.GetValueOrDefault("Encoding") is PdfName encName ? encName.Value : null,
                BaseEncoding = _file.ResolveDict(font.GetValueOrDefault("Encoding"))?.GetValueOrDefault("BaseEncoding")
                    is PdfName be ? be.Value : null,
                Differences = ReadEncodingDifferences(
                    _file.ResolveDict(font.GetValueOrDefault("Encoding"))?.GetValueOrDefault("Differences")),
                Flags = (int)(_file.ResolveNumber(descriptor.GetValueOrDefault("Flags")) ?? 4),
                FontBBox = bbox is { Length: >= 4 } ? bbox : new double[] { 0, 0, 0, 0 },
                ItalicAngle = _file.ResolveNumber(descriptor.GetValueOrDefault("ItalicAngle")) ?? 0,
                Ascent = _file.ResolveNumber(descriptor.GetValueOrDefault("Ascent")) ?? 800,
                Descent = _file.ResolveNumber(descriptor.GetValueOrDefault("Descent")) ?? -200,
                CapHeight = _file.ResolveNumber(descriptor.GetValueOrDefault("CapHeight")) ?? 700,
                StemV = _file.ResolveNumber(descriptor.GetValueOrDefault("StemV")) ?? 80,
            };
        }

        _embeddedFontCache[font] = result;
        return result;
    }

    private (EmbeddedFontFormat Format, byte[] Program)? FindFontProgram(Dictionary<string, object?> descriptor)
    {
        if (_file.Resolve(descriptor.GetValueOrDefault("FontFile")) is PdfStream type1)
        {
            return (EmbeddedFontFormat.Type1, _file.GetStreamData(type1));
        }

        if (_file.Resolve(descriptor.GetValueOrDefault("FontFile2")) is PdfStream trueType)
        {
            return (EmbeddedFontFormat.TrueType, _file.GetStreamData(trueType));
        }

        if (_file.Resolve(descriptor.GetValueOrDefault("FontFile3")) is PdfStream cff)
        {
            string kind = (cff.Dict.GetValueOrDefault("Subtype") as PdfName)?.Value ?? "Type1C";
            EmbeddedFontFormat format = kind == "OpenType"
                ? EmbeddedFontFormat.OpenType
                : EmbeddedFontFormat.Type1C;
            return (format, _file.GetStreamData(cff));
        }

        return null;
    }

    private static List<(int Code, string Name)> ReadEncodingDifferences(object? differences)
    {
        var result = new List<(int, string)>();
        if (differences is not List<object?> list)
        {
            return result;
        }

        int code = 0;
        foreach (object? entry in list)
        {
            if (entry is long l)
            {
                code = (int)l;
            }
            else if (entry is PdfName name)
            {
                result.Add((code++, name.Value));
            }
        }

        return result;
    }

    private string BuildCidSystemInfo(Dictionary<string, object?>? info)
    {
        if (info is null)
        {
            return "/Registry (Adobe) /Ordering (Identity) /Supplement 0";
        }

        string registry = _file.Resolve(info.GetValueOrDefault("Registry")) switch
        {
            PdfName n => n.Value,
            string s2 => s2,
            _ => "Adobe",
        };
        string ordering = _file.Resolve(info.GetValueOrDefault("Ordering")) switch
        {
            PdfName n => n.Value,
            string s2 => s2,
            _ => "Identity",
        };
        int supplement = (int)(_file.ResolveNumber(info.GetValueOrDefault("Supplement")) ?? 0);
        return $"/Registry ({registry}) /Ordering ({ordering}) /Supplement {supplement}";
    }

    /// <summary>Renders a resolved PDF array/number/name back to PDF syntax.</summary>
    private string FormatPdfArray(object? value)
    {
        value = _file.Resolve(value);
        return value switch
        {
            null => "[]",
            List<object?> list => "[" + string.Join(' ', list.Select(FormatPdfArray)) + "]",
            PdfName name => "/" + name.Value,
            string s2 => $"({s2.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")})",
            double d => d.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "0",
        };
    }

    private static uint StableHash(byte[] data)
    {
        uint hash = 2166136261;
        foreach (byte b in data)
        {
            hash = (hash ^ b) * 16777619;
        }

        return hash;
    }

    /// <summary>Maps a run's text to glyph indices for the embedded programme.</summary>
    private static ushort[]? ComputeGlyphIds(EmbeddedFont font, string decoded, string rawCodes)
    {
        try
        {
            if (font.Composite)
            {
                // Identity-H: 2-byte codes are the glyph indices.
                var ids = new List<ushort>(rawCodes.Length / 2);
                for (int i = 0; i + 1 < rawCodes.Length; i += 2)
                {
                    ids.Add((ushort)((rawCodes[i] << 8) | rawCodes[i + 1]));
                }

                return ids.Count > 0 ? ids.ToArray() : null;
            }

            if (font.Format == EmbeddedFontFormat.TrueType)
            {
                var ttf = new Fonts.TrueTypeFont(font.Program);
                var ids = new ushort[decoded.Length];
                bool any = false;
                for (int i = 0; i < decoded.Length; i++)
                {
                    ids[i] = (ushort)ttf.GlyphFor(decoded[i]);
                    any |= ids[i] != 0;
                }

                return any ? ids : null;
            }

            if (font.Format is EmbeddedFontFormat.Type1C or EmbeddedFontFormat.OpenType)
            {
                Dictionary<int, ushort>? sidToGid = CffGlyphMap.SidToGid(font.Program);
                if (sidToGid is null)
                {
                    return null;
                }

                var ids = new ushort[decoded.Length];
                bool any = false;
                for (int i = 0; i < decoded.Length; i++)
                {
                    int sid = CffGlyphMap.AsciiSid(decoded[i]);
                    if (sid >= 0 && sidToGid.TryGetValue(sid, out ushort gid))
                    {
                        ids[i] = gid;
                        any = true;
                    }
                }

                return any ? ids : null;
            }
        }
        catch
        {
            // fall through to substitution
        }

        return null;
    }

    /// <summary>The font name the document asks for, for reporting substitutions.</summary>
    private string? SourceFontName(string fontName, Dictionary<string, object?> resources)
        => (FontDict(fontName, resources)?.GetValueOrDefault("BaseFont") as PdfName)?.Value ?? fontName;
    private Dictionary<string, object?>? FontDict(string fontName, Dictionary<string, object?> resources)
        => _file.ResolveDict(resources.GetValueOrDefault("Font")) is { } fonts
            ? _file.ResolveDict(fonts.GetValueOrDefault(fontName))
            : null;

    private bool IsCompositeFont(string fontName, Dictionary<string, object?> resources)
        => FontDict(fontName, resources)?.GetValueOrDefault("Subtype") is PdfName { Value: "Type0" };

    /// <summary>
    /// The font's /Encoding as a code → text map, used when the font carries no
    /// /ToUnicode CMap (which is the usual case for Helvetica and friends).
    /// </summary>
    private Dictionary<int, string>? EncodingMap(string fontName, Dictionary<string, object?> resources)
    {
        Dictionary<string, object?>? font = FontDict(fontName, resources);
        if (font is null)
        {
            return null;
        }

        if (_encodingCache.TryGetValue(font, out Dictionary<int, string>? cached))
        {
            return cached;
        }

        Dictionary<int, string>? map = PdfTextEncoding.ForFont(_file, font);
        _encodingCache[font] = map;
        return map;
    }

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
        Dictionary<string, object?>? fontDict =
            _file.ResolveDict(resources.GetValueOrDefault("Font")) is { } fonts
                ? _file.ResolveDict(fonts.GetValueOrDefault(fontName))
                : null;

        string baseFont = (fontDict?.GetValueOrDefault("BaseFont") as PdfName)?.Value ?? fontName;

        // Classify through the standard-font table rather than a handful of substring
        // tests: a PDF may name any of the fourteen standard faces, or any of their
        // many aliases, and each one identifies its family by a family word.
        StandardFonts.TryResolve(baseFont, bold: false, italic: false, out StandardFace face);
        bool bold = face.Bold;
        bool italic = face.Italic;
        string family = StandardFonts.UrwFamily(face);

        // The origin lift needs the ascent of the face we will actually render with.
        // The font's own descriptor is the best source; when it is missing, fall back to
        // the usual Latin ascent rather than borrowing a metric from a bundled font.
        double ascent = VCCad.Core.Text.TextMeasurement.TypicalAscentEm;
        if (fontDict?.GetValueOrDefault("FontDescriptor") is { } descriptorRef &&
            _file.ResolveDict(descriptorRef) is { } descriptor &&
            _file.ResolveNumber(descriptor.GetValueOrDefault("Ascent")) is { } ascentValue && ascentValue > 0)
        {
            ascent = Math.Clamp(ascentValue / 1000.0, 0.5, 1.4);
        }

        return (family, bold, italic, ascent);
    }


    private static readonly PdfName DeviceGray = new("DeviceGray");
    private static readonly PdfName DeviceRgb = new("DeviceRGB");
    private static readonly PdfName DeviceCmyk = new("DeviceCMYK");

    /// <summary>
    /// The four ink components a general colour operator just set, or null when it did not
    /// set a DeviceCMYK colour.
    ///
    /// <c>CS</c>/<c>SCN</c> name a colour space indirectly and then supply components, so
    /// the values can only be read against that space. A pattern, an Indexed space or an
    /// RGB profile is not DeviceCMYK and carries no ink values to preserve.
    /// </summary>
    private double[]? InkValues(object? space, List<double> components,
        Dictionary<string, object?> resources)
        => components.Count >= 4 && ComponentCount(space, resources) == 4
            ? new[] { components[0], components[1], components[2], components[3] }
            : null;

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
    /// <summary>
    /// The bytes of a function's stream, which is where a sampled function keeps its
    /// table and a type 4 keeps its program.
    /// </summary>
    private byte[]? StreamBytes(object? function)
        => _file.Resolve(function) is PdfStream stream ? _file.GetStreamData(stream) : null;

    /// <summary>
    /// The program inside a type 4 function, which is a stream rather than a dictionary
    /// entry: the code is the stream's own data.
    /// </summary>
    /// <summary>
    /// The clip paths in force, in the order they were set. PDF accumulates them, so an
    /// item painted under two clips is inside both, and q/Q restore them like the rest of
    /// the graphics state.
    /// </summary>
    private readonly List<ClipSpec> _clips = new();

    private readonly Stack<List<ClipSpec>> _clipStack = new();

    /// <summary>Whether the path just built is a clip, and by which rule.</summary>
    private bool _pendingClip;
    private FillRule _pendingClipRule = FillRule.NonZero;

    /// <summary>
    /// Set when a clip was taken by the immediately preceding path. A gradient is painted
    /// by <c>sh</c> through the current clip, and the usual file shape is
    /// <c>path W n /Sh sh</c>: when the clip was taken just before the shading, that clip
    /// is the gradient's shape and becomes the path's geometry.
    /// </summary>
    private bool _clipJustTaken;

    /// <summary>
    /// The text block the last show operation added, and where it ended in model space.
    ///
    /// A file may split one line across many show operations — the Transparency Guide
    /// writes a single glyph per Tj, each in its own BT/ET, so a page of twenty-five lines
    /// arrived as a hundred and thirty-one text objects and the reading order was whatever
    /// order they happened to land in. Two fragments that continue each other along the
    /// same baseline are one line, and merging them is what makes the block read as text
    /// rather than as a scatter of glyphs.
    /// </summary>
    private TextItem? _lastText;

    private Point2D _lastTextEnd;
    private double _lastTextRotation;
    private ColorRgb _lastTextColor;
    private bool _lastTextValid;

    /// <summary>Ends the current text line, so the next show starts a block of its own.</summary>
    private void EndTextLine() => _lastTextValid = false;

    /// <summary>
    /// Records the clips in force onto an item as it is created. Clipping is applied at
    /// render and export time; the importer's job is to remember which outline applied.
    /// </summary>
    private void Attach(LayerItem item)
    {
        foreach (ClipSpec clip in _clips)
        {
            item.Clips.Add(clip.Clone());
        }
    }

    private string? StreamCode(object? function)
    {
        if (_file.Resolve(function) is PdfStream stream)
        {
            return System.Text.Encoding.Latin1.GetString(_file.GetStreamData(stream));
        }

        return function as string;
    }

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

        if (type == 3)
        {
            // Stitching: pick the sub-function whose sub-domain the input falls in, then
            // hand it the input through that part's encode range.
            return PdfFunctions.Type3(dict, inputs.ToArray(),
                (sub, inner) => ApplyFunction(sub, inner, resources), ReadNumbers);
        }

        if (type == 4)
        {
            // PostScript calculator. Used for spot-colour tint transforms and shadings,
            // and missing entirely before now, which turned those colours black.
            object? code = _file.Resolve(dict.GetValueOrDefault("Code"))
                           ?? _file.Resolve(dict.GetValueOrDefault("Function"));
            double[]? computed = PdfFunctions.Type4(code ?? StreamCode(function), inputs.ToArray(),
                inputs.Count);
            if (computed is not null)
            {
                return computed;
            }
        }

        if (type == 0)
        {
            // A sampled function is a table, and the table is the whole function. The
            // previous version read the first sample and scaled the range, which is not an
            // approximation of a sampled function — it ignores every sample but one, so a
            // tint transform built from a table came out as a straight ramp from nothing.
            byte[]? table = StreamBytes(function);
            if (table is not null)
            {
                double[]? sampled = PdfFunctions.Sampled(
                    table,
                    ReadNumbers(dict.GetValueOrDefault("Size")),
                    ReadNumbers(dict.GetValueOrDefault("Domain")),
                    ReadNumbers(dict.GetValueOrDefault("Encode")),
                    ReadNumbers(dict.GetValueOrDefault("Range")),
                    (int)(_file.ResolveNumber(dict.GetValueOrDefault("BitsPerSample")) ?? 8),
                    inputs.ToArray());
                if (sampled is not null)
                {
                    return sampled;
                }
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

    /// <summary>Reads a numeric operand, reporting whether it was one at all.</summary>
    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case double d:
                number = d;
                return true;
            case long l:
                number = l;
                return true;
            case int i:
                number = i;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static double ScaleOf(AffineTransform transform)
        => Math.Sqrt(Math.Abs(transform.A * transform.D - transform.C * transform.B));
}
