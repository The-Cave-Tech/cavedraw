using VCCad.Geometry;

using VCCad.Core.Text;

namespace VCCad.Core.Model;

/// <summary>A styled span of text. Rich text is a list of runs so different
/// words can use different fonts/sizes/weights within one object.</summary>
public sealed class TextRun
{
    public string Text { get; set; } = string.Empty;

    /// <summary>Font family name (must resolve to an embedded/bundled font for PDF).</summary>
    public string FontFamily { get; set; } = TextItem.DefaultFontFamily;

    public double FontSize { get; set; } = 12.0;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    /// <summary>
    /// Advance width the run should occupy, in model units. Set when importing a
    /// PDF so a substituted font can be scaled horizontally to the original
    /// metrics (otherwise wide fallback fonts reflow/overlap the layout).
    /// <c>null</c> means "use the renderer's natural width".
    ///
    /// This is the whole distance to the next run, so it already includes
    /// <see cref="GapAfter"/>.
    /// </summary>
    public double? AdvanceWidth { get; set; }

    /// <summary>
    /// The part of <see cref="AdvanceWidth"/> that is white space rather than glyphs: the
    /// room a <c>TJ</c> array left before the next piece of the same line. Zero for text
    /// that simply flows.
    ///
    /// Kept apart from the total because export has to put it back where the file had it.
    /// A letter-spaced heading drawn as one show operation per pair of glyphs comes apart
    /// on the way out otherwise: the pieces land in the right places but each is its own
    /// text object, so an extractor reads "IN TR OD UC TI ON" where the file says
    /// "INTRODUCTION". A negative adjustment before the next piece distributes the same
    /// room between the glyphs, which is what the file did.
    /// </summary>
    public double GapAfter { get; set; }

    /// <summary>
    /// Extra room after **every** glyph of this run, in model units - SVG's and CSS's
    /// <c>letter-spacing</c>. Zero means the face's own advances, unchanged.
    ///
    /// This is deliberately not folded into <see cref="AdvanceWidth"/>. That member is the whole
    /// distance to the next run, and a renderer that cannot add room between glyphs meets it by
    /// setting the run wider - so widening it to express tracking stretches the letters instead of
    /// spacing them, which is a different picture and the reason the field exists. Stored per run
    /// because a `<tspan>` states it for its own characters.
    ///
    /// A percentage resolves against the run's own font size on the way in, so what is kept here is
    /// always a length.
    /// </summary>
    public double LetterSpacing { get; set; }

    /// <summary>
    /// Extra room after each space of this run, in model units - CSS's <c>word-spacing</c>. Zero
    /// means the space's own advance, unchanged.
    ///
    /// Separate from <see cref="LetterSpacing"/> because the two are added to different characters:
    /// tracking widens every gap between letters, word spacing only the gaps between words, and a
    /// file that asks for one must not get the other. A percentage resolves against the run's own
    /// size on the way in, so what is kept here is always a length.
    /// </summary>
    public double WordSpacing { get; set; }

    /// <summary>
    /// The room this run's characters take including the tracking it asks for, one entry per
    /// character.
    ///
    /// The face's own advances come from the installed measurer; the tracking is added here because it is
    /// stored per run and belongs to the run, not to the face. Everything that moves a pen - the layout, the
    /// caret, the wrap point, the bounds, export - goes through this, so tracking cannot be applied in one
    /// place and forgotten in another.
    /// </summary>
    public IReadOnlyList<double> Advances()
    {
        IReadOnlyList<double> advances = TextMeasurement.Advances(this);
        if (LetterSpacing == 0 && WordSpacing == 0)
        {
            return advances;
        }

        var spaced = new double[advances.Count];
        for (int i = 0; i < advances.Count; i++)
        {
            spaced[i] = advances[i] + LetterSpacing + (IsSpace(i) ? WordSpacing : 0.0);
        }

        return spaced;
    }

    /// <summary>Whether the character at <paramref name="index"/> is one `word-spacing` widens.</summary>
    private bool IsSpace(int index)
        => index >= 0 && index < Text.Length && Text[index] == ' ';

