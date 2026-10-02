using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Where an item sits when its artboard is **not at the document origin** and a group above it turns or scales it.
///
/// Two compositions of the artboard origin were in the code, and they disagree exactly here:
/// <list type="bullet">
/// <item>the canvas and this engine put the artboard origin **outside** the enclosing group transforms -
/// <c>world = T(artboard) ∘ G</c>;</item>
/// <item><c>PdfDocumentExporter.WorldTransform</c> put it **inside** - <c>G ∘ T(artboard)</c>.</item>
/// </list>
/// A pure translation commutes, so every single-artboard fixture passed; a rotation does not, and the two differ
/// by a rotation of the offset. Multi-page documents lay their artboards out on a grid, so a non-zero X/Y is the
/// normal case for any page after the first, and an SVG import carries a root <c>scale(0.75)</c> group - so this
/// is the ordinary case, not an edge one (#168).
///
/// **Which order is right is not a matter of taste, and it is settled by what the canvas draws.**
/// <c>CanvasWorkspace.PaintLayers</c> starts every artboard's items at <c>T(artboard.X, artboard.Y)</c> and
/// descends with <c>toWorld.Compose(group.Transform)</c>; the hit test subtracts the artboard origin before it
/// applies <c>G⁻¹</c>. Both are the outside order, so it is <c>PdfDocumentExporter</c> that has to move - which is
/// the reverse of the order the issue guessed at, and the reason the task said to work it out rather than assume.
///
/// These tests state the arithmetic themselves rather than asking the code under test to confirm itself: the
/// expected corners below are <c>T(300,200) ∘ rotate90(about 200,200)</c> applied by hand.
/// </summary>
public class ArtboardOriginFrameTests
{
    private const double Ax = 300;
    private const double Ay = 200;

    /// <summary>
    /// A 400x400 artboard at (300,200) - page two of a grid, not the first - holding one group turned a quarter
    /// turn about the artboard's own centre, holding one 40x40 filled square at artboard-local (150,180).
    /// </summary>
    private static (CadDocument Document, PathItem Path, ArtGroup Group, Artboard Board) Fixture()
    {
        var document = new CadDocument();
        var board = new Artboard(new Size2D(400, 400), new Point2D(Ax, Ay)) { Name = "Page 2" };
        Layer layer = board.AddLayer("Artwork");
        document.AddArtboard(board);

        var group = new ArtGroup
        {
            Name = "turned",
            Transform = AffineTransform.CreateRotationAround(new Point2D(200, 200), Math.PI / 2),
        };

        layer.AddItem(group);

        var path = new PathItem { Name = "square", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(150, 180)));
        sub.Nodes.Add(new PathNode(new Point2D(190, 180)));
        sub.Nodes.Add(new PathNode(new Point2D(190, 220)));
        sub.Nodes.Add(new PathNode(new Point2D(150, 220)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);
        group.AddItem(path);

        return (document, path, group, board);
    }

    /// <summary>The square's stored corners, in the order the fixture adds them.</summary>
    private static Point2D[] Stored(PathItem path)
        => path.SubPaths[0].Nodes.Select(node => node.Anchor).ToArray();

    /// <summary>
    /// The world frame is the artboard origin composed **outside** the enclosing group transform: each stored
    /// corner reaches <c>artboard origin + G(corner)</c>, and the same square under the other order is somewhere
    /// else entirely.
    ///
    /// The turned square is 40x40 about its own centre, so rotation alone cannot distinguish the orders by its
    /// box - the corners can, and the offset is what the rotation acts on, which is why the expected points below
    /// are written out one by one rather than as a box.
    /// </summary>
    [Fact]
    public void TheArtboardOriginIsComposedOutsideTheGroupTransform()
    {
        (_, PathItem path, ArtGroup group, _) = Fixture();

        // rotate90 about (200,200) is (x,y) -> (400 - y, x); then the artboard origin (300,200) is added.
        Point2D[] expected =
        {
            new(520, 350), // (150,180) -> (220,150)
            new(520, 390), // (190,180) -> (220,190)
            new(480, 390), // (190,220) -> (180,190)
            new(480, 350), // (150,220) -> (180,220)
        };

        // The frame below the origin is the group transform and nothing else: placing the origin is what turns it
        // into world coordinates, and stating that here is what makes "outside" a fact about this codebase rather
        // than about this test.
        AffineTransform toArtboard = SelectionEngine.ToArtboard(path);
        Assert.Equal(group.Transform.A, toArtboard.A, 9);
        Assert.Equal(group.Transform.B, toArtboard.B, 9);
        Assert.Equal(group.Transform.C, toArtboard.C, 9);
        Assert.Equal(group.Transform.D, toArtboard.D, 9);
        Assert.Equal(group.Transform.E, toArtboard.E, 9);
        Assert.Equal(group.Transform.F, toArtboard.F, 9);

        AffineTransform toWorld = SelectionEngine.ToWorld(path);
        Point2D[] stored = Stored(path);
        for (int i = 0; i < expected.Length; i++)
        {
            Point2D actual = toWorld.Transform(stored[i]);
            Assert.Equal(expected[i].X, actual.X, 6);
            Assert.Equal(expected[i].Y, actual.Y, 6);
        }

        // The other composition - the origin inside the item's frame, `G ∘ T(artboard)` - puts the same square at
        // (-20,450)..(20,490). Without this half the test would pass for either order, because both are affine
        // maps and only one of them is in the place the canvas draws.
        AffineTransform inside = toArtboard.Compose(AffineTransform.CreateTranslation(Ax, Ay));
        Point2D rotatedOffset = inside.Transform(stored[0]);
        Assert.Equal(20, rotatedOffset.X, 6);
        Assert.Equal(450, rotatedOffset.Y, 6);
    }

