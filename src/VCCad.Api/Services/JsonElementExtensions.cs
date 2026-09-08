using System.Text.Json;
using VCCad.Core.Model;

namespace VCCad.Api.Services;

/// <summary>
/// Convenience readers over a JSON-RPC params object. Every accessor throws the
/// JSON-RPC invalid-params error (-32602) with a message naming the missing key,
/// so transport layers do not need to duplicate validation.
/// </summary>
internal static class JsonElementExtensions
{
    public static Guid RequireGuid(this JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out Guid id))
        {
            throw new RpcException(-32602, $"Parameter '{key}' must be a document/item id (Guid).");
        }

        return id;
    }

    public static Guid? GetNullableGuid(this JsonElement element, string key)
        => element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? Guid.Parse(value.GetString()!)
            : null;

    public static string? GetString(this JsonElement element, string key)
        => element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static double GetDouble(this JsonElement element, string key, double fallback)
    {
        if (!element.TryGetProperty(key, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out double parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>Reads a required numeric parameter, or raises invalid-params.</summary>
    public static double GetDouble(this JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new RpcException(-32602, $"Parameter '{key}' (number) is required.");
        }

        return value.GetDouble();
    }

    /// <summary>Reads an optional colour as [r, g, b] over 0..255. Returns false when absent.</summary>
    public static bool TryGetColorArray(this JsonElement element, string key, out ColorRgb color)
    {
        color = ColorRgb.Black;
        if (!element.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var channels = new double[3];
        int count = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (count >= 3 || item.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            double channel = item.GetDouble();
            if (channel is < 0.0 or > 255.0)
            {
                throw new RpcException(-32602, $"Colour channel must be in 0..255 (got {channel}).");
            }

            channels[count++] = channel;
        }

        if (count != 3)
        {
            throw new RpcException(-32602, "Colour must be an [r, g, b] array of three channels.");
        }

        color = ColorRgb.FromBytes((byte)channels[0], (byte)channels[1], (byte)channels[2]);
        return true;
    }

    public static ColorRgb ParseColor(this JsonElement element, string key, ColorRgb fallback)
        => element.TryGetColorArray(key, out ColorRgb color) ? color : fallback;
}
