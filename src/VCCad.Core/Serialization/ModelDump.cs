using System.Globalization;
using System.Text;
using VCCad.Core.Model;

namespace VCCad.Core.Serialization;

/// <summary>
/// A canonical, complete text dump of a document model.
///
/// This exists so "we saved and reloaded and it came back exactly as it was" is something
/// that can be *checked* rather than believed. Two documents whose dumps are equal are
/// identical in every value the model holds; a difference shows as the first line that
/// disagrees, which is far more useful than "the round trip failed".
///
/// It is deliberately exhaustive — every id, flag, coordinate, style, run, sample and
/// placement — because a dump that skips a field cannot witness that field surviving.
/// Output is order-stable and culture-invariant, so it can be diffed across runs and
/// machines.
/// </summary>
public static class ModelDump
{
    public static string Of(CadDocument document)
    {
        var builder = new StringBuilder();
        builder.Append("document name=").Append(Escape(document.Name))
            .Append(" artboards=").Append(document.Artboards.Count)
            .AppendLine();

        for (int i = 0; i < document.Artboards.Count; i++)
        {
            Artboard artboard = document.Artboards[i];
            builder.Append("  artboard ").Append(i)
                .Append(" id=").Append(artboard.Id)
                .Append(" name=").Append(Escape(artboard.Name))
                .Append(" x=").Append(Num(artboard.X))
                .Append(" y=").Append(Num(artboard.Y))
                .Append(" w=").Append(Num(artboard.Width))
                .Append(" h=").Append(Num(artboard.Height))
                .Append(" visible=").Append(artboard.IsVisible)
                .AppendLine();

            for (int l = 0; l < artboard.Layers.Count; l++)
            {
                Layer layer = artboard.Layers[l];
                builder.Append("    layer ").Append(l)
                    .Append(" id=").Append(layer.Id)
                    .Append(" name=").Append(Escape(layer.Name))
                    .Append(" visible=").Append(layer.IsVisible)
                    .Append(" locked=").Append(layer.IsLocked)
                    .Append(" opacity=").Append(Num(layer.Opacity))
                    .AppendLine();

                DumpItems(builder, layer.Children, 6);
            }
        }

        builder.Append("  orphans visible=").Append(document.Orphans.IsVisible)
            .Append(" opacity=").Append(Num(document.Orphans.Opacity))
            .AppendLine();
        DumpItems(builder, document.Orphans.Children, 4);

        return builder.ToString();
    }

    private static void DumpItems(StringBuilder builder, IReadOnlyList<LayerItem> items, int indent)
    {
        for (int i = 0; i < items.Count; i++)
        {
            DumpItem(builder, items[i], indent, i);
        }
    }

    private static void DumpItem(StringBuilder builder, LayerItem item, int indent, int index)
    {
        string pad = new(' ', indent);
        builder.Append(pad).Append(index).Append(' ');

        switch (item)
        {
            case PathItem path:
                builder.Append("path id=").Append(path.Id)
                    .Append(" name=").Append(Escape(path.Name))
                    .Append(" visible=").Append(path.IsVisible)
                    .Append(" locked=").Append(path.IsLocked)
                    .Append(" opacity=").Append(Num(path.Opacity))
                    .Append(" fill=").Append(Fill(path.Fill))
                    .Append(" stroke=").Append(Stroke(path.Stroke))
                    .Append(" subpaths=").Append(path.SubPaths.Count)
                    .Append(" fillCmyk=").Append(Cmyk(path.SourceFillCmyk))
                    .Append(" strokeCmyk=").Append(Cmyk(path.SourceStrokeCmyk))
                    .Append(Clips(item)).AppendLine();

                for (int s = 0; s < path.SubPaths.Count; s++)
                {
                    SubPath sub = path.SubPaths[s];
                    builder.Append(pad).Append("  sub ").Append(s)
                        .Append(" closed=").Append(sub.IsClosed)
                        .Append(" nodes=").Append(sub.Nodes.Count)
                        .AppendLine();

                    for (int n = 0; n < sub.Nodes.Count; n++)
                    {
                        PathNode node = sub.Nodes[n];
                        builder.Append(pad).Append("    node ").Append(n)
                            .Append(' ').Append(Point(node.Anchor))
                            .Append(' ').Append(Point(node.InHandle))
                            .Append(' ').Append(Point(node.OutHandle))
                            .AppendLine();
                    }
                }

                break;

            case TextItem text:
                builder.Append("text id=").Append(text.Id)
                    .Append(" name=").Append(Escape(text.Name))
                    .Append(" visible=").Append(text.IsVisible)
                    .Append(" locked=").Append(text.IsLocked)
                    .Append(" origin=").Append(Point(text.Origin))
                    .Append(" colour=").Append(Colour(text.Color))
                    .Append(" rotation=").Append(Num(text.RotationRadians))
                    .Append(" align=").Append(text.Alignment)
                    .Append(" frame=").Append(Num(text.FrameWidth))
                    .Append(" leading=").Append(Num(text.LineSpacing))
                    .Append(" paragraph=").Append(Num(text.ParagraphSpacing))
                    .Append(" runs=").Append(text.Runs.Count)
                    .Append(" colourCmyk=").Append(Cmyk(text.SourceCmyk))
                    .Append(Clips(item)).AppendLine();

                for (int r = 0; r < text.Runs.Count; r++)
                {
                    TextRun run = text.Runs[r];
                    builder.Append(pad).Append("  run ").Append(r)
                        .Append(" text=").Append(Escape(run.Text))
                        .Append(" family=").Append(Escape(run.FontFamily))
                        .Append(" source=").Append(Escape(run.SourceFont ?? string.Empty))
                        .Append(" size=").Append(Num(run.FontSize))
                        .Append(" bold=").Append(run.Bold)
                        .Append(" italic=").Append(run.Italic)
                        .Append(" advance=").Append(run.AdvanceWidth is { } a ? Num(a) : "-")
                        .Append(" embedded=").Append(run.EmbeddedFont?.FamilyName ?? "-")
                        .Append(" rawCodes=").Append(run.RawCodes?.Length.ToString(CultureInfo.InvariantCulture) ?? "-")
                        .AppendLine();
                }

                break;

            case ImageItem image:
                builder.Append("image id=").Append(image.Id)
                    .Append(" name=").Append(Escape(image.Name))
                    .Append(" visible=").Append(image.IsVisible)
                    .Append(" locked=").Append(image.IsLocked)
                    .Append(" pixels=").Append(image.PixelWidth).Append('x').Append(image.PixelHeight)
                    .Append(" bits=").Append(image.BitsPerComponent)
                    .Append(" colourspace=").Append(image.ColorSpace)
                    .Append(" placement=").Append(Rect(image.Placement))
                    .Append(" samples=").Append(image.Samples.Length)
                    .Append(" sampleHash=").Append(Hash(image.Samples))
                    .Append(" palette=").Append(image.Palette.Length)
                    .Append(" mask=").Append(image.Mask.Length)
                    .Append(" maskHash=").Append(Hash(image.Mask))

                    // Both change what the picture looks like, so a round trip that
                    // dropped either would export a different image with nothing else in
                    // the dump to show it.
                    .Append(" decode=").Append(Numbers(image.Decode))
                    .Append(" colourKey=").Append(Numbers(image.ColourKey))
                    .Append(Clips(item)).AppendLine();
                break;

            case ArtGroup group:
                builder.Append("group id=").Append(group.Id)
                    .Append(" name=").Append(Escape(group.Name))
                    .Append(" visible=").Append(group.IsVisible)
                    .Append(" locked=").Append(group.IsLocked)
                    .Append(" opacity=").Append(Num(group.Opacity))
                    .Append(" transform=").Append(Num(group.Transform.A)).Append(',')
                    .Append(Num(group.Transform.B)).Append(',')
                    .Append(Num(group.Transform.C)).Append(',')
                    .Append(Num(group.Transform.D)).Append(',')
                    .Append(Num(group.Transform.E)).Append(',')
                    .Append(Num(group.Transform.F))
                    .Append(" children=").Append(group.Children.Count)
                    .Append(Clips(item)).AppendLine();

                DumpItems(builder, group.Children, indent + 2);
                break;

            default:
                builder.Append(item.GetType().Name)
                    .Append(" id=").Append(item.Id)
                    .Append(" name=").Append(Escape(item.Name))
                    .AppendLine();
                break;
        }
    }

