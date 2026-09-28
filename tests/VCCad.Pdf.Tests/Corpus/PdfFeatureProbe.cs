using System.Globalization;
using System.Text;
using VCCad.Pdf.Parsing;

namespace VCCad.Pdf.Tests.Corpus;

/// <summary>
/// Names of the individual document features reported by
/// <see cref="PdfFeatureProbe"/>. Kept as string constants (rather than an enum)
/// so an inventory dump reads naturally and new probes can be added without
/// breaking a switch.
/// </summary>
public static class PdfFeature
{
    // ---- probe self-reporting -------------------------------------------
    public const string ProbeStructuredParsed = "probe.structuredParsed";
    public const string ProbeCatalog = "probe.catalog";

    // ---- page / document ------------------------------------------------
    public const string DocumentUnparseable = "doc.unparseable";
    public const string DocumentEncrypted = "doc.encrypt";
    public const string DocumentPdfA = "doc.pdfa";
    public const string DocumentCropBox = "doc.cropBox";
    public const string DocumentRotate = "doc.rotate";
    public const string DocumentAnnotations = "doc.annots";
    public const string DocumentOutlines = "doc.outlines";
    public const string DocumentPieceInfo = "doc.pieceInfo";
    public const string DocumentAiPrivateData = "doc.aiPrivateData";
    public const string DocumentEmbeddedFiles = "doc.embeddedFiles";
    public const string DocumentMetadataXmp = "doc.metadataXmp";
    public const string DocumentOptionalContent = "doc.optionalContent";
    public const string DocumentOcg = "doc.ocg";
    public const string DocumentTagged = "doc.tagged";
    public const string DocumentLinearized = "doc.linearized";
    public const string DocumentObjectStreams = "doc.objectStreams";
    public const string DocumentXrefStream = "doc.xrefStream";
    public const string DocumentSignature = "doc.signature";

    // ---- fonts ----------------------------------------------------------
    public const string FontAny = "font.any";
    public const string FontType1 = "font.type1";
    public const string FontTrueType = "font.trueType";
    public const string FontType0 = "font.type0";
    public const string FontType3 = "font.type3";
    public const string FontMmType1 = "font.mmType1";
    public const string FontCidType0 = "font.cidFontType0";
    public const string FontCidType2 = "font.cidFontType2";
    public const string FontCff = "font.cff";
    public const string FontOpenType = "font.opentype";
    public const string FontEmbedded = "font.embedded";
    public const string FontNonEmbedded = "font.nonEmbedded";
    public const string FontToUnicode = "font.toUnicode";
    public const string FontEncodingDifferences = "font.encodingDifferences";
    public const string FontIdentityH = "font.identityH";
    public const string FontCmapName = "font.cmapName";
    public const string FontEncodingStream = "font.encodingStream";
    public const string FontSubtypeUnknown = "font.subtypeUnknown";

    // ---- path / paint operators -----------------------------------------
    public const string OpAny = "op.any";
    public const string OpMove = "op.move";
    public const string OpLine = "op.line";
    public const string OpCurve = "op.curve";
    public const string OpCurveV = "op.curveV";
    public const string OpCurveY = "op.curveY";
    public const string OpRect = "op.rect";
    public const string OpClose = "op.close";
    public const string OpFill = "op.fill";
    public const string OpStroke = "op.stroke";
    public const string OpFillStroke = "op.fillStroke";
    public const string OpEvenOdd = "op.evenOdd";
    public const string OpClip = "op.clip";
    public const string OpClipEvenOdd = "op.clipEvenOdd";
    public const string OpGs = "op.gs";
    public const string OpDo = "op.do";
    public const string OpShading = "op.sh";
    public const string OpText = "op.text";
    public const string OpInlineImage = "op.inlineImage";

    // ---- transparency ---------------------------------------------------
    public const string GsExtGState = "gs.extGState";
    public const string GsSoftMask = "gs.smask";
    public const string GsStrokeAlpha = "gs.strokeAlpha";
    public const string GsFillAlpha = "gs.fillAlpha";
    public const string GsStrokeAlphaTranslucent = "gs.strokeAlphaTranslucent";
    public const string GsFillAlphaTranslucent = "gs.fillAlphaTranslucent";
    public const string GsBlendMode = "gs.blendMode";
    public const string GsBlendModeNonNormal = "gs.blendModeNonNormal";
    public const string GsTransparencyGroup = "gs.transparencyGroup";
    public const string GsTransferFunction = "gs.transferFunction";
    public const string TransparencyGroup = "transparency.group";
    public const string TransparencySoftMaskImage = "transparency.softMaskImage";

    // ---- patterns / shading ---------------------------------------------
    public const string PatternTiling = "pattern.tiling";
    public const string PatternShading = "pattern.shading";
    public const string PatternAny = "pattern.any";
    public const string ShadingAny = "shading.any";
    public const string ShadingDictResource = "shading.dictResource";
    public const string PatternDictResource = "pattern.dictResource";