    /// <summary>
    /// The colour this run is drawn in, or <c>null</c> to draw it in the block's
    /// <see cref="TextItem.Color"/>.
    ///
    /// A block may hold several colours, which is what SVG states with a `<tspan fill=...>` and what a
    /// PDF content stream states by setting a colour partway through a text object. The block's own colour
    /// stays the default so a document that has one colour per block is unchanged; a run that differs
    /// carries its own.
    /// </summary>
    public ColorRgb? Color { get; set; }

    /// <summary>
    /// The width axis the file asked the face for, as CSS spells it - <c>condensed</c>, <c>semi-expanded</c>,
    /// <c>110%</c> - or <c>null</c> when nothing said one.
    ///
    /// Kept as the file wrote it because that is the whole fact: the model names one family per run and does not
    /// choose a face by width, so nothing downstream can turn a normalised number back into the word the author
    /// used, and inventing one would be data the file has not got.
    /// </summary>
    public string? FontStretch { get; set; }

    /// <summary>
    /// The variant the file asked the face for, as CSS spells it - <c>small-caps</c> - or <c>null</c> when
    /// nothing said one. Kept for the same reason as <see cref="FontStretch"/>.
    /// </summary>
    public string? FontVariant { get; set; }

    /// <summary>
    /// The ascent the block's top-left was placed with, as a fraction of the em, when it was
    /// imported. Zero means "not recorded — work it out from the face".
    ///
    /// The model stores a block's top-left, but PDF places text on the baseline, so getting
    /// back to the baseline means subtracting an ascent. That has to be the <em>same</em>
    /// ascent on the way out as on the way in, or the text lands beside where it was: a
    /// substituted face takes its ascent from the standard-font table while the exporter was
    /// taking one from the installed TrueType's ascender, and the licence block on the
    /// LILLIE sample sat 1.468pt too high on eleven pages because of it.
    ///
    /// Kept per run because the face, and so the ascent, is per run.
    /// </summary>
    public double PlacedAscentEm { get; set; }

    /// <summary>
    /// The font the document asked for (for example <c>Helvetica-Bold</c>), kept even
    /// when the file does not embed it and the run is drawn with a substitute. Without
    /// it a substitution cannot be reported in terms the person recognises.
    /// </summary>
    public string? SourceFont { get; set; }

    /// <summary>The embedded font programme this run was drawn with, when the
    /// source PDF embedded one. Null means the renderer should substitute.</summary>
    public EmbeddedFont? EmbeddedFont { get; set; }

    /// <summary>Original glyph codes (one char per byte) for pass-through export
    /// when <see cref="EmbeddedFont"/> is set.</summary>
    public string? RawCodes { get; set; }

    /// <summary>Glyph indices for rendering with <see cref="EmbeddedFont"/>
    /// (bare CFF has no Unicode cmap, so the canvas draws by glyph id).</summary>
    public ushort[]? GlyphIds { get; set; }

    public TextRun Clone() => new()
    {
        Text = Text,
        FontFamily = FontFamily,
        FontSize = FontSize,
        Bold = Bold,
        Italic = Italic,
        AdvanceWidth = AdvanceWidth,
        GapAfter = GapAfter,
        LetterSpacing = LetterSpacing,
        WordSpacing = WordSpacing,
        Color = Color,
        FontStretch = FontStretch,
        FontVariant = FontVariant,
        PlacedAscentEm = PlacedAscentEm,
        SourceFont = SourceFont,
        EmbeddedFont = EmbeddedFont,
        RawCodes = RawCodes,
        GlyphIds = GlyphIds,
    };
}

/// <summary>Horizontal alignment of text lines within a text block.</summary>
public enum TextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>
/// A text object. Rich text is represented as an ordered list of
/// <see cref="TextRun"/>s; newlines inside a run start a new line. The origin is
/// the top-left of the text block, in artboard-local coordinates (like paths).
///
/// Widths used for bounds/selection are estimated here (font metrics live in the
/// PDF layer); the canvas measures precisely with the UI font stack.
/// </summary>
public sealed class TextItem : LayerItem
{
    /// <summary>Font bundled with VCCad and embedded in exported PDFs.</summary>
    public const string DefaultFontFamily = "Nimbus Sans";

