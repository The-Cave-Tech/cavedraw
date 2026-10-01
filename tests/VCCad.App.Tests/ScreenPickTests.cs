using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.Picking;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The eyedropper picks the colour under the cursor, and the colour it picked is readable afterwards.
///
/// The screen is one of the things a headless test genuinely cannot touch, so the capability is behind
/// <see cref="ScreenColour.Sampler"/> with the platform implementation as its default. Substituting one drives
/// the whole path - the operation, the state, the circle that shows it - instead of leaving it unreachable.
/// </summary>
public class ScreenPickTests : IDisposable
{
    /// <summary>A sampler that answers from a table, and can refuse the way an unsupported platform does.</summary>
    private sealed class FakeScreen : IScreenColourSampler
    {
        private readonly Dictionary<(int X, int Y), ColorRgb> _pixels = new();

        public bool IsSupported { get; init; } = true;

        public FakeScreen With(int x, int y, ColorRgb colour)
        {
            _pixels[(x, y)] = colour;
            return this;
        }

        public ColorRgb? Sample(int x, int y) => _pixels.TryGetValue((x, y), out ColorRgb c) ? c : null;
    }

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    public void Dispose()
    {
        // Never leak the stand-in into the next test.
        ScreenColour.ResetSampler();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void PickingAPointRecordsTheColourAndMakesItCurrent()
    {
        ScreenColour.Sampler = new FakeScreen().With(400, 300, ColorRgb.FromBytes(18, 52, 86));

        var context = new AutomationContext { ViewModel = new EditorViewModel() };
        object? result = EditorOperations.Invoke(context, "color.pickAt", Params(new { x = 400, y = 300 }));

        Assert.NotNull(result);
        string json = JsonSerializer.Serialize(result);
        Assert.Contains("\"picked\":true", json, StringComparison.Ordinal);
        Assert.Contains("#123456", json, StringComparison.OrdinalIgnoreCase);

        // The state holds it, which is what the small circle beside the eyedropper shows.
        Assert.Equal(ColorRgb.FromBytes(18, 52, 86), EditorColorState.Shared.LastPicked);
        Assert.Equal(ColorRgb.FromBytes(18, 52, 86), EditorColorState.Shared.Color);

        // And it is remembered as recent, so it can be clicked again later.
        Assert.Contains(EditorColorState.Shared.Recent, c => c == ColorRgb.FromBytes(18, 52, 86));
    }

    [Fact]
    public void PickingReadsTheScreenRatherThanAnythingCached()
    {
        var screen = new FakeScreen();
        ScreenColour.Sampler = screen;
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        screen.With(1, 1, ColorRgb.Red);
        EditorOperations.Invoke(context, "color.pickAt", Params(new { x = 1, y = 1 }));
        Assert.Equal(ColorRgb.Red, EditorColorState.Shared.LastPicked);

        screen.With(1, 1, ColorRgb.White);
        EditorOperations.Invoke(context, "color.pickAt", Params(new { x = 1, y = 1 }));
        Assert.Equal(ColorRgb.White, EditorColorState.Shared.LastPicked);
    }

    /// <summary>An unsupported platform is reported, never silent, and never a guessed colour.</summary>
    [Fact]
    public void AnUnsupportedPlatformSaysSoRatherThanReturningBlack()
    {
        ScreenColour.Sampler = new FakeScreen { IsSupported = false };

        // The colour state is shared, so the property is that a refused pick leaves it **as it was** rather
        // than that it is empty - which would be an assertion about whatever ran before this test.
        ColorRgb? before = EditorColorState.Shared.LastPicked;

        var context = new AutomationContext { ViewModel = new EditorViewModel() };
        object? result = EditorOperations.Invoke(context, "color.pickAt", Params(new { x = 10, y = 10 }));

        string json = JsonSerializer.Serialize(result);
        Assert.Contains("\"picked\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"supported\":false", json, StringComparison.Ordinal);
        Assert.Contains("cannot read the screen", json, StringComparison.Ordinal);
        Assert.Equal(before, EditorColorState.Shared.LastPicked);
    }

    /// <summary>A point outside every display is a refusal too, and says which kind it is.</summary>
    [Fact]
    public void APointOutsideEveryDisplayIsRefused()
    {
        ScreenColour.Sampler = new FakeScreen().With(0, 0, ColorRgb.Black);

        var context = new AutomationContext { ViewModel = new EditorViewModel() };
        object? result = EditorOperations.Invoke(context, "color.pickAt", Params(new { x = 99999, y = 99999 }));

        string json = JsonSerializer.Serialize(result);
        Assert.Contains("\"picked\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"supported\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOperationsAreInTheCatalogue()
    {
        Assert.True(EditorOperations.TryGet("color.pickAt", out _));
        Assert.True(EditorOperations.TryGet("color.picked", out _));
    }
}
