using SoftTrace.Core;

namespace SoftTrace.Core.Tests;

public sealed class CloudSyncRetryScheduleTests
{
    [Fact]
    public void FailureBackoffIsBoundedAndSuccessRestoresNormalCadence()
    {
        var clock = new Clock();
        var schedule = new CloudSyncRetrySchedule(clock, () => 0.5);
        Assert.Equal(TimeSpan.Zero, schedule.Delay);
        foreach (var minutes in new[] { 2, 4, 8, 15, 15, 15 })
        {
            schedule.RecordFailure();
            Assert.Equal(TimeSpan.FromMinutes(minutes), schedule.Delay);
            clock.Now += TimeSpan.FromMinutes(minutes);
            Assert.Equal(TimeSpan.Zero, schedule.Delay);
        }
        schedule.RecordSuccess();
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.Delay);
        schedule.RecordFailure();
        Assert.Equal(TimeSpan.FromMinutes(2), schedule.Delay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void JitterNeverExceedsCapAndNetworkRecoveryDoesNotEraseFailures(double jitter)
    {
        var clock = new Clock();
        var schedule = new CloudSyncRetrySchedule(clock, () => jitter);
        for (var n = 0; n < 20; n++) schedule.RecordFailure();
        Assert.InRange(schedule.Delay.TotalSeconds, 815.999, 900);
        schedule.RequestNow();
        Assert.Equal(TimeSpan.Zero, schedule.Delay);
        schedule.RecordFailure();
        Assert.InRange(schedule.Delay.TotalSeconds, 815.999, 900);
        schedule.RecordSuccess();
        clock.Now += TimeSpan.FromSeconds(75);
        Assert.Equal(TimeSpan.Zero, schedule.Delay);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-23T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
