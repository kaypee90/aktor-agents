using AgentRuntime.Workspaces;
using Xunit;

namespace AgentRuntime.Tests;

public class CronScheduleTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("*/15 * * * *", "2026-09-24T10:07", "2026-09-24T10:15")]
    [InlineData("0 9 * * *", "2026-09-24T09:00", "2026-09-25T09:00")]   // strictly after
    [InlineData("0 9 * * 1-5", "2026-09-26T08:00", "2026-09-28T09:00")] // Saturday -> Monday
    [InlineData("30 8,17 * * *", "2026-09-24T09:00", "2026-09-24T17:30")]
    [InlineData("0 0 1 * *", "2026-09-24T00:00", "2026-10-01T00:00")]
    [InlineData("0 12 * * 0", "2026-09-24T00:00", "2026-09-27T12:00")]  // Sunday as 0
    [InlineData("0 12 * * 7", "2026-09-24T00:00", "2026-09-27T12:00")]  // Sunday as 7
    [InlineData("0 0 29 2 *", "2026-03-01T00:00", "2028-02-29T00:00")]  // next leap day
    public void NextAfter_FindsTheNextMatchingMinute(string expression, string after, string expected)
    {
        var next = CronSchedule.Parse(expression).NextAfter(DateTimeOffset.Parse(after + "Z"));
        Assert.Equal(DateTimeOffset.Parse(expected + "Z"), next);
    }

    [Fact]
    public void DayOfMonthAndDayOfWeek_MatchEither_AsInClassicCron()
    {
        // The 1st of the month OR any Friday.
        var cron = CronSchedule.Parse("0 6 1 * 5");
        Assert.Equal(Utc(2026, 9, 25, 6, 0), cron.NextAfter(Utc(2026, 9, 24, 0, 0))); // Friday 25th
        Assert.Equal(Utc(2026, 10, 1, 6, 0), cron.NextAfter(Utc(2026, 9, 25, 7, 0))); // Thursday 1st
    }

    [Theory]
    [InlineData("* * * *")]
    [InlineData("61 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("a * * * *")]
    [InlineData("5-1 * * * *")]
    public void InvalidExpressions_AreRejected(string expression)
    {
        Assert.False(CronSchedule.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ImpossibleDate_NeverFires() =>
        Assert.Null(CronSchedule.Parse("0 0 31 2 *").NextAfter(Utc(2026, 1, 1, 0, 0)));
}
