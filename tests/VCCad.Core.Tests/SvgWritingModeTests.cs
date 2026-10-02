using System.Text;
using System.Text.Json.Nodes;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// SVG 12's two writing modes and the two directions that travel with them - issue #127.
///
/// **The acceptance is the geometry, not the member.** A block that round-trips a `writing-mode` and is still drawn
/// as one left-to-right line has lost the file's layout while looking complete, which is the shape this repository
/// has already been bitten by four times. So every claim below is asserted on where a glyph actually lands and on
/// the block's own extent: a vertical run's glyphs sit one below the other and its lines stack sideways, an `rtl`
/// block's characters are in the visual order the bidirectional algorithm gives them, and both survive a write and
/// a re-read.
///
/// The measurer is fixed at four units a character - the same seam <see cref="SvgTextTests"/> uses - so a position
/// is the file's arithmetic and not this machine's fonts.
/// </summary>
public class SvgWritingModeTests
{
    private static readonly string Header =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Header + body + "</svg>");

    private static TextItem Block(SvgImportResult result)
        => Assert.Single(result.Document.AllItems().OfType<TextItem>());

    /// <summary>
    /// Every character of the block in the order the pen reaches it, positioned **on the page**.
    ///
    /// Read out of the layout's own glyph list rather than by indexing the string, because **the whole question is
    /// which order the pen reaches the characters in** - a helper that walked the string would answer "logical
    /// order" for every block and could never see a reordering. The block's own origin is added back, so a number
    /// here is where the file's coordinates put the glyph and not a distance inside the block.
    /// </summary>
    private static List<(string Text, double X, double Y, double Inline, double Cross)> VisualCharacters(TextItem item)
    {
        TextLayout layout = TextLayoutEngine.Compute(item);
        string flat = item.PlainText;

        return layout.Glyphs
            .OrderBy(g => g.Inline)
            .Select(g => (
                flat[g.Index].ToString(),
                item.Origin.X + g.X,
                item.Origin.Y + g.Y,
                g.Inline,
                g.Cross))
            .ToList();
    }

    /// <summary>The characters in the order the pen reaches them, as one string.</summary>
    private static string VisualOrder(TextItem item)
        => string.Concat(VisualCharacters(item).Select(g => g.Text));

    /// <summary>A measurer that sets every character at one width, so a position is arithmetic and not a font.</summary>
    private sealed class FixedAdvance(double advance) : ITextMetrics
    {
        public IReadOnlyList<double> Advances(TextRun run)
            => Enumerable.Repeat(advance, run.Text.Length).ToArray();

        public double Ascent(TextRun run) => run.FontSize * 0.8;

        public double Descent(TextRun run) => run.FontSize * 0.2;
    }

    /// <summary>A string's characters, sorted, so two orders of the same characters compare equal.</summary>
    private static string Sorted(string text)
        => string.Concat(text.OrderBy(c => (int)c));

    private static T Measured<T>(double advance, Func<T> body)
    {
        ITextMetrics? previous = TextMeasurement.Current;
        try
        {
            TextMeasurement.Current = new FixedAdvance(advance);
            return body();
        }
        finally
        {
            TextMeasurement.Current = previous;
        }
    }

    private static void Measured(double advance, Action body)
    {
        ITextMetrics? previous = TextMeasurement.Current;
        try
        {
            TextMeasurement.Current = new FixedAdvance(advance);
            body();
        }
        finally
        {
            TextMeasurement.Current = previous;
        }
    }

    // ---------------------------------------------------------------- what the model holds

    /// <summary>
    /// **A text block is horizontal until something says otherwise.** The default is SVG's initial value, so a
    /// document that names no writing mode is drawn exactly as it was before this existed.
    /// </summary>
    [Fact]
    public void ABlockIsHorizontalLeftToRightByDefault()
    {
        var item = new TextItem();
        Assert.Equal(TextWritingMode.HorizontalTb, item.WritingMode);
        Assert.Equal(TextDirection.LeftToRight, item.Direction);

        var run = new TextRun();
        Assert.Equal(GlyphOrientation.Auto, run.FontOrientation);
    }

    /// <summary>
    /// **`writing-mode` is kept on the block instead of being reported as a loss.** The value the file wrote is the
    /// value the model holds, and no warning names it - a warning that said the model cannot hold it would be a lie
    /// the moment the layout acts on it.
    /// </summary>
    [Theory]
    [InlineData("vertical-rl", TextWritingMode.VerticalRl)]
    [InlineData("vertical-lr", TextWritingMode.VerticalLr)]
    [InlineData("horizontal-tb", TextWritingMode.HorizontalTb)]
    public void WritingModeIsKeptOnTheBlock(string written, TextWritingMode expected)
    {
        SvgImportResult result = Read($"<text x=\"0\" y=\"0\" writing-mode=\"{written}\">hi</text>");

        Assert.Equal(expected, Block(result).WritingMode);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("writing-mode", StringComparison.Ordinal));
    }

    /// <summary>`direction` is kept on the block the same way, and is not reported as a loss.</summary>
    [Theory]
    [InlineData("rtl", TextDirection.RightToLeft)]
    [InlineData("ltr", TextDirection.LeftToRight)]
    public void DirectionIsKeptOnTheBlock(string written, TextDirection expected)
    {
        SvgImportResult result = Read($"<text x=\"0\" y=\"0\" direction=\"{written}\">hi</text>");

        Assert.Equal(expected, Block(result).Direction);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("direction=", StringComparison.Ordinal));
    }

    /// <summary>
    /// **`glyph-orientation-vertical` is kept on the run**, because SVG states it per element and the model states a
    /// face per run. The three spellings are the property's own.
    /// </summary>
    [Theory]
    [InlineData("auto", GlyphOrientation.Auto)]
    [InlineData("0", GlyphOrientation.Upright)]
    [InlineData("90", GlyphOrientation.Rotate)]
    public void GlyphOrientationIsKeptOnTheRun(string written, GlyphOrientation expected)
    {
        SvgImportResult result = Read(
            $"<text x=\"0\" y=\"0\" writing-mode=\"vertical-rl\" glyph-orientation-vertical=\"{written}\">hi</text>");

        Assert.Equal(expected, Assert.Single(Block(result).Runs).FontOrientation);
    }

    // ---------------------------------------------------------------- vertical layout

    /// <summary>
    /// **A `vertical-rl` run advances downward.** The whole of the issue's first test: the pen moves down the block
    /// rather than along it, so the first glyph sits at the block's origin, the second one advance below it, and the
    /// block's own extent is one column wide and as tall as the string is long.
    /// </summary>
    [Fact]
    public void AVerticalRunAdvancesDownward()
    {
        Measured(4.0, () =>
        {
            TextItem item = Block(Read(
                "<text x=\"10\" y=\"20\" writing-mode=\"vertical-rl\" font-size=\"10\">abc</text>"));

            Assert.Equal(TextWritingMode.VerticalRl, item.WritingMode);
            Assert.Equal(10.0, item.Origin.X, 9);
            Assert.Equal(20.0, item.Origin.Y, 9);

            List<(string Text, double X, double Y, double Inline, double Cross)> glyphs = VisualCharacters(item);
            Assert.Equal(new[] { "a", "b", "c" }, glyphs.Select(g => g.Text));

            // The pen runs down the page and does not move across it, so every glyph shares one column.
            Assert.All(glyphs, g => Assert.Equal(10.0, g.X, 9));
            Assert.Equal(new[] { 20.0, 24.0, 28.0 }, glyphs.Select(g => g.Y));

            // The block is as tall as the pen travelled and one line box deep across.
            Rect2D box = item.LocalBounds();
            Assert.Equal(12.0, box.Height, 6);
            Assert.Equal(12.0, box.Width, 6);
        });
    }

    /// <summary>
    /// **`vertical-rl` and `vertical-lr` stack their columns on opposite sides.**
    ///
    /// A one-column block cannot tell the two apart, so this is a block of two lines - which is what a newline in
    /// the text makes. The second column is one line box across from the first, to the left under `vertical-rl` and
    /// to the right under `vertical-lr`, and the columns do not sit on top of each other.
    /// </summary>
    [Theory]
    [InlineData(TextWritingMode.VerticalRl, -1.0)]
    [InlineData(TextWritingMode.VerticalLr, 1.0)]
    public void VerticalColumnsStackPerTheWritingMode(TextWritingMode mode, double sign)
    {
        Measured(4.0, () =>
        {
            // Built rather than read: SVG has no attribute that says where a `tspan` starts a new column, so the
            // model's own two-line block is the honest way to ask. See the writer's report for that gap.
            var item = new TextItem
            {
                WritingMode = mode,
                Origin = new Point2D(0, 0),
                LineSpacing = 1.0,
                FrameWidth = 0,
            };
            item.Runs.Add(new TextRun { Text = "ab\ncd", FontFamily = "Nimbus Sans", FontSize = 10 });

            TextLayout layout = TextLayoutEngine.Compute(item);
            Assert.Equal(2, layout.Lines.Count);

            double first = layout.Lines[0].Cross;
            double second = layout.Lines[1].Cross;
            Assert.True(
                Math.Sign(second - first) == Math.Sign(sign) && Math.Abs(second - first) > 1e-6,
                $"the second column sits at {second} against the first at {first}, and {mode} puts it at sign {sign}");

            // The characters of the two lines are on their own columns and nowhere else.
            List<(string Text, double X, double Y, double Inline, double Cross)> glyphs = VisualCharacters(item);
            Assert.Equal(4, glyphs.Count);
            Assert.Equal(2, glyphs.Count(g => Math.Abs(g.X - glyphs[0].X) < 1e-9));
            Assert.Equal(new[] { "a", "b" }, glyphs.Where(g => Math.Abs(g.X - glyphs[0].X) < 1e-9).Select(g => g.Text));
        });
    }

    // ---------------------------------------------------------------- right-to-left

    /// <summary>
    /// **A pure `rtl` string is laid out in visual order, not logical order.** The first character of the file is
    /// the rightmost one on the page: the pen starts at the far end and moves back towards the origin. Text drawn
    /// left-to-right would put the same characters in the same places with the words reversed, which is legible and
    /// wrong, so this is asserted on the pen order rather than on "a direction was read".
    /// </summary>
    [Fact]
    public void APureRightToLeftStringIsReorderedIntoVisualOrder()
    {
        Measured(4.0, () =>
        {
            // Four Hebrew letters: alef, bet, gimel, dalet - one character each, no shaping needed.
            TextItem item = Block(Read(
                "<text x=\"100\" y=\"0\" direction=\"rtl\" font-size=\"10\">\u05d0\u05d1\u05d2\u05d3</text>"));

            Assert.Equal(TextDirection.RightToLeft, item.Direction);
            Assert.Equal("\u05d0\u05d1\u05d2\u05d3", item.PlainText);

            List<(string Text, double X, double Y, double Inline, double Cross)> glyphs = VisualCharacters(item);

            // Logical order reversed: dalet, gimel, bet, alef - with dalet rightmost.
            Assert.Equal(
                new[] { "\u05d3", "\u05d2", "\u05d1", "\u05d0" },
                glyphs.Select(g => g.Text));

            // Ascending x: the leftmost character is the file's last, which is the whole of "visual order".
            for (int i = 1; i < glyphs.Count; i++)
            {
                Assert.True(
                    glyphs[i].X > glyphs[i - 1].X,
                    $"glyph {i} of {string.Join(",", glyphs.Select(g => $"{g.Text}@{g.X}"))} is not further right");
            }
        });
    }

    /// <summary>
    /// **A Latin word inside an `rtl` paragraph keeps its own direction.** The words change places; the letters
    /// inside "abc" do not. This is the half a reader gets wrong by reversing the whole string, and it is why the
    /// bidirectional algorithm is not a `Reverse()`.
    /// </summary>
    [Fact]
    public void LatinInsideRightToLeftKeepsItsLettersInOrder()
    {
        Measured(4.0, () =>
        {
            TextItem item = Block(Read(
                "<text x=\"100\" y=\"0\" direction=\"rtl\" font-size=\"10\">abc \u05d0\u05d1\u05d2</text>"));

            string visual = VisualOrder(item);
            string logical = item.PlainText;

            // The characters are the file's, in another order.
            Assert.Equal(Sorted(logical), Sorted(visual));

            // "abc" survives as a run of three, in that order - the letters are not mirrored.
            int at = visual.IndexOf("abc", StringComparison.Ordinal);
            Assert.True(at >= 0, $"the Latin word is not intact in the visual order \"{visual}\"");

            // And the word it was written *before* is drawn at the visual right, because a right-to-left paragraph
            // begins at its right-hand edge: "abc אבג" comes out as the Hebrew reversed, then the space, then the
            // intact Latin word.
            //
            // **This assertion used to read `at == 0`** - the Latin word at the visual *left* - which is what the
            // level assignment produced while rule I2 raised a right-to-left character by two levels at an odd
            // paragraph level instead of leaving it at its own. The comment beside it already described the order
            // below, so the assertion and its own comment disagreed and the test was reading the code rather than
            // the algorithm. See `Bidi.ResolveLevels`.
            Assert.True(
                at == visual.Length - 3,
                $"the visual string is \"{visual}\", and a right-to-left paragraph starts at its right-hand edge, " +
                "so the word written first is the rightmost one");

            Assert.StartsWith("\u05d2\u05d1\u05d0", visual, StringComparison.Ordinal);

            // The Hebrew half is reversed and intact, which is the half a left-to-right draw cannot produce.
            Assert.Contains("\u05d2\u05d1\u05d0", visual, StringComparison.Ordinal);
        });
    }

    // ---------------------------------------------------------------- faithful round trips

    /// <summary>
    /// **Both properties are written back and read again.** Fidelity both ways, or the round trip is a lie: a block
    /// written without its `writing-mode` re-imports as a horizontal line, which is the silent loss the issue names.
    /// </summary>
    [Theory]
    [InlineData(TextWritingMode.VerticalRl, TextDirection.LeftToRight)]
    [InlineData(TextWritingMode.VerticalLr, TextDirection.LeftToRight)]
    [InlineData(TextWritingMode.VerticalRl, TextDirection.RightToLeft)]
    [InlineData(TextWritingMode.HorizontalTb, TextDirection.RightToLeft)]
    [InlineData(TextWritingMode.HorizontalTb, TextDirection.LeftToRight)]
    public void WritingModeAndDirectionSurviveAWriteAndReRead(TextWritingMode mode, TextDirection direction)
    {
        Measured(4.0, () =>
        {
            var text = new TextItem { Origin = new Point2D(30, 40), WritingMode = mode, Direction = direction };
            text.Runs.Add(new TextRun { Text = "abc", FontFamily = "Nimbus Sans", FontSize = 10 });

            string svg = SvgWriter.Write(DocumentWith(text));
            TextItem back = Assert.Single(
                SvgReader.Read(svg).Document.AllItems().OfType<TextItem>());

            Assert.Equal(mode, back.WritingMode);
            Assert.Equal(direction, back.Direction);
            Assert.Equal("abc", back.PlainText);
        });
    }

    /// <summary>
    /// **A vertical block's glyph advances come back as the room the file left.** The written `y` positions are the
    /// pen's own, so re-importing gives the same downward advance rather than a single point.
    /// </summary>
    [Fact]
    public void AVerticalBlocksGlyphPositionsSurviveAWriteAndReRead()
    {
        Measured(4.0, () =>
        {
            TextItem original = Block(Read(
                "<text x=\"10\" y=\"20\" writing-mode=\"vertical-rl\" font-size=\"10\">abc</text>"));

            string svg = SvgWriter.Write(DocumentWith(original));
            TextItem back = Assert.Single(
                SvgReader.Read(svg).Document.AllItems().OfType<TextItem>());

            Assert.Equal(TextWritingMode.VerticalRl, back.WritingMode);
            Assert.Equal(original.Origin.X, back.Origin.X, 6);
            Assert.Equal(original.Origin.Y, back.Origin.Y, 6);
            Assert.Equal(
                VisualCharacters(original).Select(g => g.Y),
                VisualCharacters(back).Select(g => g.Y));
        });
    }

    /// <summary>
    /// **`direction` is the file's and nothing else's.** A document that states none runs left to right whatever
    /// its content is, because that is the property's initial value and therefore the file's meaning; the
    /// right-to-left *island* inside the line is a different rule and is still reordered.
    ///
    /// This is asserted on the block's position as well as on its member, because that is where inferring the
    /// direction can be seen: the corpus file `test-rtl-vertical.svg` puts an untagged Arabic line at `x="50"`, and
    /// its own expected rendering draws that word's ink at x 51..111 - inside the page, starting at the file's own
    /// coordinate. A reader that read the direction out of the content put the same word at x -36..50: one line
    /// width to the left, half of it off the artboard, for a file that draws it inside.
    /// </summary>
    [Fact]
    public void AFileThatStatesNoDirectionRunsLeftToRightWhateverItHolds()
    {
        Measured(4.0, () =>
        {
            TextItem item = Block(Read(
                "<text x=\"50\" y=\"0\" font-size=\"10\">\u05d0\u05d1\u05d2</text>"));

            Assert.Equal(TextDirection.LeftToRight, item.Direction);
            Assert.Equal(50.0, item.Origin.X, 9);

            // The Hebrew word is still an island: its characters come out in the algorithm's visual order, which
            // for a right-to-left run inside a left-to-right line is reversed within the line.
            Assert.Equal(
                new[] { "\u05d2", "\u05d1", "\u05d0" },
                VisualCharacters(item).Select(g => g.Text));
        });
    }

    /// <summary>
    /// **The paragraph level the layout uses is the block's direction**, and it can be seen in a mixed line: a Latin
    /// word followed by a Hebrew one is drawn with the Latin word at the visual left under `ltr` and at the visual
    /// right under `rtl`. A pure right-to-left string reverses the same way at either level, so it cannot tell the
    /// two apart - which is why this case exists next to the one above rather than instead of it.
    /// </summary>
    [Fact]
    public void TheBaseDirectionDecidesWhereAMixedLineStarts()
    {
        Measured(4.0, () =>
        {
            TextItem leftToRight = Block(Read(
                "<text x=\"0\" y=\"0\" font-size=\"10\">abc \u05d0\u05d1\u05d2</text>"));
            Assert.Equal(TextDirection.LeftToRight, leftToRight.Direction);
            Assert.Equal("abc", string.Concat(VisualCharacters(leftToRight).Take(3).Select(g => g.Text)));

            // The same words with `rtl` stated: the Hebrew is written first on the page instead.
            TextItem rightToLeft = Block(Read(
                "<text x=\"0\" y=\"0\" direction=\"rtl\" font-size=\"10\">abc \u05d0\u05d1\u05d2</text>"));
            Assert.Equal(TextDirection.RightToLeft, rightToLeft.Direction);
            Assert.Equal("\u05d2\u05d1\u05d0", string.Concat(VisualCharacters(rightToLeft).Take(3).Select(g => g.Text)));

            // **And the case that tells the two levels apart for a file that states nothing.** A Latin word first
            // leaves the first strong character left to right, so it cannot distinguish a paragraph level read from
            // the block's direction from one re-derived from the content. A Hebrew word first can: the level is
            // still `ltr`, because the file said nothing and the property's initial value is `ltr`, so the Hebrew
            // island stays where it was written - at the visual left - and only its own characters are reversed.
            // Re-deriving the level from the content would put the Latin word there instead.
            TextItem hebrewFirst = Block(Read(
                "<text x=\"0\" y=\"0\" font-size=\"10\">\u05d0\u05d1\u05d2 abc</text>"));
            Assert.Equal(TextDirection.LeftToRight, hebrewFirst.Direction);
            Assert.Equal(
                "\u05d2\u05d1\u05d0",
                string.Concat(VisualCharacters(hebrewFirst).Take(3).Select(g => g.Text)));
        });
    }

    /// <summary>
    /// **A stated direction does not leak sideways.** The first block says `rtl` and the second says nothing and is
    /// its own paragraph, so the second runs left to right and starts at its own `x` - it does not inherit the
    /// sibling's direction, and the drawing does not become the source of one.
    /// </summary>
    [Fact]
    public void ARightToLeftBlockDoesNotTurnItsNeighbourRightToLeft()
    {
        Measured(4.0, () =>
        {
            SvgImportResult result = Read(
                "<text x=\"0\" y=\"0\" direction=\"rtl\" font-size=\"10\">\u05d0\u05d1</text>" +
                "<text x=\"50\" y=\"40\" font-size=\"10\">RTL text in vertical mode</text>");

            List<TextItem> items = result.Document.AllItems().OfType<TextItem>().ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal(TextDirection.RightToLeft, items[0].Direction);
            Assert.Equal(TextDirection.LeftToRight, items[1].Direction);
            Assert.Equal(50.0, items[1].Origin.X, 9);
        });
    }

    /// <summary>
    /// **A right-to-left line starts where the file says its pen starts.** Under `rtl` the text begins at its
    /// right-hand end and runs back, so the file's `x` is the far edge of the line and the model's origin - the
    /// block's own left edge - is one line width short of it. Writing the model's own coordinate instead lost a
    /// line width on the first read and another one on every read after that.
    /// </summary>
    [Fact]
    public void ARightToLeftLinesOriginSurvivesAWriteAndReRead()
    {
        Measured(4.0, () =>
        {
            TextItem item = Block(Read(
                "<text x=\"100\" y=\"0\" direction=\"rtl\" font-size=\"10\">\u05d0\u05d1\u05d2\u05d3</text>"));

            // Four characters at four units each: the ink ends at the file's x and therefore begins 16 short of it.
            Assert.Equal(84.0, item.Origin.X, 9);

            string svg = SvgWriter.Write(DocumentWith(item));
            Assert.Contains("x=\"100\"", svg, StringComparison.Ordinal);

            TextItem back = Assert.Single(SvgReader.Read(svg).Document.AllItems().OfType<TextItem>());
            Assert.Equal(TextDirection.RightToLeft, back.Direction);
            Assert.Equal(item.Origin.X, back.Origin.X, 6);
            Assert.Equal(item.Origin.Y, back.Origin.Y, 6);
        });
    }

    /// <summary>
    /// **A vertical column starts at the file's `y` under either direction.** SVG runs a column's pen down the
    /// page whatever the base direction says - the direction is which way the *columns* stack - so a right-to-left
    /// column's origin is its `y` and not one piece further up, and it comes back there after a round trip.
    /// </summary>
    [Fact]
    public void AVerticalColumnStartsWhereTheFilePutsItWhateverTheDirection()
    {
        Measured(4.0, () =>
        {
            TextItem item = Block(Read(
                "<text x=\"10\" y=\"20\" writing-mode=\"vertical-rl\" direction=\"rtl\" font-size=\"10\">abc</text>"));

            Assert.Equal(TextDirection.RightToLeft, item.Direction);
            Assert.Equal(10.0, item.Origin.X, 9);
            Assert.Equal(20.0, item.Origin.Y, 9);

            string svg = SvgWriter.Write(DocumentWith(item));
            TextItem back = Assert.Single(SvgReader.Read(svg).Document.AllItems().OfType<TextItem>());
            Assert.Equal(item.Origin.X, back.Origin.X, 6);
            Assert.Equal(item.Origin.Y, back.Origin.Y, 6);
        });
    }

    /// <summary>
    /// **The glyph orientation is written back as the property it came from.** A column the file set upright comes
    /// back upright rather than turned: the reader cannot tell the two apart from the geometry alone, because both
    /// are a run of advance after advance down one `x`, so the property has to be in the file for the run to be
    /// the run it was. A block that asks for nothing writes nothing, which keeps an ordinary document's bytes.
    /// </summary>
    [Fact]
    public void TheGlyphOrientationSurvivesAWriteAndReRead()
    {
        Measured(4.0, () =>
        {
            TextItem item = Block(Read(
                "<text x=\"10\" y=\"20\" writing-mode=\"vertical-lr\" style=\"text-orientation:upright\" " +
                "font-size=\"10\">abc</text>"));
            Assert.Equal(GlyphOrientation.Upright, Assert.Single(item.Runs).FontOrientation);

            string svg = SvgWriter.Write(DocumentWith(item));
            Assert.Contains("glyph-orientation-vertical=\"0\"", svg, StringComparison.Ordinal);

            TextItem back = Assert.Single(SvgReader.Read(svg).Document.AllItems().OfType<TextItem>());
            Assert.Equal(GlyphOrientation.Upright, Assert.Single(back.Runs).FontOrientation);
            Assert.Equal(item.Origin.X, back.Origin.X, 6);
            Assert.Equal(item.Origin.Y, back.Origin.Y, 6);

            // And a run that asks for nothing writes nothing, so a document with no vertical text is unchanged.
            TextItem plain = Block(Read(
                "<text x=\"10\" y=\"20\" writing-mode=\"vertical-lr\" font-size=\"10\">abc</text>"));
            Assert.Equal(GlyphOrientation.Auto, Assert.Single(plain.Runs).FontOrientation);
            Assert.DoesNotContain(
                "glyph-orientation-vertical", SvgWriter.Write(DocumentWith(plain)), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// **An anchored right-to-left line is anchored on the side the anchor means.** `text-anchor` states where the
    /// *start*, the *middle* or the *end* of the text sits, and under `rtl` the start is the right-hand end - so the
    /// shift that reaches the model's own left edge runs the other way. It is asserted on the ink rather than on the
    /// member: a middle-anchored block's ink has to straddle the file's `x` and an end-anchored one's has to begin
    /// there, which is what a sign taken from the wrong direction gets backwards.
    /// </summary>
    [Theory]
    [InlineData("middle", 92.0, 108.0)]
    [InlineData("end", 100.0, 116.0)]
    public void AnAnchoredRightToLeftLineIsAnchoredOnTheSideTheAnchorMeans(
        string anchor, double inkStart, double inkEnd)
    {
        Measured(4.0, () =>
        {
            TextItem item = Block(Read(
                $"<text x=\"100\" y=\"0\" direction=\"rtl\" text-anchor=\"{anchor}\" font-size=\"10\">" +
                "\u05d0\u05d1\u05d2\u05d3</text>"));

            Rect2D ink = item.LocalBounds();
            Assert.Equal(inkStart, ink.X, 6);
            Assert.Equal(inkEnd, ink.X + ink.Width, 6);

            // And the anchor survives the trip: the same `x` the file gave comes back out of the writer.
            string svg = SvgWriter.Write(DocumentWith(item));
            Assert.Contains("x=\"100\"", svg, StringComparison.Ordinal);

            TextItem back = Assert.Single(SvgReader.Read(svg).Document.AllItems().OfType<TextItem>());
            Assert.Equal(item.Origin.X, back.Origin.X, 6);
            Assert.Equal(item.Origin.Y, back.Origin.Y, 6);
        });
    }

    private static CadDocument DocumentWith(TextItem text)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(text);
        return document;
    }

    // ---------------------------------------------------------------- the sidecar

    /// <summary>
    /// **The sidecar carries all three**, and a document that names none of them keeps its old bytes - so a file
    /// written before vertical writing existed loads as a horizontal block rather than as a missing value.
    /// </summary>
    [Fact]
    public void TheSidecarCarriesWritingModeDirectionAndOrientation()
    {
        var text = new TextItem { WritingMode = TextWritingMode.VerticalRl, Direction = TextDirection.RightToLeft };
        text.Runs.Add(new TextRun { Text = "hi", FontOrientation = GlyphOrientation.Upright });

        byte[] json = VccadDocumentSerializer.SerializeToBytes(DocumentWith(text));
        CadDocument back = VccadDocumentSerializer.Deserialize(json);
        TextItem loaded = back.Artboards[0].Layers[0].Children.OfType<TextItem>().Single();

        Assert.Equal(TextWritingMode.VerticalRl, loaded.WritingMode);
        Assert.Equal(TextDirection.RightToLeft, loaded.Direction);
        Assert.Equal(GlyphOrientation.Upright, Assert.Single(loaded.Runs).FontOrientation);
    }

    /// <summary>
    /// **A plain block writes none of the new members and keeps writing the same bytes.** The rule the four spacing
    /// members follow: a document without vertical writing is byte-identical to one written before it existed.
    /// </summary>
    [Fact]
    public void APlainBlockWritesNoneOfTheNewMembers()
    {
        var text = new TextItem { Name = "label", Origin = new Point2D(3, 4) };
        text.Runs.Add(new TextRun { Text = "one" });

        CadDocument document = DocumentWith(text);
        byte[] first = VccadDocumentSerializer.SerializeToBytes(document);

        string json = Encoding.UTF8.GetString(first);
        Assert.DoesNotContain("WritingMode", json);
        Assert.DoesNotContain("FontOrientation", json);

        JsonObject block = (JsonObject)JsonNode.Parse(json)!
            ["Artboards"]![0]!["Layers"]![0]!["Items"]![0]!;
        Assert.DoesNotContain("Direction", block.Select(pair => pair.Key));

        // And a load/save cycle does not grow one.
        Assert.Equal(first, VccadDocumentSerializer.SerializeToBytes(VccadDocumentSerializer.Deserialize(first)));
    }
}
