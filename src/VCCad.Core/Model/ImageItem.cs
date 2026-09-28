using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// The colour space of an image's samples, as PDF names it.
///
/// The samples are kept in the space the file used rather than converted on import:
/// a DeviceCMYK raster turned into RGB on the way in has already lost something by the
/// time it is written back out, and the fidelity rule is that what we did not have to
/// change, we do not change.
/// </summary>
public enum ImageColorSpace
{
    Gray,
    Rgb,
    Cmyk,
    /// <summary>Indexed (palette) colour, with the palette kept alongside.</summary>
    Indexed,
}

/// <summary>
/// An embedded raster image.
///
/// Samples are stored **decoded but unfiltered** — the bytes a reader gets after
/// undoing the stream filter, before any colour conversion. Rendering converts from
/// <see cref="ColorSpace"/>; export re-applies FlateDecode and writes them back
/// unchanged, so a CMYK scan stays a CMYK scan.
/// </summary>
public sealed class ImageItem : LayerItem
{
    /// <summary>Pixel width of the sample grid.</summary>
    public int PixelWidth { get; set; }

    /// <summary>Pixel height of the sample grid.</summary>
    public int PixelHeight { get; set; }

    /// <summary>Bits per sample: 1, 2, 4, 8 or 16.</summary>
    public int BitsPerComponent { get; set; } = 8;

    /// <summary>The colour space the samples are in.</summary>
    public ImageColorSpace ColorSpace { get; set; } = ImageColorSpace.Rgb;

    /// <summary>
    /// Decoded sample bytes, row-major, no filter applied. Length is
    /// ceil(Width * Components * BitsPerComponent / 8) * Height.
    /// </summary>
    public byte[] Samples { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Palette for <see cref="ImageColorSpace.Indexed"/>, three bytes per entry, or
    /// empty. Kept so an indexed image round-trips without being expanded.
    /// </summary>
    public byte[] Palette { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Optional single-channel soft mask, same pixel dimensions, 8 bits per sample.
    /// Empty when the image is fully opaque.
    /// </summary>
    public byte[] Mask { get; set; } = Array.Empty<byte>();

    /// <summary>Whether <see cref="Mask"/> carries a real soft mask.</summary>
    public bool HasMask => Mask.Length >= PixelWidth * PixelHeight && PixelWidth > 0 && PixelHeight > 0;

    /// <summary>
    /// Where the image sits on the artboard, in artboard-local coordinates. The image
    /// fills this rectangle; the transform that placed it in the PDF is folded into it,
    /// so the model stores a placement rather than a matrix.
    /// </summary>
    public Rect2D Placement { get; set; }

    /// <summary>Components per pixel for the current colour space.</summary>
    public int Components => ColorSpace switch
    {
        ImageColorSpace.Gray => 1,
        ImageColorSpace.Cmyk => 4,
        ImageColorSpace.Indexed => 1,
        _ => 3,
    };

    /// <summary>Bytes per row of samples, as PDF defines it.</summary>
    public int RowBytes
        => ((PixelWidth * Components * BitsPerComponent) + 7) / 8;

    public override LayerItem Clone()
    {
        var copy = new ImageItem
        {
            Name = Name,
            IsVisible = IsVisible,
            PixelWidth = PixelWidth,
            PixelHeight = PixelHeight,
            BitsPerComponent = BitsPerComponent,
            ColorSpace = ColorSpace,
            Samples = (byte[])Samples.Clone(),
            Palette = (byte[])Palette.Clone(),
            Mask = (byte[])Mask.Clone(),
            Placement = Placement,
        };

        return copy;
    }

    /// <summary>World-space bounds: the placement shifted by the artboard origin.</summary>
    public Rect2D WorldBounds()
    {
        Vector2D offset = ArtboardOffset();
        return new Rect2D(Placement.X + offset.X, Placement.Y + offset.Y,
            Placement.Width, Placement.Height);
    }

    /// <summary>Bounds in artboard-local space, which is what selection chrome uses.</summary>
    public Rect2D BoundingBox() => Placement;

    /// <summary>
    /// The colour of one pixel, converted from whatever space the samples are in.
    ///
    /// This lives on the model rather than in the renderer so it can be tested without a
    /// graphics toolkit — reading CMYK samples as RGB is not a subtle mistake, it turns a
    /// pale tint black, and it needs a test that says so.
    /// </summary>
    public ColorRgb PixelAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight || BitsPerComponent != 8)
        {
            return ColorRgb.Black;
        }

        int components = Components;
        int at = ((y * PixelWidth) + x) * components;
        if (at + components > Samples.Length)
        {
            return ColorRgb.Black;
        }

        switch (ColorSpace)
        {
            case ImageColorSpace.Gray:
                double g = Samples[at] / 255.0;
                return new ColorRgb(g, g, g);

            case ImageColorSpace.Cmyk:
                double c = Samples[at] / 255.0;
                double m = Samples[at + 1] / 255.0;
                double yl = Samples[at + 2] / 255.0;
                double k = Samples[at + 3] / 255.0;
                return new ColorRgb((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - yl) * (1 - k));

            case ImageColorSpace.Indexed:
                int entry = Samples[at] * 3;
                return entry + 2 < Palette.Length
                    ? new ColorRgb(Palette[entry] / 255.0, Palette[entry + 1] / 255.0,
                        Palette[entry + 2] / 255.0)
                    : ColorRgb.Black;

            default:
                return new ColorRgb(
                    Samples[at] / 255.0, Samples[at + 1] / 255.0, Samples[at + 2] / 255.0);
        }
    }

    /// <summary>Coverage of one pixel: the soft mask when present, opaque otherwise.</summary>
    public double CoverageAt(int x, int y)
    {
        if (!HasMask || x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight)
        {
            return 1.0;
        }

        return Mask[(y * PixelWidth) + x] / 255.0;
    }
}