    /// <summary>Top-left of the text block, artboard-local.</summary>
    public Point2D Origin { get; set; }

    /// <summary>Rich-text runs in order.</summary>
    public List<TextRun> Runs { get; } = new();

    /// <summary>Text colour.</summary>
    public ColorRgb Color { get; set; } = ColorRgb.Black;

    /// <summary>Rotation of the text block about its origin, in radians.</summary>
    public double RotationRadians { get; set; }

    /// <summary>
    /// Whether the block is mirrored across its own vertical axis - a horizontal flip.
    ///
    /// The mirror is STATE, not baked geometry, for the reason §9 of AGENTS.md gives: a run has to
    /// keep its full string and one glyph id per character, and rewriting the text into reversed
    /// outlines would destroy both. It is applied in the block's own space, before the rotation, so
    /// a mirrored block that is turned still reads correctly.
    /// </summary>
    public bool MirrorX { get; set; }

    /// <summary>Whether the block is mirrored across its own horizontal axis - a vertical flip.</summary>
    public bool MirrorY { get; set; }

    /// <summary>The sign the block's own x axis runs in: -1 when mirrored horizontally.</summary>
    public double XSign => MirrorX ? -1.0 : 1.0;

    /// <summary>The sign the block's own y axis runs in.</summary>
    public double YSign => MirrorY ? -1.0 : 1.0;

    /// <summary>
    /// Width of the text frame, in model units. Zero means the block grows to fit its
    /// content; a positive value wraps the text inside it, which is what makes a drawn
    /// text box behave like a text box rather than a single endless line.
    /// </summary>
    public double FrameWidth { get; set; }

    /// <summary>
    /// Line height as a multiple of the font size. Paragraph style, not content: it
    /// changes how the block is set, never what it says.
    /// </summary>
    public double LineSpacing { get; set; } = 1.2;

    /// <summary>Extra leading inserted before each paragraph except the first.</summary>
    public double ParagraphSpacing { get; set; }

    /// <summary>Horizontal alignment of lines within the block.</summary>
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;

