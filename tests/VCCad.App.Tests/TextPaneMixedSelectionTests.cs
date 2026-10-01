using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The text panel describing a **selection**, which is not obliged to agree with itself.
///
/// A panel that reads the first selected block's size and presents it as everyone's is the defect this guards
/// against: a person reads a number, believes it describes what they selected, and it does not. `TextSummary`
/// already knows which members the selection agrees on and which are **mixed**, so the pane has to say so rather
/// than pick a member and hope.
///
/// Everything is asserted on the **model** and cross-checked against `TextSummary.Of`, and the edit that matters
/// most is the one to an agreeing member: the mixed members must be left exactly as they were rather than being
/// written from a field nobody touched.
/// </summary>
public class TextPaneMixedSelectionTests
{
    // ---------------------------------------------------------------- driving the pane

    private static (TextPane Pane, EditorViewModel ViewModel, TextItem First, TextItem Second) Host(
        TextItem first, TextItem second)
    {
        var viewModel = new EditorViewModel();
        viewModel.Document.Artboards[0].Layers[0].AddItem(first);
        viewModel.Document.Artboards[0].Layers[0].AddItem(second);

        // Built **before** Attach: attaching is what subscribes the pane, and attaching first would leave it never
        // having seen the selection at all.
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);

        var pane = new TextPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 440, Height = 780, Content = pane };
        window.Show();
        TextPaneTests.Settle();

        return (pane, viewModel, first, second);
    }

    /// <summary>Hosts two blocks with only the first selected, so the selection can be moved after Attach.</summary>
    private static (TextPane Pane, EditorViewModel ViewModel, TextItem First, TextItem Second) HostFirstOnly(
        TextItem first, TextItem second)
    {
        var viewModel = new EditorViewModel();
        viewModel.Document.Artboards[0].Layers[0].AddItem(first);
        viewModel.Document.Artboards[0].Layers[0].AddItem(second);
        viewModel.SelectObject(first);

        var pane = new TextPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 440, Height = 780, Content = pane };
        window.Show();
        TextPaneTests.Settle();

        return (pane, viewModel, first, second);
    }

    private static TextItem Block(string words, string family = "Nimbus Sans", double size = 12,
        bool bold = false, bool italic = false, ColorRgb? colour = null, TextAlignment alignment = TextAlignment.Left,
        double lineSpacing = 1.2, double paragraphSpacing = 0, double frameWidth = 0, double rotationDegrees = 0)
    {
        var item = new TextItem
        {
            Name = words,
            Origin = new Point2D(0, 0),
            Color = colour ?? ColorRgb.Black,
            Alignment = alignment,
            LineSpacing = lineSpacing,
            ParagraphSpacing = paragraphSpacing,
            FrameWidth = frameWidth,
            RotationRadians = rotationDegrees * Math.PI / 180.0,
        };
        item.Runs.Add(TextPaneTests.Run(words, family, size, bold, italic));
        return item;
    }

    private static TextBox Box(TextPane pane, string name) => TextPaneTests.Box(pane, name);

    private static ComboBox Combo(TextPane pane, string name) => TextPaneTests.Combo(pane, name);

    private static CheckBox Check(TextPane pane, string name) => TextPaneTests.Check(pane, name);

    private static TextBlock Text(TextPane pane, string name) => TextPaneTests.Text(pane, name);

    private static void Commit(TextPane pane, string name, string text) => TextPaneTests.Commit(pane, name, text);

    private static string? Watermark(TextPane pane, string name) => TextPaneTests.Watermark(pane, name);

    // ---------------------------------------------------------------- what a mixed selection reads as

    /// <summary>
    /// One disagreeing member does not hide the others: the words differ and are **mixed**, and the face, colour,
    /// alignment and paragraph style the two blocks agree on are still shown. Blanking the whole panel because one
    /// member differs would throw away most of what a panel is for.
    /// </summary>
    [AvaloniaFact]
    public void ADisagreeingMemberIsMixedAndTheAgreeingOnesKeepTheirValue()
    {
        (TextPane pane, _, _, _) = Host(Block("one"), Block("two"));

        Assert.Equal(string.Empty, Box(pane, "ContentBox").Text);
        Assert.Equal("mixed", Watermark(pane, "ContentBox"));
        Assert.Equal("12", Box(pane, "SizeBox").Text);
        Assert.Equal("Nimbus Sans", Combo(pane, "FamilyBox").SelectedItem);
        Assert.Equal("0,0,0,255", Box(pane, "ColorBox").Text);
        Assert.Equal(0, Combo(pane, "AlignBox").SelectedIndex);
        Assert.Equal("2 text blocks", Text(pane, "SelectionLabel").Text);
    }

    /// <summary>**Every member that disagrees says mixed rather than one block's value**, and the pane names them,
    /// so a person cannot read a value here and believe it describes the selection.</summary>
    [AvaloniaFact]
    public void EveryDisagreeingMemberSaysMixed()
    {
        (TextPane pane, _, _, _) = Host(
            Block("one", "Nimbus Sans", 12, bold: true, italic: false, colour: ColorRgb.Black,
                alignment: TextAlignment.Left, lineSpacing: 1.2, paragraphSpacing: 0, frameWidth: 0,
                rotationDegrees: 0),
            Block("two", "Nimbus Roman", 20, bold: false, italic: true, colour: ColorRgb.Red,
                alignment: TextAlignment.Right, lineSpacing: 2.0, paragraphSpacing: 6, frameWidth: 100,
                rotationDegrees: 30));

        foreach (string name in new[] { "ContentBox", "ColorBox", "SizeBox", "LeadingBox", "ParagraphBox", "RotationBox", "FrameWidthBox" })
        {
            Assert.Equal(string.Empty, Box(pane, name).Text);
            Assert.Equal("mixed", Watermark(pane, name));
        }

        Assert.Equal(-1, Combo(pane, "AlignBox").SelectedIndex);
        Assert.Equal("mixed", Combo(pane, "AlignBox").PlaceholderText);
        Assert.Equal(-1, Combo(pane, "FamilyBox").SelectedIndex);
        Assert.Equal("mixed", Combo(pane, "FamilyBox").PlaceholderText);
        Assert.Null(Check(pane, "BoldBox").IsChecked);
        Assert.Null(Check(pane, "ItalicBox").IsChecked);

        string named = Text(pane, "MixedLabel").Text ?? string.Empty;
        Assert.True(Text(pane, "MixedLabel").IsVisible);
        foreach (string member in new[] { "content", "colour", "align", "family", "size", "bold", "italic", "leading", "space", "turn", "frame" })
        {
            Assert.True(named.Contains(member, StringComparison.OrdinalIgnoreCase),
                $"the mixed readout should name '{member}' but says '{named}'");
        }
    }

    /// <summary>
    /// **Nothing is shown as common when `TextSummary` says it is mixed.** The summary is the authority on
    /// agreement, so the test asks it directly for a set of agreement patterns and requires the pane to agree with
    /// it member by member - in both directions, because a pane that says "mixed" over an agreeing selection is as
    /// unusable as one that shows a value over a disagreeing one.
    /// </summary>
    [AvaloniaFact]
    public void NothingIsShownAsCommonWhenTheSummarySaysItIsMixed()
    {
        var patterns = new (TextItem First, TextItem Second)[]
        {
            (Block("one"), Block("two")),
            (Block("same"), Block("same")),
            (Block("a", size: 10), Block("b", size: 10)),
            (Block("a", size: 10), Block("b", size: 14)),
            (Block("a", colour: ColorRgb.Red), Block("b", colour: ColorRgb.Red)),
            (Block("a", colour: ColorRgb.Red), Block("b", colour: ColorRgb.Blue)),
            (Block("a", bold: true), Block("b", bold: false)),
            (Block("a", alignment: TextAlignment.Center), Block("b", alignment: TextAlignment.Right)),
        };

        foreach ((TextItem first, TextItem second) in patterns)
        {
            (TextPane pane, EditorViewModel viewModel, _, _) = Host(first, second);
            TextSummary summary = TextSummary.Of(viewModel.ActiveSession.SelectedTextItems(), 0);

            string context = $"blocks '{first.PlainText}' / '{second.PlainText}'";

            if (summary.ContentMixed)
            {
                Assert.True(Box(pane, "ContentBox").Text == string.Empty,
                    $"{context}: the summary says the content is mixed but the pane shows '{Box(pane, "ContentBox").Text}'");
            }
            else
            {
                Assert.Equal(summary.Content, Box(pane, "ContentBox").Text);
            }

            if (summary.FontSizeMixed)
            {
                Assert.Equal(string.Empty, Box(pane, "SizeBox").Text);
            }
            else
            {
                Assert.Equal(summary.FontSize!.Value.ToString("0.##"), Box(pane, "SizeBox").Text);
            }

            if (summary.ColorMixed)
            {
                Assert.Equal(string.Empty, Box(pane, "ColorBox").Text);
            }
            else
            {
                ColorRgb colour = summary.Color!.Value;
                Assert.Equal(
                    $"{(int)Math.Round(colour.R * 255)},{(int)Math.Round(colour.G * 255)},{(int)Math.Round(colour.B * 255)},{(int)Math.Round(colour.A * 255)}",
                    Box(pane, "ColorBox").Text);
            }

            Assert.Equal(summary.BoldMixed ? null : summary.Bold, Check(pane, "BoldBox").IsChecked);
            Assert.Equal(
                summary.AlignmentMixed ? -1 : summary.Alignment == TextAlignment.Center ? 1 : summary.Alignment == TextAlignment.Right ? 2 : 0,
                Combo(pane, "AlignBox").SelectedIndex);
        }
    }

    // ---------------------------------------------------------------- where an edit lands

    /// <summary>
    /// **An edit to an agreeing member does not write the mixed one.** This is the defect the mixed readout exists
    /// to prevent, and it is not a display bug: the old panel passed the first block's words and colour to
    /// `UpdateSelectedText` for the whole selection, so setting a size wrote one block's text over the other's from
    /// a field the person never touched.
    /// </summary>
    [AvaloniaFact]
    public void AnEditToAnAgreeingMemberLeavesTheMixedMembersAlone()
    {
        (TextPane pane, _, TextItem first, TextItem second) = Host(
            Block("one", colour: ColorRgb.Red), Block("two", colour: ColorRgb.Blue));

        Commit(pane, "SizeBox", "20");

        Assert.Equal(20.0, first.Runs[0].FontSize, 6);
        Assert.Equal(20.0, second.Runs[0].FontSize, 6);

        // The words and the colours are exactly as they were: nobody touched those fields.
        Assert.Equal("one", first.PlainText);
        Assert.Equal("two", second.PlainText);
        Assert.Equal(ColorRgb.Red, first.Color);
        Assert.Equal(ColorRgb.Blue, second.Color);
    }

    /// <summary>Typing into a mixed member is how the disagreement is resolved, and it lands on every block.</summary>
    [AvaloniaFact]
    public void AnEditToAMixedMemberResolvesIt()
    {
        (TextPane pane, _, TextItem first, TextItem second) = Host(
            Block("one", colour: ColorRgb.Red), Block("two", colour: ColorRgb.Blue));

        Commit(pane, "ColorBox", "0,255,0,255");

        Assert.Equal(ColorRgb.FromBytes(0, 255, 0, 255), first.Color);
        Assert.Equal(ColorRgb.FromBytes(0, 255, 0, 255), second.Color);
    }

    /// <summary>A selection with an agreeing member shows that member's value and says nothing about mixing.</summary>
    [AvaloniaFact]
    public void AnAgreeingSelectionShowsNoMixedReadout()
    {
        (TextPane pane, _, _, _) = Host(Block("same"), Block("same"));

        Assert.False(Text(pane, "MixedLabel").IsVisible);
        Assert.Equal("same", Box(pane, "ContentBox").Text);
        Assert.Equal("12", Box(pane, "SizeBox").Text);
    }

    /// <summary>One block is a selection of one, and a selection of one cannot disagree with itself.</summary>
    [AvaloniaFact]
    public void ASingleSelectedBlockIsNeverMixed()
    {
        var viewModel = new EditorViewModel();
        TextItem block = Block("one");
        viewModel.Document.Artboards[0].Layers[0].AddItem(block);
        viewModel.SelectObject(block);

        var pane = new TextPane();
        pane.Attach(viewModel);
        var window = new Window { Width = 440, Height = 780, Content = pane };
        window.Show();
        TextPaneTests.Settle();

        Assert.False(Text(pane, "MixedLabel").IsVisible);
        Assert.Equal("one", Box(pane, "ContentBox").Text);
        Assert.Equal("1 text block", Text(pane, "SelectionLabel").Text);
    }

    /// <summary>
    /// **A selection change refreshes the panel.** The issue names this: a person clicks a different block and the
    /// fields have to describe it, not the one that was selected when the pane last happened to read the model.
    /// </summary>
    [AvaloniaFact]
    public void ASelectionChangeRefreshesThePanel()
    {
        (TextPane pane, EditorViewModel viewModel, TextItem first, TextItem second) = HostFirstOnly(
            Block("one", size: 10), Block("two", size: 30));

        Assert.Equal("one", Box(pane, "ContentBox").Text);

        viewModel.SelectObject(second);
        TextPaneTests.Settle();

        Assert.Equal("two", Box(pane, "ContentBox").Text);
        Assert.Equal("30", Box(pane, "SizeBox").Text);
        Assert.False(Text(pane, "MixedLabel").IsVisible);

        viewModel.SelectObject(first);
        TextPaneTests.Settle();

        Assert.Equal("one", Box(pane, "ContentBox").Text);
        Assert.Equal("10", Box(pane, "SizeBox").Text);
    }
}
