using System.Runtime.InteropServices;
using VCCad.Core.Model;

namespace VCCad.App.Picking;

/// <summary>
/// Reads a colour off the screen, anywhere on it.
///
/// The point of picking a colour is usually that it is somewhere else - a photograph, a website, another
/// window - so this reads the **screen**, not this application's rendering of it. An eyedropper that can only
/// read its own document is a colour history, which the recent-swatch pad already is.
/// </summary>
public interface IScreenColourSampler
{
    /// <summary>Whether this platform can read the screen at all.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// The colour of the screen at a point in screen pixels, or null where it cannot be read - an unsupported
    /// platform, or a point outside every display. Null means "could not", never "black".
    /// </summary>
    ColorRgb? Sample(int x, int y);
}

/// <summary>
/// The sampler the application uses, and the seam that makes the picker testable.
///
/// The screen is one of those things a headless test genuinely cannot touch, so the capability is behind an
/// interface with the platform implementation as its default. A test substitutes one and drives the whole path
/// - the operation, the state, the swatch that shows it - rather than being unable to reach any of it.
/// </summary>
public static class ScreenColour
{
    private static IScreenColourSampler _sampler = new PlatformScreenColourSampler();

    /// <summary>The current sampler. Assigning one is how a test stands in for the screen.</summary>
    public static IScreenColourSampler Sampler
    {
        get => _sampler;
        set => _sampler = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Puts the platform sampler back, so a test cannot leak into the next one.</summary>
    public static void ResetSampler() => _sampler = new PlatformScreenColourSampler();

    /// <summary>Samples the screen, reporting whether it could rather than guessing a colour.</summary>
    public static bool TrySample(int x, int y, out ColorRgb colour)
    {
        if (_sampler.Sample(x, y) is { } found)
        {
            colour = found;
            return true;
        }

        colour = ColorRgb.Black;
        return false;
    }
}

/// <summary>
/// The platform sampler: a GDI pixel read on Windows, and an honest refusal everywhere else.
///
/// A full-screen topmost window is what receives the pick, and it paints nothing over the cursor - a hollow
/// ring rather than a filled one - or it would read its own overlay instead of what is behind it. Reading the
/// screen buffer also means a colour sitting in another application is picked correctly, which is the point.
/// </summary>
internal sealed class PlatformScreenColourSampler : IScreenColourSampler
{
    private const uint ClrInvalid = 0xFFFFFFFF;

    public bool IsSupported => OperatingSystem.IsWindows();

    public ColorRgb? Sample(int x, int y)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            uint pixel = GetPixel(screen, x, y);
            if (pixel == ClrInvalid)
            {
                return null;
            }

            // COLORREF is 0x00BBGGRR, the reverse of the way a colour is usually written down.
            return ColorRgb.FromBytes(
                (byte)(pixel & 0xFF),
                (byte)((pixel >> 8) & 0xFF),
                (byte)((pixel >> 16) & 0xFF));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);
}
