using System.Globalization;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A clip on a **group** reaches the text inside it (issue #169).
///
/// The exporter opened a <c>q</c>, wrote the group's own clips and then walked the group's children - and
/// <c>PaintItem</c> returned early for every <see cref="TextItem"/>, because text was written by a separate loop
/// (<c>AllPlacedText</c>) that ran entirely outside that <c>q</c>/<c>Q</c>. So a group carrying a clip clipped its
/// paths and its images and not its text: the crop the person sees on the canvas was in the file for one kind of
/// content and not another. The canvas pushes each group's clip as it descends its own walk
/// (<c>CanvasWorkspace.PaintItem</c>), so canvas and export disagreed about the same document.
///
/// #164 fixed the **block's own** clips, which the separate loop wrote in the group's frame; the **ancestor's**
/// clip is what a loop that re-derives the frame cannot inherit however well it derives it. The two cases are kept
/// apart deliberately: <see cref="GroupTransformTextExportTests"/> pins the block's own clip, these facts pin the
/// group's.
///
/// **The assertion is on the exported geometry**, read back through <see cref="PdfImporter.TryImportVector"/>.
/// <see cref="PdfImporter.Import(byte[])"/> returns our own lossless sidecar and would hand back the model it was
/// given - clip and all - whatever the content stream said (issue #170). A clip is a **container** in the
/// re-imported tree rather than a decoration on the object, so what is asked is "is this text inside a clip, and is
/// it the same clip the path beside it is inside"; that is also the question the canvas answers before it draws.
/// </summary>
public class GroupClipTextExportTests
{
    /// <summary>
    /// The group both clip facts place their items in: <c>translate(50,50) scale(2)</c>.
    ///
    /// It is the figure #164 measured, and it is not the identity on purpose - a clip written in the wrong frame is
    /// a crop that has moved, and the frames differ by exactly this. The probe that established these facts read
    /// the frame <c>PaintItem</c> carries at each level: the port group's clip is written at the identity (the
    /// group is placed at the artboard), the inner group's at <c>(1,0,0,1,40,50)</c> (its *placement* frame, the
    /// port's space - a group's own transform maps its children into that frame rather than moving its clip out of
    /// it), and the block and the path at <c>(2,0,0,2,90,100)</c>.
    /// </summary>
    private static readonly AffineTransform GroupFrame = new(2, 0, 0, 2, 50, 50);

    /// <summary>A closed rectangular outline, the shape of every clip in these facts.</summary>
    private static ClipSpec Rectangle(double x, double y, double width, double height)
    {
        var clip = new ClipSpec();
        var sub = new SubPath { IsClosed = true };
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + width, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + width, y + height)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + height)));
        clip.SubPaths.Add(sub);
        return clip;
    }

    /// <summary>A filled square, so the group holds a path the same clip visibly cuts.</summary>
    private static PathItem Square(double x, double y, double size)
    {
        var path = new PathItem { Name = "Shape" };
        var sub = new SubPath { IsClosed = true };
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        path.SubPaths.Add(sub);
        path.Fill = new FillSpec(true, new ColorRgb(200, 0, 0), FillRule.NonZero, null, null);
        return path;
    }

    /// <summary>A two-glyph block, small enough that a 20pt-wide clip cuts it visibly.</summary>
    private static TextItem Label(double x, double y)
    {
        var text = new TextItem { Name = "Label", Origin = new Point2D(x, y), Color = ColorRgb.Black };
        text.Runs.Add(new TextRun { Text = "AB", FontFamily = "Nimbus Sans", FontSize = 12 });
        return text;
    }

    /// <summary>A group at <see cref="GroupFrame"/> holding a square and a text block.</summary>
    private static ArtGroup ClippedGroup(ClipSpec clip)
    {
        var group = new ArtGroup { Name = "clipped", Transform = GroupFrame };
        group.AddItem(Square(5, 5, 40));
        group.AddItem(Label(8, 20));
        group.Clips.Add(clip);
        return group;
    }

    /// <summary>Exports and reads the page back the way a reader outside this application would.</summary>
    private static CadDocument VectorRoundTrip(CadDocument document)
    {
        byte[] pdf = PdfDocumentExporter.Export(document);
        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? back) && back is not null,
            "the exported PDF did not come back through the vector import");
        return back!;
    }

    /// <summary>
    /// The square's **filled** path as the vector import recovers it.
    ///
    /// The importer gives the fill and the stroke of one painted square separate objects - the stroke is its own
    /// painting operation there - so the square comes back as two. It is the filled one these facts compare with
    /// the text, because that is the one the group clip is meant to cut identically.
    /// </summary>
    private static PathItem FilledPath(CadDocument document)
        => document.AllItems().OfType<PathItem>().Single(p => p.Fill.IsVisible);

    /// <summary>
    /// Every clip outline that applies to an item, in **artboard** coordinates - each carried out of the frame it
    /// was recorded in.
    ///
    /// The frames differ: a group's clips are recorded in the frame the group is *placed* in, so they are carried
    /// up through the placement frames of the containers above them, and the item's own clips through the frame the
    /// item is placed in. That is the composition the exporter makes as it descends - the probe above read the
    /// numbers straight out of <c>PaintItem</c> - and it is deliberately not <see cref="SelectionEngine.ClipsOn"/>,
    /// which answers with clips in their own frames because it is comparing points.
    /// </summary>
    private static List<Rect2D> ClipsInArtboard(LayerItem item)
    {
        var boxes = new List<Rect2D>();

        for (LayerItem? holder = item; holder is not null; holder = holder.Container as LayerItem)
        {
            // The item's own clips are in the frame the item is placed in; a group's are in the frame the group
            // itself is placed in, which is one level higher.
            AffineTransform frame = SelectionEngine.ToArtboard(holder);

            foreach (ClipSpec clip in holder.Clips)
            {
                foreach (SubPath sub in clip.SubPaths)
                {
                    Rect2D box = sub.BoundingBox();
                    boxes.Add(Rect2D.FromPoints(
                        frame.Transform(new Point2D(box.Left, box.Top)),
                        frame.Transform(new Point2D(box.Right, box.Bottom))));
                }
            }
        }

        return boxes;
    }

    /// <summary>
    /// The text of a clipped group is inside the group's clip, and inside **the same** clip as the path beside it.
    ///
    /// Against the exporter this replaces, no clip reaches the text at all: the group's outline is written for the
    /// path and for nothing else, so the text re-imports bare and the crop the canvas shows is missing from the
    /// file. The failure message prints both lists, so which half is wrong is visible rather than asserted away.
    /// </summary>
    [Fact]
    public void AGroupClipReachesTheTextInsideTheGroup()
    {
        if (!StandardFontFixture.Available) { return; }

        // In the frame the group is placed in - the artboard, for a top-level group, and that is the frame the
        // probe read the written outline in. The block's own local numbers are (8,20) and the group's transform
        // maps that to (66,90); none of these four numbers is any of those, so a clip that arrived in the other
        // frame cannot coincide with what is asserted below. The rectangle is (60,80)-(90,140), which cuts the
        // block's (66,90)-(98,119) on one corner - a strict part of it, which is what makes the crop observable.
        const double ClipX = 60, ClipY = 80, ClipWidth = 30, ClipHeight = 60;

        CadDocument document = CadDocument.CreateDefault("Clipped group");
        document.Artboards[0].Layers[0].AddItem(ClippedGroup(Rectangle(ClipX, ClipY, ClipWidth, ClipHeight)));

        CadDocument back = VectorRoundTrip(document);

        PathItem path = FilledPath(back);
        TextItem text = Assert.Single(back.AllItems().OfType<TextItem>());

        List<Rect2D> pathClips = ClipsInArtboard(path);
        List<Rect2D> textClips = ClipsInArtboard(text);
        string got = string.Join(", ", textClips.Select(Box));

        Assert.True(textClips.Count > 0,
            "the text inside the clipped group re-imports with no clip at all: the group's crop was written " +
            "around its paths and not around its text.");

        Rect2D wanted = new(ClipX, ClipY, ClipWidth, ClipHeight);
        Assert.True(textClips.Any(box => Same(box, wanted)),
            $"the text is clipped, but not by the group's outline: wanted {Box(wanted)} and the re-imported text " +
            $"carries {got}.");

        // The path beside it is the control: whatever clip it is inside is the one the text has to be inside too.
        Assert.True(pathClips.Any(box => Same(box, wanted)),
            $"the path's own clip is not the group's outline either ({string.Join(", ", pathClips.Select(Box))}), " +
            "so the two cannot be compared.");

        // And the crop bites: the outline is not the whole of what the block draws, which is what makes "a clip
        // exists" mean "something is hidden" rather than a page-box clip that cuts nothing.
        Rect2D placed = text.BoundingBox();
        Assert.True(wanted.Intersects(placed) && !wanted.Contains(placed),
            $"the clip {Box(wanted)} does not partly cut the re-imported block {Box(placed)}, so this fact would " +
            "pin nothing.");
    }

    /// <summary>
    /// A clip that **composes with a nested-viewport port** is the same crop for text as for a path.
    ///
    /// A nested <c>svg</c> is read as a second clip on the element's own group, so a group inside one carries two
    /// outlines and PDF intersects them with successive <c>W n</c> operators. Text has to be inside both - not
    /// inside the innermost one and outside the port - which is what the separate loop produced, and the port's
    /// outline is recorded one frame higher than the element's, so a loop that got the frame right for one item
    /// would still have to get it right for the other.
    /// </summary>
    [Fact]
    public void AGroupClipComposingWithANestedViewportPortReachesTheText()
    {
        if (!StandardFontFixture.Available) { return; }

        // Both outlines are stated in **artboard** coordinates first, so what they do to the page is readable
        // without composing three frames by hand, and only then carried back into the frames the model records
        // them in. The block sits at (106,140)-(138,169), and the two outlines are (95,130)-(115,155) and
        // (105,140)-(125,165): each one on its own removes part of the block - the first its left-hand columns and
        // its top, the second its right-hand columns and its bottom - and their intersection (105,140)-(115,155)
        // is a smaller window again. Neither is redundant: the port alone would leave the right-hand columns the
        // block actually has.
        Rect2D portInArtboard = new(95, 130, 20, 25);
        Rect2D elementInArtboard = new(105, 140, 20, 25);

        var port = new ArtGroup { Name = "port", Transform = new AffineTransform(1, 0, 0, 1, 40, 50) };
        // The port's outline is recorded in the frame the port group is placed in; the element's in the frame the
        // element's group is placed in, which is one level in - and that difference of frame is the point of the
        // fact. Both are stated above in the artboard and carried into their own frame here.
        port.Clips.Add(BackIntoFrame(portInArtboard, SelectionEngine.ToArtboard(port)));
        var inner = ClippedGroup(BackIntoFrame(elementInArtboard, new AffineTransform(1, 0, 0, 1, 40, 50)));
        port.AddItem(inner);

        CadDocument document = CadDocument.CreateDefault("Port and clip");
        document.Artboards[0].Layers[0].AddItem(port);

        CadDocument back = VectorRoundTrip(document);

        PathItem path = FilledPath(back);
        TextItem text = Assert.Single(back.AllItems().OfType<TextItem>());

        List<Rect2D> pathClips = ClipsInArtboard(path);
        List<Rect2D> textClips = ClipsInArtboard(text);

        // Two clips, and the same two for both.
        Assert.Equal(2, pathClips.Count);
        Assert.Equal(2, textClips.Count);

        var wanted = new List<Rect2D> { portInArtboard, elementInArtboard };

        foreach (Rect2D box in wanted)
        {
            Assert.True(textClips.Any(c => Same(c, box)),
                $"the re-imported text is missing the clip {Box(box)}; it carries " +
                $"{string.Join(", ", textClips.Select(Box))}.");
            Assert.True(pathClips.Any(c => Same(c, box)),
                $"the re-imported path is missing the clip {Box(box)}; it carries " +
                $"{string.Join(", ", pathClips.Select(Box))}.");
        }

        // The intersection is what actually shows, and it is a strict part of what the block draws - so the two
        // clips are doing work rather than merely being written.
        Rect2D window = wanted[0].Intersect(wanted[1]);
        Rect2D placed = text.BoundingBox();
        Assert.True(window.Intersects(placed) && !window.Contains(placed),
            $"the port and the element clip together give {Box(window)}, which does not partly cut the re-imported " +
            $"block {Box(placed)} - so this fact would pin nothing.");
    }

    /// <summary>
    /// **A document with no clips is a no-op**: the text reports no clip, and one is not invented for it.
    ///
    /// The walk now writes every item's clips in one place, so the case that must not change is the one where there
    /// is nothing to write - and a clip conjured out of the group's own transform would show up here as a clip on
    /// unclipped text.
    /// </summary>
    [Fact]
    public void UnclippedTextInAGroupReportsNoClip()
    {
        if (!StandardFontFixture.Available) { return; }

        var group = new ArtGroup { Name = "plain", Transform = GroupFrame };
        group.AddItem(Square(5, 5, 40));
        group.AddItem(Label(8, 20));

        CadDocument document = CadDocument.CreateDefault("Plain group");
        document.Artboards[0].Layers[0].AddItem(group);

        string operators = PdfDrawing.Of(PdfDocumentExporter.Export(document));
        Assert.DoesNotContain("W", operators);

        CadDocument back = VectorRoundTrip(document);
        TextItem text = Assert.Single(back.AllItems().OfType<TextItem>());
        PathItem path = FilledPath(back);
        Assert.Empty(ClipsInArtboard(text));
        Assert.Empty(ClipsInArtboard(path));

        // The group's frame still reaches it - the walk it now travels in is the one that carries `toDoc`
        // (issue #164), and this fact would otherwise be satisfied by writing the block at its local origin.
        Rect2D placed = text.BoundingBox();
        Assert.True(placed.Left >= 60 && placed.Top >= 60,
            $"the unclipped block re-imports at {Box(placed)}, outside the frame its group gives it.");
    }

    /// <summary>
    /// A rectangle stated in artboard coordinates, as the same outline in <paramref name="frame"/>'s own
    /// coordinates - which is the form a clip is recorded in.
    ///
    /// Undoing the frame rather than applying it is the whole subtlety of these facts: a clip is written where the
    /// container that holds it is *placed*, so an outline means something different one frame down.
    /// </summary>
    private static ClipSpec BackIntoFrame(Rect2D box, AffineTransform frame)
    {
        Assert.True(frame.IsInvertible, $"the frame ({frame.A},{frame.B},{frame.C},{frame.D},{frame.E},{frame.F}) " +
                                        "is not invertible, so no outline can be stated in it.");
        AffineTransform local = frame.Inverted();
        Point2D topLeft = local.Transform(new Point2D(box.Left, box.Top));
        Point2D bottomRight = local.Transform(new Point2D(box.Right, box.Bottom));
        return Rectangle(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    private static bool Same(Rect2D a, Rect2D b)
        => Math.Abs(a.Left - b.Left) < 1e-2 && Math.Abs(a.Top - b.Top) < 1e-2 &&
           Math.Abs(a.Right - b.Right) < 1e-2 && Math.Abs(a.Bottom - b.Bottom) < 1e-2;

    private static string Box(Rect2D box)
        => $"({Number(box.Left)}, {Number(box.Top)})-({Number(box.Right)}, {Number(box.Bottom)})";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
