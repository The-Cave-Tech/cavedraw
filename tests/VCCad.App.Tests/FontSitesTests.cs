using VCCad.App.Fonts;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **Where a missing font can be got from** (issue #262).
///
/// These are the assertions that replace clicking the control. The request is that a person looking for a font the document
/// names but this machine cannot draw is given the places it can be found - and the part that matters is that the places come
/// with what their licensing is like, because "free" on one of them is not the same offer as "free" on another. Installing a
/// face whose licence forbids it is the person's decision, but it has to be a knowing one.
///
/// Asserted here rather than through the window on purpose: the pane's own preview strip is rewritten by the pane, so reading
/// it proves nothing about what a control did, and three rounds went into learning that. A method can be asserted.
/// </summary>
public class FontSitesTests
{
    [Fact]
    public void EveryPlaceOffersASearchAndSaysWhatItsLicencesAreLike()
    {
        Assert.NotEmpty(FontSites.All);

        foreach (FontSource site in FontSites.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(site.Name), "a site with no name cannot be offered");
            Assert.False(string.IsNullOrWhiteSpace(site.Search), $"{site.Name} has no search URL");

            // The licence note is the point of the feature: a list of sites without it would be a list of links.
            Assert.False(
                string.IsNullOrWhiteSpace(site.Licence),
                $"{site.Name} carries no licence note, so the person cannot judge it before installing");
        }
    }

    [Fact]
    public void ASearchUrlCarriesTheFontNameAndIsEscaped()
    {
        FontSource google = FontSites.All.First(s => s.Name == "Google Fonts");

        Assert.Equal("https://fonts.google.com/?query=Century%20Gothic", google.UrlFor("Century Gothic"));

        // A name with characters that mean something in a URL must not break out of the query: PDF subset names contain '+'
        // and '-', and a name with a space or an ampersand must not truncate the search.
        Assert.Contains("NPFRLV%2BCenturyGothic-Bold", google.UrlFor("NPFRLV+CenturyGothic-Bold"));
        Assert.EndsWith("a%26b", google.UrlFor("a&b"));
    }

    [Fact]
    public void TheDownloadablePlacesAreAllOfThemExceptTheReference()
    {
        // One is a reference for identifying a face rather than somewhere to obtain one, so it is offered but not counted as
        // a place to download from.
        Assert.Equal(FontSites.All.Count - 1, FontSites.Downloads.Count);
        Assert.DoesNotContain(FontSites.Downloads, s => s.Name == "Fonts In Use");
        Assert.All(FontSites.Downloads, d => Assert.Contains(FontSites.All, a => a.Name == d.Name));
    }

    [Fact]
    public void TheInstallHintSaysNothingIsRedistributed()
    {
        // The product rule: fonts are never bundled. The hint has to say so, because it is the sentence a person reads
        // before they go and install something.
        Assert.Contains("font directory", FontSites.InstallHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redistributed", FontSites.InstallHint, StringComparison.OrdinalIgnoreCase);
    }
}
