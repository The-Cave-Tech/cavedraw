using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using VCCad.App.Automation;
using VCCad.App.Fonts;
using VCCad.App.Views;
using VCCad.Core.Model;

namespace VCCad.App;

/// <summary>
/// Avalonia application entry point. The browser host starts this through
/// <c>StartBrowserAppAsync</c>; the desktop host runs the same shell in a classic
/// desktop window (the <see cref="IClassicDesktopStyleApplicationLifetime"/>
/// branch).
///
/// On the desktop the application also brings up the automation services: a
/// loopback HTTP endpoint that exposes every editor operation, and the in-app
/// assistant that drives those same operations for the model. The diagnostics
/// overlay shows both, semi-transparently, on top of the document.
/// </summary>
public partial class App : Application
{
    private DiagnosticsOverlay? _diagnostics;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
            {
                var view = new EditorView();

                // The editor fills the window; the diagnostics overlay is stacked on
                // top of it so the document stays visible through the panel.
                var root = new Panel();
                root.Children.Add(view);

                var window = new Window
                {
                    Title = "VCCad",
                    Width = 960,
                    Height = 900,
                    Content = root,
                };

                desktop.MainWindow = window;
                window.Opened += (_, _) => AttachAutomation(view, root, window, desktop);
                break;
            }

            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new EditorView();
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Brings up the automation endpoint and assistant against the window's live
    /// document, adds the diagnostics overlay and its shortcut, and honours
    /// startup options.
    /// </summary>
    private void AttachAutomation(
        EditorView view, Panel root, Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        DesktopStartupOptions options = DesktopStartup.Options;

        // Quitting asks first when anything has unsaved changes. Both routes go through
        // this: the menu and app.exit call it directly, and closing the window is
        // intercepted below - stopping at only one of them would let the other one lose
        // the work without a word.
        bool exitConfirmed = false;
        void RequestExit()
        {
            if (exitConfirmed)
            {
                desktop.Shutdown();
                return;
            }

            view.PromptBeforeExit(() =>
            {
                exitConfirmed = true;
                SessionJournal.Clear();
                desktop.Shutdown();
            });
        }


        if (!options.NoDock)
        {
            DockRightHalf(window);
        }

        // Supply the standard PDF fonts (Helvetica, Times, Courier, Symbol, Dingbats) from
        // this machine: the URW Core 35 faces if present, else a metric-compatible clone.
        StandardFontResolver.RegisterAvailable();

            // One measurement source for the whole program: the model's bounds and the
            // canvas's caret and edit box both ask the text shaper through this.
            VCCad.Core.Text.TextMeasurement.Current = new AvaloniaTextMetrics();

        Controls.CanvasWorkspace workspace = view.WorkspaceControl;
        Views.PageRenderer.Workspace = workspace;

        // The diary: everything that happens in the application, on disk, searchable.
        var diary = new InteractionLog(options.HistoryDirectory ?? InteractionLog.DefaultDirectory());
        diary.StartSession(string.Join(' ', options.OriginalArguments ?? Array.Empty<string>()));
        UiEventRecorder? recorder = null;

        AutomationHost host = AutomationHost.Create(
            view.ViewModel,
            () => ScreenCapture.CaptureWindow(window),
            () => window,
            options.Llm,
            options.Port,
            startServer: !options.NoServer,
            uiTreeDump: maxNodes => VisualTreeDump.Ui(window, maxNodes),
            uiRoot: () => window,
            viewport: new ViewportActions(
                Fit: workspace.ZoomToFit,
                ActualSize: workspace.ZoomToActualSize,
                Zoom: workspace.ZoomTo,
                GetZoom: () => workspace.Zoom,
                IsAutoFit: () => workspace.IsAutoFit,
                ZoomIn: workspace.ZoomIn,
                ZoomOut: workspace.ZoomOut,
                GetClipToArtboard: () => workspace.ClipToArtboard,
                SetClipToArtboard: enabled => workspace.ClipToArtboard = enabled,
                CenterOn: (x, y) => workspace.CenterOn(new VCCad.Geometry.Point2D(x, y)),
                GetViewCenter: () => (workspace.ViewCenter.X, workspace.ViewCenter.Y)),
            host: new HostActions(
                Panes: () => view.Panes.Select(p => new PaneInfo(p.Id, p.Title, p.IsOpen)).ToArray(),
                SetPaneOpen: view.SetPaneOpen,
                PanelSizes: () => view.PanelSizes
                    .Select(s => new PaneSize(s.Id, s.Title, s.Stretchable, s.Height, s.Weight))
                    .ToArray(),
                SetPaneStretchable: view.SetPaneStretchable,
                SetPaneSize: view.SetPaneSize,
                Exit: RequestExit),
            history: diary);

        // Record what the person does, including hover, drag and drop and keystrokes.
        if (!options.NoRecording)
        {
            recorder = UiEventRecorder.Attach(window, diary);
        }

        _diagnostics = new DiagnosticsOverlay(host) { IsVisible = false };
        root.Children.Add(_diagnostics);

        // The busy badge goes into the root *after* the overlay, so it stays visible
        // over it: the person watching a long turn may well have the panel open, and
        // an indicator that the panel can cover is no indicator at all.
        var assistantBusy = new Controls.AssistantBusyIndicator
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Thickness(14, 0, 0, 34),
        };
        root.Children.Add(assistantBusy);

