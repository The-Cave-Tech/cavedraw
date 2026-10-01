using System.Runtime.CompilerServices;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Pdf;

/// <summary>
/// A scanline coverage rasteriser, in managed code.
///
/// The exporter draws vectors, and a filter is not a vector: a blur, a composite's arithmetic or a blend has no
/// geometry that draws it. So a filtered object is drawn into pixels here, the filter graph runs over those
/// pixels, and the answer is placed as an image - which is the only way PDF can carry one without a soft mask,
/// and the same route the canvas takes (<c>FilterRenderer</c>).
///
/// **Why its own rasteriser rather than a drawing toolkit.** <c>VCCad.Pdf</c> is loaded by the browser host and
/// has no native dependency on purpose, so a picture cannot be produced by asking Skia. What a filter needs is
/// also a small subset of what a renderer does: fill and stroke polygons, with coverage. That is what this is,
/// and it is the same flattened geometry the rest of the exporter already writes
/// (<see cref="Core.Model.PathFlattener"/> and <see cref="Core.Model.StrokeOutlineBuilder"/>), so the pixels and
/// the vectors cannot disagree about where the shape is.
///
/// Coverage is sampled **2x2 per pixel**, which is what gives a soft edge: one sample is a hard staircase, and
/// more than four costs four times the fill work for a difference a filter then blurs away.
/// </summary>
internal static class VectorRasteriser
{
    private const int SubSamples = 4;

    /// <summary>
    /// A rectangle of coverage, 0..1 per pixel, row-major from the top left.
    ///
    /// Kept apart from the colour and the alpha on purpose. A shape's own subpaths overlap, and coverage written
    /// as colour would let the overlap paint twice and come out darker along a seam; a mask is a property of the
    /// shape and is composited once.
    /// </summary>
    internal sealed class Mask
    {
        public Mask(int width, int height)
        {
            Width = width;
            Height = height;
            Coverage = new float[width * height];
        }

        public int Width { get; }

        public int Height { get; }

        public float[] Coverage { get; }

        public float Max { get; private set; }

        /// <summary>Whether anything at all was covered.</summary>
        public bool Any => Max > 0f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Cover(int x, int y, float value)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            {
                return;
            }

            int at = (y * Width) + x;