    // ---- images / XObjects ----------------------------------------------
    public const string ImageAny = "image.any";
    public const string ImageDct = "image.dctDecode";
    public const string ImageJpx = "image.jpxDecode";
    public const string ImageCcitt = "image.ccittFaxDecode";
    public const string ImageJbig2 = "image.jbig2Decode";
    public const string ImageRunLength = "image.runLengthDecode";
    public const string ImageFlate = "image.flateDecode";
    public const string ImageLzw = "image.lzwDecode";
    public const string ImageMask = "image.imageMask";
    public const string ImageSoftMask = "image.smask";
    public const string ImageColorKeyMask = "image.colorKeyMask";
    public const string XObjectForm = "xobject.form";
    public const string XObjectImage = "xobject.image";
    public const string XObjectPostScript = "xobject.ps";

    // ---- resources / colour spaces --------------------------------------
    public const string ResResources = "res.resources";
    public const string ResXObject = "res.xobject";
    public const string ResFont = "res.font";
    public const string ResColorSpace = "res.colorSpace";
    public const string ResIccBased = "res.iccBased";
    public const string ResSeparation = "res.separation";
    public const string ResDeviceN = "res.deviceN";
    public const string ResIndexed = "res.indexed";
    public const string ResLab = "res.lab";
    public const string ResPatternColorSpace = "res.patternColorSpace";
    public const string ResOutputIntents = "res.outputIntents";
    public const string ResExtGState = "res.extGState";
    public const string ResShading = "res.shading";
    public const string ResPattern = "res.pattern";
    public const string ResProperties = "res.properties";

    /// <summary>
    /// Every feature name, discovered from the <c>const string</c> fields above so
    /// this list cannot drift out of sync with the constants.
    /// </summary>
    public static readonly IReadOnlyList<string> All = typeof(PdfFeature)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// A byte/object-level classifier for PDF documents: it inspects a PDF and
/// reports which document features it uses, without depending on the VCCad
/// import pipeline (and therefore without depending on how much of those
/// features the importer currently understands). It is the instrument the
/// corpus harness uses to choose representative files and to guard the range of
/// features the corpus still exercises.
///
/// Two passes are fused:
/// <list type="number">
/// <item>a raw-byte pass for the handful of markers that live in the trailer or
/// in XMP (encryption, PDF/A identification, Illustrator private data, image
/// filters) — these are never inside a compressed object stream;</item>
/// <item>a structural pass over the object graph (<see cref="PdfFile"/>, which
/// the test assembly can see through <c>InternalsVisibleTo</c>) that identifies
/// fonts, XObjects, patterns, shadings, graphics-state dictionaries, page boxes
/// and annotations, and tokenises decoded page/Form content streams to tally the
/// painting operators.</item>
/// </list>
///
/// The structural pass is authoritative wherever it succeeds; the raw pass only
/// ever adds evidence. Neither throws: any failure is captured as a feature
/// ("doc.unparseable") rather than propagated.
/// </summary>
public static class PdfFeatureProbe
{
    /// <summary>Classifies <paramref name="bytes"/>; never throws.</summary>
    public static PdfFeatureReport Probe(byte[] bytes, string name = "")
    {
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string feature in PdfFeature.All)
        {
            flags[feature] = false;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? error = null;

        try
        {
            ScanRaw(bytes, flags);
        }
        catch (Exception ex)
        {
            error = "raw scan: " + ex.GetType().Name + ": " + ex.Message;
        }

        try
        {
            new Scan(bytes, flags, counts).Run();
        }
        catch (Exception ex)
        {
            error = Join(error, "structured scan: " + ex.GetType().Name + ": " + ex.Message);
        }

        bool parseFailed = !flags[PdfFeature.ProbeStructuredParsed];
        if (parseFailed)
        {
            flags[PdfFeature.DocumentUnparseable] = true;
        }

        return new PdfFeatureReport(name, parseFailed, error, flags, counts);
    }

    /// <summary>Convenience overload for a file on disk.</summary>
    public static PdfFeatureReport ProbeFile(string path)
        => Probe(File.ReadAllBytes(path), Path.GetFileName(path));

    private static string Join(string? existing, string extra)
        => existing is null ? extra : existing + "; " + extra;

    // ==================================================================
    // raw-byte pass
    // ==================================================================

