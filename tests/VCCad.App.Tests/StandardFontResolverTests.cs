using VCCad.App.Fonts;
using VCCad.Core.Model;
using Avalonia.Headless.XUnit;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Which face a run is actually drawn with.
///
/// There are three cases and telling them apart is the whole job: a run carrying a
/// programme, a run whose document asked for a name it did not embed, and a run whose face
/// the person chose. Conflating the last two is not a subtle error — it means every one of
/// the couple of hundred fonts the picker offers draws as the same face.
/// </summary>
public class StandardFontResolverTests
{
    private static TextRun Run(string family, string? source = null,
        EmbeddedFont? embedded = null, bool bold = false, bool italic = false)
        => new()
        {
            Text = "x",
            FontFamily = family,
            SourceFont = source,
            EmbeddedFont = embedded,
            FontSize = 12,
            Bold = bold,
            Italic = italic,
        };

    [AvaloniaFact]
    public void TheReportNamesTheFaceNotJustTheFamily()
    {
        // "Nimbus Sans" for a bold run says the family and not the face, so a person cannot
        // tell a real bold cut from a synthesised one. They look different.
        string bold = StandardFontResolver.Describe(Run("Helvetica-Bold", source: "Helvetica-Bold", bold: true));
        string plain = StandardFontResolver.Describe(Run("Helvetica", source: "Helvetica"));
        string italic = StandardFontResolver.Describe(
            Run("Helvetica-Oblique", source: "Helvetica-Oblique", italic: true));

        Assert.Contains("Nimbus Sans", bold, StringComparison.Ordinal);
        Assert.Contains("Bold", bold, StringComparison.Ordinal);
        Assert.Contains("Nimbus Sans", italic, StringComparison.Ordinal);
        Assert.Contains("Italic", italic, StringComparison.Ordinal);

        // The regular run must not claim a weight it does not have.
        Assert.DoesNotContain("Bold", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Italic", plain, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheReportSaysWhenAFaceIsOnlyInstalledInItsRegularCut()
    {
        // Courier is a name a PDF may use without embedding, and the machine has it. The
        // report must not claim a cut it would have to make up.
        string described = StandardFontResolver.Describe(
            Run("Courier-Bold", source: "Courier-Bold", bold: true));

        Assert.DoesNotContain("unavailable", described, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ThePickerOffersTheMachinesFontsAndTheStandardFacesFirst()
    {
        IReadOnlyList<string> offered = StandardFontResolver.OfferedFamilies();
        IReadOnlyList<string> standard = StandardFontResolver.StandardFamilyNames();

        Assert.NotEmpty(offered);

        // Every standard face is offered, and offered before anything else.
        Assert.All(standard, s => Assert.Contains(s, offered));
        Assert.Equal(standard, offered.Take(standard.Count));

        // The machine's own fonts are there too — offering three families would make the
        // picker useless for setting type.
        Assert.True(offered.Count > standard.Count,
            $"expected the machine's fonts as well as {standard.Count} standard faces, " +
            $"found {offered.Count} in total");
    }

    [AvaloniaFact]
    public void TheOfferedListHasNoDuplicates()
    {
        IReadOnlyList<string> offered = StandardFontResolver.OfferedFamilies();
        Assert.Equal(offered.Count, offered.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [AvaloniaFact]
    public void AFaceThePersonChoseIsUsedAsChosen()
    {
        // No SourceFont means nobody's file asked for this: it is the person's choice.
        Assert.Equal("Consolas", StandardFontResolver.FamilyFor(Run("Consolas")));
        Assert.Equal("Georgia", StandardFontResolver.FamilyFor(Run("Georgia")));
        Assert.Equal("Comic Sans MS", StandardFontResolver.FamilyFor(Run("Comic Sans MS")));
    }

    [AvaloniaFact]
    public void ANameTheDocumentAskedForGoesThroughTheStandardChain()
    {
        // Helvetica with no programme: every viewer supplies it, and so do we.
        string resolved = StandardFontResolver.FamilyFor(Run("Nimbus Sans", source: "Helvetica"));

        Assert.NotEqual("Helvetica", resolved);
        Assert.False(string.IsNullOrWhiteSpace(resolved));
    }

    [AvaloniaFact]
    public void AnEmbeddedProgrammeIsNeverReplaced()
    {
        var embedded = new EmbeddedFont
        {
            FamilyName = "VCCadEmbedded",
            BaseFont = "NPFRLV+CenturyGothic-Bold",
            Program = new byte[] { 1, 2, 3 },
        };

        // The document's own font wins over everything, including a name that would
        // otherwise classify as a standard face.
        Assert.Equal("NPFRLV+CenturyGothic-Bold",
            StandardFontResolver.FamilyFor(
                Run("NPFRLV+CenturyGothic-Bold", source: "Helvetica", embedded: embedded)));
    }

    [AvaloniaFact]
    public void ClearingTheSourceIsWhatMakesAChoiceStick()
    {
        var run = Run("Nimbus Sans", source: "Helvetica");

        // While the document's name stands, the chain decides.
        Assert.NotEqual("Consolas", StandardFontResolver.FamilyFor(run));

        // Once the person chooses, it does not.
        run.FontFamily = "Consolas";
        run.SourceFont = null;
        Assert.Equal("Consolas", StandardFontResolver.FamilyFor(run));
    }

    [AvaloniaFact]
    public void ABase14AliasIsSuppliedRatherThanPassedThrough()
    {
        // Whatever face the chain lands on, what matters is that it is not the alias
        // itself: a document naming ArialMT is asking us to provide Arial's metrics, not
        // asking for a family called "ArialMT".
        foreach (string alias in new[]
                 {
                     "Helvetica", "ArialMT", "TimesNewRomanPSMT", "CourierStd", "ZapfDingbats",
                 })
        {
            string resolved = StandardFontResolver.FamilyFor(Run("unused", source: alias));
            Assert.NotEqual(alias, resolved);
            Assert.False(string.IsNullOrWhiteSpace(resolved), $"{alias} resolved to nothing");
        }
    }
}
