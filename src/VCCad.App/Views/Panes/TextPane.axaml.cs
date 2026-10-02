using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.Core.Model;

namespace VCCad.App.Views.Panes;

/// <summary>
/// The text panel: what the selection's words are, the face they are set in, and how the block is set.
///
/// **Every control calls the same session method a driver's operation calls.** A capability that exists only inside a
/// click handler is a defect in this repository, and the way to avoid one is for the panel to be a view of the model
/// rather than a second place the state lives: the content, the face and the block colour go through
/// <see cref="DocumentSession.ApplyTextFieldsAt"/>, alignment through <see cref="DocumentSession.SetTextAlignment"/>
/// (`text.setAlignment`), and the paragraph style through <see cref="DocumentSession.ApplyTextStyle"/> (`text.style`).
///
/// **The selection is not obliged to agree with itself**, so every field shows the **common** value or says
/// **mixed**, read through <see cref="TextSummary"/> - the same reading `StrokeSummary` gives a stroke. A field is
/// written only when the person changed it: an apply that had to invent a value for a mixed member would write one
/// block's text or colour over the other's from a field nobody touched, which is the defect the mixed readout
/// exists to remove.
///
/// **Face is per run and the model holds at most one colour per block**, so the pane is split along that line: the
/// FACE fields describe one run - named in the label, the run the caret is in while a block is open for editing -
/// and content, colour and paragraph style describe the block. The **run's own colour** (TextRun.Color) is a
/// per-run value and is offered beside the block's: the model holds one per run, an imported line may carry several,
/// and the canvas and the exporter both paint it - so the pane commits it through
/// <see cref="DocumentSession.ApplyTextFieldsAt"/> (`text.update`) exactly as the face fields are, one level below
/// the block colour that paints every run stating none of its own.
///
/// **The block's writing mode and base direction are offered too**, because the model holds both (#127) and the
/// layout, the caret, the bounds and the SVG export all act on them: they go through
/// <see cref="DocumentSession.ApplyTextStyle"/> (`text.style`) exactly as the leading and the turn do, so a person
/// and a driver set them the same way and neither can set something the other cannot. The **glyph orientation** is
/// offered for the same reason, one level down: it is the run's own (#127), the layout turns every glyph by it and
/// the SVG writer writes it back, and it goes through <see cref="DocumentSession.ApplyTextFieldsAt"/> (`text.update`)
/// exactly as the tracking and the variant do. What the model cannot hold is **not** offered here, because a control
/// that does nothing is worse than an absent one: `baseline-shift` with per-glyph positioning (#128) has no member
/// on <see cref="TextItem"/> or <see cref="TextRun"/>. Letter and word spacing, font stretch and variant **are**
/// offered, because the model does hold them (#147). Face reporting is offered too, because the model carries the
/// face actually used - see <see cref="FaceReport"/>.
/// </summary>
public partial class TextPane : UserControl
{
    private EditorViewModel? _vm;
    private bool _syncing;

    /// <summary>The word every mixed field reads, so the pane says the same thing in every place.</summary>
    private const string MixedWord = "mixed";

    /// <summary>The families the font box offers, read once: enumerating the machine's fonts on every refresh would
    /// pay for the whole system font list each time a field is committed.</summary>
    private readonly List<string> _families = new(StandardFontResolver.OfferedFamilies());

    // What the last refresh put in each control. Tracked because a field the person did not touch is **not** an
    // edit: a mixed field reads "mixed", and an apply that could not tell that from a typed value would have to
    // invent one - which is the defect the mixed readout exists to remove, one level down.
    private string _shownContent = string.Empty;
    private string _shownColor = string.Empty;
    private string _shownRunColor = string.Empty;
    private int _shownAlign = -1;
    private string _shownFamily = string.Empty;
    private string _shownSize = string.Empty;
    private bool? _shownBold;
    private bool? _shownItalic;
    private string _shownLeading = string.Empty;
    private string _shownParagraph = string.Empty;
    private string _shownRotation = string.Empty;
    private string _shownFrameWidth = string.Empty;
    private string _shownLetterSpacing = string.Empty;
    private string _shownWordSpacing = string.Empty;
    private string _shownFontStretch = string.Empty;
    private string _shownFontVariant = string.Empty;
    private int _shownOrientation = -1;
    private int _shownWritingMode = -1;
    private int _shownDirection = -1;

