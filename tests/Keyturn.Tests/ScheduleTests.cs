using Keyturn;

namespace Keyturn.Tests;

public class ScheduleTests
{
    [Theory]
    [InlineData("30m", "30m")]
    [InlineData("12h", "12h")]
    [InlineData("7d", "7d")]
    [InlineData("24h", "1d")]
    [InlineData("daily@06:00", "daily@06:00")]
    [InlineData("daily@6:05", "daily@06:05")]
    [InlineData(" 12H ", "12h")]
    public void Parses(string input, string canonical) =>
        Assert.Equal(canonical, Schedule.Parse(input).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("0h")]
    [InlineData("-1h")]
    [InlineData("12")]
    [InlineData("1w")]
    [InlineData("daily@25:00")]
    [InlineData("daily@6")]
    public void Rejects(string input) => Assert.False(Schedule.TryParse(input, out _));

    [Fact]
    public void IntervalExpiresAfterInterval()
    {
        var set = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00");
        Assert.Equal(set.AddHours(12), Schedule.Parse("12h").ExpiresAfter(set));
    }

    [Fact]
    public void DailyExpiresAtNextBoundary()
    {
        var local = TimeZoneInfo.Local;
        var before = new DateTimeOffset(new DateTime(2026, 3, 10, 5, 0, 0), local.GetUtcOffset(new DateTime(2026, 3, 10, 5, 0, 0)));
        var after = new DateTimeOffset(new DateTime(2026, 3, 10, 7, 0, 0), local.GetUtcOffset(new DateTime(2026, 3, 10, 7, 0, 0)));
        var s = Schedule.Parse("daily@06:00");

        Assert.Equal(new DateTime(2026, 3, 10, 6, 0, 0), s.ExpiresAfter(before).DateTime);
        Assert.Equal(new DateTime(2026, 3, 11, 6, 0, 0), s.ExpiresAfter(after).DateTime);
    }

    [Fact]
    public void DailyAtExactBoundaryRollsToNextDay()
    {
        var t = new DateTime(2026, 3, 10, 6, 0, 0);
        var set = new DateTimeOffset(t, TimeZoneInfo.Local.GetUtcOffset(t));
        Assert.Equal(new DateTime(2026, 3, 11, 6, 0, 0), Schedule.Parse("daily@06:00").ExpiresAfter(set).DateTime);
    }
}

public class WindowsCredentialLimitTests
{
    [Fact]
    public void AcceptsUpToTheLimit() =>
        WindowsCredentialBackend.EnsureFits(new string('x', WindowsCredentialBackend.MaxChars));

    [Fact]
    public void RejectsOverTheLimitWithAClearMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => WindowsCredentialBackend.EnsureFits(new string('x', WindowsCredentialBackend.MaxChars + 1)));
        Assert.Contains("1280", ex.Message);
    }
}
