using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using VCCad.Core.Model;
using VCCad.Geometry;
using MediaGradientStop = Avalonia.Media.GradientStop;
using ModelStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Controls;

/// <summary>
/// Where a gradient's normalised geometry lands on the object being painted.
///
/// The model stores gradient geometry relative to the object's bounding box so a resize carries
/// the gradient with it. Rendering happens in the space the path is built in (the canvas builds
/// paths in world space), so the mapping is done per paint rather than cached on the model.
/// </summary>
/// <param name="Start">Linear start, in paint space.</param>
/// <param name="End">Linear end, in paint space.</param>
/// <param name="Centre">Radial centre, in paint space.</param>
/// <param name="RadiusX">Radial horizontal radius, in paint-space units.</param>
/// <param name="RadiusY">Radial vertical radius, in paint-space units.</param>
/// <param name="RotationDegrees">Rotation of an elliptical radial.</param>
/// <param name="Focus">Radial focus - where the highlight sits - in paint space, or null when the
/// gradient has none. Null is the model's own state rather than a missing coordinate: a gradient
/// with no focus paints the picture a gradient naming its centre paints.</param>
public readonly record struct GradientGeometry(
    Point Start,
    Point End,
    Point Centre,
    double RadiusX,
    double RadiusY,
    double RotationDegrees,
    Point? Focus = null)
{
    /// <summary>Maps the spec's normalised geometry onto the box the painted object occupies.</summary>
    public static GradientGeometry For(GradientSpec spec, Rect box)
    {
        return new GradientGeometry(
            Map(spec.Start, box),
            Map(spec.End, box),
            Map(spec.Center, box),
            Math.Abs(spec.RadiusX * box.Width),
            Math.Abs(spec.RadiusY * box.Height),
            spec.Rotation,
            ClampedFocus(spec, box));
    }

    private static Point Map(Point2D point, Rect box) => new(
        box.X + (point.X * box.Width),
        box.Y + (point.Y * box.Height));

    /// <summary>
    /// The focus in paint space, clamped into the ellipse, or null when the gradient has none.
    ///
    /// The clamp is measured in the ELLIPSE's own frame - the offset is turned back by the
    /// ellipse's rotation and measured in units of each radius - because that is the frame SVG's
    /// fx/fy and PDF's inner circle both live in. Measuring it against an axis-aligned ellipse of
    /// the same radii would move the focus of a rotated radial that is perfectly well inside it,
    /// and the canvas would paint a clamp nobody asked for.
    /// </summary>
    private static Point? ClampedFocus(GradientSpec spec, Rect box)
    {
        if (spec.FocalPoint is not { } focal)
        {
            return null;
        }

        Point centre = Map(spec.Center, box);
        Point point = Map(focal, box);
        double radiusX = Math.Abs(spec.RadiusX * box.Width);
        double radiusY = Math.Abs(spec.RadiusY * box.Height);

        // A degenerate radial has no interior, so nothing is inside it - not even a focus it names.
        if (!(radiusX > 0) || !(radiusY > 0))
        {
            return null;
        }

        double radians = spec.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double offsetX = point.X - centre.X;
        double offsetY = point.Y - centre.Y;

        double alongX = ((offsetX * cos) + (offsetY * sin)) / radiusX;
        double alongY = ((-offsetX * sin) + (offsetY * cos)) / radiusY;
        double length = Math.Sqrt((alongX * alongX) + (alongY * alongY));

        if (double.IsFinite(length) && length > 1.0)
        {
            double scale = 1.0 / length;
            alongX *= scale;
            alongY *= scale;
            point = new Point(
                centre.X + ((alongX * radiusX * cos) - (alongY * radiusY * sin)),
                centre.Y + ((alongX * radiusX * sin) + (alongY * radiusY * cos)));
        }

        // A focus on the centre is the picture a concentric gradient paints, and the model keeps
        // that state as null rather than as a coordinate.
        return Near(point, centre) ? null : point;
    }

    /// <summary>A linear gradient needs two distinct ends to have a direction at all.</summary>
    public bool IsLinear => !Near(Start, End);

    /// <summary>A radial gradient needs a non-degenerate radius.</summary>
    public bool IsRadial => RadiusX > 1e-9 && RadiusY > 1e-9;

    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;
}

