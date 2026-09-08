using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using VCCad.App;

internal sealed partial class Program
{
    /// <summary>
    /// Browser entry point: builds the Avalonia app and mounts it into the DOM
    /// element named "out" (see wwwroot/index.html). The compiled .NET/WASM
    /// payload is bootstrapped by wwwroot/main.js via the .NET runtime.
    /// </summary>
    private static Task Main(string[] args) => BuildAvaloniaApp()
        .WithInterFont()
        .StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();
}
