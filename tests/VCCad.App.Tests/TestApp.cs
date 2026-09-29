using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
[assembly: AvaloniaTestApplication(typeof(VCCad.App.Tests.TestAppBuilder))]
namespace VCCad.App.Tests;
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Avalonia.Application>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .AfterSetup(builder => builder.Instance!.Styles.Add(new FluentTheme()));
}
