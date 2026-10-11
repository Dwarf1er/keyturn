using System.Globalization;

namespace Keyturn;

/// <summary>Human-readable text for schedules and states, shared by every UI.</summary>
public static class Humanize
{
    public static string Schedule(Schedule schedule)
    {
        if (schedule.DailyAt is { } t)
            return $"Daily at {(DateTime.Today + t).ToString("t", CultureInfo.CurrentCulture)}";

        var i = schedule.Interval!.Value;
        if (i.TotalDays >= 1 && i.TotalDays % 1 == 0) return Every((int)i.TotalDays, "day");
        if (i.TotalHours >= 1 && i.TotalHours % 1 == 0) return Every((int)i.TotalHours, "hour");
        return Every((int)i.TotalMinutes, "minute");
    }

    /// <summary>"45m", "3h", "2d": the coarsest unit that keeps the number small.</summary>
    public static string Span(TimeSpan span)
    {
        span = span.Duration();
        if (span.TotalMinutes < 1) return "<1m";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 48) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }

    public static string Status(SecretStatus status, DateTimeOffset now) => status.State switch
    {
        SecretState.Missing => "Not set",
        SecretState.Stale => $"Expired {Span(now - status.ExpiresAt!.Value)} ago",
        _ => $"{Span(status.ExpiresAt!.Value - now)} left",
    };

    public static string Count(int n, string singular, string plural) => n == 1 ? $"1 {singular}" : $"{n} {plural}";

    static string Every(int n, string unit) => n == 1 ? $"Every {unit}" : $"Every {n} {unit}s";
}
