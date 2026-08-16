using System.Text;
using System.Text.Json;

namespace Lurp.Storage;

internal static class CursorUtils
{
    public static string EncodeBase64<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static T? TryDecodeBase64<T>(string encoded) where T : class
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }
}