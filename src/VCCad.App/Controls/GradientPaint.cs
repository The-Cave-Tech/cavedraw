using Avalonia;
using Avalonia.Media;
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
public readonly record struct GradientGeometry(
    Point Start,
    Point End,
    Point Centre,
    double RadiusX,
    double RadiusY,
    double RotationDegrees)
{
    /// <summary>Maps the spec's normalised geometry onto the box the painted object occupies.</summary>
    public static GradientGeometry For(GradientSpec spec, Rect box)
    {
        static Point Map(Point2D point, Rect box) => new(
            box.X + (point.X * box.Width),
            box.Y + (point.Y * box.Height));

        return new GradientGeometry(
            Map(spec.Start, box),
            Map(spec.End, box),
            Map(spec.Center, box),
            Math.Abs(spec.RadiusX * box.Width),
            Math.Abs(spec.RadiusY * box.Height),
            spec.Rotation);
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
    /// The colour and opacity at a ramp position, blended the way the shader blends.
    ///
    /// The model has an evaluation of its own (<c>GradientSpec.Sample</c>), but its blend feeds
    /// byte-scaled channels into a 0..1 colour, so anything that samples a ramp for display -
    /// adding a stop where the person clicked, for instance - has to come through here.
    /// </summary>
    public static (ColorRgb Colour, double Opacity) Sample(GradientSpec spec, double t)
    {
        IReadOnlyList<ModelStop> stops = spec.Normalised();
        if (stops.Count == 0)
        {
            return (ColorRgb.White, 1.0);
        }

        if (stops.Count == 1 || t <= stops[0].Position)
        {
            return (stops[0].Color, stops[0].Opacity);
        }

        if (t >= stops[^1].Position)
        {
            return (stops[^1].Color, stops[^1].Opacity);
        }

        for (int i = 0; i < stops.Count - 1; i++)
        {
            ModelStop a = stops[i];
            ModelStop b = stops[i + 1];
            if (t < a.Position || t > b.Position)
            {
                continue;
            }

            double span = b.Position - a.Position;
            if (span <= 1e-9)
            {
                return (b.Color, b.Opacity);
            }

            double u = (t - a.Position) / span;
            double midpoint = Math.Clamp(a.Midpoint, 1e-6, 1.0 - 1e-6);
            if (Math.Abs(midpoint - 0.5) > 1e-9)
            {
                u = Math.Pow(u, Math.Log(0.5) / Math.Log(midpoint));
            }

            return (Blend(a.Color, b.Color, u), a.Opacity + ((b.Opacity - a.Opacity) * u));
        }

        return (stops[^1].Color, stops[^1].Opacity);
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

        if (spec.Kind == GradientKind.Radial && geometry.IsRadial)
        {
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(geometry.Centre, RelativeUnit.Absolute),
                GradientOrigin = new RelativePoint(geometry.Centre, RelativeUnit.Absolute),
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

    private static SKShader Radial(
        GradientSpec spec,
        GradientGeometry geometry,
        SKColor[] colours,
        float[] positions,
        SKShaderTileMode tile)
    {
        double radius = Math.Max(geometry.RadiusX, geometry.RadiusY);
        SKShader shader = SKShader.CreateRadialGradient(
            ToSkia(geometry.Centre), (float)radius, colours, positions, tile);

        double scaleX = geometry.RadiusX / radius;
        double scaleY = geometry.RadiusY / radius;
        bool circular = Math.Abs(scaleX - 1.0) < 1e-9 && Math.Abs(scaleY - 1.0) < 1e-9;
        if (circular && Math.Abs(geometry.RotationDegrees) < 1e-9)
        {
            return shader;
        }

        // The shader is a circle; the local matrix is what turns it into the ellipse the model
        // describes. A local matrix maps a device point back into the shader's own space, so the
        // matrix itself carries the radii RATIOS: a radius half the other must give a matrix that
        // stretches the short axis by two on the way back, which is what squashes it on screen.
        float cx = (float)geometry.Centre.X;
        float cy = (float)geometry.Centre.Y;
        SKMatrix matrix = SKMatrix.CreateTranslation(cx, cy);
        if (Math.Abs(geometry.RotationDegrees) > 1e-9)
        {
            matrix = matrix.PreConcat(SKMatrix.CreateRotationDegrees((float)geometry.RotationDegrees));
        }

        matrix = matrix.PreConcat(SKMatrix.CreateScale((float)scaleX, (float)scaleY));
        matrix = matrix.PreConcat(SKMatrix.CreateTranslation(-cx, -cy));
        return shader.WithLocalMatrix(matrix);
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
