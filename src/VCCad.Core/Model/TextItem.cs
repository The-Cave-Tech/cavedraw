using VCCad.Geometry;

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

    public TextRun Clone() => new()
    {
        Text = Text,
        FontFamily = FontFamily,
        FontSize = FontSize,
        Bold = Bold,
        Italic = Italic,
    };
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
    public const string DefaultFontFamily = "DejaVu Sans";

    /// <summary>Top-left of the text block, artboard-local.</summary>
    public Point2D Origin { get; set; }

    /// <summary>Rich-text runs in order.</summary>
    public List<TextRun> Runs { get; } = new();

    /// <summary>Text colour.</summary>
    public ColorRgb Color { get; set; } = ColorRgb.Black;

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

    /// <summary>The largest font size among the runs (line height driver).</summary>
    public double MaxFontSize => Runs.Count == 0 ? 12.0 : Runs.Max(r => r.FontSize);

    /// <summary>Approximate tight bounds (see class remarks).</summary>
    public Rect2D BoundingBox()
    {
        double width = 0;
        double lineWidth = 0;
        double height = 0;
        double lineHeight = MaxFontSize * 1.2;
        bool anyLine = false;

        foreach (TextRun run in Runs)
        {
            foreach (char ch in run.Text)
            {
                if (ch == '\n')
                {
                    width = Math.Max(width, lineWidth);
                    lineWidth = 0;
                    height += lineHeight;
                    anyLine = true;
                    continue;
                }

                lineWidth += run.FontSize * 0.6;
            }
        }

        width = Math.Max(width, lineWidth);
        height += anyLine || Runs.Count > 0 ? lineHeight : 0;

        return new Rect2D(Origin.X, Origin.Y, width, height);
    }

    /// <summary>Bounds in document/world space (local bounds + artboard origin).</summary>
    public Rect2D WorldBounds()
    {
        Rect2D box = BoundingBox();
        Vector2D offset = ArtboardOffset();
        return new Rect2D(box.X + offset.X, box.Y + offset.Y, box.Width, box.Height);
    }

    /// <summary>Copies runs/origin/colour from another text item (undo support).</summary>
    public void CopyFrom(TextItem other)
    {
        Origin = other.Origin;
        Color = other.Color;
        Runs.Clear();
        Runs.AddRange(other.Runs.Select(r => r.Clone()));
    }

    /// <inheritdoc/>
    public override LayerItem Clone()
    {
        var copy = new TextItem
        {
            Name = Name,
            IsVisible = IsVisible,
            IsLocked = IsLocked,
            Origin = Origin,
            Color = Color,
        };
        copy.Runs.AddRange(Runs.Select(r => r.Clone()));
        return copy;
    }
}