    public TextPane()
    {
        InitializeComponent();

        // Changes take effect when they are committed - a combo as soon as it changes, a field on Enter or on
        // losing focus - the same commit-on-interaction-end pattern the stroke inspector uses, so one gesture is one
        // undo step. Content keeps AcceptsReturn, so Enter is a newline there and only losing focus commits it.
        AlignBox.SelectionChanged += (_, _) => ApplyFields();
        OrientationBox.SelectionChanged += (_, _) => ApplyFields();
        WritingModeBox.SelectionChanged += (_, _) => ApplyFields();
        DirectionBox.SelectionChanged += (_, _) => ApplyFields();
        FamilyBox.SelectionChanged += (_, _) => ApplyFields();
        BoldBox.IsCheckedChanged += (_, _) => ApplyFields();
        ItalicBox.IsCheckedChanged += (_, _) => ApplyFields();
        ContentBox.LostFocus += (_, _) => ApplyFields();

        foreach (TextBox box in new[]
                 {
                     SizeBox, ColorBox, RunColorBox, LeadingBox, ParagraphBox, RotationBox, FrameWidthBox,
                     LetterSpacingBox, WordSpacingBox, FontStretchBox, FontVariantBox,
                 })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ApplyFields();
                    e.Handled = true;
                }
            };
            box.LostFocus += (_, _) => ApplyFields();
        }
    }

    /// <summary>Binds the panel to the editor and shows the current selection.</summary>
    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Refresh();
        vm.SelectionChanged += (_, _) => Refresh();

        // Which run the face fields describe is shared state, and a change to it - or to the selection that clamps
        // it - has to re-read the panel. The caret's own run raises no change; the run index that the panel actually
        // reads now does, so entering and leaving text edit is covered by the same notification as a driver's
        // `text.inspectRun`, and the two cannot describe different runs.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.IsEditingText) or nameof(EditorViewModel.InspectedRun))
            {
                Refresh();
            }
        };

        Refresh();
    }

    // ------------------------------------------------------------------
    // Writing: each member the person changed, through the session
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies the fields the person **actually changed** to the selected blocks, by calling the session methods the
    /// operations call rather than by building the edit here.
    ///
    /// Only the changed ones, because a selection is not obliged to agree with itself: with two blocks holding
    /// different words the content field reads "mixed", and a method that had to be given a string for it would
    /// write one block's words over the other's from a field nobody touched. Every "changed" decision is taken
    /// **before** the first apply, because a successful apply raises the document change that re-reads the panel and
    /// rewrites these controls, which would otherwise lose a field that is still pending.
    /// </summary>
    private void ApplyFields()
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        List<TextItem> items = _vm.ActiveSession.SelectedTextItems().ToList();
        if (items.Count == 0)
        {
            return;
        }

        int runIndex = InspectedRunIndex(items);
        int? faceRun = runIndex >= 0 ? runIndex : null;

        string? content = ContentBox.Text ?? string.Empty;
        content = content == _shownContent ? null : content;

        string? typedColor = ColorBox.Text ?? string.Empty;
        typedColor = typedColor == _shownColor ? null : typedColor;

        ColorRgb? color = null;
        if (typedColor is not null)
        {
            color = ParseColor(typedColor);
            if (color is null)
            {
                // A colour nobody can read is not an edit, and saying so is better than leaving the person to
                // wonder whether the panel took it.
                _vm.ReportStatus($"Text colour: expected r,g,b,a in 0..255, not \"{typedColor.Trim()}\"");
            }
        }

        string? family = (FamilyBox.SelectedItem as string ?? string.Empty) == _shownFamily
            ? null
            : FamilyBox.SelectedItem as string;
        bool? bold = BoldBox.IsChecked == _shownBold ? null : BoldBox.IsChecked;
        bool? italic = ItalicBox.IsChecked == _shownItalic ? null : ItalicBox.IsChecked;
        double? size = (SizeBox.Text?.Trim() ?? string.Empty) == _shownSize ? null : Parse(SizeBox.Text);

        // Tracking, word spacing, stretch and variant are per run, so they are face members too and stop describing
        // anything when there is no run to name.
        double? letterSpacing = faceRun is null ? null : ChangedDouble(LetterSpacingBox, _shownLetterSpacing);
        double? wordSpacing = faceRun is null ? null : ChangedDouble(WordSpacingBox, _shownWordSpacing);
        string? fontStretch = faceRun is null ? null : ChangedWord(FontStretchBox, _shownFontStretch);
        string? fontVariant = faceRun is null ? null : ChangedWord(FontVariantBox, _shownFontVariant);

        // The glyph orientation is per run too, so it stops describing anything when there is no run to name. The
        // combo's own words are `text.update`'s: mixed is the initial value and is what `auto` means.
        GlyphOrientation? orientation = faceRun is null
            || OrientationBox.SelectedIndex == _shownOrientation
            || OrientationBox.SelectedIndex < 0
                ? null
                : OrientationBox.SelectedIndex switch
                {
                    1 => GlyphOrientation.Upright,
                    2 => GlyphOrientation.Rotate,
                    _ => GlyphOrientation.Auto,
                };

        // The run's own colour is per run as well, and **it is not the block's**: the field is committed through the
        // same `ApplyTextFieldsAt` the `text.update` operation calls, one level below the Block field above. An
        // emptied field is an edit here - it is how a run gives its colour back to the block - so the value carries
        // the presence and the wrapper carries the colour.
        string typedRunColor = RunColorBox.Text?.Trim() ?? string.Empty;
        RunColorEdit? runColor = null;
        if (faceRun is not null && typedRunColor != _shownRunColor)
        {
            if (typedRunColor.Length == 0)
            {
                runColor = new RunColorEdit(null);
            }
            else if (ParseColor(typedRunColor) is { } own)
            {
                runColor = new RunColorEdit(own);
            }
            else
            {
                // A colour nobody can read is not an edit, and saying so is better than leaving the person to
                // wonder whether the panel took it.
                _vm.ReportStatus($"Run colour: expected r,g,b,a in 0..255, not \"{typedRunColor}\"");
            }
        }

        if (faceRun is null)
        {
            // There is no run to name, so no face member can honestly be applied.
            family = null;
            bold = null;
            italic = null;
            size = null;
        }

        TextAlignment? alignment = AlignBox.SelectedIndex == _shownAlign || AlignBox.SelectedIndex < 0
            ? null
            : AlignBox.SelectedIndex switch
            {
                1 => TextAlignment.Center,
                2 => TextAlignment.Right,
                _ => TextAlignment.Left,
            };

        double? leading = ChangedDouble(LeadingBox, _shownLeading);
        double? paragraph = ChangedDouble(ParagraphBox, _shownParagraph);
        double? rotation = ChangedDouble(RotationBox, _shownRotation);
        double? frameWidth = ChangedDouble(FrameWidthBox, _shownFrameWidth);

        // The block's own axes and its base direction are paragraph members, so they travel with the leading and the
        // turn and are applied by the same call. A combo that has not moved is not an edit, the same rule every other
        // field follows - and an empty selection has no index, which is not an edit either.
        TextWritingMode? writingMode = WritingModeBox.SelectedIndex == _shownWritingMode
            || WritingModeBox.SelectedIndex < 0
                ? null
                : WritingModeBox.SelectedIndex switch
                {
                    1 => TextWritingMode.VerticalRl,
                    2 => TextWritingMode.VerticalLr,
                    _ => TextWritingMode.HorizontalTb,
                };

        TextDirection? direction = DirectionBox.SelectedIndex == _shownDirection || DirectionBox.SelectedIndex < 0
            ? null
            : DirectionBox.SelectedIndex switch
            {
                1 => TextDirection.RightToLeft,
                _ => TextDirection.LeftToRight,
            };

        if (content is not null || color is not null || family is not null || size is not null
            || bold is not null || italic is not null || letterSpacing is not null || wordSpacing is not null
            || fontStretch is not null || fontVariant is not null || orientation is not null
            || runColor is not null)
        {
            _vm.ActiveSession.ApplyTextFieldsAt(faceRun, content, family, size, bold, italic, color,
                letterSpacing, wordSpacing, fontStretch, fontVariant, orientation, runColor);
        }

        if (alignment is { } align)
        {
            _vm.SetTextAlignment(align);
        }

        if (leading is not null || paragraph is not null || rotation is not null || frameWidth is not null
            || writingMode is not null || direction is not null)
        {
            _vm.ApplyTextStyle(leading, paragraph, rotation, frameWidth, null, writingMode, direction);
        }
    }

    // ------------------------------------------------------------------
    // Reading: the common value, or that the selection disagrees
    // ------------------------------------------------------------------

    /// <summary>
    /// Shows what the selection says about each member: the **common** value, or the word "mixed" where it
    /// disagrees.
    ///
    /// <see cref="TextSummary"/> is the authority on agreement, so the pane asks it rather than forming a second
    /// opinion that could drift from the one a driver is given. The value shown for an agreeing member is the
    /// summary's common value, never one block's, which is the difference between describing a selection and
    /// describing whichever block happened to be first.
    /// </summary>
    private void Refresh()
    {
        List<TextItem> items = _vm?.ActiveSession.SelectedTextItems().ToList() ?? new List<TextItem>();
        int runIndex = InspectedRunIndex(items);

        _syncing = true;
        try
        {
            SelectionLabel.Text = items.Count switch
            {
                0 => "none selected",
                1 => "1 text block",
                _ => $"{items.Count} text blocks",
            };

            if (items.Count == 0)
            {
                Clear();
                return;
            }

            TextSummary summary = TextSummary.Of(items, runIndex);
            TextRun? run = runIndex >= 0 && runIndex < items[0].Runs.Count ? items[0].Runs[runIndex] : null;

            RunLabel.Text = runIndex < 0
                ? "no run: this block holds no text"
                : $"run {runIndex + 1} of {items[0].Runs.Count}, the run the face fields describe";
            FontReport.Text = run is null ? string.Empty : FaceReport(run, runIndex);
            FontReport.IsVisible = run is not null;

            // Content, colour and paragraph style belong to the block, so they are read from the block.
            ContentBox.Watermark = summary.ContentMixed ? MixedWord : string.Empty;
            if (!ContentBox.IsFocused)
            {
                ContentBox.Text = summary.ContentMixed ? string.Empty : summary.Content ?? string.Empty;
                _shownContent = ContentBox.Text ?? string.Empty;
            }

            ColorBox.Watermark = summary.ColorMixed ? MixedWord : string.Empty;
            if (!ColorBox.IsFocused)
            {
                ColorBox.Text = summary.ColorMixed || summary.Color is not { } colour
                    ? string.Empty
                    : Describe(colour);
                _shownColor = ColorBox.Text ?? string.Empty;
            }

            // The run's own colour, read at the inspected run like the rest of the run's members. Empty means the run
            // states none and is drawn in the block's - which is what the watermark says - and a selection whose runs
            // disagree is shown as mixed rather than as one run's paint.
            RunColorBox.Watermark = summary.RunColorMixed ? MixedWord : "block";
            if (!RunColorBox.IsFocused)
            {
                RunColorBox.Text = summary.RunColorMixed || summary.RunColor is not { } runColour
                    ? string.Empty
                    : Describe(runColour);
                _shownRunColor = RunColorBox.Text ?? string.Empty;
            }

            AlignBox.PlaceholderText = summary.AlignmentMixed ? MixedWord : string.Empty;
            AlignBox.SelectedIndex = summary.AlignmentMixed
                ? -1
                : AlignIndex(summary.Alignment ?? TextAlignment.Left);
            _shownAlign = AlignBox.SelectedIndex;

            // The mode and the direction come from the same summary a driver reads through `text.common`, so the
            // panel and the report cannot describe different blocks - and a selection that disagrees about either is
            // shown as mixed rather than as the first block's value.
            WritingModeBox.PlaceholderText = summary.WritingModeMixed ? MixedWord : string.Empty;
            WritingModeBox.SelectedIndex = summary.WritingModeMixed
                ? -1
                : WritingModeIndex(summary.WritingMode ?? TextWritingMode.HorizontalTb);
            _shownWritingMode = WritingModeBox.SelectedIndex;

            DirectionBox.PlaceholderText = summary.DirectionMixed ? MixedWord : string.Empty;
            DirectionBox.SelectedIndex = summary.DirectionMixed
                ? -1
                : DirectionIndex(summary.Direction ?? TextDirection.LeftToRight);
            _shownDirection = DirectionBox.SelectedIndex;

            if (!LeadingBox.IsFocused)
            {
                LeadingBox.Watermark = summary.LineSpacingMixed ? MixedWord : string.Empty;
                LeadingBox.Text = Text(summary.LineSpacing, summary.LineSpacingMixed, "0.##");
                _shownLeading = LeadingBox.Text ?? string.Empty;
            }

            if (!ParagraphBox.IsFocused)
            {
                ParagraphBox.Watermark = summary.ParagraphSpacingMixed ? MixedWord : string.Empty;
                ParagraphBox.Text = Text(summary.ParagraphSpacing, summary.ParagraphSpacingMixed, "0.##");
                _shownParagraph = ParagraphBox.Text ?? string.Empty;
            }

            if (!RotationBox.IsFocused)
            {
                RotationBox.Watermark = summary.RotationMixed ? MixedWord : string.Empty;
                RotationBox.Text = Text(summary.RotationDegrees, summary.RotationMixed, "0.#");
                _shownRotation = RotationBox.Text ?? string.Empty;
            }

            if (!FrameWidthBox.IsFocused)
            {
                FrameWidthBox.Watermark = summary.FrameWidthMixed ? MixedWord : string.Empty;
                FrameWidthBox.Text = Text(summary.FrameWidth, summary.FrameWidthMixed, "0.##");
                _shownFrameWidth = FrameWidthBox.Text ?? string.Empty;
            }

            // Face is per run, so these are read from the inspected run - or say mixed where the selection's runs at
            // that index disagree. A block with no run there is a gap and is not asked.
            string shownFamily = summary.Family ?? run?.FontFamily ?? string.Empty;
            var names = new List<string>(_families);
            if (shownFamily.Length > 0 && !names.Contains(shownFamily, StringComparer.OrdinalIgnoreCase))
            {
                // A family the machine does not offer - one an imported file named - is still what this run is set
                // in, so the box offers it rather than showing nothing and silently replacing it on the next edit.
                names.Insert(0, shownFamily);
            }

            FamilyBox.ItemsSource = names;
            FamilyBox.PlaceholderText = summary.FamilyMixed ? MixedWord : string.Empty;
            FamilyBox.SelectedIndex = summary.FamilyMixed || shownFamily.Length == 0
                ? -1
                : names.IndexOf(shownFamily);
            _shownFamily = FamilyBox.SelectedItem as string ?? string.Empty;

            if (!SizeBox.IsFocused)
            {
                SizeBox.Watermark = summary.FontSizeMixed ? MixedWord : string.Empty;
                SizeBox.Text = Text(summary.FontSize, summary.FontSizeMixed, "0.##");
                _shownSize = SizeBox.Text ?? string.Empty;
            }

            // Tracking, word spacing, stretch and variant are read at the inspected run, the way the face is: the
            // model holds one of each per run, so a block with two runs can disagree with itself about them.
            if (!LetterSpacingBox.IsFocused)
            {
                LetterSpacingBox.Watermark = summary.LetterSpacingMixed ? MixedWord : string.Empty;
                LetterSpacingBox.Text = Text(summary.LetterSpacing, summary.LetterSpacingMixed, "0.##");
                _shownLetterSpacing = LetterSpacingBox.Text ?? string.Empty;
            }

            if (!WordSpacingBox.IsFocused)
            {
                WordSpacingBox.Watermark = summary.WordSpacingMixed ? MixedWord : string.Empty;
                WordSpacingBox.Text = Text(summary.WordSpacing, summary.WordSpacingMixed, "0.##");
                _shownWordSpacing = WordSpacingBox.Text ?? string.Empty;
            }

            // A width or a variant has no value for "mixed" and none for "not stated"; both read as an empty box,
            // and the watermark is what tells the two apart - the same reading the colour and content fields take.
            if (!FontStretchBox.IsFocused)
            {
                FontStretchBox.Watermark = summary.FontStretchMixed ? MixedWord : string.Empty;
                FontStretchBox.Text = summary.FontStretchMixed ? string.Empty : summary.FontStretch ?? string.Empty;
                _shownFontStretch = FontStretchBox.Text ?? string.Empty;
            }

            if (!FontVariantBox.IsFocused)
            {
                FontVariantBox.Watermark = summary.FontVariantMixed ? MixedWord : string.Empty;
                FontVariantBox.Text = summary.FontVariantMixed ? string.Empty : summary.FontVariant ?? string.Empty;
                _shownFontVariant = FontVariantBox.Text ?? string.Empty;
            }

            // The glyph orientation is read from the same summary a driver reads through `text.common`, at the
            // inspected run like the rest of the run's own members - and a selection that disagrees about it is shown
            // as mixed rather than as the first run's turn.
            OrientationBox.PlaceholderText = summary.OrientationMixed ? MixedWord : string.Empty;
            OrientationBox.SelectedIndex = summary.OrientationMixed
                ? -1
                : OrientationIndex(summary.Orientation ?? GlyphOrientation.Auto);
            _shownOrientation = OrientationBox.SelectedIndex;

            BoldBox.IsChecked = summary.BoldMixed ? null : summary.Bold ?? false;
            _shownBold = BoldBox.IsChecked;

            ItalicBox.IsChecked = summary.ItalicMixed ? null : summary.Italic ?? false;
            _shownItalic = ItalicBox.IsChecked;

            List<string> mixed = MixedMembers(summary);
            MixedLabel.Text = mixed.Count == 0 ? string.Empty : MixedWord + ": " + string.Join(", ", mixed);
            MixedLabel.IsVisible = mixed.Count > 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Every member the selection disagrees about, named once each.</summary>
    private static List<string> MixedMembers(TextSummary summary)
    {
        var mixed = new List<string>();
        if (summary.ContentMixed)
        {
            mixed.Add("content");
        }

        if (summary.ColorMixed)
        {
            mixed.Add("colour");
        }

        if (summary.RunColorMixed)
        {
            mixed.Add("run colour");
        }

        if (summary.AlignmentMixed)
        {
            mixed.Add("align");
        }

        if (summary.WritingModeMixed)
        {
            mixed.Add("mode");
        }

        if (summary.DirectionMixed)
        {
            mixed.Add("direction");
        }

        if (summary.FamilyMixed)
        {
            mixed.Add("family");
        }

        if (summary.FontSizeMixed)
        {
            mixed.Add("size");
        }

        if (summary.BoldMixed)
        {
            mixed.Add("bold");
        }

        if (summary.ItalicMixed)
        {
            mixed.Add("italic");
        }

        if (summary.LetterSpacingMixed)
        {
            mixed.Add("tracking");
        }

        if (summary.WordSpacingMixed)
        {
            mixed.Add("word");
        }

        if (summary.FontStretchMixed)
        {
            mixed.Add("stretch");
        }

        if (summary.FontVariantMixed)
        {
            mixed.Add("variant");
        }

        if (summary.OrientationMixed)
        {
            mixed.Add("orient");
        }

        if (summary.LineSpacingMixed)
        {
            mixed.Add("leading");
        }

        if (summary.ParagraphSpacingMixed)
        {
            mixed.Add("space");
        }

        if (summary.RotationMixed)
        {
            mixed.Add("turn");
        }

        if (summary.FrameWidthMixed)
        {
            mixed.Add("frame");
        }

        return mixed;
    }

    /// <summary>Empties every field, for a selection with nothing in it.</summary>
    private void Clear()
    {
        RunLabel.Text = "no run";
        FontReport.Text = string.Empty;
        FontReport.IsVisible = false;
        MixedLabel.Text = string.Empty;
        MixedLabel.IsVisible = false;

        ContentBox.Text = string.Empty;
        ContentBox.Watermark = string.Empty;
        ColorBox.Text = string.Empty;
        ColorBox.Watermark = string.Empty;
        RunColorBox.Text = string.Empty;
        RunColorBox.Watermark = string.Empty;
        AlignBox.PlaceholderText = string.Empty;
        AlignBox.SelectedIndex = -1;
        WritingModeBox.PlaceholderText = string.Empty;
        WritingModeBox.SelectedIndex = -1;
        DirectionBox.PlaceholderText = string.Empty;
        DirectionBox.SelectedIndex = -1;
        FamilyBox.ItemsSource = new List<string>(_families);
        FamilyBox.SelectedIndex = -1;
        FamilyBox.PlaceholderText = string.Empty;
        SizeBox.Text = string.Empty;
        SizeBox.Watermark = string.Empty;
        BoldBox.IsChecked = false;
        ItalicBox.IsChecked = false;
        LeadingBox.Text = string.Empty;
        LeadingBox.Watermark = string.Empty;
        ParagraphBox.Text = string.Empty;
        ParagraphBox.Watermark = string.Empty;
        RotationBox.Text = string.Empty;
        RotationBox.Watermark = string.Empty;
        FrameWidthBox.Text = string.Empty;
        FrameWidthBox.Watermark = string.Empty;
        LetterSpacingBox.Text = string.Empty;
        LetterSpacingBox.Watermark = string.Empty;
        WordSpacingBox.Text = string.Empty;
        WordSpacingBox.Watermark = string.Empty;
        FontStretchBox.Text = string.Empty;
        FontStretchBox.Watermark = string.Empty;
        FontVariantBox.Text = string.Empty;
        FontVariantBox.Watermark = string.Empty;
        OrientationBox.PlaceholderText = string.Empty;
        OrientationBox.SelectedIndex = -1;

        _shownContent = string.Empty;
        _shownColor = string.Empty;
        _shownRunColor = string.Empty;
        _shownAlign = -1;
        _shownFamily = string.Empty;
        _shownSize = string.Empty;
        _shownBold = false;
        _shownItalic = false;
        _shownLeading = string.Empty;
        _shownParagraph = string.Empty;
        _shownRotation = string.Empty;
        _shownFrameWidth = string.Empty;
        _shownLetterSpacing = string.Empty;
        _shownWordSpacing = string.Empty;
        _shownFontStretch = string.Empty;
        _shownFontVariant = string.Empty;
        _shownOrientation = -1;
        _shownWritingMode = -1;
        _shownDirection = -1;
    }

    /// <summary>
    /// Which face actually draws a run: the embedded programme the file carried, or what the resolver supplies in
    /// its place - asked of the resolver rather than assumed from the family name, because a family the document
    /// named may be neither embedded nor installed.
    ///
    /// The name the document itself asked for is repeated when it differs from the family in force, because
    /// "Nimbus Sans" alone does not tell a person that the file said "Helvetica-Bold" and is being approximated.
    /// </summary>
    private static string FaceReport(TextRun run, int runIndex)
    {
        string face = run.EmbeddedFont is { } embedded
            ? $"the embedded programme {embedded.BaseFont}"
            : StandardFontResolver.Describe(run);

        string report = $"Run {runIndex + 1} is drawn with {face}";
        if (run.SourceFont is { Length: > 0 } source &&
            !string.Equals(source, run.FontFamily, StringComparison.OrdinalIgnoreCase))
        {
            report += $"; the document asked for {source}";
        }

        return report + ".";
    }

    /// <summary>
    /// The run the face fields describe: the shared <see cref="EditorViewModel.InspectedRun"/>.
    ///
    /// The panel used to take the run under the caret while a block was open for editing and the first run
    /// otherwise. A caret is not a run picker - outside editing it holds whatever the last edit left, which may
    /// belong to a block that is no longer selected - so the index lives in the view model, clamped to the selection
    /// on read, the way <see cref="EditorViewModel.InspectedStroke"/> does for the appearance stack. That is what
    /// lets the panel and a driver agree about which run is being described, and what lets the index outlive the
    /// caret.
    /// </summary>
    private int InspectedRunIndex(List<TextItem> items)
    {
        if (_vm is null || items.Count == 0 || items[0].Runs.Count == 0)
        {
            return -1;
        }

        return _vm.InspectedRun;
    }

    /// <summary>A number field's value, or null when the person did not change it.</summary>
    private static double? ChangedDouble(TextBox box, string shown)
    {
        string text = box.Text?.Trim() ?? string.Empty;
        return text == shown ? null : Parse(text);
    }

    /// <summary>
    /// A word field's value, or null when the person did not change it.
    ///
    /// Unlike a number, an **empty** value is an edit here: it is how a `font-stretch` or a `font-variant` the run
    /// carries is put back, and "nobody touched it" is already distinguishable because the value shown for an
    /// agreeing run is exactly what the box holds. The session reads an empty word - or CSS's `normal` - as absence,
    /// which is how the model spells one.
    /// </summary>
    private static string? ChangedWord(TextBox box, string shown)
    {
        string text = box.Text?.Trim() ?? string.Empty;
        return text == shown ? null : text;
    }

    private static double? Parse(string? text)
        => double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    /// <summary>A value to show, or nothing at all when the selection disagrees about it or has none.</summary>
    private static string Text(double? value, bool mixed, string format)
        => value is null || mixed ? string.Empty : value.Value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>A colour as the bytes every field and operation speaks, which is where the model's fractions are read from.</summary>
    private static string Describe(ColorRgb color) => $"{Byte(color.R)},{Byte(color.G)},{Byte(color.B)},{Byte(color.A)}";

    private static int Byte(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

    /// <summary>A colour typed as <c>r,g,b,a</c> in 0..255, or null when it is not one.</summary>
    private static ColorRgb? ParseColor(string? text)
    {
        string[] parts = (text ?? string.Empty).Split(
            ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4))
        {
            return null;
        }

        var channels = new byte[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return null;
            }

            channels[i] = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }

        if (parts.Length == 3)
        {
            channels[3] = 255;
        }

        return ColorRgb.FromBytes(channels[0], channels[1], channels[2], channels[3]);
    }

    private static int AlignIndex(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => 1,
        TextAlignment.Right => 2,
        _ => 0,
    };

    /// <summary>Which item of the mode combo a block's writing mode is, which is the order the combo lists them in.</summary>
    private static int WritingModeIndex(TextWritingMode mode) => mode switch
    {
        TextWritingMode.VerticalRl => 1,
        TextWritingMode.VerticalLr => 2,
        _ => 0,
    };

    /// <summary>Which item of the direction combo a block's base direction is.</summary>
    private static int DirectionIndex(TextDirection direction)
        => direction == TextDirection.RightToLeft ? 1 : 0;

    /// <summary>Which item of the orientation combo a run's turn is, which is the order the combo lists them in.</summary>
    private static int OrientationIndex(GlyphOrientation orientation) => orientation switch
    {
        GlyphOrientation.Upright => 1,
        GlyphOrientation.Rotate => 2,
        _ => 0,
    };
}