    /// <summary>
    /// The world box the canvas measures a selection with is the box that frame carries - the turned square at
    /// (480,350)..(520,390) - and not the narrower <see cref="PathItem.WorldBounds"/>, which adds the artboard
    /// origin and ignores every group above the path.
    /// </summary>
    [Fact]
    public void TheCanvasWorldBoxIsTheBoxTheFrameCarries()
    {
        (_, PathItem path, _, _) = Fixture();

        Rect2D box = SelectionEngine.WorldBounds(new[] { path });

        Assert.Equal(480, box.Left, 6);
        Assert.Equal(350, box.Top, 6);
        Assert.Equal(40, box.Width, 6);
        Assert.Equal(40, box.Height, 6);

        // The narrow answer is a different box 30 units away, which is why the exporter's filter region could not
        // be asked of it - see the export test that pins the same rule through a re-imported page.
        Rect2D narrow = path.WorldBounds();
        Assert.Equal(450, narrow.Left, 6);
        Assert.NotEqual(box.Left, narrow.Left, 6);
    }

    /// <summary>
    /// A click picks the square where the world frame puts it, and picks nothing where the inside order would.
    ///
    /// The hit test walks the other way - it subtracts the artboard origin and then applies <c>G⁻¹</c>, which is
    /// the inverse of <c>T(artboard) ∘ G</c> and of nothing else - so this is the second, independent place that
    /// says outside, and the second half is what makes the first half mean something.
    /// </summary>
    [Fact]
    public void AClickPicksTheSquareWhereTheWorldFramePutsIt()
    {
        (CadDocument document, PathItem path, _, Artboard board) = Fixture();

        Point2D centre = new(500, 370);

        // Drilling reaches the square itself, through the group's own transform.
        Assert.Same(path, SelectionEngine.Within(board, centre));
        Assert.Contains(path, SelectionEngine.Chain(board, centre));

        // A single click selects the group it lives in and focuses page two, not page one.
        SelectionResult click = SelectionEngine.Click(document, centre);
        Assert.Same(board, click.Focused);
        Assert.Equal("turned", Assert.Single(click.Items).Name);

        // The inside order's centre is (0,470): past the left edge of every artboard, so it picks nothing at all.
        SelectionResult elsewhere = SelectionEngine.Click(document, new Point2D(0, 470));
        Assert.Empty(elsewhere.Items);
    }

    /// <summary>
    /// The same rule for a **scale** and a **skew**, which the issue names alongside rotation - and a scaling
    /// group on an off-origin artboard is not hypothetical: the root unit conversion of every SVG import is an
    /// <c>ArtGroup</c> carrying <c>scale(0.75)</c>, so an imported file on page two is exactly this case.
    ///
    /// The composition is stated by this test rather than asked of the code - <c>T(artboard) ∘ G</c>, applied to
    /// the stored corners - and every case also asserts that the inside order gives something else, so a case that
    /// happened to commute could not stand in for one that does not.
    /// </summary>
    public static TheoryData<string, AffineTransform> NonTranslationGroups => new()
    {
        { "rotation", AffineTransform.CreateRotationAround(new Point2D(200, 200), Math.PI / 2) },
        { "scale", AffineTransform.CreateScale(1.5, 0.75) },
        { "skew", AffineTransform.CreateSkew(Math.PI / 6, 0) },
    };

    [Theory]
    [MemberData(nameof(NonTranslationGroups))]
    public void EveryNonTranslationGroupIsComposedOutsideTheArtboardOrigin(string what, AffineTransform group)
    {
        (CadDocument document, PathItem path, ArtGroup holder, Artboard board) = Fixture();
        holder.Transform = group;

        // The fixture's square, one page over: the artboard origin composed outside the group transform.
        AffineTransform expected = AffineTransform.CreateTranslation(Ax, Ay).Compose(group);
        AffineTransform actual = SelectionEngine.ToWorld(path);

        foreach (Point2D stored in Stored(path))
        {
            Point2D want = expected.Transform(stored);
            Point2D got = actual.Transform(stored);
            Assert.Equal(want.X, got.X, 6);
            Assert.Equal(want.Y, got.Y, 6);
        }

        // ...and the other order is a different place, so this test can tell them apart for this group too.
        AffineTransform inside = group.Compose(AffineTransform.CreateTranslation(Ax, Ay));
        Assert.NotEqual(
            inside.Transform(Stored(path)[0]).X,
            actual.Transform(Stored(path)[0]).X,
            6);
    }
}