    private static readonly (string Marker, string Feature)[] RawNameMarkers =
    {
        ("Encrypt", PdfFeature.DocumentEncrypted),
        ("AIPrivateData", PdfFeature.DocumentAiPrivateData),
        ("PieceInfo", PdfFeature.DocumentPieceInfo),
        ("DCTDecode", PdfFeature.ImageDct),
        ("JPXDecode", PdfFeature.ImageJpx),
        ("CCITTFaxDecode", PdfFeature.ImageCcitt),
        ("JBIG2Decode", PdfFeature.ImageJbig2),
        ("RunLengthDecode", PdfFeature.ImageRunLength),
        ("LZWDecode", PdfFeature.ImageLzw),
        ("Type3", PdfFeature.FontType3),
        ("Type1", PdfFeature.FontType1),
        ("MMType1", PdfFeature.FontMmType1),
        ("TrueType", PdfFeature.FontTrueType),
        ("Type0", PdfFeature.FontType0),
        ("OpenType", PdfFeature.FontOpenType),
        ("Type1C", PdfFeature.FontCff),
        ("CIDFontType0C", PdfFeature.FontCff),
        ("CIDFontType0", PdfFeature.FontCidType0),
        ("CIDFontType2", PdfFeature.FontCidType2),
        ("FontFile", PdfFeature.FontEmbedded),
        ("FontFile2", PdfFeature.FontEmbedded),
        ("FontFile3", PdfFeature.FontEmbedded),
        ("ToUnicode", PdfFeature.FontToUnicode),
        ("Identity-H", PdfFeature.FontIdentityH),
        ("Differences", PdfFeature.FontEncodingDifferences),
        ("CMapName", PdfFeature.FontCmapName),
        ("ExtGState", PdfFeature.GsExtGState),
        ("OutputIntents", PdfFeature.ResOutputIntents),
        ("ICCBased", PdfFeature.ResIccBased),
        ("DeviceN", PdfFeature.ResDeviceN),
        ("Separation", PdfFeature.ResSeparation),
        ("Indexed", PdfFeature.ResIndexed),
        ("PatternType", PdfFeature.PatternAny),
        ("ShadingType", PdfFeature.ShadingAny),
        ("OCProperties", PdfFeature.DocumentOptionalContent),
        ("OCGs", PdfFeature.DocumentOcg),
        ("EmbeddedFiles", PdfFeature.DocumentEmbeddedFiles),
        ("MarkInfo", PdfFeature.DocumentTagged),
        ("Outlines", PdfFeature.DocumentOutlines),
        ("Linearized", PdfFeature.DocumentLinearized),
        ("ObjStm", PdfFeature.DocumentObjectStreams),
        ("ByteRange", PdfFeature.DocumentSignature),
    };

