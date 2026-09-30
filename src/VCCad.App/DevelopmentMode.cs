using System.Diagnostics;

namespace VCCad.App;

/// <summary>
/// Whether this is a development build, and what that changes.
///
/// It exists for one decision so far - see <see cref="ShouldOfferRecovery"/> - and it is written down once, in
/// one place, so a second caller cannot answer the same question differently.
///
/// The test is deliberately something a developer already has rather than a flag every command has to
/// remember to pass. A build running out of its own output directory, or one with a debugger attached, is a
/// development build; an installed or published bundle is not. That is what separates `dotnet run` from a
/// bundle in `artifacts/desktop`, and it needs no cooperation from whoever typed the command.
/// </summary>
public static class DevelopmentMode
{
    /// <summary>True when this process is a development build.</summary>
    public static bool IsOn => Debugger.IsAttached || IsBuildOutput(AppContext.BaseDirectory);

    /// <summary>
    /// Whether a directory is a build output directory.
    ///
    /// A "bin" segment is the marker. Published bundles are laid out as `artifacts/desktop/&lt;rid&gt;` or
    /// wherever the person installed them, and neither contains one.
    /// </summary>
    public static bool IsBuildOutput(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string normalised = directory.Replace('\\', '/').TrimEnd('/');
        return normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               || normalised.EndsWith("/bin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether to offer to recover a session left behind by a run that ended badly.
    ///
    /// **Not in development.** A developer's documents are scratch files expected to be thrown away, and the
    /// prompt costs more than it saves: it is the first thing on screen, it decides which documents are open
    /// underneath whatever is being tested, and its overlay covers the whole editing area and swallows pointer
    /// events - so a driver that does not dismiss it has every gesture silently do nothing.
    ///
    /// A released build still recovers, because that is the case the feature is for. `--no-recover` still
    /// wins everywhere, and `--recover` is the way to ask for it back in development.
    /// </summary>
    public static bool ShouldOfferRecovery(bool noRecovery, bool forceRecovery, bool development)
        => forceRecovery || (!noRecovery && !development);
}
