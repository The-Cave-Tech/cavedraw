using VCCad.App.Automation;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `--size WxH` asks for a window size, and refuses nonsense instead of ignoring it.
///
/// The window is the frame of every recording, screenshot and `ui.dump`, so a driver that wants a wide, short
/// frame for a landscape page has to be able to ask for one - and a size that silently does nothing is worse
/// than one that fails, because the recording is made before anyone notices.
/// </summary>
public class DesktopStartupOptionsTests
{
    [Theory]
    [InlineData("1920x1200", 1920, 1200)]
    [InlineData("1920X1200", 1920, 1200)]
    [InlineData("800x600", 800, 600)]
    [InlineData("1x1", 1, 1)]
    public void ASizeIsWidthByHeight(string text, int width, int height)
    {
        Assert.True(DesktopStartupOptions.TryParseSize(text, out int w, out int h));
        Assert.Equal(width, w);
        Assert.Equal(height, h);
    }

    [Theory]
    [InlineData("1920")]
    [InlineData("1920x")]
    [InlineData("x1200")]
    [InlineData("0x1200")]
    [InlineData("1920x0")]
    [InlineData("-1920x1200")]
    [InlineData("1920x1200x1")]
    [InlineData("widextall")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? text)
        => Assert.False(DesktopStartupOptions.TryParseSize(text, out _, out _));

    /// <summary>A size turns docking off: half a screen is the wrong answer to a size asked for by hand.</summary>
    [Fact]
    public void ASizeTurnsDockingOff()
    {
        DesktopStartupOptions options = DesktopStartupOptions.Parse(new[] { "--size", "1920x1200" });

        Assert.Null(options.ArgumentError);
        Assert.Equal((1920, 1200), options.WindowSize);
        Assert.True(options.NoDock);
    }

    /// <summary>And nonsense names the flag rather than being ignored.</summary>
    [Fact]
    public void NonsenseNamesTheFlag()
    {
        DesktopStartupOptions options = DesktopStartupOptions.Parse(new[] { "--size", "wide" });

        Assert.NotNull(options.ArgumentError);
        Assert.Contains("--size", options.ArgumentError!, StringComparison.Ordinal);
        Assert.Contains("1920x1200", options.ArgumentError!, StringComparison.Ordinal);
        Assert.Null(options.WindowSize);
    }

    /// <summary>With no flag the default stands, and docking is left alone.</summary>
    [Fact]
    public void WithNoFlagNothingIsAskedFor()
    {
        DesktopStartupOptions options = DesktopStartupOptions.Parse(Array.Empty<string>());

        Assert.Null(options.WindowSize);
        Assert.False(options.NoDock);
        Assert.Null(options.ArgumentError);
    }
}
