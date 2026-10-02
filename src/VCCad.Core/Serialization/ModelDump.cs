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

        // The document's own assets. A filter is document state that an item refers to by name, so a dump that
        // omitted it could not see a filter being added, edited, removed or surviving a save - which is exactly the
        // false green this member was missing. Absent when the document holds none, so an ordinary document dumps
        // exactly as it did before filters were covered.
        if (document.Filters.Count > 0)
        {
            builder.Append("  filters=").Append(document.Filters.Count).AppendLine();
            for (int f = 0; f < document.Filters.Count; f++)
            {
                DumpFilter(builder, document.Filters[f], f);
            }
        }

        // The rest of the document's own assets, each printed with its content rather than a count. The rule is the
        // one the filters block gives: a member the dump does not print cannot fail a round trip, so a width profile
        // that lost a point, a brush that came back as another kind, or a definition whose artwork was dropped would
        // all compare equal. Every block is absent when the document holds none, so an ordinary document dumps
        // exactly as it did before any of these were covered.
        if (document.WidthProfiles.Count > 0)
        {
            builder.Append("  widthProfiles=").Append(document.WidthProfiles.Count).AppendLine();
            for (int p = 0; p < document.WidthProfiles.Count; p++)
            {
                DumpWidthProfile(builder, document.WidthProfiles[p], p);
            }
        }

        if (document.Brushes.Count > 0)
        {
            builder.Append("  brushes=").Append(document.Brushes.Count).AppendLine();
            for (int b = 0; b < document.Brushes.Count; b++)
            {
                DumpBrush(builder, document.Brushes[b], b);
            }
        }

        // The definitions an instance refers to by id. Not artwork - a viewer draws it only where it is used - but
        // document state all the same, and the entry's children are what an instance holds.
        if (document.Definitions.Children.Count > 0)
        {
            builder.Append("  definitions=").Append(document.Definitions.Children.Count).AppendLine();
            DumpItems(builder, document.Definitions.Children, 4);
        }

        // Root-level elements the model has no meaning for, kept verbatim as XML. Escaped, because they are text
        // rather than structure: a round trip that lost one would silently rewrite somebody's named view.
        if (document.SvgExtras.Count > 0)
        {
            builder.Append("  svgExtras=").Append(document.SvgExtras.Count).AppendLine();
            for (int e = 0; e < document.SvgExtras.Count; e++)
            {
                builder.Append("    svgExtra ").Append(e).Append(' ')
                    .Append(Escape(document.SvgExtras[e])).AppendLine();
            }
        }

        if (document.ForeignPathEffects.Count > 0)
        {
            builder.Append("  foreignPathEffects=").Append(document.ForeignPathEffects.Count).AppendLine();
            for (int e = 0; e < document.ForeignPathEffects.Count; e++)
            {
                builder.Append("    foreignPathEffect ").Append(e).Append(' ')
                    .Append(Escape(document.ForeignPathEffects[e])).AppendLine();
            }
        }

        // Sorted by prefix, because a namespace map is unordered: two documents that declare the same prefixes in a
        // different order are the same document, and a dump that followed insertion order would say otherwise.
        if (document.SvgNamespaces.Count > 0)
        {
            builder.Append("  svgNamespaces=").Append(document.SvgNamespaces.Count).AppendLine();
            foreach (KeyValuePair<string, string> entry in
                document.SvgNamespaces.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                builder.Append("    namespace ").Append(Escape(entry.Key))
                    .Append('=').Append(Escape(entry.Value)).AppendLine();
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// A reusable width profile and every point on it.
    ///
    /// The points are the profile: a name and a count would let a profile whose tapers moved, or whose interpolation
    /// changed, compare equal to the one it replaced.
    /// </summary>
    private static void DumpWidthProfile(StringBuilder builder, WidthProfileSpec profile, int index)
    {
        builder.Append("    profile ").Append(index)
            .Append(" name=").Append(Escape(profile.Name))
            .Append(" points=").Append(profile.Points.Count)
            .AppendLine();

        for (int p = 0; p < profile.Points.Count; p++)
        {
            WidthPoint point = profile.Points[p];
            builder.Append("      point ").Append(p)
                .Append(" position=").Append(Num(point.Position))
                .Append(" left=").Append(Num(point.LeftWidth))
                .Append(" right=").Append(Num(point.RightWidth))
                .Append(" interpolation=").Append(point.Interpolation)
                .AppendLine();
        }
    }

    /// <summary>
    /// A brush, with every member of every kind.
    ///
    /// One line rather than a block, because a brush has no nested list of its own: its kind's members travel
    /// together on the record, and a reader that took some of them would draw a brush the file does not describe.
    /// The kind is printed first so the members that matter for it can be read in order.
    /// </summary>
    private static void DumpBrush(StringBuilder builder, BrushSpec brush, int index)
    {
        builder.Append("    brush ").Append(index)
            .Append(" name=").Append(Escape(brush.Name))
            .Append(" kind=").Append(brush.Kind)
            .Append(" angle=").Append(Num(brush.AngleDegrees))
            .Append(" roundness=").Append(Num(brush.Roundness))
            .Append(" diameter=").Append(Num(brush.Diameter))
            .Append(" dynamics=").Append(Dynamics(brush.Dynamics))
            .Append(" artAsset=").Append(brush.ArtAsset?.ToString() ?? "-")
            .Append(" stretch=").Append(brush.Stretch)
            .Append(" flipAcross=").Append(brush.FlipAcross)
            .Append(" flipAlong=").Append(brush.FlipAlong)
            .Append(" colourisation=").Append(brush.Colourisation)
            .Append(" shade=").Append(ColourOr(brush.ShadeColour))
            .Append(" side=").Append(Tile(brush.PatternSideTile))
            .Append(" start=").Append(Tile(brush.PatternStartTile))
            .Append(" end=").Append(Tile(brush.PatternEndTile))
            .Append(" inner=").Append(Tile(brush.PatternInnerTile))
            .Append(" outer=").Append(Tile(brush.PatternOuterTile))
            .Append(" spacing=").Append(Num(brush.PatternSpacing))
            .Append(" corner=").Append(Num(brush.PatternCornerThresholdDegrees))
            .Append(" scatter=").Append(Scatter(brush.ScatterSpec))
            .Append(" bristles=").Append(Bristles(brush.BristleSpec))
            .AppendLine();
    }

    /// <summary>One pattern tile, or "-" when the slot holds nothing.</summary>
    private static string Tile(PatternTileSpec? tile)
        => tile is null
            ? "-"
            : $"({tile.Asset?.ToString() ?? "-"},{tile.FlipAcross},{tile.FlipAlong}," +
              $"{Num(tile.RotationDegrees)},{Num(tile.Scale)})";

    /// <summary>A scatter brush's five ranged controls, or "-" when the brush is not one.</summary>
    private static string Scatter(ScatterBrushSpec? spec)
        => spec is null
            ? "-"
            : $"({spec.Asset?.ToString() ?? "-"},{Ranged(spec.Spacing)},{Ranged(spec.Rotation)}," +
              $"{Ranged(spec.Scale)},{Ranged(spec.Offset)},{Ranged(spec.Opacity)})";

    /// <summary>A value and the plus-or-minus a copy's own draw may stray by, which are one setting.</summary>
    private static string Ranged(ScatterParameter parameter)
        => $"{Num(parameter.Value)}~{Num(parameter.Randomness)}";

    /// <summary>A bristle bundle's parameters, or "-" when the brush is not one.</summary>
    private static string Bristles(BristleBrushSpec? spec)
        => spec is null
            ? "-"
            : $"({spec.Count},{Num(spec.Length)},{Num(spec.Stiffness)},{Num(spec.Thickness)}," +
              $"{Num(spec.Spread)},{Num(spec.Randomness)},{Num(spec.PressureSpread)},{Num(spec.TiltTurn)}," +
              $"{Num(spec.ColourJitter)})";

    /// <summary>
    /// A brush's response curves: each enabled target with its control points, "none" when it records a spec with
    /// every target off, or "-" when it records no spec at all.
    ///
    /// The three are not the same thing - no spec is a brush that never mentioned the pen, and a spec with every
    /// target off is a stated decision - so the dump tells them apart rather than folding both to a dash.
    /// </summary>
    private static string Dynamics(DynamicsSpec? spec)
    {
        if (spec is null)
        {
            return "-";
        }

        var builder = new StringBuilder();
        foreach (DynamicsTarget target in Enum.GetValues<DynamicsTarget>())
        {
            DynamicsTargetSpec setting = spec.For(target);
            if (!setting.Enabled)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            builder.Append(target).Append(':')
                .Append(Num(setting.Curve.X1)).Append(',').Append(Num(setting.Curve.Y1)).Append(',')
                .Append(Num(setting.Curve.X2)).Append(',').Append(Num(setting.Curve.Y2));
        }

        return builder.Length == 0 ? "none" : builder.ToString();
    }

    /// <summary>
    /// A filter and its primitive chain, as one block.
    ///
    /// Every member that can differ is printed, because the point of the dump is that two documents comparing equal
    /// are the same document: a filter is a graph, so a lost primitive, a changed radius, a dropped result name or a
    /// resolution that vanished all have to move the text. The order printed is the document's own, which is the
    /// order the primitives are evaluated in and therefore part of what the filter means.
    /// </summary>
    private static void DumpFilter(StringBuilder builder, FilterSpec filter, int index)
    {
        builder.Append("    filter ").Append(index)
            .Append(" name=").Append(Escape(filter.Name))
            .Append(" x=").Append(Num(filter.X))
            .Append(" y=").Append(Num(filter.Y))
            .Append(" w=").Append(Num(filter.Width))
            .Append(" h=").Append(Num(filter.Height))
            .Append(" box=").Append(filter.ObjectBoundingBox)
            .Append(" primitiveUnitsBox=").Append(filter.PrimitiveUnitsObjectBoundingBox)
            .Append(" res=").Append(filter.HasFilterResolution
                ? filter.FilterResolutionX + "x" + filter.FilterResolutionY
                : "-")
            .Append(" output=").Append(filter.Output.Length > 0 ? Escape(filter.Output) : "-")
            .Append(" primitives=").Append(filter.Primitives.Count)
            .AppendLine();

        for (int p = 0; p < filter.Primitives.Count; p++)
        {
            builder.Append("      primitive ").Append(p).Append(' ')
                .Append(Primitive(filter.Primitives[p]))
                .AppendLine();
        }
    }

    /// <summary>
    /// One primitive step, every member printed.
    ///
    /// The record is a superset of what any one kind reads, and which members a kind declares is the registry's
    /// answer rather than the dump's - so the dump prints them all, and a value a kind does not read cannot make two
    /// filters that differ elsewhere compare equal.
    /// </summary>
    private static string Primitive(FilterPrimitive primitive)
        => $"kind={primitive.Kind}" +
           $" in={Text(primitive.Input)}" +
           $" in2={Text(primitive.Input2)}" +
           $" result={Text(primitive.Result)}" +
           $" radius={Num(primitive.Radius)}" +
           $" dx={Num(primitive.Dx)}" +
           $" dy={Num(primitive.Dy)}" +
           $" flood={ColourOr(primitive.FloodColor)}" +
           $" floodOpacity={Num(primitive.FloodOpacity)}" +
           $" operator={Escape(primitive.Operator)}" +
           $" mode={Escape(primitive.Mode)}" +
           $" scale={Num(primitive.Scale)}" +
           $" xChannel={Escape(primitive.XChannel)}" +
           $" yChannel={Escape(primitive.YChannel)}" +
           $" type={Escape(primitive.Type)}" +
           $" baseFrequency={Num(primitive.BaseFrequency)}" +
           $" octaves={primitive.Octaves}" +
           $" seed={primitive.Seed}" +
           $" matrix={Numbers(primitive.Matrix)}" +
           $" surfaceScale={Num(primitive.SurfaceScale)}" +
           $" specularConstant={Num(primitive.SpecularConstant)}" +
           $" specularExponent={Num(primitive.SpecularExponent)}" +
           $" diffuseConstant={Num(primitive.DiffuseConstant)}" +
           $" lighting={ColourOr(primitive.LightingColor)}" +
           $" azimuth={Num(primitive.Azimuth)}" +
           $" elevation={Num(primitive.Elevation)}";

    /// <summary>A nullable string as written, or "-" when it is unset or empty.</summary>
    private static string Text(string? value)
        => string.IsNullOrEmpty(value) ? "-" : Escape(value);

    /// <summary>A colour as written, or "-" when the member holds none.</summary>
    private static string ColourOr(ColorRgb? colour) => colour is { } c ? Colour(c) : "-";

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

                    // The rest of the stack. A dump that printed only the bottom stroke would make two
                    // different documents compare equal, which quietly weakens every round-trip test built on
                    // this text - so a path with more than one reports all of them, and a path with one is
                    // written exactly as it was before strokes became a stack.
                    .Append(path.Strokes.Count > 1
                        ? " strokes=" + string.Join(" | ", path.Strokes.Select(Stroke))
                        : string.Empty)
                    .Append(" subpaths=").Append(path.SubPaths.Count)
                    .Append(" fillCmyk=").Append(Cmyk(path.SourceFillCmyk))
                    .Append(" strokeCmyk=").Append(Cmyk(path.SourceStrokeCmyk))
                    .Append(Clips(item)).Append(FilterId(item)).Append(Blend(item)).AppendLine();

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
                    .Append(Clips(item)).Append(FilterId(item)).Append(Blend(item)).AppendLine();

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
                        .Append(" gap=").Append(Num(run.GapAfter))
                        .Append(" placedAscent=").Append(Num(run.PlacedAscentEm))
                        .Append(" letterSpacing=").Append(Num(run.LetterSpacing))
                        .Append(" wordSpacing=").Append(Num(run.WordSpacing))

                        // A run whose colour is the block's says "-", because that is what it holds: a run with no
                        // colour of its own, not a second copy of the block's.
                        .Append(" colour=").Append(run.Color is { } own ? Colour(own) : "-")
                        .Append(" stretch=").Append(Escape(run.FontStretch ?? string.Empty))
                        .Append(" variant=").Append(Escape(run.FontVariant ?? string.Empty))
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
                    .Append(" filter=").Append(image.Filter ?? "-")
                    .Append(" maskFilter=").Append(image.MaskFilter ?? "-")
                    .Append(Clips(item)).Append(FilterId(item)).Append(Blend(item)).AppendLine();
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
                    .Append(Clips(item)).Append(FilterId(item)).Append(Blend(item)).AppendLine();

                DumpItems(builder, group.Children, indent + 2);
                break;

            default:
                builder.Append(item.GetType().Name)
                    .Append(" id=").Append(item.Id)
                    .Append(" name=").Append(Escape(item.Name))
                    .Append(FilterId(item))
                    .Append(Blend(item))
                    .AppendLine();
                break;
        }
    }

    private static string Fill(FillSpec fill)
        => $"{fill.IsVisible}/{Colour(fill.Color)}/{fill.Rule}{Gradient(fill.Gradient)}";

    /// <summary>
    /// Every value of a gradient fill, or nothing for a solid one.
    ///
    /// A driver with no eyes reads the document from this dump, so a gradient has to be
    /// legible here — and by the same rule as the rest of the file, every field that can
    /// differ has to be present, or a round trip that dropped one would still compare
    /// equal. That includes the geometry of kinds other than the current one, because a
    /// gradient's type can be switched without losing it.
    /// </summary>
    private static string Gradient(GradientSpec? gradient)
    {
        if (gradient is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append(" gradient=").Append(gradient.Kind).Append('/').Append(gradient.Spread);
        builder.Append(" stops=[");

        for (int i = 0; i < gradient.Stops.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(';');
            }

            GradientStop stop = gradient.Stops[i];
            builder.Append(Num(stop.Position)).Append(':').Append(Colour(stop.Color))
                .Append('@').Append(Num(stop.Opacity))
                .Append('~').Append(Num(stop.Midpoint));
            if (stop.Name is { Length: > 0 } name)
            {
                builder.Append('\'').Append(Escape(name)).Append('\'');
            }
        }

        builder.Append(']');
        builder.Append(" linear=").Append(Point(gradient.Start)).Append("->").Append(Point(gradient.End));
        builder.Append(" radial=").Append(Point(gradient.Center))
            .Append('/').Append(Num(gradient.RadiusX))
            .Append('/').Append(Num(gradient.RadiusY))
            .Append('/').Append(Num(gradient.Rotation));
        builder.Append(" angle=").Append(Num(gradient.Angle));
        builder.Append(" points=[");

        for (int i = 0; i < gradient.Points.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(';');
            }

            FreeformPoint point = gradient.Points[i];
            builder.Append(Point(point.Position)).Append(':').Append(Colour(point.Color))
                .Append('@').Append(Num(point.Opacity));
        }

        builder.Append("] mode=").Append(gradient.FreeformMode);
        builder.Append(" lines=[");

        for (int i = 0; i < gradient.Lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(';');
            }

            builder.Append(gradient.Lines[i].From).Append('-').Append(gradient.Lines[i].To);
        }

        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>
    /// One stroke as the dump prints it.
    ///
    /// The opacity and the blend mode are printed **only when the stroke states them**, which is the same rule the
    /// sidecar and the dump's own stroke-stack member follow: a stroke that says nothing about its opacity is not
    /// the same document as one that states 1, and printing "opacity=1" for both would make two different
    /// documents compare equal. That is exactly the failure the dump exists to prevent in the round-trip tests.
    /// </summary>
    private static string Stroke(StrokeSpec stroke)
        => $"{stroke.HasVisibleOutline}/{Colour(stroke.Color)}/{Num(stroke.Width)}" +
           $"/{stroke.Cap}/{stroke.Join}/{Num(stroke.MiterLimit)}/{stroke.Alignment}" +
           $"/dash:{stroke.Dash.Segments.Count}@{Num(stroke.Dash.Offset)}" +
           (stroke.Opacity is { } opacity ? $"/opacity:{Num(opacity)}" : string.Empty) +
           (stroke.Blend is { } blend ? $"/blend:{blend.ToSvgName()}" : string.Empty);

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
    /// The filter an item refers to, or nothing when it refers to none.
    ///
    /// Part of the dump because it is part of the picture and because it is a **reference**: an item that names a
    /// filter draws differently, and a round trip that dropped the name would draw the shape unfiltered with
    /// nothing else in the dump to show it. Absent when unset, so an item that names no filter is written exactly
    /// as it was before this member existed - and a reference that is present and one that is absent still compare
    /// different, which is what lets a dump-based test witness the reference at all.
    /// </summary>
    private static string FilterId(LayerItem item)
        => string.IsNullOrEmpty(item.FilterId) ? string.Empty : " filterId=" + Escape(item.FilterId);

    /// <summary>
    /// The blend mode an item composites with, or nothing when it is the default.
    ///
    /// Every kind of item can be blended, so it is printed on the item's own line rather than as document state -
    /// and a round trip that dropped it would draw the item composited normally with nothing else in the dump to
    /// show it. Written only when it is not <see cref="BlendMode.Normal"/>, so an item that states nothing is
    /// exactly as it was before this member was covered, while one that is blended and one that is not still
    /// compare different - which is what lets a dump-based test witness the blend at all.
    /// </summary>
    private static string Blend(LayerItem item)
        => item.BlendMode == BlendMode.Normal ? string.Empty : " blend=" + item.BlendMode.ToSvgName();

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
