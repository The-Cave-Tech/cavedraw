using VCCad.Core.Commands;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Renaming an object, and getting it back.
///
/// The name is not the whole change. Whether the name is the person's or the panel's guess
/// from the geometry travels with it: undoing a rename that only restored the string would
/// leave the object holding a derived name as though somebody had typed it, and it would stop
/// following its own shape for ever after.
/// </summary>
public class RenameItemCommandTests
{
    private static PathItem Object()
    {
        var path = new PathItem { Name = "Path", NameIsUserSet = false };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(10, 0)));
        return path;
    }

    [Fact]
    public void RenamingMarksTheNameAsThePersons()
    {
        PathItem item = Object();
        new RenameItemCommand(item, "Left sleeve").Do();

        Assert.Equal("Left sleeve", item.Name);
        Assert.True(item.NameIsUserSet);

        // Which is what the panel reads: the typed name, not the shape's.
        Assert.Equal("Left sleeve", ObjectNaming.DisplayName(item));
    }

    [Fact]
    public void UndoRestoresTheNameAndWhetherItWasThePersons()
    {
        PathItem item = Object();
        var command = new RenameItemCommand(item, "Left sleeve");

        command.Do();
        command.Undo();

        Assert.Equal("Path", item.Name);
        Assert.False(item.NameIsUserSet);

        // And so the row goes back to describing the shape.
        Assert.Equal("Line", ObjectNaming.DisplayName(item));
    }

    [Fact]
    public void UndoOfARenameOntoAnAlreadyNamedObjectKeepsThatName()
    {
        PathItem item = Object();
        item.Name = "First name";
        item.NameIsUserSet = true;

        var command = new RenameItemCommand(item, "Second name");
        command.Do();
        Assert.Equal("Second name", item.Name);

        command.Undo();
        Assert.Equal("First name", item.Name);
        Assert.True(item.NameIsUserSet);
    }

    [Fact]
    public void RedoAfterUndoAppliesAgain()
    {
        PathItem item = Object();
        var command = new RenameItemCommand(item, "Left sleeve");

        command.Do();
        command.Undo();
        command.Do();

        Assert.Equal("Left sleeve", item.Name);
        Assert.True(item.NameIsUserSet);

        command.Undo();
        Assert.False(item.NameIsUserSet);
    }

    [Fact]
    public void TheUndoEntrySaysWhatItWillReverse()
        => Assert.Equal("Rename to \u201cLeft sleeve\u201d",
            new RenameItemCommand(Object(), "Left sleeve").Description);

    [Fact]
    public void ACommandStackUndoesAndRedoesTheRename()
    {
        PathItem item = Object();
        var stack = new CommandStack();

        stack.Execute(new RenameItemCommand(item, "Left sleeve"));
        Assert.Equal("Left sleeve", item.Name);
        Assert.True(stack.CanUndo);

        stack.Undo();
        Assert.Equal("Path", item.Name);
        Assert.False(item.NameIsUserSet);
        Assert.True(stack.CanRedo);

        stack.Redo();
        Assert.Equal("Left sleeve", item.Name);
    }

    [Fact]
    public void RenamingToAnEmptyStringIsStillThePersonsChoice()
    {
        // Blanking a name is a thing a person can do, and the row should then show nothing
        // rather than falling back to the geometry - which is what the flag decides.
        PathItem item = Object();
        new RenameItemCommand(item, string.Empty).Do();

        Assert.Equal(string.Empty, item.Name);
        Assert.True(item.NameIsUserSet);
    }
}