            // Clamped, because a coverage is a fraction of a pixel: a contour that crosses itself under the
            // nonzero rule lays two spans over the same pixel, and their sum is not twice as opaque.
            float sum = Math.Min(1f, Coverage[at] + value);
            Coverage[at] = sum;
            if (sum > Max)
            {
                Max = sum;
            }
        }
    }

    /// <summary>One polygon, in raster pixels.</summary>
    internal readonly struct Polygon
    {
        public Polygon(IReadOnlyList<Point2D> points, bool closed)
        {
            Points = points;
            Closed = closed;
        }

        public IReadOnlyList<Point2D> Points { get; }

        public bool Closed { get; }
    }

    /// <summary>
    /// Fills polygons into a mask under a fill rule.
    ///
    /// Even-odd and nonzero are both honoured because they are genuinely different pictures - an annulus is a
    /// disc under one and a ring under the other - and the model carries the rule for exactly that reason.
    /// </summary>
    public static void Fill(Mask mask, IReadOnlyList<Polygon> polygons, FillRule rule)
    {
        int sub = SubSamples;
        float weight = 1f / sub;
        var crossings = new List<(double X, int Winding)>();

        for (int y = 0; y < mask.Height; y++)
        {
            for (int s = 0; s < sub; s++)
            {
                double scanY = y + ((s + 0.5) / sub);
                crossings.Clear();

                foreach (Polygon polygon in polygons)
                {
                    int count = polygon.Points.Count;
                    if (count < 2)
                    {
                        continue;
                    }

                    int last = polygon.Closed ? count : count - 1;
                    for (int i = 0; i < last; i++)
                    {
                        Point2D a = polygon.Points[i];
                        Point2D b = polygon.Points[(i + 1) % count];
                        if (a.Y == b.Y)
                        {
                            continue; // horizontal edges cross no scanline
                        }

                        double top = Math.Min(a.Y, b.Y);
                        double bottom = Math.Max(a.Y, b.Y);
                        if (scanY < top || scanY >= bottom)
                        {
                            continue;
                        }

                        double t = (scanY - a.Y) / (b.Y - a.Y);

                        // +1 for a downward edge. Model space has y growing **down**, which is the opposite of
                        // the convention the nonzero rule is usually written in, and taking the usual sign fills
                        // every clockwise contour - which is every rectangle - with nothing at all.
                        crossings.Add((a.X + (t * (b.X - a.X)), b.Y > a.Y ? -1 : 1));
                    }
                }

                if (crossings.Count < 2)
                {
                    continue;
                }

                crossings.Sort(static (a, b) => a.X.CompareTo(b.X));
                int winding = 0;

                for (int i = 0; i < crossings.Count - 1; i++)
                {
                    // Each span runs from a crossing to the next, so the winding is updated *before* the test:
                    // it describes the span that starts here, not the one that ended.
                    winding += crossings[i].Winding;
                    bool inside = rule == FillRule.EvenOdd ? (i % 2) == 0 : winding != 0;
                    if (inside)
                    {
                        Accumulate(mask, y, crossings[i].X, crossings[i + 1].X, weight);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Adds one horizontal span's overlap with each pixel it touches, weighted for the sub-scanline it is on.
    ///
    /// The horizontal extent is **exact** - a span ending a third of the way into a pixel covers a third of it -
    /// and the vertical resolution is the sub-sampling above, so each of the <c>SubSamples</c> scanlines through
    /// a pixel contributes its share. Coverage **adds up** rather than being taken as a maximum: the spans of one
    /// scanline do not overlap, so the interior of a shape reaches 1, and taking the largest would leave it at
    /// the fraction a single scanline accounts for.
    /// </summary>
    private static void Accumulate(
        Mask mask, int y, double from, double to, float weight)
    {
        if (to <= from)
        {
            return;
        }

        int first = Math.Max(0, (int)Math.Floor(from));
        int last = Math.Min(mask.Width - 1, (int)Math.Ceiling(to) - 1);

        for (int x = first; x <= last; x++)
        {
            double overlap = Math.Min(to, x + 1.0) - Math.Max(from, (double)x);
            if (overlap <= 0)
            {
                continue;
            }

            mask.Cover(x, y, (float)(overlap * weight));
        }
    }

    /// <summary>
    /// Paints a mask over a buffer in a colour at an alpha.
    ///
    /// Source-over, which is what every renderer does and what makes a stroke land on top of the fill it belongs
    /// to. Colour is stored **straight**, so the alpha is carried alongside it rather than multiplied into it -
    /// multiplying is what the engine's own compositing does at the point it needs to.
    /// </summary>
    public static void Composite(
        Core.Raster.FilterBuffer buffer,
        Mask mask,
        ColorRgb colour,
        double alpha)
    {
        float a0 = (float)Math.Clamp(alpha, 0.0, 1.0);
        if (a0 <= 0f)
        {
            return;
        }

        for (int y = 0; y < mask.Height && y < buffer.Height; y++)
        {
            for (int x = 0; x < mask.Width && x < buffer.Width; x++)
            {
                float coverage = mask.Coverage[(y * mask.Width) + x];
                if (coverage <= 0.0005f)
                {
                    continue;
                }

                float sa = coverage * a0;
                (float dr, float dg, float db, float da) = buffer.Get(x, y);
                float inverse = 1f - sa;

                buffer.Set(
                    x,
                    y,
                    ((float)colour.R * sa) + (dr * inverse),
                    ((float)colour.G * sa) + (dg * inverse),
                    ((float)colour.B * sa) + (db * inverse),
                    sa + (da * inverse));
            }
        }
    }
}
