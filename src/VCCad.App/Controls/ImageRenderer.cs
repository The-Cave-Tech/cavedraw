using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VCCad.Core.Model;

namespace VCCad.App.Controls;

/// <summary>
/// Turns an <see cref="ImageItem"/>'s stored samples into something Skia can draw.
///
/// The colour conversion itself lives on the model (<see cref="ImageItem.PixelAt"/> and
/// <see cref="ImageItem.CoverageAt"/>) so it is covered by tests that need no graphics
/// toolkit. This class only packs those pixels into a bitmap, and caches the result
/// because the conversion is per-pixel work.
/// </summary>
internal static class ImageRenderer
{
    private static readonly Dictionary<ImageItem, Bitmap> Cache = new();

    /// <summary>A drawable bitmap for the image, built on first use and cached.</summary>
    public static Bitmap BitmapFor(ImageItem image)
    {
        if (Cache.TryGetValue(image, out Bitmap? cached))
        {
            return cached;
        }

        Bitmap bitmap = Build(image);
        Cache[image] = bitmap;
        return bitmap;
    }

    /// <summary>Drops cached pixels for an image whose samples changed.</summary>
    public static void Invalidate(ImageItem image)
    {
        if (Cache.Remove(image, out Bitmap? old))
        {
            old.Dispose();
        }
    }

    /// <summary>Drops every cached bitmap; used when the document is replaced.</summary>
    public static void Clear()
    {
        foreach (Bitmap bitmap in Cache.Values)
        {
            bitmap.Dispose();
        }

        Cache.Clear();
    }

    private static Bitmap Build(ImageItem image)
    {
        int width = Math.Max(1, image.PixelWidth);
        int height = Math.Max(1, image.PixelHeight);
        var pixels = new byte[width * height * 4];

        bool usable = image.Samples.Length > 0 && image.BitsPerComponent == 8;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;

                if (!usable)
                {
                    // Nothing decodable: transparent rather than a black rectangle, so a
                    // gap reads as a gap.
                    continue;
                }

                ColorRgb colour = image.PixelAt(x, y);

                // Bgra8888, so blue and red are written in that order.
                pixels[at] = ToByte(colour.B);
                pixels[at + 1] = ToByte(colour.G);
                pixels[at + 2] = ToByte(colour.R);
                pixels[at + 3] = ToByte(image.CoverageAt(x, y));
            }
        }

        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using (ILockedFramebuffer buffer = writeable.Lock())
        {
            // A locked framebuffer's rows can be padded, so copy row by row rather than
            // assuming the stride equals the pixel width.
            int stride = width * 4;
            if (buffer.RowBytes == stride)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    pixels, 0, buffer.Address, pixels.Length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        pixels, y * stride, buffer.Address + (y * buffer.RowBytes), stride);
                }
            }
        }

        return writeable;
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
