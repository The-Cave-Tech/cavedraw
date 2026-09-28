using Avalonia;
using VCCad.App;
using VCCad.App.Automation;

namespace VCCad.App.Desktop;

/// <summary>
/// Desktop host for the VCCad editor shell (Windows and Linux). The shell
/// itself lives in <c>VCCad.App</c>; this project only selects the platform
/// subsystems and the classic desktop lifetime, which is the branch
/// <see cref="VCCad.App.App.OnFrameworkInitializationCompleted"/> turns into the
/// editor window. It mirrors <c>VCCad.App.Browser</c>, which hosts the same
/// shell through the single-view (browser) lifetime.
///
/// The host also parses the automation/assistant options, so the editor can be
/// started with work already queued:
///
///     VCCad.App.Desktop --chat "draw a red circle in the middle" --diagnostics
/// </summary>
internal sealed class Program
{
    /// <summary>
    /// Process entry point. <c>StartWithClassicDesktopLifetime</c> owns the
    /// dispatcher loop and must run on the process's main thread.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        DesktopStartupOptions options = DesktopStartupOptions.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(DesktopStartupOptions.Usage);
            return;
        }

        DesktopStartup.Options = options;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Builds the Avalonia application: platform detection picks Win32, X11 or
    /// macOS at runtime, the Inter font matches the browser host's typography,
    /// and trace logging feeds the usual logging backends.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
