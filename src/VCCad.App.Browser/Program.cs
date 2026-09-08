using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using VCCad.App;
using VCCad.App.ViewModels;

internal sealed partial class Program
{
    /// <summary>
    /// Browser entry point: builds the Avalonia app and mounts it into the DOM
    /// element named "out" (see wwwroot/index.html). The compiled .NET/WASM
    /// payload is bootstrapped by wwwroot/main.js via the .NET runtime.
    /// The first argument is the page URL; it seeds the automation host base
    /// so File → Save/Open reach the same origin that served the app.
    /// </summary>
    private static Task Main(string[] args)
    {
        if (args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out Uri? pageUrl))
        {
            EditorViewModel.ServerBase = pageUrl.GetLeftPart(UriPartial.Authority);
        }

        return BuildAvaloniaApp()
            .WithInterFont()
            .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();
}
