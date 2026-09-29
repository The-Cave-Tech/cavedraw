using System.Runtime.InteropServices;
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
///
/// It is responsible for the promises on the command line. This assembly is a
/// WinExe (<c>OutputType=WinExe</c>, no console attached) and the endpoint is
/// created later, inside a window event, so the port and the instance name are
/// secured <em>here</em>, before Avalonia starts: a failure then reaches the
/// launcher as a message and a non-zero exit code instead of a window that
/// quietly answers somewhere else.
/// </summary>
internal sealed class Program
{
    /// <summary>
    /// Process entry point. <c>StartWithClassicDesktopLifetime</c> owns the
    /// dispatcher loop and must run on the process's main thread.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        EnsureConsole();

        DesktopStartupOptions options = DesktopStartupOptions.Parse(args);

        if (options.ArgumentError is not null)
        {
            Fail(options.ArgumentError);
            Console.Error.WriteLine();
            Console.Error.WriteLine(DesktopStartupOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(DesktopStartupOptions.Usage);
            return 0;
        }

        // Secures --port N and --name before the UI exists. A promise that cannot
        // be kept stops the launch here, where a caller is still watching.
        string? problem = AutomationHost.PrepareAutomationPort(options);
        if (problem is not null)
        {
            Fail(problem);
            return 2;
        }

        DesktopStartup.Options = options;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Reports a launch failure on every channel available: stderr, stdout, and a
    /// non-zero exit code. A WinExe may have no console at all, so the exit code is
    /// the one signal that always reaches the launcher.
    /// </summary>
    private static void Fail(string message)
    {
        Console.Error.WriteLine($"[vccad] {message}");
        Console.Error.Flush();
        Console.Out.WriteLine($"[vccad] {message}");
        Console.Out.Flush();
    }

    /// <summary>
    /// Attaches to the launching terminal when there is one. This assembly is a
    /// WinExe, so a message written normally goes nowhere for a person who started
    /// the app from a shell; attaching to the parent console makes stdout and
    /// stderr land in that shell. When the streams were redirected there is nothing
    /// to attach and the redirected handles are already correct.
    /// </summary>
    private static void EnsureConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (AttachConsole(AttachParentProcess))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
        }
        catch (Exception)
        {
            // No parent console, or no permission to attach: the exit code remains.
        }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

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
