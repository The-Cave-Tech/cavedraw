using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VCCad.App.Ai;
using VCCad.App.Automation;
using VCCad.Core.Input;

namespace VCCad.App.Views;

/// <summary>
/// The diagnostics overlay: a semi-transparent panel that sits over the document
/// area of the editor window, showing the assistant conversation and every API
/// call the application has made, whoever made it.
///
/// It is deliberately an overlay inside the main window rather than a separate
/// top-level window: the person must be able to watch the model work *and* see the
/// document it is changing at the same time, with the artwork visible through the
/// panel.
///
/// Built in code rather than XAML because it is a diagnostic tool: it must keep
/// working (and keep compiling) independently of the editor's visual tree.
/// </summary>
public sealed class DiagnosticsOverlay : UserControl
{
    private readonly AutomationHost _host;
    private readonly ObservableCollection<ApiCallRecord> _calls = new();
    private readonly ObservableCollection<InteractionRecord> _diary = new();

    private readonly ChatTranscript _transcript = new();
    private readonly ListBox _callList = new();
    private readonly ListBox _historyList = new();
    private readonly TextBox _promptBox = new();
    private readonly TextBox _callDetail = new();
    private readonly TextBox _historyDetail = new();
    private readonly TextBox _historyFilter = new();
    private readonly TextBox _skillTitle = new();
    private readonly TextBlock _historySummary = new();
    private readonly CheckBox _withScreenshot = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _callSummary = new();
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };

    /// <summary>Default overlay opacity: the document stays readable underneath.</summary>
    private const double OverlayOpacity = 0.86;

    public DiagnosticsOverlay(AutomationHost host)
    {
        _host = host;

        // Wide and short, anchored to the bottom of the canvas area: it spans the
        // document width (clear of the left tool rail and the right docked panes)
        // so it reads as a console strip over the artwork rather than a pane.
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Bottom;
        Height = 300;
        Margin = new Thickness(52, 0, 284, 44);

        var tabs = new TabControl
        {
            Background = Brushes.Transparent,
            Items =
            {
                new TabItem { Header = "Assistant", Content = BuildChatTab() },
                new TabItem { Header = "History", Content = BuildHistoryTab() },
                new TabItem { Header = "Operations", Content = BuildOperationsTab() },
                new TabItem { Header = "API calls", Content = BuildCallsTab() },
                new TabItem { Header = "Endpoint", Content = BuildEndpointTab() },
            },
        };

        var body = new DockPanel { LastChildFill = true };
        Control titleBar = BuildTitleBar();
        DockPanel.SetDock(titleBar, Dock.Top);
        body.Children.Add(titleBar);
        body.Children.Add(tabs);

        // A framed, elevated, semi-transparent card: the canvas stays visible
        // through it but it clearly floats above the document.
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x14, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0x6E, 0x9E, 0xD8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 28,
                OffsetY = 8,
                Color = Color.FromArgb(0xB0, 0, 0, 0),
            }),
            ClipToBounds = true,
            Child = body,
        };

        _host.Agent.TranscriptChanged += (_, _) => Dispatcher.UIThread.Post(SyncChat);
        _host.Agent.Progress += (_, message) => Dispatcher.UIThread.Post(() => _status.Text = message);
        _host.BusyChanged += (_, _) => Dispatcher.UIThread.Post(UpdateBusyState);
        DiagnosticsLog.Recorded += (_, record) => Dispatcher.UIThread.Post(() => AddCall(record));

        SyncChat();
        foreach (ApiCallRecord record in DiagnosticsLog.Recent(300))
        {
            AddCall(record);
        }

        UpdateStatus();
    }

    /// <summary>
    /// The strip's title bar: what the panel is, plus a close button. Kept small so
    /// the overlay stays a short strip at the bottom of the canvas.
    /// </summary>
    private Control BuildTitleBar()
    {
        var title = new TextBlock
        {
            Text = "Diagnostics — assistant · operations · API calls",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hint = new TextBlock
        {
            Text = "F12 toggles",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA0)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var close = new Button
        {
            Content = "×",
            Padding = new Thickness(8, 0, 8, 0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
        };
        close.Click += (_, _) => IsVisible = false;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(hint, 1);
        Grid.SetColumn(close, 3);
        grid.Children.Add(title);
        grid.Children.Add(hint);
        grid.Children.Add(close);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x60, 0x2A, 0x3A, 0x52)),
            Padding = new Thickness(8, 2, 4, 2),
            Child = grid,
        };
    }

    /// <summary>Queues a prompt as if the user typed it (used by the CLI/automation).</summary>
    public void SubmitPrompt(string prompt, bool withScreenshot)
    {
        _promptBox.Text = prompt;
        _withScreenshot.IsChecked = withScreenshot;
        _ = SendAsync();
    }

    private Control BuildChatTab()
    {
        _promptBox.Watermark = "Tell the assistant what to do — it edits the document through the same API a person uses…";
        _promptBox.AcceptsReturn = true;
        _promptBox.Height = 64;
        _promptBox.TextWrapping = TextWrapping.Wrap;
        _promptBox.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x22, 0x22, 0x28));
        _promptBox.Foreground = Brushes.White;
        _promptBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && !e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
            {
                e.Handled = true;
                _ = SendAsync();
            }
        };

        _withScreenshot.Content = "Screenshot";
        _withScreenshot.Foreground = Brushes.White;
        _withScreenshot.IsChecked = true;

        var send = new Button { Content = "Send", Margin = new Thickness(6, 0, 0, 0) };
        send.Click += (_, _) => _ = SendAsync();

        // Cancelling must always be possible while the model is working: it aborts
        // the turn, leaves the transcript intact and re-enables the editor.
        _cancel.Margin = new Thickness(6, 0, 0, 0);
        _cancel.Click += (_, _) =>
        {
            _host.CancelCurrent();
            _status.Text = "cancelling…";
        };

        var clear = new Button { Content = "Clear", Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) =>
        {
            _host.Agent.Reset();
            SyncChat();
        };

        _status.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(6, 2, 6, 4);

        var composer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(6),
        };
        Grid.SetColumn(_promptBox, 0);
        Grid.SetColumn(_withScreenshot, 1);
        Grid.SetColumn(send, 2);
        Grid.SetColumn(_cancel, 3);
        Grid.SetColumn(clear, 4);
        composer.Children.Add(_promptBox);
        composer.Children.Add(_withScreenshot);
        composer.Children.Add(send);
        composer.Children.Add(_cancel);
        composer.Children.Add(clear);

        var header = new TextBlock
        {
            Text = $"{_host.Client.Model} · 127.0.0.1:{_host.Port} · F12 hides this panel",
            Margin = new Thickness(6, 4, 6, 2),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xAE, 0xBE)),
            TextWrapping = TextWrapping.Wrap,
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(_transcript, 1);
        Grid.SetRow(composer, 2);
        Grid.SetRow(_status, 3);
        root.Children.Add(header);
        root.Children.Add(_transcript);
        root.Children.Add(composer);
        root.Children.Add(_status);
        return root;
    }

    /// <summary>
    /// Lets the person run any operation the assistant could run: the other half of
    /// the "the model can do anything the user can, and the user can do anything the
    /// model can" rule.
    /// </summary>
    private Control BuildOperationsTab()
    {
        string[] names = EditorOperations.All.Select(o => o.Name).ToArray();

        var picker = new ComboBox
        {
            ItemsSource = names,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var summary = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB8, 0xC0)),
            TextWrapping = TextWrapping.Wrap,
        };

        var parameters = new TextBox
        {
            Text = "{}",
            AcceptsReturn = true,
            Height = 64,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x0E, 0x12)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 11,
        };

        var result = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x0E, 0x12)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 11,
        };

        var run = new Button { Content = "Run" };

        async Task RunAsync()
        {
            if (picker.SelectedItem is not string name)
            {
                return;
            }

            result.Text = "running…";
            try
            {
                JsonElement parsed = string.IsNullOrWhiteSpace(parameters.Text)
                    ? default
                    : JsonSerializer.Deserialize<JsonElement>(parameters.Text);

                // Off the UI thread, so a slow (vision) operation can be watched and
                // cancelled instead of freezing the window.
                await Task.Yield();
                object? value = await EditorOperations.InvokeAsync(
                    _host.Context, name, parsed, ApiCallSource.Ui, _host.CurrentToken());
                result.Text = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (OperationCanceledException)
            {
                result.Text = "cancelled";
            }
            catch (Exception ex)
            {
                result.Text = "ERROR: " + ex.Message;
            }
        }

        void ShowSelected()
        {
            if (picker.SelectedItem is string name && EditorOperations.TryGet(name, out EditorOperation op))
            {
                summary.Text = op.Summary + (op.Parameters.Length == 0 ? string.Empty : "\nparams: " + op.Parameters);
            }
        }

        picker.SelectionChanged += (_, _) => ShowSelected();
        run.Click += async (_, _) => await RunAsync();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void AddPreset(string label, string op, string json)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) =>
            {
                picker.SelectedItem = op;
                parameters.Text = json;
                await RunAsync();
            };
            actions.Children.Add(button);
        }

        AddPreset("Dump screen", "ui.dump", "{\"scope\":\"all\"}");
        AddPreset("Describe window", "ui.describe", "{\"target\":\"window\"}");
        AddPreset("Describe selection", "ui.describe", "{\"target\":\"selection\"}");
        actions.Children.Add(run);

        ShowSelected();

        var header = new TextBlock
        {
            Text = "Every operation the assistant can run is runnable here — the registry is shared.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)),
            TextWrapping = TextWrapping.Wrap,
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
        SetRow(header, 0);
        SetRow(picker, 1);
        SetRow(summary, 2);
        SetRow(parameters, 3);
        SetRow(result, 4);
        SetRow(actions, 5);
        foreach (Control child in new Control[] { header, picker, summary, parameters, result, actions })
        {
            child.Margin = new Thickness(6, 2, 6, 2);
            root.Children.Add(child);
        }

        return root;
    }

    private static void SetRow(Control control, int row) => Grid.SetRow(control, row);

    /// <summary>
    /// The diary: everything that has happened — the person's pointer and keyboard
    /// actions, every operation from any source, and every model request — newest
    /// last, with a search box over the whole history and a "learn as a skill"
    /// action so a finished task becomes reusable knowledge.
    /// </summary>
    private Control BuildHistoryTab()
    {
        _historyList.Background = Brushes.Transparent;
        _historyList.ItemTemplate = new FuncDataTemplate<InteractionRecord>((record, _) => new TextBlock
        {
            Text = record.Describe(),
            Foreground = record.Kind switch
            {
                InteractionKind.Skill => new SolidColorBrush(Color.FromRgb(0xC8, 0xE6, 0xC9)),
                InteractionKind.Llm => new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)),
                InteractionKind.Api => new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x8A)),
                InteractionKind.Ui => new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                _ => new SolidColorBrush(Color.FromRgb(0x9E, 0xAE, 0xBE)),
            },
            FontSize = 11,
        }, true);

        _historyList.SelectionChanged += (_, _) =>
        {
            if (_historyList.SelectedItem is InteractionRecord record)
            {
                _historyDetail.Text =
                    $"#{record.Sequence}  {record.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}\n" +
                    $"session {record.SessionId}\n" +
                    $"{record.Kind.ToString().ToLowerInvariant()} / {record.Category.ToString().ToLowerInvariant()}  " +
                    $"{record.Name}   success: {record.Success}   {record.DurationMs:F1} ms\n" +
                    $"target: {record.Target ?? "(none)"}\n" +
                    $"tags: {(record.Tags.Length == 0 ? "(none)" : string.Join(", ", record.Tags))}\n\n" +
                    $"{record.Details ?? "(no details)"}";
            }
        };

        _historyDetail.IsReadOnly = true;
        _historyDetail.AcceptsReturn = true;
        _historyDetail.TextWrapping = TextWrapping.Wrap;
        _historyDetail.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x0E, 0x12));
        _historyDetail.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        _historyDetail.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        _historyDetail.FontSize = 11;

        _historyFilter.Watermark = "Search the whole history… (blank shows the live feed)";
        _historyFilter.Margin = new Thickness(6, 2, 6, 2);
        _historyFilter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RefreshHistory();
            }
        };

        var search = new Button { Content = "Search", Margin = new Thickness(6, 2, 2, 2) };
        search.Click += (_, _) => RefreshHistory();

        var live = new Button { Content = "Live", Margin = new Thickness(2, 2, 6, 2) };
        live.Click += (_, _) =>
        {
            _historyFilter.Text = string.Empty;
            RefreshHistory();
        };

        _historySummary.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xAE, 0xBE));
        _historySummary.Margin = new Thickness(6, 2, 6, 4);
        _historySummary.TextWrapping = TextWrapping.Wrap;

        // Learning a skill is a first-class button here: the person watches a task
        // succeed, names it, and it becomes retrievable knowledge for later sessions.
        _skillTitle.Watermark = "Name this skill, e.g. \"US size 10 bodice block\"";
        _skillTitle.Margin = new Thickness(6, 2, 2, 2);
        var learn = new Button { Content = "Learn as skill", Margin = new Thickness(2, 2, 6, 2) };
        learn.Click += (_, _) =>
        {
            string title = _skillTitle.Text?.Trim() ?? string.Empty;
            if (title.Length == 0)
            {
                _historySummary.Text = "Give the skill a name first.";
                return;
            }

            string description = _historyList.SelectedItem is InteractionRecord selected
                ? $"Based on {selected.Name} ({selected.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm})."
                : string.Empty;

            try
            {
                _host.Context.History?.Learn(title, description);
                _skillTitle.Text = string.Empty;
                _historyFilter.Text = string.Empty;
                RefreshHistory();
                _historySummary.Text = $"Learned \"{title}\" — future sessions can retrieve it.";
            }
            catch (Exception ex)
            {
                _historySummary.Text = $"Could not learn the skill: {ex.Message}";
            }
        };

        // The other half of the parity rule: replaying a session is not an API-only trick. A person
        // watches a task succeed and exports it as a gesture here.
        var exportBatch = new Button { Content = "Export as batch", Margin = new Thickness(2, 2, 6, 2) };
        exportBatch.Click += (_, _) =>
        {
            if (_host.History is not { } log)
            {
                _historySummary.Text = "No diary in this host.";
                return;
            }

            try
            {
                InputBatch batch = DiaryBatchExport.From(log, log.SessionId);
                if (batch.Events.Count == 0)
                {
                    _historySummary.Text = "Nothing to export yet: no pointer or key events recorded.";
                    return;
                }

                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VCCad", "batches", $"session-{log.SessionId}.json");

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                batch.Save(path);

                _historySummary.Text =
                    $"Exported {batch.Events.Count} event(s) to {path} — replay it with input.batchFile.";
            }
            catch (Exception ex)
            {
                _historySummary.Text = $"Could not export the batch: {ex.Message}";
            }
        };

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,*,Auto,Auto") };
        Grid.SetColumn(_historyFilter, 0);
        Grid.SetColumn(search, 1);
        Grid.SetColumn(live, 2);
        Grid.SetColumn(_skillTitle, 3);
        Grid.SetColumn(learn, 4);
        Grid.SetColumn(exportBatch, 5);
        foreach (Control child in new Control[] { _historyFilter, search, live, _skillTitle, learn, exportBatch })
        {
            searchRow.Children.Add(child);
        }

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*") };
        Grid.SetColumn(_historyList, 0);
        Grid.SetColumn(_historyDetail, 1);
        split.Children.Add(_historyList);
        split.Children.Add(_historyDetail);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        SetRow(searchRow, 0);
        SetRow(split, 1);
        SetRow(_historySummary, 2);
        root.Children.Add(searchRow);
        root.Children.Add(split);
        root.Children.Add(_historySummary);

        // Live feed: every entry is appended as it happens, from any thread.
        if (_host.History is { } diary)
        {
            foreach (InteractionRecord record in diary.Tail(150))
            {
                _diary.Add(record);
            }

            diary.Recorded += (_, record) => Dispatcher.UIThread.Post(() =>
            {
                // While searching, keep the result list stable rather than mixing in
                // the live feed.
                if (string.IsNullOrWhiteSpace(_historyFilter.Text))
                {
                    _diary.Add(record);
                    while (_diary.Count > 400)
                    {
                        _diary.RemoveAt(0);
                    }

                    if (_diary.Count > 0)
                    {
                        _historyList.ScrollIntoView(_diary[^1]);
                    }
                }
            });
        }

        _historyList.ItemsSource = _diary;
        RefreshHistory();
        return root;
    }

    /// <summary>Fills the history list from the diary (search results, or the live feed).</summary>
    private void RefreshHistory()
    {
        InteractionLog? diary = _host.History;
        if (diary is null)
        {
            _historySummary.Text = "No diary in this host.";
            return;
        }

        string query = _historyFilter.Text?.Trim() ?? string.Empty;
        _diary.Clear();

        if (query.Length == 0)
        {
            foreach (InteractionRecord record in diary.Tail(150))
            {
                _diary.Add(record);
            }

            _historySummary.Text = $"Live feed — {diary.Tail(1).Count} shown of the current session. " +
                                   "Type a query to search every session that has ever been recorded.";
            return;
        }

        IReadOnlyList<InteractionRecord> matches = diary.Search(query, limit: 200);
        foreach (InteractionRecord record in matches)
        {
            _diary.Add(record);
        }

        _historySummary.Text = matches.Count == 0
            ? $"Nothing in the diary matches \"{query}\"."
            : $"{matches.Count} match(es) for \"{query}\" across all recorded sessions.";
    }

    private Control BuildCallsTab()
    {
        _callList.Background = Brushes.Transparent;
        _callList.ItemTemplate = new FuncDataTemplate<ApiCallRecord>((record, _) => new TextBlock
        {
            Text = record.Describe(),
            Foreground = record.Success
                ? new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4))
                : new SolidColorBrush(Color.FromRgb(0xF4, 0x8F, 0x8F)),
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 11,
        }, true);
        _callList.ItemsSource = _calls;
        _callList.SelectionChanged += (_, _) =>
        {
            if (_callList.SelectedItem is ApiCallRecord record)
            {
                _callDetail.Text =
                    $"{record.Timestamp:HH:mm:ss.fff}  {record.Source.ToString().ToLowerInvariant()}  {record.Operation}\n" +
                    $"success: {record.Success}   duration: {record.DurationMs:F1} ms\n" +
                    (record.Error is null ? string.Empty : $"error: {record.Error}\n") +
                    $"\nparameters:\n{record.Parameters ?? "(none)"}\n\nresult:\n{record.Result ?? "(none)"}";
            }
        };

        _callDetail.IsReadOnly = true;
        _callDetail.AcceptsReturn = true;
        _callDetail.TextWrapping = TextWrapping.Wrap;
        _callDetail.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x0E, 0x12));
        _callDetail.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        _callDetail.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        _callDetail.FontSize = 11;

        _callSummary.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0xAE, 0xBE));
        _callSummary.Margin = new Thickness(6, 2, 6, 4);
        _callSummary.TextWrapping = TextWrapping.Wrap;

        var clear = new Button { Content = "Clear log", Margin = new Thickness(6, 2, 6, 2) };
        clear.Click += (_, _) =>
        {
            DiagnosticsLog.Clear();
            _calls.Clear();
            UpdateStatus();
        };

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetColumn(_callList, 0);
        Grid.SetColumn(_callDetail, 1);
        split.Children.Add(_callList);
        split.Children.Add(_callDetail);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(clear, 0);
        Grid.SetRow(split, 1);
        Grid.SetRow(_callSummary, 2);
        root.Children.Add(clear);
        root.Children.Add(split);
        root.Children.Add(_callSummary);
        return root;
    }

    private Control BuildEndpointTab()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        panel.Children.Add(new TextBlock
        {
            Text = "Automation endpoint",
            FontSize = 14,
            Foreground = Brushes.White,
        });
        panel.Children.Add(new TextBlock
        {
            Text =
                $"Base URL:  http://127.0.0.1:{_host.Port}/api/v1/\n" +
                "  GET  health\n" +
                "  GET  operations            (JSON catalog)\n" +
                "  GET  operations.txt        (text catalog)\n" +
                "  GET  diagnostics?since=N   (audit trail)\n" +
                "  GET  screenshot            (PNG base64)\n" +
                "  POST invoke   {\"op\":\"...\",\"params\":{...}}\n" +
                "  POST chat     {\"prompt\":\"...\",\"withScreenshot\":true}\n" +
                $"\nBound to loopback only. Operations exposed: {EditorOperations.All.Count}.\n" +
                "Prefer these calls over synthetic clicks.",
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 11,
        });
        panel.Children.Add(new TextBlock
        {
            Text =
                $"Model: {_host.Client.Model}\nEndpoint: {_host.Client.BaseUrl}\n" +
                "Override with --model / --llm-url / --api-key, or VCCAD_LLM_MODEL, " +
                "VCCAD_LLM_BASE, VCCAD_LLM_KEY.",
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 11,
        });
        return new ScrollViewer { Content = panel };
    }

    private async Task SendAsync()
    {
        string prompt = _promptBox.Text?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            return;
        }

        _promptBox.Text = string.Empty;
        _status.Text = "starting…";
        try
        {
            AgentTurnResult result = await _host.ChatAsync(prompt, _withScreenshot.IsChecked == true);
            _status.Text = $"done in {result.Steps} step(s), {result.Actions.Count} action(s).";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "cancelled — the interface is usable again.";
        }
        catch (Exception ex)
        {
            _status.Text = "error: " + ex.Message;
        }
    }

    /// <summary>Keeps the Cancel affordance in step with the host's busy state.</summary>
    private void UpdateBusyState()
    {
        bool busy = _host.IsBusy;
        _cancel.IsEnabled = busy;
        if (busy)
        {
            _status.Text = "working… (Cancel stops it and re-enables the editor)";
        }
    }

    private void SyncChat() => _transcript.Show(_host.Agent.Transcript);

    private void AddCall(ApiCallRecord record)
    {
        _calls.Add(record);
        if (_calls.Count > DiagnosticsLog.Capacity)
        {
            _calls.RemoveAt(0);
        }

        _callList.ScrollIntoView(record);
        UpdateStatus();
    }

    private void UpdateStatus()
        => _callSummary.Text = $"{DiagnosticsLog.Sequence} operations recorded " +
                               $"({_calls.Count} shown) — UI, HTTP and assistant calls all appear here.";
}
