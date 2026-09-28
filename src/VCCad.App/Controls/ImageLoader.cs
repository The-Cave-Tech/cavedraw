using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Controls;

/// <summary>
/// Reads an image file into the document's own representation.
///
/// The model stores samples in a colour space rather than a bitmap, so an imported PNG or
/// JPEG is decoded once here and kept as straight RGB with an optional coverage mask. From
/// that point on it behaves exactly like an image that came out of a PDF: it draws through
/// <see cref="ImageRenderer"/>, scales through its placement box, and exports as an XObject
/// in the same colour space.
/// </summary>
internal static class ImageLoader
{
    /// <summary>
    /// Loads a file as an <see cref="ImageItem"/>, sized to <paramref name="target"/> when
    /// one is given and to its natural pixel size otherwise.
    /// </summary>
    public static ImageItem Load(string path, Rect2D? target = null)
    {
        using var stream = File.OpenRead(path);
        using var bitmap = new Bitmap(stream);

        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;

        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        bool anyTransparent = false;

        var writeable = new WriteableBitmap(
            bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using (ILockedFramebuffer buffer = writeable.Lock())
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, width, height),
                buffer.Address,
                buffer.RowBytes * height,
                buffer.RowBytes);
        }

        // Read back through the writeable bitmap's buffer, which is what CopyPixels wrote.
        var pixels = new byte[width * height * 4];
        using (ILockedFramebuffer buffer = writeable.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(
                buffer.Address, pixels, 0, Math.Min(pixels.Length, buffer.RowBytes * height));
        }

        for (int i = 0; i < width * height; i++)
        {
            // BGRA in, RGB plus coverage out.
            byte b = pixels[(i * 4) + 0];
            byte g = pixels[(i * 4) + 1];
            byte r = pixels[(i * 4) + 2];
            byte a = pixels[(i * 4) + 3];

            rgb[(i * 3) + 0] = r;
            rgb[(i * 3) + 1] = g;
            rgb[(i * 3) + 2] = b;
            alpha[i] = a;

            if (a != 255)
            {
                anyTransparent = true;
            }
        }

        Rect2D placement = target ?? new Rect2D(0, 0, width, height);

        return new ImageItem
        {
            Name = Path.GetFileNameWithoutExtension(path),
            PixelWidth = width,
            PixelHeight = height,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Rgb,
            Samples = rgb,
            Mask = anyTransparent ? alpha : Array.Empty<byte>(),
            Placement = placement,
        };
    }
}