/// <summary>
/// Turns a <see cref="GradientSpec"/> into something that can actually paint.
///
/// One place builds the stops - position, colour, and opacity folded into alpha - and both the
/// Skia shader and the Avalonia brush are built from that, so the pixels a test reads back from a
/// bare <c>SKSurface</c> are produced by the same stop maths the canvas paints with. Opacity is
/// multiplied in here rather than folded into the model colour, which is the model's rule: a stop
/// opacity is a separate channel from the colour's own alpha, and both apply.
/// </summary>
public static class GradientPaint
{
    /// <summary>Sub-stops inserted across a segment whose midpoint is biased away from 0.5.</summary>
    private const int MidpointSubdivisions = 8;

    /// <summary>
    /// The stops a shader can consume: positions ascending and colours with alpha already carrying
    /// the stop's opacity and the object's own opacity.
    ///
    /// A biased midpoint is a power curve, which a multi-stop shader cannot express directly, so a
    /// biased segment is subdivided into <see cref="MidpointSubdivisions"/> sampled stops. A normal
    /// ramp produces exactly the model's stops - hard edges (equal positions) and all.
    /// </summary>
    public static (float[] Positions, SKColor[] Colours) BuildStops(GradientSpec spec, double opacity)
    {
        IReadOnlyList<ModelStop> stops = Expanded(spec);
        var positions = new float[stops.Count];
        var colours = new SKColor[stops.Count];
        for (int i = 0; i < stops.Count; i++)
        {
            positions[i] = (float)Math.Clamp(stops[i].Position, 0.0, 1.0);
            colours[i] = ToSkia(stops[i].Color, stops[i].Opacity * opacity);
        }

        return (positions, colours);
    }

    /// <summary>
    /// The colour and opacity at a ramp position, sampled by the model itself so that the
    /// panel, the exporter and the shader cannot drift apart on what a ramp means.
    /// </summary>
    public static (ColorRgb Colour, double Opacity) Sample(GradientSpec spec, double t)
    {
        (ColorRgb Colour, double Opacity) sample = spec.Sample(t);
        return sample;
    }

    /// <summary>
    /// The shader for a gradient, or null when this kind cannot be expressed as one. Null is the
    /// caller's signal to fall back to <see cref="FillSpec.Color"/>, which the model guarantees is
    /// always meaningful even on a gradient.
    /// </summary>
    public static SKShader? CreateShader(GradientSpec spec, GradientGeometry geometry, double opacity)
    {
        (float[] positions, SKColor[] colours) = BuildStops(spec, opacity);
        if (colours.Length == 0)
        {
            return null;
        }

        SKShaderTileMode tile = spec.Spread switch
        {
            GradientSpread.Reflect => SKShaderTileMode.Mirror,
            GradientSpread.Repeat => SKShaderTileMode.Repeat,
            _ => SKShaderTileMode.Clamp,
        };

        return spec.Kind switch
        {
            GradientKind.Linear when geometry.IsLinear =>
                SKShader.CreateLinearGradient(
                    ToSkia(geometry.Start), ToSkia(geometry.End), colours, positions, tile),
            GradientKind.Radial when geometry.IsRadial =>
                Radial(spec, geometry, colours, positions, tile),

            // A sweep, turned to the ramp's start angle. The angle is a rotation of the shader's own
            // space, so it goes on as a local matrix about the centre rather than into the geometry.
            GradientKind.Conical =>
                SKShader.CreateSweepGradient(
                    ToSkia(geometry.Centre),
                    colours,
                    positions,
                    SKMatrix.CreateRotationDegrees(
                        (float)spec.Angle, (float)geometry.Centre.X, (float)geometry.Centre.Y))
                    .WithLocalMatrix(SKMatrix.CreateRotationDegrees(
                        (float)spec.Angle, (float)geometry.Centre.X, (float)geometry.Centre.Y)),
            _ => null,
        };
    }

