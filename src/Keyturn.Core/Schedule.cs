using System.Globalization;

namespace Keyturn;

/// <summary>
/// When a secret goes stale. Two forms are supported:
/// <list type="bullet">
/// <item><c>12h</c>, <c>7d</c>, <c>30m</c>: stale this long after it was last set.</item>
/// <item><c>daily@06:00</c>: stale once the most recent local 06:00 has passed since it was last set.</item>
/// </list>
/// </summary>
public sealed record Schedule
{
    public TimeSpan? Interval { get; private init; }
    public TimeSpan? DailyAt { get; private init; }

    public static Schedule Every(TimeSpan interval) =>
        interval > TimeSpan.Zero
            ? new Schedule { Interval = interval }
            : throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");

    public static Schedule Daily(TimeSpan timeOfDay) =>
        timeOfDay >= TimeSpan.Zero && timeOfDay < TimeSpan.FromDays(1)
            ? new Schedule { DailyAt = timeOfDay }
            : throw new ArgumentOutOfRangeException(nameof(timeOfDay), "Time of day must be within 00:00-23:59.");

    public static Schedule Parse(string text)
    {
        if (!TryParse(text, out var schedule))
            throw new FormatException($"Invalid schedule '{text}'. Use e.g. 12h, 7d, 30m or daily@06:00.");
        return schedule;
    }

    public static bool TryParse(string? text, out Schedule schedule)
    {
        schedule = null!;
        text = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text)) return false;

        if (text.StartsWith("daily@", StringComparison.Ordinal))
        {
            if (!TimeSpan.TryParseExact(text["daily@".Length..], @"h\:mm", CultureInfo.InvariantCulture, out var t) &&
                !TimeSpan.TryParseExact(text["daily@".Length..], @"hh\:mm", CultureInfo.InvariantCulture, out t))
                return false;
            if (t < TimeSpan.Zero || t >= TimeSpan.FromDays(1)) return false;
            schedule = Daily(t);
            return true;
        }

        if (text == "daily")
        {
            schedule = Daily(TimeSpan.Zero);
            return true;
        }

        if (text.Length < 2 || !int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return false;

        var interval = text[^1] switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => (TimeSpan?)null,
        };
        if (interval is null) return false;
        schedule = Every(interval.Value);
        return true;
    }

    /// <summary>The moment a secret set at <paramref name="lastSet"/> becomes stale.</summary>
    public DateTimeOffset ExpiresAfter(DateTimeOffset lastSet)
    {
        if (Interval is { } interval) return lastSet + interval;

        // Next local occurrence of DailyAt strictly after lastSet.
        var local = lastSet.ToLocalTime();
        var candidate = new DateTimeOffset(local.Date + DailyAt!.Value, local.Offset);
        if (candidate <= local) candidate = new DateTimeOffset(local.Date.AddDays(1) + DailyAt.Value, local.Offset);
        // Re-resolve the offset in case a DST change falls between the two dates.
        return new DateTimeOffset(candidate.DateTime, TimeZoneInfo.Local.GetUtcOffset(candidate.DateTime));
    }

    public override string ToString() =>
        Interval is { } i
            ? i.TotalDays >= 1 && i.TotalDays % 1 == 0 ? $"{(int)i.TotalDays}d"
              : i.TotalHours >= 1 && i.TotalHours % 1 == 0 ? $"{(int)i.TotalHours}h"
              : $"{(int)i.TotalMinutes}m"
            : $"daily@{DailyAt!.Value:hh\\:mm}";
}
