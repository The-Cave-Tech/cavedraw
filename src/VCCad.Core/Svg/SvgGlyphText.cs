using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>
/// Draws a run of text with the glyph drawings the file itself carries.
///
/// A document that supplies a face - through `@font-face` naming an SVG-in-OpenType programme, or through an SVG
/// font declared with `<glyph>` elements in the document - is asking for *its* glyphs. The model has no face to hand
/// a renderer, because an SVG import has no font registry behind it, so the honest way to draw the picture the file
/// describes is to place the glyph artwork where the model's own layout puts each character.
///
/// **That turns text into outlines, and the caller says so.** The picture is right and the text is no longer text; a
/// reader that kept the `TextItem` instead would keep the words and draw a substituted face, which is the wrong
/// picture. The loss is reported rather than left for someone to discover.
///
/// Glyph drawings are in **font units with Y up** - the baseline is zero and the em square is
/// <see cref="ISvgGlyphFont.UnitsPerEm"/> units tall - so each shape is scaled by the run's size over that em and
/// flipped about the baseline it is placed on.
/// </summary>
internal static class SvgGlyphText
{
    /// <summary>
    /// The artwork for <paramref name="text"/>, or null when the block is not wholly drawn with a supplied face -
    /// half outlines and half substituted text would be a picture neither the file nor the model describes.
    /// </summary>
    public static ArtGroup? Build(TextItem text, SvgFontFaces faces, Action<string> warn)
    {
        var fonts = new ISvgGlyphFont?[text.Runs.Count];
        for (int i = 0; i < text.Runs.Count; i++)
        {
            fonts[i] = faces.Find(text.Runs[i].FontFamily);
        }

        if (fonts.Any(font => font is null))
        {
            return null;
        }

        string flat = string.Concat(text.Runs.Select(r => r.Text));
        TextLayout layout = TextLayoutEngine.Compute(text);
        var group = new ArtGroup { Name = text.Name };
        int placed = 0;
        int missing = 0;

        // Where each run begins in the flattened text, so a ligature can be looked for **inside one run**: a
        // sequence that ran across two runs would be a drawing the file never asked for.
        var runStart = new int[text.Runs.Count];
        for (int i = 1; i < text.Runs.Count; i++)
        {
            runStart[i] = runStart[i - 1] + text.Runs[i - 1].Text.Length;
        }

        int coveredUntil = -1;

        foreach (GlyphBox glyph in layout.Glyphs)
        {
            TextRun run = text.Runs[glyph.Run];
            ISvgGlyphFont font = fonts[glyph.Run]!;
            double scale = font.UnitsPerEm > 0 ? run.FontSize / font.UnitsPerEm : 0;
            if (scale <= 0 || glyph.Index < 0 || glyph.Index >= flat.Length)
            {
                continue;
            }

            if (glyph.Index < coveredUntil)
            {
                // Already drawn: this character is part of a ligature placed at an earlier one.
                continue;
            }

            // **A ligature is one drawing for several characters**, so the sequence is asked for before the
            // character is, and the characters it covers are skipped rather than drawn on top of it. Longest first,
            // because a font may name both `f`+`i` and a longer run beginning with them.
            int id = 0;
            int span = 1;
            int available = Math.Min(4, (runStart[glyph.Run] + run.Text.Length) - glyph.Index);
            for (int length = available; length >= 2; length--)
            {
                int sequence = font.GlyphForSequence(flat.Substring(glyph.Index, length));
                if (sequence != 0)
                {
                    id = sequence;
                    span = length;
                    break;
                }
            }

            id = id != 0 ? id : font.GlyphFor(flat[glyph.Index]);
            if (id == 0)
            {
                // A character the supplied face has no glyph for: reported, because a missing glyph is invisible on
                // the page and looks deliberate. A font that declares a `<missing-glyph>` answers with it instead,
                // so this counts only characters it truly has no drawing for.
                missing++;
                continue;
            }

            coveredUntil = glyph.Index + span;

            double penX = text.Origin.X + glyph.X;

            // **The baseline is the one the file stated, not the line box's.** A block's origin was placed one
            // ascent above its baseline, and the ascent it was placed with is recorded per run
            // (`PlacedAscentEm`); the layout's line ascent is that plus half the leading, which is a different
            // number - 18.56 against 18 at 20px - so `origin + glyph.Y` draws the glyphs 0.56pt above where the
            // file put them, and above where the exporter puts them. The recorded ascent is the one that agrees
            // with the page.
            TextLine placedLine = layout.Lines.First(line => line.Start <= glyph.Index && glyph.Index < line.End);
            double penY = text.Origin.Y + glyph.Y;
            if (run.PlacedAscentEm > 0)
            {
                penY += (run.PlacedAscentEm * run.FontSize) - placedLine.Ascent;
            }
            foreach (GlyphShape shape in font.Glyphs(id, warn))
            {
                if (SvgPathData.Parse(shape.PathData, warn) is not { Count: > 0 } parsed)
                {
                    continue;
                }

                var path = new PathItem { Fill = shape.Fill };
                path.Strokes.Clear();

                // **A glyph that names no paint is drawn in the run's own colour.** In SVG 1.1 the glyph is the shape
                // and the text element is the paint, so a `<glyph>` with no fill or stroke of its own is the ordinary
                // case rather than an error - and leaving it unpainted would draw nothing at all.
                if (!shape.Fill.IsVisible && !shape.Stroke.IsVisible)
                {
                    path.Fill = FillSpec.Solid(text.ColourOf(run));
                }
                else
                {
                    // **Only a visible stroke's width is scaled.** A drawing that states `stroke:none` still has a
                    // stroke spec in the model - the invisible one - and multiplying its width by the scale would
                    // change a number nothing draws while making the model differ from what a round trip produces
                    // (the writer emits `stroke="none"` and no width, so the width comes back as the default). The
                    // corpus caught exactly that on `text-gzipped-svg-glyph.svg`.
                    path.Strokes.Add(
                        shape.Stroke.IsVisible && shape.Stroke.Width > 0
                            ? shape.Stroke with { Width = shape.Stroke.Width * scale }
                            : shape.Stroke);
                }

                foreach (SubPath source in parsed)
                {
                    SubPath sub = path.AddSubPath(source.IsClosed);
                    foreach (PathNode node in source.Nodes)
                    {
                        sub.Nodes.Add(Place(node, scale, penX, penY));
                    }
                }

                group.AddItem(path);
                placed++;
            }
        }

        if (missing > 0)
        {
            warn(
                $"the file's own font has no drawing for {missing} of its characters, and "
                + $"{placed} shapes were placed from the ones it has");
        }

        return placed > 0 ? group : null;
    }

    /// <summary>
    /// A node in the model's frame: font units scaled by the run's size, with Y flipped about the baseline the glyph
    /// is placed on. A node's handles are absolute points, so they are mapped exactly as its anchor is.
    /// </summary>
    private static PathNode Place(PathNode node, double scale, double penX, double penY)
    {
        Point2D Map(Point2D point) => new(penX + (point.X * scale), penY - (point.Y * scale));
        return new PathNode(Map(node.Anchor), Map(node.InHandle), Map(node.OutHandle));
    }
}
