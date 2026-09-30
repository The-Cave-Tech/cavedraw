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
    /// AES-256 (/V 5, /R 6) used to be reported here as protected-but-not-opened, because it did not
    /// open: the assertion that pinned that is now the wrong way round, so it is a positive one.
    ///
    /// The property it was really guarding - a file we cannot open still says what it permits - is
    /// still exercised, by <see cref="PasswordEntryTests.AWrongPasswordDoesNotOpenIt"/> on a file
    /// whose password we do not have.
    /// </summary>
    [Fact]
    public void Aes256OpensAndReportsItsPermissions()
    {
        CadDocument document = PdfImporter.Import(
            File.ReadAllBytes(Path.Combine(FixtureDirectory, "aes-256.pdf")));

        Assert.NotNull(document.Security);
        Assert.True(document.Security!.Opened);
        Assert.Equal("AES-256", document.Security.Cipher);
        Assert.True(document.Security.CanPrint);
    }
}