    /// <summary>All runs concatenated (used for simple editing/measurement).</summary>
    public string PlainText
    {
        get => string.Concat(Runs.Select(r => r.Text));
        set
        {
            if (Runs.Count == 0)
            {
                Runs.Add(new TextRun());
            }

            Runs[0].Text = value;
            for (int i = Runs.Count - 1; i >= 1; i--)
            {
                Runs.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// The colour a run is drawn in: its own when it states one, otherwise the block's.
    ///
    /// One place answers this, so a run cannot be drawn in one colour and reported in another.
    /// </summary>
    public ColorRgb ColourOf(TextRun run) => run.Color ?? Color;

    /// <summary>The largest font size among the runs (line height driver).</summary>
    public double MaxFontSize => Runs.Count == 0 ? 12.0 : Runs.Max(r => r.FontSize);

    /// <summary>
    /// Tight bounds of the block: its wrap width and the height its lines need.
    ///
    /// The line heights come from the layout engine, which measures each line from the faces
    /// actually on it. This used to size every line as the block's largest font times the line
    /// spacing, so a block whose second line was set smaller reported that line at the first line's
    /// height - and the box, the caret and the selection all drifted from the text.
    ///
    /// Widths come from the installed <see cref="ITextMetrics"/> — the shaper's own
    /// advances, kerning included — so the box reported here is the box drawn round the
    /// text. Falling back to a per-character estimate is only correct when no host has
    /// installed a measurer, which is the headless case; see
    /// <see cref="TextMeasurement.IsReal"/>.
    /// </summary>
    public Rect2D LocalBounds()
    {
        TextLayout layout = TextLayoutEngine.Compute(this);
        return new Rect2D(Origin.X, Origin.Y, layout.Width, layout.Height);
    }

    /// <summary>
    /// The block's bounds with its rotation folded in: the axis-aligned box of the turned corners.
    ///
    /// This answers "what does this text cover on the page". A caller working *inside* the block
    /// wants <see cref="LocalBounds"/> instead - a point turned back into the block's own space is
    /// upright there, and measuring it against this box compares two different things.
    /// </summary>
    public Rect2D BoundingBox()
    {
        Rect2D local = LocalBounds();

        // A mirrored block sits on the other side of its origin: the layout is unchanged, the page
        // box is not. `LocalBounds` stays in the block's own space because that is where the
        // layout, the caret and the hit tests are worked out; this is the one that answers "where
        // is it on the page".
        if (MirrorX)
        {
            local = new Rect2D(Origin.X - local.Width, local.Y, local.Width, local.Height);
        }

        if (MirrorY)
        {
            local = new Rect2D(local.X, Origin.Y - local.Height, local.Width, local.Height);
        }

        if (Math.Abs(RotationRadians) < 1e-9)
        {
            return local;
        }

        Point2D Rotate(Point2D p)
        {
            double cos = Math.Cos(RotationRadians);
            double sin = Math.Sin(RotationRadians);
            double dx = p.X - Origin.X;
            double dy = p.Y - Origin.Y;
            return new Point2D(Origin.X + dx * cos - dy * sin, Origin.Y + dx * sin + dy * cos);
        }

        return Rect2D.FromPoints(new[]
        {
            Rotate(new Point2D(local.Left, local.Top)),
            Rotate(new Point2D(local.Right, local.Top)),
            Rotate(new Point2D(local.Right, local.Bottom)),
            Rotate(new Point2D(local.Left, local.Bottom)),
        });
    }

    /// <summary>Bounds in document/world space (local bounds + artboard origin).</summary>
    public Rect2D WorldBounds()
    {
        Rect2D box = BoundingBox();
        Vector2D offset = ArtboardOffset();
        return new Rect2D(box.X + offset.X, box.Y + offset.Y, box.Width, box.Height);
    }

    /// <summary>How many explicit line breaks the block's runs contain.</summary>
    private int CountNewlines()
    {
        int count = 0;
        foreach (TextRun run in Runs)
        {
            foreach (char ch in run.Text)
            {
                if (ch == '\n')
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Copies runs/origin/colour from another text item (undo support).</summary>
    public void CopyFrom(TextItem other)
    {
        Origin = other.Origin;
        Color = other.Color;
        RotationRadians = other.RotationRadians;
        MirrorX = other.MirrorX;
        MirrorY = other.MirrorY;
        FrameWidth = other.FrameWidth;
        LineSpacing = other.LineSpacing;
        ParagraphSpacing = other.ParagraphSpacing;
        SourceCmyk = other.SourceCmyk is null ? null : (double[])other.SourceCmyk.Clone();
        Alignment = other.Alignment;
        Runs.Clear();
        Runs.AddRange(other.Runs.Select(r => r.Clone()));
    }

    /// <inheritdoc/>
    /// <summary>
    /// The CMYK components the file painted this text with, when it used DeviceCMYK, or
    /// null. See <see cref="PathItem.SourceFillCmyk"/> for why the original ink values are
    /// kept rather than derived back from the RGB.
    /// </summary>
    public double[]? SourceCmyk { get; set; }

    public override LayerItem Clone()
    {
        var copy = new TextItem
        {
            Name = Name,
            IsVisible = IsVisible,
            IsLocked = IsLocked,
            Origin = Origin,
            Color = Color,
            RotationRadians = RotationRadians,
            MirrorX = MirrorX,
            MirrorY = MirrorY,
            FrameWidth = FrameWidth,
            LineSpacing = LineSpacing,
            ParagraphSpacing = ParagraphSpacing,
            SourceCmyk = SourceCmyk is null ? null : (double[])SourceCmyk.Clone(),
            Alignment = Alignment,
        };
        copy.Runs.AddRange(Runs.Select(r => r.Clone()));
        return copy;
    }
}
