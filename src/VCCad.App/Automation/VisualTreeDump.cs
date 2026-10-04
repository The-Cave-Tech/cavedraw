using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.VisualTree;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Automation;

/// <summary>
/// Serialises what the application is showing into text an LLM can read.
///
/// A development tool driving VCCad often has no vision: it cannot see a
/// screenshot. This dump is the text substitute — the whole Avalonia visual tree
/// with each control's type, name, displayed text, geometry, visibility and
/// state, plus a hierarchical view of the document itself. The point is that a
/// model (or a person reading a log) can tell exactly what is on screen and what
/// the user is looking at, without pixels.
/// </summary>
public static class VisualTreeDump
{
    /// <summary>Default cap on dumped visual nodes; prevents runaway output.</summary>
    public const int DefaultMaxNodes = 20000;

    /// <summary>
    /// Dumps the window's visual tree as indented text. Must be called on the UI
    /// thread.
    /// </summary>
    public static string Ui(Window? window, int maxNodes = DefaultMaxNodes)
    {
        if (window is null)
        {
            return "(no window)";
        }

        var sb = new StringBuilder();
        sb.AppendLine("WINDOW DUMP (Avalonia visual tree)");
        Screen? screen = window.Screens.Primary;
        sb.AppendLine($"window: {window.Title}  size: {window.ClientSize.Width:F0}x{window.ClientSize.Height:F0}" +
                      $"  position: {window.Position.X},{window.Position.Y}" +
                      (screen is null
                          ? string.Empty
                          : $"  primary screen: {screen.WorkingArea.Width}x{screen.WorkingArea.Height}" +
                            $" @{screen.WorkingArea.X},{screen.WorkingArea.Y} scaling {screen.Scaling:F2}") +
                      $"  state: {window.WindowState}  focused: {window.IsFocused}");
        sb.AppendLine();

        int count = 0;
        bool truncated = false;
        Walk(window, 0, maxNodes, ref count, ref truncated, sb);
        if (truncated)
        {
            sb.AppendLine($"(truncated at {maxNodes} nodes)");
        }

        sb.AppendLine();
        sb.AppendLine($"nodes: {count}");
        return sb.ToString();
    }

    private static void Walk(Visual visual, int depth, int maxNodes, ref int count, ref bool truncated, StringBuilder sb)
    {
        if (truncated)
        {
            return;
        }

        if (count >= maxNodes)
        {
            truncated = true;
            return;
        }

        count++;
        sb.Append(new string(' ', Math.Min(depth, 40) * 2));
        sb.Append(DescribeControl(visual));

        // Focus marker: tells a driver where keyboard input would go.
        if (visual is Control { IsFocused: true })
        {
            sb.Append("  <focus>");
        }

        sb.AppendLine();

        foreach (Visual child in visual.GetVisualChildren())
        {
            Walk(child, depth + 1, maxNodes, ref count, ref truncated, sb);
        }
    }

