using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Raster;

/// <summary>
/// A rectangle of RGBA pixels that a filter is evaluated over.
///
/// **Filters are raster operations** - that is what SVG filters are, and there is no way to express a blur, a
/// composite's arithmetic or a lighting primitive as vector geometry. So the engine works on pixels, and this is
/// the pixel container it works on.
///
/// Colour is stored **straight** (not premultiplied), each channel 0..1, row-major from the top left - matching the
/// model's coordinate space, where y grows downward. The Porter-Duff operators in <see cref="FilterEngine"/> do
/// their arithmetic premultiplied, because that is the form the compositing formulas are defined in, and converting
/// at the boundary keeps both halves honest: a caller reading a pixel gets the colour it would draw, not a colour
/// that has been multiplied by its own alpha.
/// </summary>
public sealed class FilterBuffer
{
    /// <summary>A buffer of the given size, fully transparent.</summary>
    public FilterBuffer(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "a buffer needs a positive size");
        }

        Width = width;
        Height = height;
        Pixels = new float[width * height * 4];
    }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>Straight RGBA, four floats per pixel, row-major from the top left.</summary>
    public float[] Pixels { get; }

    /// <summary>Whether a coordinate is inside the buffer.</summary>
    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>The pixel at a coordinate. Out of bounds reads as fully transparent, which is what a filter region
    /// edge means: nothing beyond it.</summary>
    public (float R, float G, float B, float A) Get(int x, int y)
    {
        if (!Contains(x, y))
        {
            return (0f, 0f, 0f, 0f);
        }

        int index = Index(x, y);
        return (Pixels[index], Pixels[index + 1], Pixels[index + 2], Pixels[index + 3]);
    }

    /// <summary>Writes a pixel. Out of bounds writes are dropped, so a filter may sample freely and clamp.</summary>
    public void Set(int x, int y, float r, float g, float b, float a)
    {
        if (!Contains(x, y))
        {
            return;
        }

        int index = Index(x, y);
        Pixels[index] = r;
        Pixels[index + 1] = g;
        Pixels[index + 2] = b;
        Pixels[index + 3] = a;
    }

    /// <summary>Writes a pixel from a colour and an alpha.</summary>
    public void Set(int x, int y, ColorRgb colour, float alpha)
        => Set(x, y, (float)colour.R, (float)colour.G, (float)colour.B, alpha);

    /// <summary>Every pixel the same, which is what `feFlood` produces.</summary>
    public void Fill(ColorRgb colour, float alpha)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Set(x, y, colour, alpha);
            }
        }
    }

    /// <summary>Alpha only, which is what `SourceAlpha` is: the source's shape with no colour of its own.</summary>
    public FilterBuffer ToAlpha()
    {
        var alpha = new FilterBuffer(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                alpha.Set(x, y, 0f, 0f, 0f, Get(x, y).A);
            }
        }

        return alpha;
    }

    /// <summary>A copy, so a graph can feed two consumers without one of them writing over the other's input.</summary>
    public FilterBuffer Clone()
    {
        var copy = new FilterBuffer(Width, Height);
        Array.Copy(Pixels, copy.Pixels, Pixels.Length);
        return copy;
    }

    /// <summary>
    /// The same picture at another size, sampled bilinearly at each destination pixel's centre.
    ///
    /// This is what lets a filter be evaluated at the resolution its file asks for (`filterRes`) rather than at
    /// whatever scale the caller happens to draw at: the source is resampled into the region's pixel grid, so a
    /// coarse resolution really does compute the blur on few pixels and the difference is visible, rather than being
    /// a number the model carries and nothing reads.
    ///
    /// Bilinear rather than area-averaged, and clamped to the edge: the source is a rendering of the same picture,
    /// so a sample is a sample of it - and an area average over a shrinking footprint is the same arithmetic with
    /// more of it written out. Outside the buffer reads as transparent, as everywhere else in this class.
    /// </summary>
    public FilterBuffer Resampled(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "a buffer needs a positive size");
        }

        var output = new FilterBuffer(width, height);
        if (width == Width && height == Height)
        {
            Array.Copy(Pixels, output.Pixels, Pixels.Length);
            return output;
        }

        // Destination pixel centres against source pixel centres, which is the mapping that keeps a picture in the
        // same place when it is resampled: the centre of the destination maps to the centre of the source.
        double scaleX = (double)Width / width;
        double scaleY = (double)Height / height;

        for (int y = 0; y < height; y++)
        {
            double sourceY = ((y + 0.5) * scaleY) - 0.5;
            for (int x = 0; x < width; x++)
            {
                double sourceX = ((x + 0.5) * scaleX) - 0.5;
                (float r, float g, float b, float a) = Sample(sourceX, sourceY);
                output.Set(x, y, r, g, b, a);
            }
        }

        return output;
    }

    /// <summary>One pixel, bilinearly interpolated, transparent outside the buffer.</summary>
    private (float R, float G, float B, float A) Sample(double x, double y)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double fx = x - x0;
        double fy = y - y0;

        float r = 0, g = 0, b = 0, a = 0;
        for (int j = 0; j <= 1; j++)
        {
            for (int i = 0; i <= 1; i++)
            {
                double weight = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                if (weight == 0)
                {
                    continue;
                }

                (float pr, float pg, float pb, float pa) = Get(x0 + i, y0 + j);
                r += (float)(pr * weight);
                g += (float)(pg * weight);
                b += (float)(pb * weight);
                a += (float)(pa * weight);
            }
        }

        return (r, g, b, a);
    }

    /// <summary>The number of pixels with any alpha at all - a cheap way for a test to ask "did anything draw?".</summary>
    public int OpaquePixels()
    {
        int count = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (Get(x, y).A > 0.0001f)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>The alpha at a pixel, which is what most filter tests assert on.</summary>
    public float AlphaAt(int x, int y) => Get(x, y).A;

    /// <summary>
    /// Places another buffer inside this one at an offset, copying pixel for pixel.
    ///
    /// This is how the source graphic lands in the filter region, and how a primitive that changes the region's
    /// origin puts its input back. Pixels that fall outside are dropped rather than clamped, because a buffer
    /// edge is a hard boundary in SVG: the region is the canvas.
    /// </summary>
    public void Blit(FilterBuffer source, int offsetX, int offsetY, bool add = false)
    {
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                (float r, float g, float b, float a) = source.Get(x, y);
                int targetX = x + offsetX;
                int targetY = y + offsetY;
                if (!Contains(targetX, targetY))
                {
                    continue;
                }

                if (!add)
                {
                    Set(targetX, targetY, r, g, b, a);
                    continue;
                }

                (float r0, float g0, float b0, float a0) = Get(targetX, targetY);
                Set(targetX, targetY, r0 + r, g0 + g, b0 + b, a0 + a);
            }
        }
    }

    /// <summary>Compares two buffers pixel for pixel within a tolerance, for round-trip and determinism tests.</summary>
    public bool Matches(FilterBuffer other, float tolerance = 0.002f)
    {
        if (Width != other.Width || Height != other.Height)
        {
            return false;
        }

        for (int i = 0; i < Pixels.Length; i++)
        {
            if (Math.Abs(Pixels[i] - other.Pixels[i]) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private int Index(int x, int y) => ((y * Width) + x) * 4;
}