    private static void ScanRaw(byte[] bytes, IDictionary<string, bool> flags)
    {
        string text = Encoding.Latin1.GetString(bytes);

        foreach ((string marker, string feature) in RawNameMarkers)
        {
            if (HasName(text, marker))
            {
                flags[feature] = true;
            }
        }

        if (text.Contains("pdfaid", StringComparison.Ordinal))
        {
            flags[PdfFeature.DocumentPdfA] = true;
        }

        // Value-carrying markers need the literal (whitespace-normalised) text.
        string normalised = CollapseWhitespace(text);
        if (normalised.Contains("PatternType 1", StringComparison.Ordinal))
        {
            flags[PdfFeature.PatternTiling] = true;
        }

        if (normalised.Contains("PatternType 2", StringComparison.Ordinal))
        {
            flags[PdfFeature.PatternShading] = true;
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> occurs as a complete PDF name
    /// (<c>/Name</c> followed by a delimiter or whitespace), so <c>/Type1</c>
    /// does not match inside <c>/Type1C</c>.
    /// </summary>
    private static bool HasName(string text, string name)
    {
        string needle = "/" + name;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            int after = index + needle.Length;
            if (after >= text.Length || IsNameTerminator(text[after]))
            {
                return true;
            }

            index = after;
        }

        return false;
    }

    private static bool IsNameTerminator(char c)
        => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0' or '/'
            or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%';

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inSpace = false;
        foreach (char c in text)
        {
            bool isSpace = c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0';
            if (isSpace)
            {
                inSpace = true;
                continue;
            }

            if (inSpace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            inSpace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    // ==================================================================
    // structural pass
    // ==================================================================

    private sealed class Scan
    {
        private static readonly string[] FontSubtypes =
        {
            "Type1", "MMType1", "TrueType", "Type0", "Type3",
            "Type1C", "CIDFontType0", "CIDFontType2", "OpenType",
        };

        private static readonly string[] CidSubtypes = { "CIDFontType0", "CIDFontType2" };

        private readonly PdfFile _file;
        private readonly Dictionary<string, bool> _flags;
        private readonly Dictionary<string, int> _counts;

        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PdfStream> _contentSeen = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Dictionary<string, object?>> _countedFonts =
            new(ReferenceEqualityComparer.Instance);

        private readonly List<Dictionary<string, object?>> _pages = new();
        private readonly List<PdfStream> _contentStreams = new();

        private int _pageTreeCount;
        private bool _encrypted;

        public Scan(byte[] bytes, Dictionary<string, bool> flags, Dictionary<string, int> counts)
        {
            _file = new PdfFile(bytes);
            _flags = flags;
            _counts = counts;
        }

        public void Run()
        {
            _encrypted = _flags[PdfFeature.DocumentEncrypted];

            int objects = 0;
            foreach (int number in _file.ObjectNumbers)
            {
                objects++;
                try
                {
                    object? obj = _file.GetObject(number);
                    if (obj is not null)
                    {
                        Visit(obj, 0);
                    }
                }
                catch
                {
                    // A single malformed object must not abort the whole sweep.
                }
            }

            bool catalog = false;
            try
            {
                catalog = _file.FindCatalog() is not null;
            }
            catch
            {
                catalog = false;
            }

            if (catalog)
            {
                _flags[PdfFeature.ProbeCatalog] = true;
            }

            _flags[PdfFeature.ProbeStructuredParsed] = catalog || _pages.Count > 0 || objects > 0;

            _counts["count.objects"] = objects;
            _counts["count.pages"] = _pages.Count > 0 ? _pages.Count : _pageTreeCount;

            ScanContent();
        }

        // --------------------------------------------------------------
        // object-graph walk
        // --------------------------------------------------------------

        private void Visit(object? value, int depth)
        {
            if (value is null || depth > 24)
            {
                return;
            }

            object? resolved;
            try
            {
                resolved = _file.Resolve(value);
            }
            catch
            {
                return;
            }

            switch (resolved)
            {
                case PdfStream stream:
                    if (!_visited.Add(stream))
                    {
                        return;
                    }

                    ClassifyDict(stream.Dict, stream);
                    foreach (KeyValuePair<string, object?> pair in stream.Dict)
                    {
                        Visit(pair.Value, depth + 1);
                    }

                    break;

                case Dictionary<string, object?> dict:
                    if (!_visited.Add(dict))
                    {
                        return;
                    }

                    ClassifyDict(dict, null);
                    foreach (KeyValuePair<string, object?> pair in dict)
                    {
                        Visit(pair.Value, depth + 1);
                    }

                    break;

                case List<object?> list:
                    foreach (object? item in list)
                    {
                        Visit(item, depth + 1);
                    }

                    break;
            }
        }

        // --------------------------------------------------------------
        // dictionary classification
        // --------------------------------------------------------------

        private void ClassifyDict(Dictionary<string, object?> dict, PdfStream? stream)
        {
            string? type = NameOf(dict, "Type");
            string? subtype = NameOf(dict, "Subtype");

            if (type == "Catalog")
            {
                _flags[PdfFeature.ProbeCatalog] = true;
            }

            switch (type)
            {
                case "Page":
                    _pages.Add(dict);
                    if (dict.ContainsKey("Annots"))
                    {
                        _flags[PdfFeature.DocumentAnnotations] = true;
                    }

                    AddContents(dict.GetValueOrDefault("Contents"));
                    break;

                case "Pages":
                    int count = NumberOf(dict, "Count") ?? 0;
                    _pageTreeCount = Math.Max(_pageTreeCount, count);
                    break;

                case "Annot":
                    _flags[PdfFeature.DocumentAnnotations] = true;
                    _counts["count.annots"] = _counts.GetValueOrDefault("count.annots") + 1;
                    break;

                case "ExtGState":
                    ClassifyExtGState(dict);
                    break;

                case "XRef":
                    _flags[PdfFeature.DocumentXrefStream] = true;
                    break;

                case "ObjStm":
                    _flags[PdfFeature.DocumentObjectStreams] = true;
                    break;

                case "OCG":
                case "OCMD":
                    _flags[PdfFeature.DocumentOcg] = true;
                    _counts["count.ocgs"] = _counts.GetValueOrDefault("count.ocgs") + 1;
                    break;
            }

            if (subtype is not null && FontSubtypes.Contains(subtype, StringComparer.Ordinal))
            {
                ClassifyFont(dict, subtype);
            }

            if (subtype == "Image")
            {
                ClassifyImage(dict);
            }
            else if (subtype == "Form")
            {
                _flags[PdfFeature.XObjectForm] = true;
                _counts["count.formXObjects"] = _counts.GetValueOrDefault("count.formXObjects") + 1;
                if (stream is not null)
                {
                    AddContentStream(stream);
                }
            }
            else if (subtype == "PS")
            {
                _flags[PdfFeature.XObjectPostScript] = true;
            }

            if (type != "ExtGState" && subtype != "Image" &&
                (dict.ContainsKey("ca") || dict.ContainsKey("CA") ||
                 dict.ContainsKey("SMask") || dict.ContainsKey("BM")))
            {
                ClassifyExtGState(dict);
            }

            if (type == "FontDescriptor" || dict.ContainsKey("FontFile") ||
                dict.ContainsKey("FontFile2") || dict.ContainsKey("FontFile3"))
            {
                _flags[PdfFeature.FontEmbedded] = true;
            }

            ClassifyKeys(dict);

            if (dict.ContainsKey("PatternType"))
            {
                _flags[PdfFeature.PatternAny] = true;
                _counts["count.patterns"] = _counts.GetValueOrDefault("count.patterns") + 1;
                int patternType = NumberOf(dict, "PatternType") ?? 0;
                if (patternType == 1)
                {
                    _flags[PdfFeature.PatternTiling] = true;
                }
                else if (patternType == 2)
                {
                    _flags[PdfFeature.PatternShading] = true;
                }
            }

            if (dict.ContainsKey("ShadingType"))
            {
                _flags[PdfFeature.ShadingAny] = true;
                _counts["count.shadings"] = _counts.GetValueOrDefault("count.shadings") + 1;
            }

            ClassifyArrayHeads(dict);
        }

        private void ClassifyKeys(Dictionary<string, object?> dict)
        {
            if (dict.ContainsKey("PieceInfo"))
            {
                _flags[PdfFeature.DocumentPieceInfo] = true;
            }

            if (dict.ContainsKey("AIPrivateData"))
            {
                _flags[PdfFeature.DocumentAiPrivateData] = true;
            }

            if (dict.ContainsKey("Metadata"))
            {
                _flags[PdfFeature.DocumentMetadataXmp] = true;
                if (_file.Resolve(dict.GetValueOrDefault("Metadata")) is PdfStream xmp)
                {
                    ScanXmpForPdfA(xmp);
                }
            }

            if (dict.ContainsKey("OCProperties"))
            {
                _flags[PdfFeature.DocumentOptionalContent] = true;
            }

            if (dict.ContainsKey("OCGs"))
            {
                _flags[PdfFeature.DocumentOcg] = true;
            }

            if (dict.ContainsKey("EmbeddedFiles"))
            {
                _flags[PdfFeature.DocumentEmbeddedFiles] = true;
            }

            if (dict.ContainsKey("Outlines"))
            {
                _flags[PdfFeature.DocumentOutlines] = true;
            }

            if (dict.ContainsKey("OutputIntents"))
            {
                _flags[PdfFeature.ResOutputIntents] = true;
            }

            if (dict.ContainsKey("CropBox"))
            {
                _flags[PdfFeature.DocumentCropBox] = true;
            }

            if (NumberOf(dict, "Rotate") is int rotate && rotate != 0)
            {
                _flags[PdfFeature.DocumentRotate] = true;
            }

            if (dict.ContainsKey("MarkInfo"))
            {
                _flags[PdfFeature.DocumentTagged] = true;
            }

            if (dict.ContainsKey("Group"))
            {
                _flags[PdfFeature.TransparencyGroup] = true;
            }

            if (dict.ContainsKey("Resources"))
            {
                _flags[PdfFeature.ResResources] = true;
            }

            if (dict.ContainsKey("XObject"))
            {
                _flags[PdfFeature.ResXObject] = true;
            }

            if (dict.ContainsKey("Font"))
            {
                _flags[PdfFeature.ResFont] = true;
            }

            if (dict.ContainsKey("ColorSpace"))
            {
                _flags[PdfFeature.ResColorSpace] = true;
            }

            if (dict.ContainsKey("ExtGState"))
            {
                _flags[PdfFeature.ResExtGState] = true;
            }

            if (dict.ContainsKey("Shading"))
            {
                _flags[PdfFeature.ResShading] = true;
                _flags[PdfFeature.ShadingDictResource] = true;
            }

            if (dict.ContainsKey("Pattern"))
            {
                _flags[PdfFeature.ResPattern] = true;
                _flags[PdfFeature.PatternDictResource] = true;
            }

            if (dict.ContainsKey("Properties"))
            {
                _flags[PdfFeature.ResProperties] = true;
            }
        }

        private void ClassifyArrayHeads(Dictionary<string, object?> dict)
        {
            foreach (KeyValuePair<string, object?> pair in dict)
            {
                if (SafeResolve(pair.Value) is not List<object?> list || list.Count == 0)
                {
                    continue;
                }

                if (SafeResolve(list[0]) is not PdfName head)
                {
                    continue;
                }

                switch (head.Value)
                {
                    case "ICCBased":
                        _flags[PdfFeature.ResIccBased] = true;
                        break;
                    case "Separation":
                        _flags[PdfFeature.ResSeparation] = true;
                        break;
                    case "DeviceN":
                        _flags[PdfFeature.ResDeviceN] = true;
                        break;
                    case "Indexed":
                    case "I":
                        _flags[PdfFeature.ResIndexed] = true;
                        break;
                    case "Lab":
                        _flags[PdfFeature.ResLab] = true;
                        break;
                    case "Pattern":
                        _flags[PdfFeature.ResPatternColorSpace] = true;
                        break;
                }
            }
        }

        private void ClassifyExtGState(Dictionary<string, object?> dict)
        {
            _flags[PdfFeature.GsExtGState] = true;
            _counts["count.extGStates"] = _counts.GetValueOrDefault("count.extGStates") + 1;

            if (dict.ContainsKey("SMask"))
            {
                _flags[PdfFeature.GsSoftMask] = true;
            }

            if (dict.ContainsKey("BM"))
            {
                _flags[PdfFeature.GsBlendMode] = true;
                if (NameOf(dict, "BM") is string mode && mode != "Normal")
                {
                    _flags[PdfFeature.GsBlendModeNonNormal] = true;
                }
            }

            if (dict.ContainsKey("CA"))
            {
                _flags[PdfFeature.GsStrokeAlpha] = true;
                if (DoubleOf(dict, "CA") is double strokeAlpha && strokeAlpha < 1.0)
                {
                    _flags[PdfFeature.GsStrokeAlphaTranslucent] = true;
                }
            }

            if (dict.ContainsKey("ca"))
            {
                _flags[PdfFeature.GsFillAlpha] = true;
                if (DoubleOf(dict, "ca") is double fillAlpha && fillAlpha < 1.0)
                {
                    _flags[PdfFeature.GsFillAlphaTranslucent] = true;
                }
            }

            if (dict.ContainsKey("Group"))
            {
                _flags[PdfFeature.GsTransparencyGroup] = true;
                _flags[PdfFeature.TransparencyGroup] = true;
            }

            if (dict.ContainsKey("TR") || dict.ContainsKey("TR2"))
            {
                _flags[PdfFeature.GsTransferFunction] = true;
            }
        }

        private void ClassifyFont(Dictionary<string, object?> font, string subtype)
        {
            _flags[PdfFeature.FontAny] = true;

            bool descendant = CidSubtypes.Contains(subtype, StringComparer.Ordinal);
            if (!descendant && _countedFonts.Add(font))
            {
                _counts["count.fonts"] = _counts.GetValueOrDefault("count.fonts") + 1;
            }

            switch (subtype)
            {
                case "Type1":
                    _flags[PdfFeature.FontType1] = true;
                    break;
                case "MMType1":
                    _flags[PdfFeature.FontType1] = true;
                    _flags[PdfFeature.FontMmType1] = true;
                    break;
                case "TrueType":
                    _flags[PdfFeature.FontTrueType] = true;
                    break;
                case "Type0":
                    _flags[PdfFeature.FontType0] = true;
                    break;
                case "Type3":
                    _flags[PdfFeature.FontType3] = true;
                    break;
                case "Type1C":
                    _flags[PdfFeature.FontCff] = true;
                    break;
                case "CIDFontType0":
                    _flags[PdfFeature.FontCidType0] = true;
                    _flags[PdfFeature.FontCff] = true;
                    break;
                case "CIDFontType2":
                    _flags[PdfFeature.FontCidType2] = true;
                    break;
                case "OpenType":
                    _flags[PdfFeature.FontOpenType] = true;
                    break;
                default:
                    _flags[PdfFeature.FontSubtypeUnknown] = true;
                    break;
            }

            if (font.ContainsKey("ToUnicode"))
            {
                _flags[PdfFeature.FontToUnicode] = true;
            }

            switch (SafeResolve(font.GetValueOrDefault("Encoding")))
            {
                case PdfName encoding:
                    ClassifyEncodingName(encoding.Value);
                    break;

                case Dictionary<string, object?> encodingDict:
                    if (encodingDict.ContainsKey("CMapName"))
                    {
                        _flags[PdfFeature.FontCmapName] = true;
                    }

                    if (encodingDict.ContainsKey("Differences"))
                    {
                        _flags[PdfFeature.FontEncodingDifferences] = true;
                    }

                    if (NameOf(encodingDict, "BaseEncoding") is string baseEncoding)
                    {
                        ClassifyEncodingName(baseEncoding);
                    }

                    break;

                case PdfStream encodingStream:
                    _flags[PdfFeature.FontEncodingStream] = true;
                    if (encodingStream.Dict.ContainsKey("CMapName"))
                    {
                        _flags[PdfFeature.FontCmapName] = true;
                    }

                    break;
            }

            Dictionary<string, object?>? descriptor = DescriptorOf(font);
            string? program = descriptor is null ? null : ProgramKind(descriptor);
            if (program is not null)
            {
                _flags[PdfFeature.FontEmbedded] = true;
                switch (program)
                {
                    case "Type1C":
                    case "CIDFontType0C":
                        _flags[PdfFeature.FontCff] = true;
                        break;
                    case "OpenType":
                        _flags[PdfFeature.FontOpenType] = true;
                        break;
                }
            }
            else if (subtype != "Type3" && !descendant)
            {
                // Type3 glyphs are inline CharProcs, not an embedded programme, so
                // they are deliberately not counted as "non-embedded".
                _flags[PdfFeature.FontNonEmbedded] = true;
            }
        }

        private void ClassifyEncodingName(string name)
        {
            if (name.Contains("Identity", StringComparison.Ordinal))
            {
                _flags[PdfFeature.FontIdentityH] = true;
            }
        }

        private void ClassifyImage(Dictionary<string, object?> dict)
        {
            _flags[PdfFeature.ImageAny] = true;
            _flags[PdfFeature.XObjectImage] = true;
            _counts["count.images"] = _counts.GetValueOrDefault("count.images") + 1;

            if (dict.ContainsKey("SMask"))
            {
                _flags[PdfFeature.ImageSoftMask] = true;
                _flags[PdfFeature.TransparencySoftMaskImage] = true;
            }

            if (dict.ContainsKey("Mask"))
            {
                _flags[PdfFeature.ImageColorKeyMask] = true;
            }

            if (SafeResolve(dict.GetValueOrDefault("ImageMask")) is bool imageMask && imageMask)
            {
                _flags[PdfFeature.ImageMask] = true;
            }

            foreach (string filter in FiltersOf(dict))
            {
                switch (filter)
                {
                    case "DCTDecode":
                        _flags[PdfFeature.ImageDct] = true;
                        break;
                    case "JPXDecode":
                        _flags[PdfFeature.ImageJpx] = true;
                        break;
                    case "CCITTFaxDecode":
                        _flags[PdfFeature.ImageCcitt] = true;
                        break;
                    case "JBIG2Decode":
                        _flags[PdfFeature.ImageJbig2] = true;
                        break;
                    case "RunLengthDecode":
                        _flags[PdfFeature.ImageRunLength] = true;
                        break;
                    case "FlateDecode":
                        _flags[PdfFeature.ImageFlate] = true;
                        break;
                    case "LZWDecode":
                        _flags[PdfFeature.ImageLzw] = true;
                        break;
                }
            }
        }

        private IEnumerable<string> FiltersOf(Dictionary<string, object?> dict)
        {
            object? filter = SafeResolve(dict.GetValueOrDefault("Filter"));
            if (filter is PdfName name)
            {
                yield return name.Value;
                yield break;
            }

            if (filter is List<object?> list)
            {
                foreach (object? entry in list)
                {
                    if (SafeResolve(entry) is PdfName entryName)
                    {
                        yield return entryName.Value;
                    }
                }
            }
        }

        private Dictionary<string, object?>? DescriptorOf(Dictionary<string, object?> font)
        {
            if (SafeResolveDict(font.GetValueOrDefault("FontDescriptor")) is { } direct)
            {
                return direct;
            }

            if (SafeResolve(font.GetValueOrDefault("DescendantFonts")) is List<object?> descendants &&
                descendants.Count > 0 &&
                SafeResolveDict(descendants[0]) is { } descendant)
            {
                return SafeResolveDict(descendant.GetValueOrDefault("FontDescriptor"));
            }

            return null;
        }

        private string? ProgramKind(Dictionary<string, object?> descriptor)
        {
            if (SafeResolve(descriptor.GetValueOrDefault("FontFile")) is PdfStream)
            {
                return "Type1";
            }

            if (SafeResolve(descriptor.GetValueOrDefault("FontFile2")) is PdfStream)
            {
                return "TrueType";
            }

            if (SafeResolve(descriptor.GetValueOrDefault("FontFile3")) is PdfStream cff)
            {
                return (cff.Dict.GetValueOrDefault("Subtype") as PdfName)?.Value ?? "Type1C";
            }

            return null;
        }

        private void ScanXmpForPdfA(PdfStream stream)
        {
            if (stream.Raw.Length == 0 || stream.Raw.Length > 4 * 1024 * 1024)
            {
                return;
            }

            try
            {
                string xml = Encoding.Latin1.GetString(_file.GetStreamData(stream));
                if (xml.Contains("pdfaid", StringComparison.Ordinal))
                {
                    _flags[PdfFeature.DocumentPdfA] = true;
                }
            }
            catch
            {
                // Metadata is a nice-to-have; a broken XMP stream is not a probe failure.
            }
        }

        // --------------------------------------------------------------
        // content streams
        // --------------------------------------------------------------

        private void AddContents(object? contents)
        {
            object? resolved = SafeResolve(contents);
            if (resolved is PdfStream single)
            {
                AddContentStream(single);
                return;
            }

            if (resolved is List<object?> list)
            {
                foreach (object? entry in list)
                {
                    if (SafeResolve(entry) is PdfStream stream)
                    {
                        AddContentStream(stream);
                    }
                }
            }
        }

        private void AddContentStream(PdfStream stream)
        {
            if (_contentSeen.Add(stream))
            {
                _contentStreams.Add(stream);
            }
        }

        private void ScanContent()
        {
            if (_encrypted)
            {
                // An encrypted file's content streams are ciphertext; tokenising
                // them would manufacture operators that are not there.
                return;
            }

            int streams = 0;
            long bytes = 0;
            foreach (PdfStream stream in _contentStreams)
            {
                byte[] data;
                try
                {
                    data = _file.GetStreamData(stream);
                }
                catch
                {
                    continue;
                }

                if (data.Length == 0)
                {
                    continue;
                }

                streams++;
                bytes += data.Length;
                ScanOperators(data);
            }

            _counts["count.contentStreams"] = streams;
            _counts["count.contentBytes"] = (int)Math.Min(bytes, int.MaxValue);
        }

        private void ScanOperators(byte[] content)
        {
            var token = new StringBuilder(16);
            int i = 0;

            string Flush()
            {
                if (token.Length == 0)
                {
                    return string.Empty;
                }

                string op = token.ToString();
                token.Clear();
                CountOperator(op);
                return op;
            }

            bool FlushAndSkipInlineImage()
            {
                if (Flush() != "BI")
                {
                    return false;
                }

                int id = FindToken(content, i, "ID");
                if (id < 0)
                {
                    i = content.Length;
                    return true;
                }

                int ei = FindToken(content, id + 2, "EI");
                i = ei < 0 ? content.Length : ei + 2;
                return true;
            }

            while (i < content.Length)
            {
                byte c = content[i];

                if (c == (byte)'%')
                {
                    FlushAndSkipInlineImage();
                    while (i < content.Length && content[i] != (byte)'\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == (byte)'(')
                {
                    FlushAndSkipInlineImage();
                    i = SkipLiteralString(content, i);
                    continue;
                }

                if (c == (byte)'<')
                {
                    FlushAndSkipInlineImage();
                    if (i + 1 < content.Length && content[i + 1] == (byte)'<')
                    {
                        i += 2;
                        continue;
                    }

                    while (i < content.Length && content[i] != (byte)'>')
                    {
                        i++;
                    }

                    i++;
                    continue;
                }

                if (c == (byte)'>')
                {
                    FlushAndSkipInlineImage();
                    i += i + 1 < content.Length && content[i + 1] == (byte)'>' ? 2 : 1;
                    continue;
                }

                if (c == (byte)'/')
                {
                    FlushAndSkipInlineImage();
                    i++;
                    while (i < content.Length && !IsWhitespace(content[i]) && !IsDelimiter(content[i]))
                    {
                        i++;
                    }

                    continue;
                }

                if (IsWhitespace(c) || c is (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
                {
                    FlushAndSkipInlineImage();
                    i++;
                    continue;
                }

                token.Append((char)c);
                i++;
            }

            Flush();
        }

        private void CountOperator(string op)
        {
            switch (op)
            {
                case "m":
                    Increment("op.m");
                    Mark(PdfFeature.OpMove);
                    Mark(PdfFeature.OpAny);
                    break;
                case "l":
                    Increment("op.l");
                    Mark(PdfFeature.OpLine);
                    Mark(PdfFeature.OpAny);
                    break;
                case "c":
                    Increment("op.c");
                    Mark(PdfFeature.OpCurve);
                    Mark(PdfFeature.OpAny);
                    break;
                case "v":
                    Increment("op.v");
                    Mark(PdfFeature.OpCurveV);
                    Mark(PdfFeature.OpAny);
                    break;
                case "y":
                    Increment("op.y");
                    Mark(PdfFeature.OpCurveY);
                    Mark(PdfFeature.OpAny);
                    break;
                case "re":
                    Increment("op.re");
                    Mark(PdfFeature.OpRect);
                    Mark(PdfFeature.OpAny);
                    break;
                case "h":
                    Increment("op.h");
                    Mark(PdfFeature.OpClose);
                    Mark(PdfFeature.OpAny);
                    break;
                case "f":
                case "F":
                    Increment("op.f");
                    Mark(PdfFeature.OpFill);
                    Mark(PdfFeature.OpAny);
                    break;
                case "f*":
                    Increment("op.f");
                    Mark(PdfFeature.OpFill);
                    Mark(PdfFeature.OpEvenOdd);
                    Mark(PdfFeature.OpAny);
                    break;
                case "S":
                case "s":
                    Increment("op.S");
                    Mark(PdfFeature.OpStroke);
                    Mark(PdfFeature.OpAny);
                    break;
                case "B":
                case "b":
                    Increment("op.B");
                    Mark(PdfFeature.OpFill);
                    Mark(PdfFeature.OpStroke);
                    Mark(PdfFeature.OpFillStroke);
                    Mark(PdfFeature.OpAny);
                    break;
                case "B*":
                case "b*":
                    Increment("op.B");
                    Mark(PdfFeature.OpFill);
                    Mark(PdfFeature.OpStroke);
                    Mark(PdfFeature.OpFillStroke);
                    Mark(PdfFeature.OpEvenOdd);
                    Mark(PdfFeature.OpAny);
                    break;
                case "W":
                    Increment("op.W");
                    Mark(PdfFeature.OpClip);
                    Mark(PdfFeature.OpAny);
                    break;
                case "W*":
                    Increment("op.W");
                    Mark(PdfFeature.OpClip);
                    Mark(PdfFeature.OpClipEvenOdd);
                    Mark(PdfFeature.OpEvenOdd);
                    Mark(PdfFeature.OpAny);
                    break;
                case "gs":
                    Increment("op.gs");
                    Mark(PdfFeature.OpGs);
                    Mark(PdfFeature.OpAny);
                    break;
                case "Do":
                    Increment("op.Do");
                    Mark(PdfFeature.OpDo);
                    Mark(PdfFeature.OpAny);
                    break;
                case "sh":
                    Increment("op.sh");
                    Mark(PdfFeature.OpShading);
                    Mark(PdfFeature.ShadingAny);
                    Mark(PdfFeature.OpAny);
                    break;
                case "Tj":
                case "TJ":
                case "'":
                case "\"":
                    Increment("op.text");
                    Mark(PdfFeature.OpText);
                    Mark(PdfFeature.OpAny);
                    break;
                case "BI":
                    Increment("op.inlineImage");
                    Mark(PdfFeature.OpInlineImage);
                    Mark(PdfFeature.OpAny);
                    break;
            }
        }

        private void Mark(string feature) => _flags[feature] = true;

        private void Increment(string counter)
            => _counts[counter] = _counts.GetValueOrDefault(counter) + 1;

        private static int SkipLiteralString(byte[] data, int index)
        {
            index++; // (
            int depth = 1;
            while (index < data.Length && depth > 0)
            {
                byte c = data[index++];
                if (c == (byte)'\\')
                {
                    index++;
                    continue;
                }

                if (c == (byte)'(')
                {
                    depth++;
                }
                else if (c == (byte)')')
                {
                    depth--;
                }
            }

            return index;
        }

        private static int FindToken(byte[] data, int from, string token)
        {
            byte[] pattern = Encoding.ASCII.GetBytes(token);
            for (int i = Math.Max(0, from); i <= data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                {
                    continue;
                }

                bool leftOk = i == 0 || IsWhitespace(data[i - 1]) || IsDelimiter(data[i - 1]);
                int after = i + pattern.Length;
                bool rightOk = after >= data.Length || IsWhitespace(data[after]) || IsDelimiter(data[after]);
                if (leftOk && rightOk)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsWhitespace(byte c)
            => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;

        private static bool IsDelimiter(byte c)
            => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'['
                or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        // --------------------------------------------------------------
        // small resolution helpers
        // --------------------------------------------------------------

        private object? SafeResolve(object? value)
        {
            try
            {
                return _file.Resolve(value);
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, object?>? SafeResolveDict(object? value)
        {
            try
            {
                return _file.ResolveDict(value);
            }
            catch
            {
                return null;
            }
        }

        private string? NameOf(Dictionary<string, object?> dict, string key)
            => SafeResolve(dict.GetValueOrDefault(key)) is PdfName name ? name.Value : null;

        private int? NumberOf(Dictionary<string, object?> dict, string key)
            => SafeResolve(dict.GetValueOrDefault(key)) switch
            {
                double d => (int)d,
                long l => (int)l,
                _ => null,
            };

        private double? DoubleOf(Dictionary<string, object?> dict, string key)
            => SafeResolve(dict.GetValueOrDefault(key)) switch
            {
                double d => d,
                long l => l,
                _ => null,
            };
    }
}
