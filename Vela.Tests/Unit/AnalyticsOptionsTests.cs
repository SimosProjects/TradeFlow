using Vela.Analytics;

namespace Vela.Tests.Unit;

public class AnalyticsOptionsTests
{
    // -- Parse: consecutive scheduled windows --

    [Fact]
    public void Parse_WeeklyReport_ConsecutiveWindowsDoNotOverlapOrGap()
    {
        var thisRun = new DateTimeOffset(2026, 8, 28, 16, 20, 0, TimeSpan.FromHours(-4));
        var nextRun = thisRun.AddDays(7);

        var thisWeek = AnalyticsOptions.Parse(["--report", "weekly"], thisRun);
        var nextWeek = AnalyticsOptions.Parse(["--report", "weekly"], nextRun);

        thisWeek.To.Should().Be(nextWeek.From);
        (thisWeek.To - thisWeek.From).Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void Parse_MonthlyReport_ConsecutiveWindowsDoNotOverlapOrGap()
    {
        var thisRun = new DateTimeOffset(2026, 8, 28, 16, 20, 0, TimeSpan.FromHours(-4));
        var nextRun = thisRun.AddDays(30);

        var thisMonth = AnalyticsOptions.Parse(["--report", "monthly"], thisRun);
        var nextMonth = AnalyticsOptions.Parse(["--report", "monthly"], nextRun);

        thisMonth.To.Should().Be(nextMonth.From);
        (thisMonth.To - thisMonth.From).Should().Be(TimeSpan.FromDays(30));
    }
}
