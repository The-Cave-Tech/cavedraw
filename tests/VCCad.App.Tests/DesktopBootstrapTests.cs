using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using VCCad.App.Views;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Smoke tests for the desktop (Windows/Linux) host in
/// <c>VCCad.App.Desktop</c>: the host assembly must still expose the classic
/// desktop entry point, and the editor shell must construct its main window
/// when driven by a classic desktop lifetime. The window is built on the
/// Avalonia headless platform (see <see cref="TestAppBuilder"/>), so the tests
/// need no display server.
/// </summary>
public class DesktopBootstrapTests
{
    /// <summary>
    /// The desktop host is a separate executable, so assert it is present in
    /// the test output (i.e. still wired into the solution) and that it still
    /// exposes a static <c>Program.Main</c> — the entry point
    /// <c>StartWithClassicDesktopLifetime</c> hangs off.
    /// </summary>
    [Fact]
    public void DesktopHostExposesClassicDesktopEntryPoint()
    {
        Assembly host = LoadDeployedDesktopHost();

        Assert.Equal("VCCad.App.Desktop", host.GetName().Name);

        MethodInfo? entryPoint = host.EntryPoint;
        Assert.NotNull(entryPoint);
        Assert.True(entryPoint!.IsStatic);
        Assert.Equal("Main", entryPoint.Name);
        Assert.Equal("Program", entryPoint.DeclaringType?.Name);
    }

    /// <summary>
    /// Loads <c>VCCad.App.Desktop</c> from the test output, waiting out a deployment that is
    /// being rewritten.
    ///
    /// The host is put there by MSBuild's <c>Copy</c> task, which uses Win32 <c>CopyFile</c> -
    /// and that <b>removes the destination before recreating it</b>. A second build sharing this
    /// checkout (another agent, a publish beside a test run, a <c>dotnet build</c> over a
    /// <c>dotnet test</c>) therefore leaves <c>VCCad.App.Desktop.dll</c> absent for a moment, and
    /// a name-based load that lands in that moment reports <see cref="FileNotFoundException"/>
    /// even though the host is deployed and correct.
    ///
    /// Measured for issue #176: this test failed in 0 of 105 runs on an idle tree, and failed with
    /// exactly that exception whenever the deployed file was rewritten under it - 14 of 120 runs
    /// with a copy loop over the file, 75 of 120 when the loop ran flat out. Waiting for the copy
    /// to finish is what makes the assertions above statements about the host rather than about a
    /// copy caught in flight.
    ///
    /// The image is read here and loaded from those bytes rather than through
    /// <c>Assembly.Load("VCCad.App.Desktop")</c>, and that is not incidental: the binder caches
    /// the miss, so a name-based retry keeps reporting <see cref="FileNotFoundException"/> for the
    /// rest of the process even after the file is whole again. Measured - a writer that stopped
    /// after three seconds still failed the name-based retry at five. Loading the image that was
    /// just read sidesteps the binder's verdict entirely.
    ///
    /// This is deliberately *not* a retry of the test and it cannot invent a host that is not
    /// there: nothing below is skipped or weakened, a genuinely absent or unloadable host
    /// exhausts the budget, and the failure that comes out names the state that was observed.
    /// </summary>
    private static Assembly LoadDeployedDesktopHost()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "VCCad.App.Desktop.dll");

        // Generous enough that only a defect can exhaust it, short enough not to stall a run.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        Exception? failure = null;

        while (true)
        {
            try
            {
                return Assembly.Load(File.ReadAllBytes(path));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException)
            {
                // Absent (FileNotFoundException), or read only in part because a copy was still
                // in flight (BadImageFormatException, or a short image).
                failure = ex;
                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        $"VCCad.App.Desktop was still not loadable after waiting for its deployment to settle: " +
                        $"{path} {(File.Exists(path) ? $"is {new FileInfo(path).Length} bytes" : "does not exist")}",
                        failure);
                }

                Thread.Sleep(25);
            }
        }
    }

    /// <summary>
    /// Boots the editor shell the way the desktop host does — a classic desktop
    /// lifetime driving <see cref="VCCad.App.App"/> — and asserts that framework
    /// initialization completes far enough to build the editor window with the
    /// shell mounted. A crash before window construction fails the test.
    ///
    /// The window's content is a <see cref="Panel"/> holding the editor, because the
    /// diagnostics overlay is stacked on top of it (see
    /// <c>DiagnosticsOverlay</c>); the size is applied at open time by the
    /// right-half docking logic, so it is only required to be sane here.
    /// </summary>
    [AvaloniaFact]
    public void ClassicDesktopLifetimeConstructsEditorMainWindow()
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime();

        var app = new VCCad.App.App();
        // Mirrors AppBuilder.SetupUnsafe: the lifetime must be assigned before
        // Application.RegisterServices flips its "setup completed" latch.
        app.ApplicationLifetime = lifetime;
        app.Initialize();
        app.OnFrameworkInitializationCompleted();

        Window window = Assert.IsType<Window>(lifetime.MainWindow);
        Assert.Equal("VCCad", window.Title);
        Assert.True(window.Width > 0 && window.Height > 0, "the window must have a usable size");

        var root = Assert.IsType<Panel>(window.Content);
        Assert.NotEmpty(root.Children);
        Assert.IsType<EditorView>(root.Children[0]);
    }
}