    /// <summary>The paint-space shader for a gradient over a given object box.</summary>
    public static SKShader? CreateShader(GradientSpec spec, Rect box, double opacity)
        => CreateShader(spec, GradientGeometry.For(spec, box), opacity);

    /// <summary>
    /// The Avalonia brush the canvas paints with. Built from <see cref="BuildStops"/>, so it carries
    /// the same stops, the same spread and the same folded opacity as the shader.
    ///
    /// Coordinates are absolute in the paint space (the canvas pushes the world transform before
    /// painting), not relative to the geometry's bounds: a relative brush would keep an axis-aligned
    /// ramp and shear it away from a rotated object.
    /// </summary>
    public static IBrush? CreateBrush(GradientSpec spec, GradientGeometry geometry, double opacity)
    {
        (float[] positions, SKColor[] colours) = BuildStops(spec, opacity);
        if (colours.Length == 0)
        {
            return null;
        }

        var spread = spec.Spread switch
        {
            GradientSpread.Reflect => GradientSpreadMethod.Reflect,
            GradientSpread.Repeat => GradientSpreadMethod.Repeat,
            _ => GradientSpreadMethod.Pad,
        };

        var stops = new GradientStops();
        for (int i = 0; i < colours.Length; i++)
        {
            SKColor c = colours[i];
            stops.Add(new MediaGradientStop(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue), positions[i]));
        }