    /// <summary>One line describing a visual: type, name, text and geometry.</summary>
    private static string DescribeControl(Visual visual)
    {
        var sb = new StringBuilder();
        sb.Append(visual.GetType().Name);

        if (visual is Control control)
        {
            if (!string.IsNullOrEmpty(control.Name))
            {
                sb.Append('#').Append(control.Name);
            }

            if (control.Classes.Count > 0)
            {
                sb.Append('.').Append(string.Join('.', control.Classes));
            }

            Rect bounds = control.Bounds;
            sb.Append($" [x={bounds.X:F0} y={bounds.Y:F0} w={bounds.Width:F0} h={bounds.Height:F0}]");

            if (!control.IsVisible)
            {
                sb.Append(" !hidden");
            }

            if (!control.IsEnabled)
            {
                sb.Append(" !disabled");
            }

            if (control is Control { IsHitTestVisible: false })
            {
                sb.Append(" !no-hit-test");
            }

            string? text = ExtractText(visual);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append("  \"").Append(Compact(text)).Append('"');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The meaningful user-visible content of a control, or null. Shared with
    /// <see cref="UiAutomation"/> so searching and dumping agree on what a
    /// control "says".
    /// </summary>
    public static string? TextOf(Visual visual) => ExtractText(visual);

    /// <summary>Pulls the meaningful user-visible content out of a control.</summary>
    private static string? ExtractText(Visual visual) => visual switch
    {
        TextBlock t => t.Text,
        TextBox t => (t.Text ?? string.Empty) + (string.IsNullOrEmpty(t.Watermark as string) ? string.Empty : $"  (watermark: {t.Watermark})"),
        Window w => w.Title,
        MenuItem m => m.Header as string ?? string.Empty,
        TabItem ti => ti.Header as string ?? string.Empty,
        TabControl tc => $"selectedIndex={tc.SelectedIndex}",
        CheckBox cb => $"checked={cb.IsChecked}",
        RadioButton rb => $"checked={rb.IsChecked}",
        ToggleButton tb => $"checked={tb.IsChecked}",
        Slider s => $"value={s.Value:F2}",
        ProgressBar pb => $"value={pb.Value:F1}",
        ComboBox cb => $"selected={cb.SelectedItem ?? "(none)"} items={cb.ItemCount}",
        ListBox lb => $"selected={lb.SelectedItems?.Count ?? 0} items={lb.ItemCount}",
        NumericUpDown n => $"value={n.Value}",
        Button b => b.Content as string ?? string.Empty,
        ContentControl cc when cc.Content is string s => s,
        Image => "(image)",
        _ => null,
    };

    private static string Compact(string text)
    {
        string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    // ------------------------------------------------------------------
    // Document dumps
    // ------------------------------------------------------------------

    /// <summary>
    /// The document as text: artboards → layers → objects, with ids, geometry and
    /// text content. <paramref name="layerName"/> narrows it to one layer (matching
    /// is case-insensitive and matches the layer's full name).
    /// </summary>
    public static string Document(CadDocument document, string? layerName = null, bool includeHidden = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DOCUMENT DUMP");
        sb.AppendLine($"document: {document.Name}  artboards: {document.Artboards.Count}");

        if (!string.IsNullOrWhiteSpace(layerName))
        {
            sb.AppendLine($"filter: layer = \"{layerName}\"");
        }

        sb.AppendLine();

        bool matched = string.IsNullOrWhiteSpace(layerName);
        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                bool isTarget = string.IsNullOrWhiteSpace(layerName) ||
                                string.Equals(layer.Name, layerName, StringComparison.OrdinalIgnoreCase);
                matched |= isTarget;
                if (!isTarget)
                {
                    continue;
                }

                sb.AppendLine($"ARTBOARD \"{artboard.Name}\" {artboard.Width:F1} x {artboard.Height:F1} pt at ({artboard.X:F1}, {artboard.Y:F1})");
                sb.AppendLine($"  LAYER \"{layer.Name}\" visible={layer.IsVisible} locked={layer.IsLocked} opacity={layer.Opacity:F2} objects={layer.Children.Count}");
                DumpItems(layer.Children, 4, includeHidden, sb);
            }
        }

        if (!matched)
        {
            sb.AppendLine($"(no layer named \"{layerName}\"; layers present:");
            foreach (string name in document.Artboards.SelectMany(a => a.Layers).Select(l => l.Name))
            {
                sb.AppendLine($"  - {name}");
            }

            sb.AppendLine(")");
        }

        // Pasteboard objects.
        if (document.Orphans.Children.Count > 0)
        {
            sb.AppendLine($"PASTEBOARD objects={document.Orphans.Children.Count}");
            DumpItems(document.Orphans.Children, 2, includeHidden, sb);
        }

        return sb.ToString();
    }

    private static void DumpItems(IReadOnlyList<LayerItem> items, int indent, bool includeHidden, StringBuilder sb)
    {
        string pad = new(' ', indent * 2);
        foreach (LayerItem item in items)
        {
            if (!includeHidden && !item.IsEffectivelyVisible())
            {
                continue;
            }

            string geometry = item switch
            {
                PathItem p => Bounds(p.BoundingBox()),
                TextItem t => Bounds(t.BoundingBox()),
                ArtGroup g => Bounds(g.BoundingBox()),
                _ => string.Empty,
            };

            sb.Append(pad).Append(item switch
            {
                PathItem p => $"path \"{item.Name}\" subpaths={p.SubPaths.Count} nodes={p.SubPaths.Sum(s => s.Nodes.Count)}",
                TextItem t => $"text \"{item.Name}\" \"{t.PlainText.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"")}\" fontSize={t.MaxFontSize:F1}",
                ArtGroup => $"group \"{item.Name}\"",
                _ => item.GetType().Name,
            });

            sb.Append($"{geometry} id={item.Id}");
            if (!item.IsVisible)
            {
                sb.Append(" !hidden");
            }

            if (item.IsLocked)
            {
                sb.Append(" !locked");
            }

            sb.AppendLine();

            if (item is ArtGroup group)
            {
                DumpItems(group.Children, indent + 1, includeHidden, sb);
            }
        }
    }

    private static string Bounds(Rect2D r) => $" [x={r.X:F1} y={r.Y:F1} w={r.Width:F1} h={r.Height:F1}]";
}
