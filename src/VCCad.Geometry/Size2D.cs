namespace VCCad.Geometry;

/// <summary>
/// A 2D extent — width and height with no position. Used for sizes of artboards,
/// viewports and other measurements that float freely of an origin.
/// </summary>
public readonly record struct Size2D(double Width, double Height)
{
    /// <summary>The empty size (0, 0).</summary>
    public static Size2D Empty { get; } = new(0.0, 0.0);

    /// <summary>Area of the rectangle this size spans.</summary>
    public double Area => Width * Height;

    /// <summary>True when either dimension is non-positive (degenerate size).</summary>
    public bool IsDegenerate => Width <= 0.0 || Height <= 0.0;

    /// <summary>Scale factor mapping this size onto <paramref name="other"/> uniformly
    /// (returns 1 when both sizes match). Guarded to avoid division by zero.</summary>
    public double UniformScaleTo(Size2D other)
    {
        if (IsDegenerate || other.IsDegenerate)
        {
            return 1.0;
        }

        return Math.Min(other.Width / Width, other.Height / Height);
    }
}
