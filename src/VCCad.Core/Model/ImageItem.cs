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
    /// The compressor the samples are still in, when they were not decoded — <c>DCTDecode</c>
    /// for a JPEG, and the others this reader cannot open.
    ///
    /// A JPEG cannot be decoded here, so its bytes are kept exactly as they arrived. They
    /// only mean anything alongside the filter that produced them: written out as raw RGB
    /// they are a fraction of the bytes a picture needs, and a viewer draws whatever it
    /// likes — one engine left the area blank and the other filled it black. Null means the
    /// bytes are samples.
    /// </summary>
    public string? Filter { get; set; }

    /// <summary>
    /// The compressor the soft mask's bytes are still in, when they were not decoded.
    ///
    /// Separate from <see cref="Filter"/> because a file may compress the two differently,
    /// and a JPEG mask written out as raw grey is a mask no viewer can read — which is what
    /// made one engine leave the picture out and another fill its place with black.
    /// </summary>
    public string? MaskFilter { get; set; }

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

    /// <summary>
    /// Whether the image is mirrored across its placement's vertical axis - a horizontal flip.
    ///
    /// State rather than resampled pixels: flipping in place must not re-encode the samples (which
    /// would be lossy and would break the "the file's bytes are what we keep" rule), and the
    /// renderer can mirror the draw without touching them.
    /// </summary>
    public bool MirrorX { get; set; }

    /// <summary>Whether the image is mirrored across its placement's horizontal axis.</summary>
    public bool MirrorY { get; set; }

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
            Filter = Filter,
            MaskFilter = MaskFilter,
            Palette = (byte[])Palette.Clone(),
            Mask = (byte[])Mask.Clone(),
            Placement = Placement,
            MirrorX = MirrorX,
            MirrorY = MirrorY,
            Decode = Decode is null ? null : (double[])Decode.Clone(),
            ColourKey = ColourKey is null ? null : (double[])ColourKey.Clone(),
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
        if (x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight)
        {
            return ColorRgb.Black;
        }

        switch (ColorSpace)
        {
            case ImageColorSpace.Gray:
                double g = SampleAt(x, y, 0);
                return new ColorRgb(g, g, g);

            case ImageColorSpace.Cmyk:
                double c = SampleAt(x, y, 0);
                double m = SampleAt(x, y, 1);
                double yl = SampleAt(x, y, 2);
                double k = SampleAt(x, y, 3);
                return new ColorRgb((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - yl) * (1 - k));

            case ImageColorSpace.Indexed:
                // The sample is a palette index, not an intensity, so it is the raw value
                // that is wanted rather than the scaled one.
                int entry = RawSampleAt(x, y, 0) * 3;
                return entry + 2 < Palette.Length
                    ? new ColorRgb(Palette[entry] / 255.0, Palette[entry + 1] / 255.0,
                        Palette[entry + 2] / 255.0)
                    : ColorRgb.Black;

            default:
                return new ColorRgb(
                    SampleAt(x, y, 0), SampleAt(x, y, 1), SampleAt(x, y, 2));
        }
    }

    /// <summary>
    /// One component of one pixel, normalised to 0..1.
    ///
    /// PDF packs sub-byte samples: a 1, 2 or 4 bit image holds several components in a
    /// byte, most significant first. Reading those as one byte per component does not
    /// merely round the picture, it reads the wrong pixel entirely — so a 1-bit scan, a
    /// logo or a fax came out as nothing at all.
    /// </summary>
    public double SampleAt(int x, int y, int component = 0)
    {
        int max = (1 << BitsPerComponent) - 1;
        if (BitsPerComponent == 16)
        {
            max = 65535;
        }

        if (max <= 0)
        {
            return 0.0;
        }

        double value = (double)RawSampleAt(x, y, component) / max;

        // The file's decode maps the stored value onto the range it stands for, which is
        // how an image is inverted. A short array repeats its last pair.
        if (Decode is { Length: >= 2 } decode)
        {
            int at = Math.Min(component * 2, decode.Length - 2);
            value = decode[at] + (value * (decode[at + 1] - decode[at]));
        }

        return value;
    }

    /// <summary>
    /// The file's <c>/Decode</c> array, or null.
    ///
    /// A decode maps each component's stored value onto the 0..1 range it stands for, and
    /// it is how an image is inverted: <c>[1 0 1 0 1 0]</c> turns a photograph negative.
    /// The samples stay exactly as the file wrote them, so this is applied when a value is
    /// read rather than by rewriting them — and it is written back out, or the export
    /// would come out a negative of a negative.
    /// </summary>
    public double[]? Decode { get; set; }

    /// <summary>
    /// The file's colour key, as component min/max pairs, or null.
    ///
    /// Declared rather than resolved into coverage so the key survives a save and a
    /// reload: a resolved mask is 8-bit, and quantising a key at import would lose the
    /// exact range the file specified.
    /// </summary>
    public double[]? ColourKey { get; set; }

    /// <summary>
    /// Whether one pixel matches the file's colour key, and so does not paint.
    ///
    /// <c>/Mask [min max min max min max]</c> makes a colour transparent, which is how a
    /// logo drawn on a white card is placed over coloured artwork. The key is expressed in
    /// the same 0..1 range the components decode into, which is why this asks
    /// <see cref="SampleAt"/> rather than reading raw samples.
    /// </summary>
    public bool IsColourKeyed(int x, int y)
    {
        if (ColourKey is not { Length: >= 2 } key)
        {
            return false;
        }

        // The specification puts the key in the same range as the decoded components,
        // 0..1. Files in the wild write 0..255 instead — the same numbers as the samples
        // they were copied from — and a key of 255 read against a value of 1 matches
        // nothing, so the whole picture stays opaque and the transparent background the
        // file asked for never appears. A key that reaches past 1 is that mistake.
        double scale = key.Any(v => v > 1.0) ? 255.0 : 1.0;

        for (int component = 0; component < Components; component++)
        {
            int at = Math.Min(component * 2, key.Length - 2);
            double value = SampleAt(x, y, component);
            if (value < key[at] / scale || value > key[at + 1] / scale)
            {
                // One component outside its range is enough to keep the pixel: the key
                // covers pixels where EVERY component is in range.
                return false;
            }
        }

        return true;
    }

    /// <summary>One component of one pixel, as the integer the file stored.</summary>
    public int RawSampleAt(int x, int y, int component = 0)
    {
        if (x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight ||
            component < 0 || component >= Components)
        {
            return 0;
        }

        int index = ((y * PixelWidth) + x) * Components + component;

        switch (BitsPerComponent)
        {
            case 16:
            {
                int at = index * 2;
                return at + 1 < Samples.Length ? (Samples[at] << 8) | Samples[at + 1] : 0;
            }

            case 8:
                return index < Samples.Length ? Samples[index] : 0;

            case 1:
            case 2:
            case 4:
            {
                int bits = BitsPerComponent;
                int bitOffset = index * bits;
                int at = bitOffset >> 3;
                if (at >= Samples.Length)
                {
                    return 0;
                }

                int shift = 8 - bits - (bitOffset & 7);
                return (Samples[at] >> shift) & ((1 << bits) - 1);
            }

            default:
                return 0;
        }
    }

    /// <summary>
    /// Coverage of one pixel: nothing where the colour key matches, then the soft mask
    /// when present, opaque otherwise.
    /// </summary>
    public double CoverageAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight)
        {
            return 1.0;
        }

        if (IsColourKeyed(x, y))
        {
            return 0.0;
        }

        return HasMask ? Mask[(y * PixelWidth) + x] / 255.0 : 1.0;
    }
}
