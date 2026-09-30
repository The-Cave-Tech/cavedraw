using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The permission bits of a protected file (ISO 32000-1 Table 22), which the status-bar padlock
/// reports.
///
/// The numbering is one-based from the least significant end, which is the kind of thing that is
/// easy to be off by one about and impossible to notice: a report that said "Printing: no" for a file
/// that permits printing would be quietly wrong in the one place a person would act on it.
/// </summary>
public class DocumentSecurityTests
{
    private static DocumentSecurity Security(int permissions, bool owner = false)
        => new("RC4-128", permissions, Opened: true, OpenedWithOwnerPassword: owner);

    /// <summary>Printing is bit 3: with only that bit set, the file permits printing and nothing else.</summary>
    [Fact]
    public void PrintingIsBitThree()
    {
        DocumentSecurity security = Security(4);

        Assert.True(security.CanPrint);
        Assert.False(security.CanModify);
        Assert.False(security.CanCopy);
        Assert.False(security.CanAnnotate);
    }

    [Fact]
    public void EachPermissionIsItsOwnBit()
    {
        Assert.True(Security(8).CanModify);
        Assert.True(Security(16).CanCopy);
        Assert.True(Security(32).CanAnnotate);
        Assert.True(Security(256).CanFillForms);
        Assert.True(Security(512).CanExtractForAccessibility);
        Assert.True(Security(1024).CanAssemble);
        Assert.True(Security(2048).CanPrintHighResolution);
    }

    /// <summary>
    /// A file may permit low-resolution printing while forbidding the high-resolution kind, which is
    /// the difference between "read it on screen" and "put it on a press".
    /// </summary>
    [Fact]
    public void PrintingAtHighResolutionIsSeparateFromPrinting()
    {
        DocumentSecurity proofOnly = Security(4);

        Assert.True(proofOnly.CanPrint);
        Assert.False(proofOnly.CanPrintHighResolution);
    }

    /// <summary>
    /// /P restricts the USER password. Someone who supplied the owner password has the whole
    /// document whatever the bits say, and reporting otherwise would misstate the file.
    /// </summary>
    [Fact]
    public void TheOwnerPasswordGrantsEverythingWhateverTheBitsSay()
    {
        DocumentSecurity restricted = Security(0, owner: true);

        Assert.All(restricted.Listed(), entry => Assert.True(entry.Allowed, entry.Name));
    }

    [Fact]
    public void EveryPermissionIsListedInTheOrderAPersonWouldAsk()
    {
        DocumentSecurity security = Security(-4);

        string[] names = security.Listed().Select(e => e.Name).ToArray();

        Assert.Equal("Printing", names[0]);
        Assert.Equal("High-resolution printing", names[1]);
        Assert.Equal("Copying text and graphics", names[2]);
        Assert.Equal("Modifying the document", names[3]);
        Assert.Equal(8, names.Length);
    }

    /// <summary>A file that was never opened still reports what it claims.</summary>
    [Fact]
    public void AnUnopenedFileStillListsItsPermissions()
    {
        var security = new DocumentSecurity("not opened", 4, Opened: false, OpenedWithOwnerPassword: false);

        Assert.False(security.Opened);
        Assert.True(security.CanPrint);
        Assert.False(security.CanModify);
    }
}
