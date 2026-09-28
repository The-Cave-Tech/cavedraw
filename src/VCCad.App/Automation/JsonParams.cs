using System.Globalization;
using System.Text.Json;
using VCCad.Core.Model;

namespace VCCad.App.Automation;

/// <summary>
/// Lenient readers for operation parameters.
///
/// Automation callers are machines and models: a missing key must mean "use the
/// default", never a crash, and an <c>undefined</c> parameter object (an operation
/// invoked with no arguments) must behave like an empty object. Every reader here
/// tolerates both.
/// </summary>
public static class JsonParamsExtensions
{
    /// <summary>Reads a string member, or null when absent/null.</summary>
    public static string? GetString(this JsonElement p, string name)
        => Has(p, name) && p.GetProperty(name).ValueKind == JsonValueKind.String
            ? p.GetProperty(name).GetString()
            : null;

    /// <summary>Reads a number member, or <paramref name="fallback"/>.</summary>
    public static double GetDouble(this JsonElement p, string name, double fallback = 0)
    {
        if (!Has(p, name))
        {
            return fallback;
        }

        JsonElement value = p.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>Reads an integer member, or <paramref name="fallback"/>.</summary>
    public static long GetLong(this JsonElement p, string name, long fallback = 0)
        => (long)Math.Round(p.GetDouble(name, fallback));

    /// <summary>Reads a boolean member, or <paramref name="fallback"/>.</summary>
    public static bool GetBool(this JsonElement p, string name, bool fallback = false)
    {
        if (!Has(p, name))
        {
            return fallback;
        }

        JsonElement value = p.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>Reads a required GUID member. The Guid type comes through as a string.</summary>
    public static Guid RequireGuid(this JsonElement p, string name)
        => TryGetGuid(p, name, out Guid value)
            ? value
            : throw new EditorOperationException($"Parameter '{name}' must be a GUID.");

    /// <summary>Reads a GUID member.</summary>
    public static bool TryGetGuid(this JsonElement p, string name, out Guid value)
    {
        value = Guid.Empty;
        string? text = p.GetString(name);
        return !string.IsNullOrWhiteSpace(text) && Guid.TryParse(text, out value);
    }

    /// <summary>Reads a required array of GUIDs.</summary>
    public static Guid[] GetGuidArray(this JsonElement p, string name)
        => TryGetGuidArray(p, name, out Guid[] ids)
            ? ids
            : throw new EditorOperationException($"Parameter '{name}' must be an array of GUIDs.");

    /// <summary>Reads an array of GUIDs.</summary>
    public static bool TryGetGuidArray(this JsonElement p, string name, out Guid[] ids)
    {
        ids = Array.Empty<Guid>();
        if (!Has(p, name) || p.GetProperty(name).ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<Guid>();
        foreach (JsonElement entry in p.GetProperty(name).EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && Guid.TryParse(entry.GetString(), out Guid id))
            {
                parsed.Add(id);
            }
        }

        ids = parsed.ToArray();
        return true;
    }

    /// <summary>Reads an <c>[r,g,b]</c> colour with 0-255 components.</summary>
    public static bool TryGetColorArray(this JsonElement p, string name, out ColorRgb color)
    {
        color = ColorRgb.Black;
        if (!Has(p, name) || p.GetProperty(name).ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        double[] parts = p.GetProperty(name).EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number)
            .Select(e => e.GetDouble())
            .ToArray();

        switch (parts.Length)
        {
            case 3:
                // [r,g,b] with 0-255 components.
                color = ColorRgb.FromBytes(Byte(parts[0]), Byte(parts[1]), Byte(parts[2]));
                return true;
            case 4:
                // [c,m,y,k] with 0-1 components.
                double k = Math.Clamp(parts[3], 0, 1);
                color = ColorRgb.FromBytes(
                    Byte(255 * (1 - Math.Clamp(parts[0], 0, 1)) * (1 - k)),
                    Byte(255 * (1 - Math.Clamp(parts[1], 0, 1)) * (1 - k)),
                    Byte(255 * (1 - Math.Clamp(parts[2], 0, 1)) * (1 - k)));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads a colour, falling back when absent.</summary>
    public static ColorRgb ParseColor(this JsonElement p, string name, ColorRgb fallback)
        => p.TryGetColorArray(name, out ColorRgb color) ? color : fallback;

    private static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static bool Has(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out _);
}
