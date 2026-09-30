using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Opening a file that needs a password **typed**.
///
/// This is the other half of issue #36 and it is independent of the unresolved AES-256 question: a
/// file may open perfectly well once the password is known, and until there is a way to pass one in,
/// nothing does - the file just opens empty, because encryption hides streams and strings and leaves
/// the structure intact.
///
/// The fixtures carry a user password of `secret`, written by qpdf at the levels the handler already
/// supports, so these tests fail if the password is ignored as loudly as they fail if the handler
/// breaks.
/// </summary>
public class PasswordEntryTests
{
    private const string Password = "secret";

    /// <summary>The rectangle in model space, after the page's y-flip: the page is 200 tall.</summary>
    private static readonly Rect2D Expected = new(10, 140, 50, 50);

    public static TheoryData<string> PasswordProtectedFixtures => new()
    {
        "rc4-128-password.pdf",  // /V 2, /R 3
        "aes-128-password.pdf",  // /V 4, /R 4, /CFM /AESv2
    };

    [Theory]
    [MemberData(nameof(PasswordProtectedFixtures))]
    public void TheFileOpensWhenThePasswordIsSupplied(string fixture)
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(FixtureDirectory, fixture));

        CadDocument document = PdfImporter.Import(pdf, Password);

        PathItem? path = document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .OfType<PathItem>()
            .FirstOrDefault();

        Assert.NotNull(path);
        Rect2D bounds = path!.WorldBounds();
        Assert.Equal(Expected.X, bounds.X, 1);
        Assert.Equal(Expected.Y, bounds.Y, 1);
        Assert.Equal(Expected.Width, bounds.Width, 1);
        Assert.Equal(Expected.Height, bounds.Height, 1);
    }

    /// <summary>
    /// And without it there is no drawing - which is the symptom, not a failure: the page comes back
    /// empty rather than the import refusing.
    /// </summary>
    [Theory]
    [MemberData(nameof(PasswordProtectedFixtures))]
    public void NothingIsDrawnWithoutThePassword(string fixture)
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(FixtureDirectory, fixture));

        CadDocument? document = null;
        try
        {
            document = PdfImporter.Import(pdf);
        }
        catch (InvalidDataException)
        {
            // Refusing is also a correct answer for a file with nothing readable in it.
            return;
        }

        PathItem? path = document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .OfType<PathItem>()
            .FirstOrDefault(p => Math.Abs(p.WorldBounds().Width - Expected.Width) < 1);

        Assert.Null(path);
    }

    /// <summary>
    /// A wrong password is a wrong password: it must not be quietly accepted, and the file must still
    /// report itself as protected rather than as a plain document.
    /// </summary>
    [Theory]
    [MemberData(nameof(PasswordProtectedFixtures))]
    public void AWrongPasswordDoesNotOpenIt(string fixture)
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(FixtureDirectory, fixture));

        CadDocument? document = null;
        try
        {
            document = PdfImporter.Import(pdf, "not-the-password");
        }
        catch (InvalidDataException)
        {
            return;
        }

        Assert.NotNull(document.Security);
        Assert.False(document.Security!.Opened);
    }

    /// <summary>
    /// Supplying a password for a file that never needed one must not stop it opening - a person who
    /// types a password into a prompt they did not need is not punished for it.
    /// </summary>
    [Fact]
    public void APasswordOnAnUnprotectedFileChangesNothing()
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(FixtureDirectory, "plain-normalised.pdf"));

        CadDocument document = PdfImporter.Import(pdf, "pointless");

        Assert.Null(document.Security);
        Assert.Contains(
            document.Artboards.SelectMany(a => a.Layers).SelectMany(l => l.Children),
            item => item is PathItem);
    }

    private static string FixtureDirectory
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Encrypted");
}
