using Keyturn;

namespace Keyturn.Tests;

public class HumanizeTests
{
    [Theory]
    [InlineData("1h", "Every hour")]
    [InlineData("12h", "Every 12 hours")]
    [InlineData("1d", "Every day")]
    [InlineData("7d", "Every 7 days")]
    [InlineData("30m", "Every 30 minutes")]
    [InlineData("90m", "Every 90 minutes")]
    public void DescribesIntervals(string schedule, string expected) =>
        Assert.Equal(expected, Humanize.Schedule(Schedule.Parse(schedule)));

    [Fact]
    public void DescribesDaily() =>
        Assert.StartsWith("Daily at ", Humanize.Schedule(Schedule.Parse("daily@06:00")));

    [Theory]
    [InlineData(20, "<1m")]
    [InlineData(60 * 45, "45m")]
    [InlineData(60 * 60 * 3, "3h")]
    [InlineData(60 * 60 * 47, "47h")]
    [InlineData(60 * 60 * 24 * 3, "3d")]
    public void SpansPickCoarsestUnit(int seconds, string expected) =>
        Assert.Equal(expected, Humanize.Span(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void StatusText()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00+00:00");
        var def = new SecretDefinition { Name = "x", Schedule = "1d" };
        Assert.Equal("Not set", Humanize.Status(new(def, SecretState.Missing, null), now));
        Assert.Equal("Expired 3h ago", Humanize.Status(new(def, SecretState.Stale, now.AddHours(-3)), now));
        Assert.Equal("18h left", Humanize.Status(new(def, SecretState.Fresh, now.AddHours(18)), now));
    }
}
