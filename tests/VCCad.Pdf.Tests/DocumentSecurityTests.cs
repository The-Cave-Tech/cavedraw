using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// What a protected file says it permits, carried onto the document for the status-bar padlock
/// (issue #37).
///
/// The important case is the last one: a file we could **not** open still reports its permissions,
/// because /Encrypt is not itself encrypted. That is exactly when a person most needs telling, and it
/// would be easy to lose by only reporting on a successful decrypt.
/// </summary>
public class DocumentSecurityTests
{
    private static string FixtureDirectory
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Encrypted");

    [Fact]
    public void AnUnprotectedFileCarriesNoSecurity()
    {
        CadDocument document = PdfImporter.Import(
            File.ReadAllBytes(Path.Combine(FixtureDirectory, "plain-normalised.pdf")));

        Assert.Null(document.Security);
    }

    [Theory]
    [InlineData("rc4-40.pdf", "RC4-40")]
    [InlineData("rc4-128.pdf", "RC4-128")]
    [InlineData("aes-128.pdf", "AES-128")]
    public void AProtectedFileReportsHowItWasProtected(string fixture, string cipher)
    {
        CadDocument document = PdfImporter.Import(File.ReadAllBytes(Path.Combine(FixtureDirectory, fixture)));

        Assert.NotNull(document.Security);
        Assert.True(document.Security!.Opened);
        Assert.Equal(cipher, document.Security.Cipher);
    }

    /// <summary>
    /// qpdf wrote these with /P -4 - every permission granted - so the padlock must say so rather
    /// than imply a restriction the file does not carry.
    /// </summary>
    [Fact]
    public void ThePermissionBitsAreReadFromTheFile()
    {
        CadDocument document = PdfImporter.Import(
            File.ReadAllBytes(Path.Combine(FixtureDirectory, "rc4-128.pdf")));

        DocumentSecurity security = document.Security!;

        Assert.True(security.CanPrint);
        Assert.True(security.CanModify);
        Assert.True(security.CanCopy);
        Assert.True(security.CanAssemble);
        Assert.Equal(8, security.Listed().Count);
        Assert.All(security.Listed(), entry => Assert.True(entry.Allowed, entry.Name));
    }

    /// <summary>
    /// The case that matters for a print shop: a file we cannot open still says what it permits.
    /// </summary>
    [Fact]
    public void AFileThatCouldNotBeOpenedStillReportsItsPermissions()
    {
        CadDocument document = PdfImporter.Import(
            File.ReadAllBytes(Path.Combine(FixtureDirectory, "aes-256.pdf")));

        // Documented gap: AES-256 does not open yet (issue #36). What must hold either way is that
        // the file is reported as protected, and by what, rather than looking like a plain document.
        Assert.NotNull(document.Security);
        Assert.False(document.Security!.Opened);
        Assert.Contains("not opened", document.Security.Cipher, StringComparison.OrdinalIgnoreCase);
        Assert.True(document.Security.CanPrint);
    }
}
