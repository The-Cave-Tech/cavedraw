using VCCad.App.Automation;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Which operations are worth journaling for crash recovery.
///
/// Only edits to the document belong in the command queue. The first version of this was
/// a deny-list of reads, so every operation nobody had thought about counted as an edit —
/// including <c>view.fit</c>, which the application runs on every launch. It therefore
/// wrote a journal every time, and the recovery prompt reappeared every time the app was
/// opened. The person dismissed it, used the app, and found it waiting again.
/// </summary>
public class SessionJournalTests
{
    [Theory]
    [InlineData("view.fit")]
    [InlineData("view.zoom")]
    [InlineData("view.centerOn")]
    [InlineData("view.toScreen")]
    [InlineData("tool.set")]
    [InlineData("selection.clear")]
    [InlineData("selection.set")]
    [InlineData("ui.find")]
    [InlineData("ui.click")]
    [InlineData("ui.dump")]
    [InlineData("document.list")]
    [InlineData("document.summary")]
    [InlineData("document.dump")]
    [InlineData("document.verifyRoundTrip")]
    [InlineData("object.find")]
    [InlineData("object.list")]
    [InlineData("text.runs")]
    [InlineData("text.caret")]
    [InlineData("pane.list")]
    [InlineData("fonts.list")]
    [InlineData("history.search")]
    [InlineData("app.operations")]
    [InlineData("image.exportPng")]
    public void ReadingIsNotAnEdit(string name)
    {
        Assert.False(SessionJournal.IsMutation(name), $"{name} must not be journaled");
    }

    [Theory]
    [InlineData("object.create")]
    [InlineData("object.delete")]
    [InlineData("object.transform")]
    [InlineData("object.arrange")]
    [InlineData("image.insert")]
    [InlineData("text.update")]
    [InlineData("text.style")]
    [InlineData("artboard.setBounds")]
    [InlineData("layer.add")]
    [InlineData("document.new")]
    [InlineData("document.importPdf")]
    public void EditingIsAnEdit(string name)
    {
        Assert.True(SessionJournal.IsMutation(name), $"{name} must be journaled");
    }

    [Fact]
    public void AnOperationNobodyHasHeardOfIsNotAnEdit()
    {
        // The allow-list exists so an unclassified operation is treated as harmless
        // rather than as an edit. Guessing the other way is what caused the prompt to
        // reappear on every launch.
        Assert.False(SessionJournal.IsMutation("something.new.entirely"));
        Assert.False(SessionJournal.IsMutation(""));
    }
}
