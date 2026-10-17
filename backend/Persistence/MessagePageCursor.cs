using System.Globalization;
using System.Text;

namespace AgentHub.Api.Persistence;

public sealed record MessagePageCursor(DateTime CreatedAt, string Id)
{
    public static string Encode(SessionMessageRecord message)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            message.CreatedAt.ToString("O", CultureInfo.InvariantCulture) + "|" + message.Id));

    public static bool TryParse(string? value, out MessagePageCursor? cursor)
    {
        cursor = null;
        if (value is null) return true;
        if (value.Length is 0 or > 256) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('|');
            if (parts.Length != 2 || parts[1].Length is 0 or > 128 ||
                !DateTime.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var created) || created.Kind != DateTimeKind.Utc)
                return false;
            cursor = new MessagePageCursor(created, parts[1]);
            return true;
        }
        catch (FormatException) { return false; }
    }
}
