using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Text blocks are selectable objects like paths. They were reachable by a click
/// but invisible to the selection chrome, the resize/rotate handles and the
/// marquee, so selecting text produced no feedback and felt like it did not work.
/// These tests pin the selection-state half of that contract; the canvas drawing
/// half lives in <c>CanvasWorkspace.PaintOverlays</c>.
/// </summary>
public class TextSelectionTests
{
    private static (DocumentSession Session, TextItem Text) SessionWithText()
    {
        CadDocument document = CadDocument.CreateDefault("text-selection");
        Artboard artboard = document.Artboards[0];
        Layer layer = artboard.Layers[0];

        var text = new TextItem { Origin = new Point2D(10, 10) };
        text.Runs.Add(new TextRun { Text = "Hello", FontSize = 12 });
        layer.AddItem(text);

        var session = new DocumentSession();
        session.Initialize(document);
        return (session, text);
    }

    [Fact]
    public void SelectingTextMakesItTransformable()
    {
        (DocumentSession session, TextItem text) = SessionWithText();

        Assert.False(session.HasTransformableSelection);
        Assert.Empty(session.SelectedTextItems());

        session.SelectObject(text);

        Assert.True(session.IsObjectSelected(text));
        Assert.Contains(text, session.SelectedTextItems());
        Assert.True(
            session.HasTransformableSelection,
            "a selected text block must be transformable, or the canvas draws neither " +
            "its selection box nor its resize/rotate handles");
    }

    [Fact]
    public void TextHasUsableBoundsForPicking()
    {
        (DocumentSession session, TextItem text) = SessionWithText();
        session.SelectObject(text);

        Rect2D bounds = text.BoundingBox();
        Assert.False(bounds.IsEmpty, "a text block with runs must have non-empty bounds");

        // The bounds are what click hit-testing and the marquee intersect against,
        // so they have to cover the glyphs: width grows with the longest run and
        // height with the line box.
        Assert.True(bounds.Width > 0);
        Assert.True(bounds.Height >= text.MaxFontSize,
            "bounds are shorter than one line box, so clicks below the glyphs would miss");

        Assert.Equal(bounds, session.SelectionBounds());
    }

    [Fact]
    public void RotatedTextStillHasPickingBounds()
    {
        (DocumentSession session, TextItem text) = SessionWithText();
        session.SelectObject(text);

        // Rotation is part of the block; its bounds must stay usable for picking.
        text.RotationRadians = Math.PI / 4;
        Rect2D rotated = text.BoundingBox();

        Assert.False(rotated.IsEmpty);
        Assert.True(rotated.Contains(text.Origin), "rotated bounds must still cover the origin");
    }
}
