using System.Globalization;

namespace AraOverlay.Core;

/// <summary>Converts lap times between seconds and the "M:SS.fff" strings used in challenges.json.</summary>
public static class TimeFormat
{
    /// <summary>Parses a lap time.</summary>
    /// <param name="text">"M:SS.fff", or plain seconds.</param>
    /// <returns>The time in seconds.</returns>
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

    /// <summary>Renders a lap time.</summary>
    /// <param name="seconds">The time in seconds.</param>
    /// <returns>The time as "M:SS.fff".</returns>
    public static string Format(double seconds)
    {
        var total = Math.Round(seconds, 3, MidpointRounding.AwayFromZero);
        var minutes = (int)(total / 60);
        var rest = total - minutes * 60;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00.000}", minutes, rest);
    }
}
