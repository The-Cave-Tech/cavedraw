using VCCad.App;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Development builds do not offer to recover unsaved work.
///
/// A developer's documents are scratch files that are expected to be thrown away, and the recovery prompt
/// costs more than it saves: it is the first thing on screen, it changes which documents are open underneath
/// whatever is being tested, and the overlay it puts up covers the whole editing area and swallows pointer
/// events - so a driver that does not dismiss it has every gesture silently do nothing.
///
/// Released builds still recover. This is a development concession, not a removal of the feature.
/// </summary>
public class DevelopmentModeTests
{
    /// <summary>A build run out of its own output directory is a development build.</summary>
    [Theory]
    [InlineData(@"C:\Development\vccad\main\src\VCCad.App.Desktop\bin\Release\net8.0\", true)]
    [InlineData(@"C:\Development\vccad\main\src\VCCad.App.Desktop\bin\Debug\net8.0\", true)]
    [InlineData("/home/dev/vccad/src/VCCad.App.Desktop/bin/Release/net8.0/", true)]
    [InlineData(@"C:\Program Files\VCCad\", false)]
    [InlineData(@"C:\Development\vccad\main\artifacts\desktop\win-x64\", false)]
    [InlineData("/opt/vccad/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ABuildRunFromItsOwnOutputIsDevelopment(string? directory, bool expected)
        => Assert.Equal(expected, DevelopmentMode.IsBuildOutput(directory));

    /// <summary>Development, with no flags: no recovery, which is the whole point.</summary>
    [Fact]
    public void DevelopmentDoesNotOfferRecovery()
        => Assert.False(DevelopmentMode.ShouldOfferRecovery(
            noRecovery: false, forceRecovery: false, development: true));

    /// <summary>A released build still offers it.</summary>
    [Fact]
    public void AReleaseBuildStillOffersRecovery()
        => Assert.True(DevelopmentMode.ShouldOfferRecovery(
            noRecovery: false, forceRecovery: false, development: false));

    /// <summary>A driver that asks for it gets it, development or not - the concession has an override.</summary>
    [Fact]
    public void RecoverForcesItOnInDevelopment()
    {
        Assert.True(DevelopmentMode.ShouldOfferRecovery(
            noRecovery: false, forceRecovery: true, development: true));
        Assert.True(DevelopmentMode.ShouldOfferRecovery(
            noRecovery: false, forceRecovery: true, development: false));
    }

    /// <summary>--no-recover still wins, and still means the same thing everywhere.</summary>
    [Fact]
    public void NoRecoverStillWins()
    {
        Assert.False(DevelopmentMode.ShouldOfferRecovery(
            noRecovery: true, forceRecovery: false, development: false));
        Assert.False(DevelopmentMode.ShouldOfferRecovery(
            noRecovery: true, forceRecovery: false, development: true));
    }
}
