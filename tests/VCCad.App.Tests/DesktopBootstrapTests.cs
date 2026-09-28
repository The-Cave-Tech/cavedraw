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
        Assembly host = Assembly.Load("VCCad.App.Desktop");

        Assert.Equal("VCCad.App.Desktop", host.GetName().Name);

        MethodInfo? entryPoint = host.EntryPoint;
        Assert.NotNull(entryPoint);
        Assert.True(entryPoint!.IsStatic);
        Assert.Equal("Main", entryPoint.Name);
        Assert.Equal("Program", entryPoint.DeclaringType?.Name);
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
