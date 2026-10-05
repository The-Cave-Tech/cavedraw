using System;
using System.Globalization;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VCCad.App.Fonts;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **How much ink a drawn run actually produces, against the width it measures** (issue #258).
///
/// The issue reports an edited block whose text stops well short of its own frame, while every width the model, the layout
/// and the paint log report agrees: the piece handed to the shaper is the whole run, `FormattedText.Width` matches the box
/// within 2-3%, and `scaleX` asks for a slight *stretch*. The ink is at about 55%.
///
/// This test removes every variable the editor brings: no window, no canvas, no transform stack, no stale binary. It builds
/// the same <see cref="FormattedText"/> the painter builds - same family through <see cref="FontFamilyResolver"/>, same
/// size, same string - draws it into an offscreen bitmap, and measures where the ink actually ends.
///
/// If the ink fills the width here, the defect is in the editor's drawing of it (the transform around `DrawText`, or what
/// happens to the piece before it is drawn). If the ink is short here, the defect is in the shaping, and it can be chased in
/// one function with no application running.
/// </summary>
public class EditedRunInkTests
{
    private const string Edited = " jelly donut and more text 3464 - LILLIE - Page 1/12";

    private static TextRun Run() => new()
    {
        Text = Edited,
        FontFamily = "Nimbus Sans",
        FontSize = 9,
    };

    [AvaloniaFact]
    public void TheInkOfADrawnRunReachesTheWidthItMeasures()
    {
        TextRun run = Run();
        FontFamily family = FontFamilyResolver.For(run);
        var typeface = new Typeface(family, FontStyle.Normal, FontWeight.Normal);

        var text = new FormattedText(
            run.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            run.FontSize,
            Brushes.Black);

        double measured = text.Width;

        int padLeft = 8;
        var size = new PixelSize((int)Math.Ceiling(measured) + (padLeft * 2), 40);
        using var target = new RenderTargetBitmap(size);
        using (DrawingContext context = target.CreateDrawingContext())
        {
            context.DrawText(text, new Point(padLeft, 6));
        }

        int stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        IntPtr native = System.Runtime.InteropServices.Marshal.AllocHGlobal(buffer.Length);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), native, buffer.Length, stride);
            System.Runtime.InteropServices.Marshal.Copy(native, buffer, 0, buffer.Length);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
        }

        int rightmost = -1;
        for (int x = size.Width - 1; x >= 0; x--)
        {
            bool hasInk = false;
            for (int y = 0; y < size.Height; y++)
            {
                int i = (y * stride) + (x * 4);
                // BGRA, premultiplied: any pixel with a meaningful alpha is ink.
                if (buffer[i + 3] > 40)
                {
                    hasInk = true;
                    break;
                }
            }

            if (hasInk)
            {
                rightmost = x;
                break;
            }
        }

        Assert.True(rightmost >= 0, "Nothing was drawn at all - the face produced no ink.");

        double drawn = rightmost - padLeft + 1;
        double ratio = measured > 0 ? drawn / measured : 0;

        Assert.True(
            ratio > 0.9,
            $"The run measures {measured:F2} units wide but only {drawn:F2} units of ink were drawn " +
            $"(ratio {ratio:F3}, family '{family.Name}', {run.Text.Length} characters). " +
            "A ratio far below 1 is issue #258: the drawing produces less than the width it was given.");
    }
}