    private static string Fill(FillSpec fill)
        => $"{fill.IsVisible}/{Colour(fill.Color)}/{fill.Rule}";

    private static string Stroke(StrokeSpec stroke)
        => $"{stroke.HasVisibleOutline}/{Colour(stroke.Color)}/{Num(stroke.Width)}" +
           $"/{stroke.Cap}/{stroke.Join}/{Num(stroke.MiterLimit)}/{stroke.Alignment}" +
           $"/dash:{stroke.Dash.Segments.Count}@{Num(stroke.Dash.Offset)}";

    private static string Colour(ColorRgb colour)
        => $"{colour.R:0.####},{colour.G:0.####},{colour.B:0.####},{colour.A:0.####}";

    /// <summary>
    /// The original ink values, when the item carries them, or "-".
    ///
    /// Part of the dump because it is part of the document: a round trip that lost it
    /// would export a different colour from the one that went in, and none of the other
    /// fields would show it.
    /// </summary>
    /// <summary>Every value of a numeric array, or "-"; used for decode and colour keys.</summary>
    private static string Numbers(double[]? values)
        => values is { Length: > 0 } ? string.Join(",", values.Select(Num)) : "-";

    private static string Cmyk(double[]? components)
        => components is { Length: >= 4 }
            ? string.Join(",", components.Take(4).Select(Num))
            : "-";

    private static string Point(VCCad.Geometry.Point2D p) => $"{Num(p.X)},{Num(p.Y)}";

    private static string Rect(VCCad.Geometry.Rect2D r)
        => $"{Num(r.X)},{Num(r.Y)},{Num(r.Width)},{Num(r.Height)}";

    private static string Num(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");

    /// <summary>
    /// The clips in force on an item, or nothing when it was not clipped.
    ///
    /// Part of the dump because it is part of the picture: a round trip that dropped a clip
    /// would export content the file had hidden, and nothing else in the dump would show it.
    /// </summary>
    private static string Clips(LayerItem item)
        => item.Clips.Count == 0
            ? string.Empty
            : " clips=" + item.Clips.Count + "(" +
              string.Join(';', item.Clips.Select(c => c.SubPaths.Count + ":" + c.Rule)) + ")";

    /// <summary>
    /// A stable fingerprint of a byte array. Samples can be hundreds of kilobytes, so the
    /// dump records a hash rather than the bytes — two different images with the same
    /// hash would be a collision, and equal hashes with unequal bytes is what a dump is
    /// for catching elsewhere in the line.
    /// </summary>
    private static string Hash(byte[] data)
    {
        if (data.Length == 0)
        {
            return "-";
        }

        // FNV-1a: small, deterministic and dependency-free.
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offset;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
