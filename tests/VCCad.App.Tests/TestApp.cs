using Avalonia;
using Avalonia.Headless;
[assembly: AvaloniaTestApplication(typeof(VCCad.App.Tests.TestAppBuilder))]
namespace VCCad.App.Tests;
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Avalonia.Application>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia();
}
