using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Ai;

namespace VCCad.App.Views;

/// <summary>
/// The assistant conversation as a scrolling list: each entry's role, its text, and
/// any screenshot the model was shown. It follows the newest entry at all times.
///
/// That last part is the whole point, and it is why this is not a plain list. The
/// view is handed the entire transcript on every step, and the obvious way to apply
/// it - clear the collection and refill it - is what made the panel stay where it
/// was: replacing the items destroys the row containers, so a scroll request issued
/// in the same pass has nothing to scroll to and is dropped. This reconciles instead,
/// leaving an unchanged prefix in place and appending only what is new, then bringing
/// the newest row into view after the layout that realises it.
/// </summary>
public sealed class ChatTranscript : UserControl
{
    private readonly ObservableCollection<ChatEntry> _entries = new();
    private readonly ListBox _list = new();

    public ChatTranscript()
    {
        _list.Background = Brushes.Transparent;
        _list.ItemTemplate = new FuncDataTemplate<ChatEntry>((entry, _) => BuildRow(entry), true);
        _list.ItemsSource = _entries;
        _list.SelectionMode = SelectionMode.Single;
        Content = _list;
    }

    /// <summary>How many entries the view is showing.</summary>
    public int EntryCount => _entries.Count;

    /// <summary>
    /// True when the newest entry is visible at the bottom of the viewport. This is
    /// the property the "always shows the latest text" promise is made of, so it is
    /// what the tests assert against.
    /// </summary>
    public bool IsShowingLatest
    {
        get
        {
            ScrollViewer? scroller = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroller is null)
            {
                // Not laid out yet: there is no scrolling to get wrong.
                return true;
            }

            return scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 1.0;
        }
    }

    /// <summary>
    /// Reconciles the view with the conversation, oldest first, and brings the newest
    /// entry into view. Safe to call on every transcript change.
    /// </summary>
    public void Show(IReadOnlyList<ChatEntry> transcript)
    {
        // The transcript only ever grows (a reset clears it), so an unchanged prefix
        // means the rows that are already there can stay. Rebuilding them would throw
        // away the containers the follow-up scroll needs.
        if (!KeepsExistingPrefix(transcript))
        {
            _entries.Clear();
        }

        for (int i = _entries.Count; i < transcript.Count; i++)
        {
            _entries.Add(transcript[i]);
        }

        FollowLatest();
    }

    private bool KeepsExistingPrefix(IReadOnlyList<ChatEntry> transcript)
    {
        if (_entries.Count > transcript.Count)
        {
            return false;
        }

        for (int i = 0; i < _entries.Count; i++)
        {
            if (!ReferenceEquals(_entries[i], transcript[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void FollowLatest()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        ChatEntry newest = _entries[^1];

        // Posted, not called: the new row has not been realised when Show returns,
        // and ScrollIntoView against a container that does not exist yet is a no-op.
        Dispatcher.UIThread.Post(
            () => _list.ScrollIntoView(newest),
            DispatcherPriority.Background);
    }

    /// <summary>One row: the role, the text, and the screenshot when there is one.</summary>
    private static Control BuildRow(ChatEntry entry)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(2) };
        panel.Children.Add(new TextBlock
        {
            Text = entry.Role.ToUpperInvariant(),
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA0)),
        });
        panel.Children.Add(new TextBlock
        {
            Text = entry.Text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = entry.Role switch
            {
                "user" => new SolidColorBrush(Color.FromRgb(0xBB, 0xDE, 0xFB)),
                "assistant" => Brushes.White,
                "action" => new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)),
                _ => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
            },
        });

        if (entry.ImagePng is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(entry.ImagePng);
                panel.Children.Add(new Image
                {
                    Source = new Bitmap(stream),
                    Height = 140,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                });
            }
            catch (Exception)
            {
                // A corrupt thumbnail must not break the transcript view.
            }
        }

        return panel;
    }
}
