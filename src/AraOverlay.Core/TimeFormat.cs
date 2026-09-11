using System.Globalization;

namespace AraOverlay.Core;

/// <summary>
/// Lap times are authored in challenges.json as human strings ("1:23.456") so the
/// file stays hand-editable, and rendered back the same way for the overlay.
/// </summary>
public static class TimeFormat
{
    /// <summary>Parses "M:SS.fff" or plain seconds into seconds. Throws on anything else.</summary>
    public static double Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Lap time is empty.");

        var parts = text.Trim().Split(':');
        if (parts.Length > 2)
            throw new FormatException($"Lap time '{text}' has too many ':' separators.");

        var secondsText = parts[^1];
        if (!double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            throw new FormatException($"Lap time '{text}' has an invalid seconds component.");

        if (parts.Length == 1)
            return seconds;

        if (seconds >= 60)
            throw new FormatException($"Lap time '{text}' has a seconds component of 60 or more.");

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes < 0)
            throw new FormatException($"Lap time '{text}' has an invalid minutes component.");

        return minutes * 60 + seconds;
    }

    /// <summary>Renders seconds as "M:SS.fff".</summary>
    public static string Format(double seconds)
    {
        // Round to milliseconds first, so 119.9996 becomes 2:00.000 rather than 1:60.000.
        var total = Math.Round(seconds, 3, MidpointRounding.AwayFromZero);
        var minutes = (int)(total / 60);
        var rest = total - minutes * 60;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00.000}", minutes, rest);
    }
}