        // Reserve the overlay's height when fitting, so a fitted artboard is never
        // hidden behind the panel; `--diagnostics` therefore opens already fitted.
        void SyncOverlayInset()
        {
            workspace.ViewportInsetBottom = _diagnostics.IsVisible ? _diagnostics.Bounds.Height + 44 : 0;
            if (workspace.IsAutoFit)
            {
                workspace.ZoomToFit();
            }
        }

        _diagnostics.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SyncOverlayInset();
                    view.RefreshDiagnosticsMenu();
                });
            }
        };

        // Windows → Diagnostics overlay toggles the panel like any other pane.
        view.AttachDiagnosticsToggle(() => _diagnostics.IsVisible, () =>
        {
            _diagnostics.IsVisible = !_diagnostics.IsVisible;
        });

        // Opening or importing a document must fit it: a newly imported page can be
        // any size, and keeping the previous zoom showed it at the wrong scale.
        void FitForNewDocument()
        {
            if (workspace.IsAutoFit)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => workspace.ZoomToFit(),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }

        view.ViewModel.Sessions.CollectionChanged += (_, _) => FitForNewDocument();
        view.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.EditorViewModel.ActiveSession))
            {
                FitForNewDocument();
            }
        };

        // While the assistant is working the editor is locked: the person must not
        // be able to fight the model for the document. The diagnostics overlay stays
        // enabled, so the transcript keeps updating and Cancel is always reachable.
        // The busy badge in the corner of the view is the other half of that: it says
        // the assistant has control without the panel having to be open or glanced at.
        host.BusyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            view.IsEnabled = !host.IsBusy;
            assistantBusy.SetBusy(host.IsBusy);
        });

        // F12 toggles the overlay (and F9, which some keyboards send for this key).
        window.KeyDown += (_, e) =>
        {
            if (e.Key is Key.F12 or Key.F9)
            {
                e.Handled = true;
                ToggleDiagnostics();
            }
        };

        // Closing the window (the X, Alt+F4) is the other way out, so it must ask too.
        window.Closing += (_, e) =>
        {
            if (exitConfirmed || !view.HasUnsavedChanges)
            {
                return;
            }

            e.Cancel = true;
            RequestExit();
        };

        desktop.ShutdownRequested += (_, _) =>
        {
            // A clean exit means there is nothing to recover next time.
            SessionJournal.Clear();
            host.Server?.Dispose();
        };

        // If the last run ended badly, the journal is still there: offer the work back,
        // with the command queue that produced it.
        //
        // Skipped when a driver asked for it: the prompt covers the whole editing area and eats
        // pointer events, so a headless run that does not dismiss it never reaches the canvas.
        if (SessionJournal.HasRecoverableSession && !options.NoRecovery)
        {
            CadDocument? recovered = SessionJournal.TryRecover();
            if (recovered is not null)
            {
                IReadOnlyList<string> queue = SessionJournal.ReadQueue();
                view.OfferRecovery(recovered, queue.Count);
            }
        }

        if (options.ShowDiagnostics)
        {
            _diagnostics.IsVisible = true;
            // The inset depends on the panel's measured height, which is only known
            // after it is laid out; refit once that has happened.
            Avalonia.Threading.Dispatcher.UIThread.Post(SyncOverlayInset,
                Avalonia.Threading.DispatcherPriority.Background);
        }

        if (!string.IsNullOrWhiteSpace(options.ChatPrompt))
        {
            _diagnostics.IsVisible = true;
            _diagnostics.SubmitPrompt(options.ChatPrompt!, options.ChatWithScreenshot);
        }
    }

    private void ToggleDiagnostics()
    {
        if (_diagnostics is not null)
        {
            _diagnostics.IsVisible = !_diagnostics.IsVisible;
        }
    }

    /// <summary>
    /// Docks the window to the right half of the primary screen's working area.
    ///
    /// Development happens with the agent harness occupying the left half, so the
    /// editor opens beside it rather than on top of it. Sizes are device-independent
    /// pixels, hence the scaling division; the position is in screen pixels.
    /// </summary>
    private static void DockRightHalf(Window window)
    {
        Screen? screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        PixelRect area = screen.WorkingArea;
        double scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = Math.Max(480, area.Width / scaling / 2.0);
        window.Height = Math.Max(400, area.Height / scaling);
        window.Position = new PixelPoint(area.X + (int)(area.Width / 2.0), area.Y);
    }
}
