using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VCCad.App.Views;

namespace VCCad.App;

/// <summary>
/// Avalonia application entry point. The browser host starts this through
/// <c>StartBrowserAppAsync</c>; the same app can later run as a desktop window
/// (the <see cref="IClassicDesktopStyleApplicationLifetime"/> branch) with no
/// changes to the editor view.
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new Window
                {
                    Title = "VCCad",
                    Width = 1280,
                    Height = 820,
                    Content = new EditorView(),
                };
                break;

            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new EditorView();
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
