using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A field edit changes that field and nothing else.
///
/// The text toolbar's size handler sends the font field's text along with the member it is editing, and that
/// text is empty whenever the block holds no single face. Writing it through erased the run's family:
/// measured, "Nimbus Sans" before a size edit and "" after. An empty name then reached Avalonia's
/// FontFamily constructor during a paint pass and terminated the process (#213). The guard in the renderer
/// stops the crash; these pin the data half, so the blank is never written in the first place.
/// </summary>
public class TextFamilyTests
{
    private static (DocumentSession Session, TextItem Text) SessionWithText(string family = "Nimbus Sans")
    {
        CadDocument document = CadDocument.CreateDefault("text-family");
        Artboard artboard = document.Artboards[0];
        Layer layer = artboard.Layers[0];

        var text = new TextItem { Origin = new Point2D(10, 10) };
        text.Runs.Add(new TextRun { Text = "Hello", FontSize = 12, FontFamily = family });
        layer.AddItem(text);

        var session = new DocumentSession();
        session.Initialize(document);
        session.SelectObject(text);
        return (session, text);
    }

    [Fact]
    public void EditingTheSizeLeavesTheFamilyAlone()
    {
        (DocumentSession session, TextItem text) = SessionWithText();

        // Exactly what the size field sends: the member it is editing, and the font field's text, which is
        // empty when the block holds no single face.
        session.ApplyTextFieldsAt(
            runIndex: null, content: null, family: string.Empty, fontSize: 24,
            bold: null, italic: null, color: null);

        Assert.Equal(24, text.Runs[0].FontSize, 6);
        Assert.Equal("Nimbus Sans", text.Runs[0].FontFamily);
    }

    [Fact]
    public void WhitespaceIsNotAFaceEither()
    {
        (DocumentSession session, TextItem text) = SessionWithText();

        session.ApplyTextFieldsAt(null, null, "   ", 18, true, null, null);

        Assert.Equal("Nimbus Sans", text.Runs[0].FontFamily);
        Assert.Equal(18, text.Runs[0].FontSize, 6);
        Assert.True(text.Runs[0].Bold);
    }

    [Fact]
    public void ARealFamilyStillReplacesTheFace()
    {
        (DocumentSession session, TextItem text) = SessionWithText();

        session.ApplyTextFieldsAt(null, null, "Times New Roman", null, null, null, null);

        Assert.Equal("Times New Roman", text.Runs[0].FontFamily);
    }

    [Fact]
    public void TheFamilySurvivesEveryOtherMemberInOneEdit()
    {
        (DocumentSession session, TextItem text) = SessionWithText();

        session.ApplyTextFieldsAt(
            runIndex: null, content: "Hello again", family: string.Empty, fontSize: 30,
            bold: true, italic: true, color: new ColorRgb(1, 0, 0));

        TextRun run = text.Runs[0];
        Assert.Equal("Nimbus Sans", run.FontFamily);
        Assert.Equal(30, run.FontSize, 6);
        Assert.True(run.Bold);
        Assert.True(run.Italic);
    }
}
