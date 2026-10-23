using SoftTrace.Core;

namespace SoftTrace.Core.Tests;

public sealed class PeriodOverviewTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, $"period-{Guid.NewGuid():N}.db");
    private ActivityStore Store => new(_path);
    private readonly TimeZoneInfo _zone = TimeZoneInfo.CreateCustomTimeZone("Test UTC+8", TimeSpan.FromHours(8), "Test UTC+8", "Test UTC+8");
    public Task InitializeAsync() => Store.InitializeAsync();
    public Task DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path+suffix);
        return Task.CompletedTask;
    }
    [Fact]
    public async Task PeriodSplitsMidnightClipsBoundsIncludesEmptyDaysAndKeepsDevicesSeparate()
    {
        var start = DateTimeOffset.Parse("2026-10-01T00:00:00+08:00");
        var app = new AppIdentity("chrome", "Chrome", null);
        await Store.AddCompletedSegmentAsync("pc", new(app,start.AddMinutes(-10),start.AddMinutes(20)));
        await Store.AddCompletedSegmentAsync("pc", new(app,start.AddHours(23).AddMinutes(55),start.AddDays(1).AddMinutes(10)));
        await Store.AddCompletedSegmentAsync("pc", new(new("state","空闲",null,Kind:"idle"),start.AddDays(1).AddMinutes(10),start.AddDays(1).AddMinutes(30)));
        await Store.AddCompletedSegmentAsync("other", new(app,start,start.AddHours(2)));
        var result = await Store.GetPeriodOverviewAsync("pc",start,start.AddDays(3),_zone);
        Assert.Equal(3,result.Days.Count);
        Assert.Equal(new[] {"2026-10-01","2026-10-02","2026-10-03"},result.Days.Select(d=>d.Date));
        Assert.Equal(new[] {1500d,600d,0d},result.Days.Select(d=>d.ActiveSeconds));
        Assert.Equal(2100,result.ActiveSeconds); Assert.Equal(1200,result.IdleSeconds);
        Assert.Equal(2,result.ActiveDays); Assert.Equal(2,result.SegmentCount);
        Assert.Equal(2100,Assert.Single(result.Apps).Seconds);
        var patterns = Assert.IsType<UsagePatternSummary>(result.Patterns);
        Assert.Equal(2, patterns.RecordedDays);
        Assert.Equal(1, patterns.MissingDays);
        Assert.Equal(2100, patterns.Hours.Sum(hour => hour.ActiveSeconds));
        Assert.Equal(2, patterns.Hours[0].RecordedDays);
        Assert.Equal(15, patterns.Hours[0].AverageMinutes);
        Assert.Equal(0, patterns.Hours[1].AverageMinutes);
        double dailySum=0;
        for(var i=0;i<3;i++)dailySum+=(await Store.GetDailyTimelineAsync("pc",start.AddDays(i),start.AddDays(i+1))).ActiveSeconds;
        Assert.Equal(dailySum,result.ActiveSeconds);
    }
    [Fact]
    public async Task AllTimeStartsAtFirstRecordedLocalDayAndActiveTimeUsesUnion()
    {
        var start = DateTimeOffset.Parse("2024-02-28T23:50:00+08:00");
        await Store.AddCompletedSegmentAsync("pc",new(new("a","A",null),start,start.AddMinutes(30)));
        await Store.AddCompletedSegmentAsync("pc",new(new("b","B",null),start.AddMinutes(5),start.AddMinutes(20)));
        var result=await Store.GetPeriodOverviewAsync("pc",null,DateTimeOffset.Parse("2024-03-01T00:00:00+08:00"),_zone);
        Assert.Equal(new[] {"2024-02-28","2024-02-29"},result.Days.Select(d=>d.Date));
        Assert.Equal(1800,result.ActiveSeconds);
        Assert.Equal(2,result.ActiveDays);
        Assert.Equal(DateTimeOffset.Parse("2024-02-28T00:00:00+08:00"),result.StartUtc);
        Assert.Equal(1800, result.Patterns!.Hours.Sum(hour => hour.ActiveSeconds));
        Assert.Equal(5, result.Patterns.Hours[23].AverageMinutes);
        Assert.Equal(10, result.Patterns.Hours[0].AverageMinutes);
    }
    [Fact]
    public async Task PatternsSplitHoursAndDistinguishRecordedIdleDaysFromMissingDays()
    {
        var monday = DateTimeOffset.Parse("2026-07-06T00:00:00+08:00");
        await Store.AddCompletedSegmentAsync("pc", new(new("a", "A", null), monday.AddHours(9.5), monday.AddHours(10.5)));
        await Store.AddCompletedSegmentAsync("pc", new(new("state", "空闲", null, Kind:"idle"), monday.AddDays(1).AddHours(9), monday.AddDays(1).AddHours(10)));
        var result = await Store.GetPeriodOverviewAsync("pc", monday, monday.AddDays(7), _zone);
        var patterns = result.Patterns!;
        Assert.Equal(2, patterns.RecordedDays);
        Assert.Equal(5, patterns.MissingDays);
        Assert.Equal(7200, patterns.RecordedSeconds);
        Assert.Equal(15, patterns.Hours[9].AverageMinutes);
        Assert.Equal(15, patterns.Hours[10].AverageMinutes);
        Assert.Equal(1, patterns.Weekdays[0].AverageHours);
        Assert.Equal(0, patterns.Weekdays[1].AverageHours);
        Assert.Null(patterns.Weekdays[2].AverageHours);
    }
    [Fact]
    public async Task AllPatternGroupsIncludeRecordedDaysWithNoActiveUse()
    {
        var start = DateTimeOffset.Parse("2026-10-05T00:00:00+08:00");
        await Store.AddCompletedSegmentAsync("pc", new(new("a", "A", null), start.AddHours(3), start.AddHours(4)));
        await Store.AddCompletedSegmentAsync("pc", new(new("a", "A", null), start.AddDays(1).AddHours(9), start.AddDays(1).AddHours(10)));
        await Store.AddCompletedSegmentAsync("pc", new(new("state", "空闲", null, Kind:"idle"), start.AddDays(7).AddHours(9), start.AddDays(7).AddHours(10)));
        var patterns = (await Store.GetPeriodOverviewAsync("pc", start, start.AddDays(14), _zone)).Patterns!;
        Assert.Equal(3, patterns.RecordedDays);
        Assert.Equal(11, patterns.MissingDays);
        Assert.Equal(1, patterns.Hours[3].RecordedDays);
        Assert.Equal(20, patterns.Hours[3].AverageMinutes);
        Assert.Equal(20, patterns.Hours[9].AverageMinutes);
        Assert.Equal(.5, patterns.Weekdays[0].AverageHours);
        Assert.Equal(2d / 3, patterns.AcademicPeriods.Single(period => period.Kind == "autumn").AverageHours!.Value, 10);
    }
    [Fact]
    public async Task CalendarComparisonUsesSeparateObservedDayDenominators()
    {
        var start = DateTimeOffset.Parse("2026-06-30T00:00:00+08:00");
        await Store.AddCompletedSegmentAsync("pc", new(new("a", "A", null), start.AddHours(9), start.AddHours(11)));
        await Store.AddCompletedSegmentAsync("pc", new(new("a", "A", null), start.AddDays(1).AddHours(9), start.AddDays(1).AddHours(10)));
        var result = await Store.GetPeriodOverviewAsync("pc", start, start.AddDays(4), _zone);
        Assert.Equal(2, result.Patterns!.AcademicPeriods.Single(period => period.Kind == "spring").AverageHours);
        Assert.Equal(1, result.Patterns.AcademicPeriods.Single(period => period.Kind == "summer").AverageHours);
        Assert.Equal(2, result.Patterns.MissingDays);
    }
    [Theory]
    [InlineData("2025-01-17", "autumn")]
    [InlineData("2025-01-18", "autumn")]
    [InlineData("2025-02-13", "winter")]
    [InlineData("2025-07-05", "summer")]
    [InlineData("2025-08-27", "summer")]
    [InlineData("2026-01-17", "autumn")]
    [InlineData("2026-02-25", "winter")]
    [InlineData("2026-07-10", "summer")]
    [InlineData("2026-08-26", "summer")]
    [InlineData("2027-01-25", "autumn")]
    [InlineData("2027-02-17", "winter")]
    [InlineData("2027-07-19", "summer")]
    [InlineData("2024-01-01", "autumn")]
    public void CalendarGroupsUseFixedMonths(string date, string expected)
    {
        Assert.Equal(expected, AcademicCalendar.Classify(DateOnly.ParseExact(date, "yyyy-MM-dd")));
    }
}
