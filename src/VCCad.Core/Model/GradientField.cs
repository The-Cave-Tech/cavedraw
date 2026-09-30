using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// The gradients that have to be sampled rather than expressed as a PDF shading.
///
/// Free-form shadings exist in PDF (`/ShadingType` 4 and the lattice types 5-7) but they are meshes:
/// expressing our freeform points and lines through them means triangulating the field and sampling
/// at the vertices, which is a second definition of the field to keep in step with the first. A
/// **conical** gradient is worse: PDF has no sweep shading type at all, so there is nothing to
/// translate it into.
///
/// Both are therefore sampled from one definition, here, in the model layer - `VCCad.Pdf` sits above
/// Core and has no rendering toolkit to rasterise with - so the canvas, the exporter and anything
/// later all paint the same field, which is the rule the rest of the gradient code follows.
///
/// The shape sampled is a **parallelogram**, not a rectangle: the picture is drawn through the
/// object's own placement matrix, so a rotated or sheared object needs its field sampled in the same
/// parallelogram it is drawn in, or the field and the art disagree.
/// </summary>
public static class GradientField
{
    /// <summary>Whether this kind has to be sampled, rather than written as a shading.</summary>
    public static bool NeedsSampling(GradientSpec spec)
        => spec.Kind is GradientKind.Freeform or GradientKind.Conical;

    /// <summary>
    /// The colour at a normalised position inside a parallelogram: <paramref name="origin"/> plus
    /// <paramref name="u"/> along one edge and <paramref name="v"/> along the other, both in
    /// **artboard space**.
    ///
    /// Artboard space is the one space both sampled kinds can be asked in: a freeform gradient's
    /// colour points are stored artboard-relative (the single piece of gradient geometry that is),
    /// and a conical gradient's centre is a fraction of the object's box.
    ///
    /// Returns null for a kind this does not sample, and for a freeform gradient with nothing in it.
    /// </summary>
    public static (ColorRgb Colour, double Opacity)? Sample(
        GradientSpec spec, Point2D origin, Vector2D u, Vector2D v, double fu, double fv)
    {
        double x = origin.X + (fu * u.X) + (fv * v.X);
        double y = origin.Y + (fu * u.Y) + (fv * v.Y);

        switch (spec.Kind)
        {
            case GradientKind.Freeform:
                return spec.SampleAt(new Point2D(x, y));

            case GradientKind.Conical:
            {
                double cx = origin.X + (spec.Center.X * u.X) + (spec.Center.Y * v.X);
                double cy = origin.Y + (spec.Center.X * u.Y) + (spec.Center.Y * v.Y);

                // Screen space has y running down, so the angle grows clockwise; the ramp's own
                // angle is measured the same way, which is what makes an exported sweep match the
                // one on screen.
                double degrees = Math.Atan2(y - cy, x - cx) * 180.0 / Math.PI;
                double t = (((degrees - spec.Angle) % 360.0) + 360.0) % 360.0 / 360.0;
                return spec.Sample(t);
            }

            default:
                return null;
        }
    }

    /// <summary>The same question asked of an axis-aligned box, which is the common case.</summary>
    public static (ColorRgb Colour, double Opacity)? Sample(GradientSpec spec, Rect2D box, double u, double v)
        => Sample(spec, new Point2D(box.X, box.Y), new Vector2D(box.Width, 0), new Vector2D(0, box.Height), u, v);

    /// <summary>
    /// Samples a gradient over a parallelogram into RGB bytes, row-major with the top row first.
    ///
    /// <paramref name="grid"/> is the resolution across the longer edge and the shorter edge follows
    /// it, so a long thin object is not sampled coarsely along its length. Alpha is not carried: a PDF
    /// image has no alpha channel, and the exporter already reports that stop opacity is not written.
    ///
    /// <paramref name="firstRowAtOrigin"/> says which edge the FIRST row of the raster belongs to.
    /// It is true for a bitmap drawn with its top-left at <paramref name="origin"/>, which is how the
    /// canvas uses this. A PDF image is the other way up inside its own unit square - its first row
    /// is drawn along the <c>+v</c> edge - so the exporter asks for false, and the field comes out the
    /// way up the page it was drawn. Getting this wrong does not fail: it renders the gradient upside
    /// down, which is exactly the kind of thing only a second renderer catches.
    /// </summary>
    public static (byte[] Rgb, int Width, int Height)? Rgb(
        GradientSpec spec, Point2D origin, Vector2D u, Vector2D v, int grid = 128, bool firstRowAtOrigin = true)
    {
        double edgeU = Math.Sqrt((u.X * u.X) + (u.Y * u.Y));
        double edgeV = Math.Sqrt((v.X * v.X) + (v.Y * v.Y));

        if (!NeedsSampling(spec) || edgeU <= 1e-6 || edgeV <= 1e-6 || grid < 2)
        {
            return null;
        }

        int width = grid;
        int height = grid;
        if (edgeU >= edgeV)
        {
            height = Math.Max(2, (int)Math.Round(grid * (edgeV / edgeU)));
        }
        else
        {
            width = Math.Max(2, (int)Math.Round(grid * (edgeU / edgeV)));
        }

        var rgb = new byte[width * height * 3];

        for (int py = 0; py < height; py++)
        {
            double row = (py + 0.5) / height;
            double fv = firstRowAtOrigin ? row : 1.0 - row;

            for (int px = 0; px < width; px++)
            {
                double fu = (px + 0.5) / width;

                if (Sample(spec, origin, u, v, fu, fv) is not { } sample)
                {
                    return null;
                }

                int at = (((py * width) + px) * 3);
                rgb[at] = Byte(sample.Colour.R);
                rgb[at + 1] = Byte(sample.Colour.G);
                rgb[at + 2] = Byte(sample.Colour.B);
            }
        }

        return (rgb, width, height);
    }

    /// <summary>The same raster for an axis-aligned box, which is the common case.</summary>
    public static (byte[] Rgb, int Width, int Height)? Rgb(GradientSpec spec, Rect2D box, int grid = 128)
        => Rgb(spec, new Point2D(box.X, box.Y), new Vector2D(box.Width, 0), new Vector2D(0, box.Height), grid);

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
