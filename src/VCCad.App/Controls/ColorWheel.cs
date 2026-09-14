using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VCCad.Core.Model;

namespace VCCad.App.Controls;

/// <summary>
/// A Photoshop-style circular colour picker: hue runs around the ring, saturation
/// from the centre outwards, and value (brightness) is a separate property. The
/// full spectrum is reachable by clicking/dragging anywhere in the disc; a ring
/// marks the current position.
/// </summary>
public sealed class ColorWheel : Control
{
    private const int BitmapSize = 256;

    private static WriteableBitmap? _wheelBitmap;

    private double _hue;          // 0..360
    private double _saturation;   // 0..1
    private double _value = 1.0;  // 0..1
    private bool _dragging;

    /// <summary>Raised whenever the selected colour changes (from the wheel).</summary>
    public event EventHandler? ColorChanged;

    /// <summary>Value/brightness in 0..1 (kept separate from the disc).</summary>
    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            InvalidateVisual();
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The current colour.</summary>
    public ColorRgb Color => HsvToRgb(_hue, _saturation, _value);

    /// <summary>Sets the colour (updating hue/saturation/value).</summary>
    public void SetColor(ColorRgb color)
    {
        (double h, double s, double v) = RgbToHsv(color);
        _hue = h;
        _saturation = s;
        _value = v;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 1)
        {
            return;
        }

        double radius = size / 2 - 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var dest = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);

        WriteableBitmap wheel = GetWheelBitmap();
        context.DrawImage(wheel, new Rect(0, 0, BitmapSize, BitmapSize), dest);

        // Value darkening overlay.
        if (_value < 1.0)
        {
            byte alpha = (byte)Math.Round((1.0 - _value) * 255);
            context.DrawEllipse(new SolidColorBrush(Avalonia.Media.Color.FromArgb(alpha, 0, 0, 0)), null, center, radius, radius);
        }

        // Current-position marker.
        double angle = _hue * Math.PI / 180.0;
        double markerRadius = _saturation * radius;
        var marker = new Point(center.X + Math.Cos(angle) * markerRadius, center.Y + Math.Sin(angle) * markerRadius);
        context.DrawEllipse(null, new Pen(Brushes.Black, 2), marker, 6, 6);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.5), marker, 5, 5);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            e.Pointer.Capture(this);
            UpdateFromPoint(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            UpdateFromPoint(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void UpdateFromPoint(Point point)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        double radius = Math.Max(1, size / 2 - 2);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        double dx = point.X - center.X;
        double dy = point.Y - center.Y;
        _saturation = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / radius, 0, 1);
        _hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (_hue < 0)
        {
            _hue += 360;
        }

        InvalidateVisual();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Builds (once) the full-saturation hue/saturation wheel bitmap.</summary>
    private static WriteableBitmap GetWheelBitmap()
    {
        if (_wheelBitmap is not null)
        {
            return _wheelBitmap;
        }

        var bitmap = new WriteableBitmap(new PixelSize(BitmapSize, BitmapSize), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        double center = BitmapSize / 2.0;
        double radius = center - 1;

        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            unsafe
            {
                byte* basePtr = (byte*)buffer.Address;
                for (int y = 0; y < BitmapSize; y++)
                {
                    byte* row = basePtr + y * buffer.RowBytes;
                    for (int x = 0; x < BitmapSize; x++)
                    {
                        double dx = x - center;
                        double dy = y - center;
                        double distance = Math.Sqrt(dx * dx + dy * dy);
                        byte* px = row + x * 4;

                        if (distance > radius)
                        {
                            px[0] = px[1] = px[2] = px[3] = 0;
                            continue;
                        }

                        double hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                        if (hue < 0)
                        {
                            hue += 360;
                        }

                        double sat = distance / radius;
                        ColorRgb rgb = HsvToRgb(hue, sat, 1.0);
                        px[0] = (byte)Math.Round(rgb.B * 255); // B
                        px[1] = (byte)Math.Round(rgb.G * 255); // G
                        px[2] = (byte)Math.Round(rgb.R * 255); // R
                        px[3] = 255;
                    }
                }
            }
        }

        _wheelBitmap = bitmap;
        return bitmap;
    }

    // ---- HSV <-> RGB -----------------------------------------------------

    private static ColorRgb HsvToRgb(double hue, double sat, double val)
    {
        double c = val * sat;
        double h = hue / 60.0;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        double m = val - c;

        (double r, double g, double b) = h switch
        {
            >= 0 and < 1 => (c, x, 0.0),
            >= 1 and < 2 => (x, c, 0.0),
            >= 2 and < 3 => (0.0, c, x),
            >= 3 and < 4 => (0.0, x, c),
            >= 4 and < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return new ColorRgb(r + m, g + m, b + m);
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(ColorRgb color)
    {
        double r = Math.Clamp(color.R, 0, 1);
        double g = Math.Clamp(color.G, 0, 1);
        double b = Math.Clamp(color.B, 0, 1);
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 1e-9)
        {
            if (max == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                hue = 60 * ((b - r) / delta + 2);
            }
            else
            {
                hue = 60 * ((r - g) / delta + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        double saturation = max <= 1e-9 ? 0 : delta / max;
        return (hue, saturation, max);
    }
}
