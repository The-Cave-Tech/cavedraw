using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// An edit that ends empty leaves nothing behind (#215).
///
/// A click with the text tool adds the object before a character is typed - CreateTextAt does that
/// deliberately, so the tool does not litter the page with a placeholder word - so abandoning the gesture used
/// to leave a zero-width empty text in the document for good: invisible on the canvas, counted by the model,
/// selectable by id, and twice mistaken for damage to the text beside it. These pin the decision where it can
/// be reached without a window: on the session, which is what the canvas calls as an edit ends.
/// </summary>
public class EmptyTextRemovalTests
{
    private static (DocumentSession Session, TextItem Text) SessionWithAnEmptyText()
    {
        var session = new DocumentSession();
        session.Initialize(CadDocument.CreateDefault("empty-text"));
        TextItem text = session.CreateTextAt(new Point2D(50, 50), "Nimbus Sans", 12);
        return (session, text);
    }

    private static int ObjectCount(DocumentSession session)
        => session.Document.Artboards[0].Layers[0].Children.Count;

    [Fact]
    public void AnEditThatEndsEmptyRemovesTheObject()
    {
        (DocumentSession session, TextItem text) = SessionWithAnEmptyText();

        Assert.Equal(1, ObjectCount(session));
        Assert.True(session.RemoveEmptyText(text), "an empty text must be removed, or the gesture leaves it behind");
        Assert.Equal(0, ObjectCount(session));
    }

    [Fact]
    public void ATextThatEndedWithCharactersIsLeftAlone()
    {
        (DocumentSession session, TextItem text) = SessionWithAnEmptyText();
        text.Runs[0].Text = "Kept";

        Assert.False(session.RemoveEmptyText(text), "a text with characters is artwork, not an abandoned gesture");
        Assert.Equal(1, ObjectCount(session));
    }

    /// <summary>Whitespace is a keystroke, so it is a decision - the object stays.</summary>
    [Fact]
    public void ATextHoldingOnlySpacesIsLeftAlone()
    {
        (DocumentSession session, TextItem text) = SessionWithAnEmptyText();
        text.Runs[0].Text = " ";

        Assert.False(session.RemoveEmptyText(text));
        Assert.Equal(1, ObjectCount(session));
    }
}