        if (spec.Kind == GradientKind.Linear && geometry.IsLinear)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(geometry.Start, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(geometry.End, RelativeUnit.Absolute),
                SpreadMethod = spread,
                GradientStops = stops,
            };
        }

        if (spec.Kind == GradientKind.Conical)
        {
            // A sweep around the centre, starting at the ramp's angle. This is the one gradient kind
            // a conic brush expresses exactly, so there is nothing to approximate.
            return new ConicGradientBrush
            {
                Center = new RelativePoint(geometry.Centre, RelativeUnit.Absolute),
                Angle = spec.Angle,
                SpreadMethod = spread,
                GradientStops = stops,
            };
        }

        if (spec.Kind == GradientKind.Radial && geometry.IsRadial)
        {
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(geometry.Centre, RelativeUnit.Absolute),

                // The origin is the focus: Avalonia's radial brush is a two-point conical whose
                // inner circle has radius zero, so an origin away from the centre IS the highlight
                // sitting off centre, and one on the centre is the concentric picture, unchanged.
                GradientOrigin = new RelativePoint(geometry.Focus ?? geometry.Centre, RelativeUnit.Absolute),
                RadiusX = new RelativeScalar(geometry.RadiusX, RelativeUnit.Absolute),
                RadiusY = new RelativeScalar(geometry.RadiusY, RelativeUnit.Absolute),
                SpreadMethod = spread,
                GradientStops = stops,
            };

            if (Math.Abs(geometry.RotationDegrees) > 1e-9)
            {
                Point c = geometry.Centre;
                Matrix rotate =
                    Matrix.CreateTranslation(-c.X, -c.Y) *
                    Matrix.CreateRotation(geometry.RotationDegrees * Math.PI / 180.0) *
                    Matrix.CreateTranslation(c.X, c.Y);
                brush.Transform = new MatrixTransform(rotate);
            }

            return brush;
        }

        return null;
    }

    /// <summary>The brush for a gradient over a given object box, or null if it cannot be painted.</summary>
    public static IBrush? CreateBrush(GradientSpec spec, Rect box, double opacity)
        => CreateBrush(spec, GradientGeometry.For(spec, box), opacity);

    /// <summary>
    /// A freeform gradient sampled over the object's box, as a bitmap ready to draw into that box.
    ///
    /// Freeform is not a ramp: the colour depends on WHERE a point is, not on how far along a line,
    /// so no shader can express it and the field is sampled into a small bitmap that is stretched
    /// over the object instead. That is honest about what freeform is - a smooth field, which a
    /// coarse grid reproduces well because there is nothing sharp in it to lose - and it means the
    /// canvas, the panel's legend and any future export all paint the one field the model defines.
    ///
    /// <paramref name="origin"/> is the artboard the object sits on: freeform colour points are
    /// stored artboard-relative, the one piece of gradient geometry that is, so the box (which is in
    /// world space) has to come back out of artboard space before the field is sampled.
    /// </summary>
    public static WriteableBitmap? CreateFreeformBitmap(GradientSpec spec, Rect box, Vector2D origin, double opacity)
    {
        const int Grid = 128;

        if (box.Width <= 1e-6 || box.Height <= 1e-6)
        {
            return null;
        }

        var pixels = new byte[Grid * Grid * 4];

        for (int y = 0; y < Grid; y++)
        {
            double worldY = box.Y + (((y + 0.5) / Grid) * box.Height);

            for (int x = 0; x < Grid; x++)
            {
                double worldX = box.X + (((x + 0.5) / Grid) * box.Width);

                if (spec.SampleAt(new Point2D(worldX - origin.X, worldY - origin.Y)) is not { } sample)
                {
                    return null;
                }

                int at = ((y * Grid) + x) * 4;
                pixels[at] = ToByte(sample.Color.B);
                pixels[at + 1] = ToByte(sample.Color.G);
                pixels[at + 2] = ToByte(sample.Color.R);
                pixels[at + 3] = ToByte(sample.Opacity * sample.Color.A * opacity);
            }
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(Grid, Grid),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            int stride = Grid * 4;
            if (buffer.RowBytes == stride)
            {
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
            }
            else
            {
                for (int y = 0; y < Grid; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        pixels, y * stride, buffer.Address + (y * buffer.RowBytes), stride);
                }
            }
        }

        return bitmap;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static SKShader Radial(
        GradientSpec spec,
        GradientGeometry geometry,
        SKColor[] colours,
        float[] positions,
        SKShaderTileMode tile)
    {
        double radius = Math.Max(geometry.RadiusX, geometry.RadiusY);
        SKPoint centre = ToSkia(geometry.Centre);

        // The local matrix that turns the shader's circle into the model's ellipse, or null when
        // the shader is already the ellipse: a circle with no rotation needs no correction.
        SKMatrix? ellipse = null;
        double scaleX = geometry.RadiusX / radius;
        double scaleY = geometry.RadiusY / radius;
        bool circular = Math.Abs(scaleX - 1.0) < 1e-9 && Math.Abs(scaleY - 1.0) < 1e-9;
        if (!circular || Math.Abs(geometry.RotationDegrees) > 1e-9)
        {
            // The shader is a circle; the local matrix is what turns it into the ellipse the model
            // describes. The matrix maps the shader's own space INTO device space, so it carries
            // the radii RATIOS: a radius half the other must give a matrix that squashes the short
            // axis by two, which is what the ratio does here.
            float cx = (float)geometry.Centre.X;
            float cy = (float)geometry.Centre.Y;
            SKMatrix matrix = SKMatrix.CreateTranslation(cx, cy);
            if (Math.Abs(geometry.RotationDegrees) > 1e-9)
            {
                matrix = matrix.PreConcat(SKMatrix.CreateRotationDegrees((float)geometry.RotationDegrees));
            }

            matrix = matrix.PreConcat(SKMatrix.CreateScale((float)scaleX, (float)scaleY));
            matrix = matrix.PreConcat(SKMatrix.CreateTranslation(-cx, -cy));
            ellipse = matrix;
        }

        SKPoint? focus = null;
        if (geometry.Focus is { } point)
        {
            SKPoint mapped = ToSkia(point);
            if (ellipse is { } matrix && matrix.TryInvert(out SKMatrix inverse))
            {
                // The shader's own geometry lives in the space the local matrix maps FROM, so an
                // elliptical radial has to bring the focus back through that matrix. Passing the
                // device point straight in would place the highlight on the mirror image of the
                // point the model names - which is still "a focus", and still wrong.
                mapped = inverse.MapPoint(mapped);
            }

            focus = mapped;
        }

        // A two-point conical with an inner radius of zero is PDF's type 3 shading: the ramp runs
        // from a point - the focus - out to the outer circle. With no focus the concentric radial
        // is the same picture, built the way it always was.
        SKShader shader = focus is { } start
            ? SKShader.CreateTwoPointConicalGradient(
                start, 0f, centre, (float)radius, colours, positions, tile)
            : SKShader.CreateRadialGradient(centre, (float)radius, colours, positions, tile);

        return ellipse is { } local ? shader.WithLocalMatrix(local) : shader;
    }

    /// <summary>
    /// The model's stops, with any midpoint-biased segment subdivided so the shader can express the
    /// bias. Blending is component-wise in RGB and in 0..1, which is what the model specifies:
    /// matching Illustrator means not blending in linear light.
    /// </summary>
    private static IReadOnlyList<ModelStop> Expanded(GradientSpec spec)
    {
        IReadOnlyList<ModelStop> stops = spec.Normalised();
        bool biased = false;
        for (int i = 0; i < stops.Count - 1 && !biased; i++)
        {
            biased = Math.Abs(stops[i].Midpoint - 0.5) > 1e-9;
        }

        if (stops.Count < 2 || !biased)
        {
            return stops;
        }

        var result = new List<ModelStop>((stops.Count - 1) * MidpointSubdivisions);
        for (int i = 0; i < stops.Count - 1; i++)
        {
            ModelStop a = stops[i];
            ModelStop b = stops[i + 1];
            result.Add(a);

            double span = b.Position - a.Position;
            if (span <= 1e-9 || Math.Abs(a.Midpoint - 0.5) <= 1e-9)
            {
                continue;
            }

            double exponent = Math.Log(0.5) / Math.Log(Math.Clamp(a.Midpoint, 1e-6, 1.0 - 1e-6));
            for (int k = 1; k < MidpointSubdivisions; k++)
            {
                double u = (double)k / MidpointSubdivisions;
                double eased = Math.Pow(u, exponent);
                result.Add(new ModelStop(
                    a.Position + (span * u),
                    Blend(a.Color, b.Color, eased),
                    a.Opacity + ((b.Opacity - a.Opacity) * eased)));
            }
        }

        result.Add(stops[^1]);
        return result;
    }

    /// <summary>Component-wise RGB blend over 0..1 channels.</summary>
    private static ColorRgb Blend(ColorRgb a, ColorRgb b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        static double Mix(double x, double y, double u) => Math.Clamp(x + ((y - x) * u), 0.0, 1.0);
        return new ColorRgb(
            Mix(a.R, b.R, t),
            Mix(a.G, b.G, t),
            Mix(a.B, b.B, t),
            Mix(a.A, b.A, t));
    }

    private static SKColor ToSkia(ColorRgb colour, double opacity)
    {
        static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255.0), 0, 255);

        double alpha = Math.Clamp(Math.Clamp(colour.A, 0.0, 1.0) * Math.Clamp(opacity, 0.0, 1.0), 0.0, 1.0);
        return new SKColor(ToByte(colour.R), ToByte(colour.G), ToByte(colour.B), ToByte(alpha));
    }

    private static SKPoint ToSkia(Point point) => new((float)point.X, (float)point.Y);
}
