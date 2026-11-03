using BytexDigital.Steam.ContentDelivery;

namespace BytexDigital.Steam.Tests.ContentDelivery;

public class DroppedServerTrackerTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan InitialProbeDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumProbeDelay = TimeSpan.FromSeconds(60);

    [Fact]
    public void NothingIsDroppedByDefault()
    {
        var tracker = CreateTracker(() => Start);

        Assert.False(tracker.IsDropped("CDN:a.example.com"));
    }

    [Fact]
    public void DroppedServerRemainsDroppedUntilARecoveryProbeSucceeds()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);

        tracker.Drop("CDN:a.example.com");
        now = Start.AddDays(1);

        Assert.True(tracker.IsDropped("CDN:a.example.com"));
    }


    [Fact]
    public void SuccessfulNormalOperationRestoresADroppedServer()
    {
        var tracker = CreateTracker(() => Start);
        tracker.Drop("a");

        Assert.True(tracker.MarkHealthy("a"));
        Assert.False(tracker.IsDropped("a"));
    }

    [Fact]
    public void SuccessfulFallbackOperationCanRestoreAServerWithoutAProbeLease()
    {
        var tracker = CreateTracker(() => Start);
        tracker.Drop("a");
        tracker.Drop("b");

        // The pool may temporarily select a dropped server when no healthy candidates remain.
        // A successful real request is sufficient evidence to restore that server.
        Assert.True(tracker.MarkHealthy("a"));

        Assert.False(tracker.IsDropped("a"));
        Assert.True(tracker.IsDropped("b"));
    }

    [Fact]
    public void MarkingHealthyInvalidatesAnOutstandingRecoveryProbeLease()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);
        tracker.Drop("a");
        now = Start.Add(InitialProbeDelay);
        Assert.True(tracker.TryBeginRecoveryProbe("a", out var lease));

        Assert.True(tracker.MarkHealthy("a"));
        Assert.False(tracker.CompleteRecoveryProbe(lease, succeeded: false));
        Assert.False(tracker.IsDropped("a"));
    }

    [Fact]
    public void MarkHealthyReturnsFalseForAServerThatWasNotDropped()
    {
        var tracker = CreateTracker(() => Start);

        Assert.False(tracker.MarkHealthy("a"));
    }

    [Fact]
    public void OtherServersAreNotAffected()
    {
        var tracker = CreateTracker(() => Start);

        tracker.Drop("CDN:a.example.com");

        Assert.False(tracker.IsDropped("CDN:b.example.com"));
        Assert.False(tracker.IsDropped("SteamCache:a.example.com"));
    }

    [Fact]
    public void WithoutDroppedRemovesDroppedItemsAndKeepsTheOrder()
    {
        var tracker = CreateTracker(() => Start);
        tracker.Drop("b");

        var result = tracker.WithoutDropped(new[] { "a", "b", "c" }, item => item);

        Assert.Equal(new[] { "a", "c" }, result);
    }

    [Fact]
    public void WithoutDroppedReturnsNothingWhenAllCandidatesAreDropped()
    {
        var tracker = CreateTracker(() => Start);
        tracker.Drop("a");
        tracker.Drop("b");

        var result = tracker.WithoutDropped(new[] { "a", "b" }, item => item);

        Assert.Empty(result);
        Assert.True(tracker.IsDropped("a"));
        Assert.True(tracker.IsDropped("b"));
    }

    [Fact]
    public void WithoutDroppedOfNothingIsNothing()
    {
        var tracker = CreateTracker(() => Start);

        Assert.Empty(tracker.WithoutDropped(Array.Empty<string>(), item => item));
    }

    [Fact]
    public void RecoveryProbeIsNotAvailableBeforeInitialDelay()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);
        tracker.Drop("a");

        now = Start.AddSeconds(4);

        Assert.False(tracker.TryBeginRecoveryProbe("a", out _));
        Assert.True(tracker.IsDropped("a"));
    }

    [Fact]
    public void RecoveryProbeBecomesAvailableAfterInitialDelay()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);
        tracker.Drop("a");

        now = Start.Add(InitialProbeDelay);

        Assert.True(tracker.TryBeginRecoveryProbe("a", out var lease));
        Assert.Equal("a", lease.Key);
        Assert.True(tracker.IsDropped("a"));
    }

    [Fact]
    public void OnlyOneRecoveryProbeCanBeActiveForAServer()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);
        tracker.Drop("a");
        now = Start.Add(InitialProbeDelay);

        Assert.True(tracker.TryBeginRecoveryProbe("a", out _));
        Assert.False(tracker.TryBeginRecoveryProbe("a", out _));
    }

    [Fact]
    public void SuccessfulRecoveryProbeRemovesTheDroppedServer()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);
        tracker.Drop("a");
        now = Start.Add(InitialProbeDelay);

        Assert.True(tracker.TryBeginRecoveryProbe("a", out var lease));
        Assert.True(tracker.CompleteRecoveryProbe(lease, succeeded: true));

        Assert.False(tracker.IsDropped("a"));
        Assert.False(tracker.TryBeginRecoveryProbe("a", out _));
    }

    [Fact]
    public void FailedRecoveryProbeKeepsServerDroppedAndAppliesExponentialBackoff()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);
        tracker.Drop("a");
        now = Start.Add(InitialProbeDelay);

        Assert.True(tracker.TryBeginRecoveryProbe("a", out var firstLease));
        Assert.True(tracker.CompleteRecoveryProbe(firstLease, succeeded: false));
        Assert.True(tracker.IsDropped("a"));
        Assert.False(tracker.TryBeginRecoveryProbe("a", out _));

        now = now.AddSeconds(9);
        Assert.False(tracker.TryBeginRecoveryProbe("a", out _));

        now = now.AddSeconds(1);
        Assert.True(tracker.TryBeginRecoveryProbe("a", out var secondLease));
        Assert.True(tracker.CompleteRecoveryProbe(secondLease, succeeded: false));

        // The second failed probe waits 20 seconds (the initial 5 seconds doubled twice).
        now = now.AddSeconds(19);
        Assert.False(tracker.TryBeginRecoveryProbe("a", out _));

        now = now.AddSeconds(1);
        Assert.True(tracker.TryBeginRecoveryProbe("a", out _));
    }

    [Fact]
    public void RecoveryProbeBackoffNeverExceedsMaximum()
    {
        var now = Start;
        var tracker = CreateTracker(() => now, maximumProbeDelay: TimeSpan.FromSeconds(20));
        tracker.Drop("a");
        now = Start.Add(InitialProbeDelay);

        Assert.True(tracker.TryBeginRecoveryProbe("a", out var firstLease));
        Assert.True(tracker.CompleteRecoveryProbe(firstLease, succeeded: false));

        now = now.AddSeconds(10);
        Assert.True(tracker.TryBeginRecoveryProbe("a", out var secondLease));
        Assert.True(tracker.CompleteRecoveryProbe(secondLease, succeeded: false));

        now = now.AddSeconds(20);
        Assert.True(tracker.TryBeginRecoveryProbe("a", out var thirdLease));
        Assert.True(tracker.CompleteRecoveryProbe(thirdLease, succeeded: false));

        now = now.AddSeconds(19);
        Assert.False(tracker.TryBeginRecoveryProbe("a", out _));

        now = now.AddSeconds(1);
        Assert.True(tracker.TryBeginRecoveryProbe("a", out _));
    }

    [Fact]
    public void CancellingRecoveryProbeDoesNotRestoreServerOrCountAsFailure()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);
        tracker.Drop("a");
        now = Start.Add(InitialProbeDelay);

        Assert.True(tracker.TryBeginRecoveryProbe("a", out var lease));
        Assert.True(tracker.CancelRecoveryProbe(lease));

        Assert.True(tracker.IsDropped("a"));
        Assert.True(tracker.TryBeginRecoveryProbe("a", out _));
    }

    [Fact]
    public void StaleProbeLeaseCannotCompleteAReplacementProbe()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);
        tracker.Drop("a");
        now = Start.Add(InitialProbeDelay);

        Assert.True(tracker.TryBeginRecoveryProbe("a", out var staleLease));
        Assert.True(tracker.CancelRecoveryProbe(staleLease));
        Assert.True(tracker.TryBeginRecoveryProbe("a", out var currentLease));

        Assert.False(tracker.CompleteRecoveryProbe(staleLease, succeeded: true));
        Assert.True(tracker.IsDropped("a"));

        Assert.True(tracker.CompleteRecoveryProbe(currentLease, succeeded: true));
        Assert.False(tracker.IsDropped("a"));
    }

    [Fact]
    public void DroppingAnAlreadyDroppedServerDoesNotResetItsBackoff()
    {
        var now = Start;
        var tracker = CreateTracker(() => now);
        tracker.Drop("a");
        now = Start.Add(InitialProbeDelay);

        Assert.True(tracker.TryBeginRecoveryProbe("a", out var lease));
        Assert.True(tracker.CompleteRecoveryProbe(lease, succeeded: false));

        now = now.AddSeconds(3);
        tracker.Drop("a");

        now = now.AddSeconds(6);
        Assert.False(tracker.TryBeginRecoveryProbe("a", out _));

        now = now.AddSeconds(1);
        Assert.True(tracker.TryBeginRecoveryProbe("a", out _));
    }

    [Fact]
    public void HealthyServerCannotAcquireRecoveryProbe()
    {
        var tracker = CreateTracker(() => Start.AddDays(1));

        Assert.False(tracker.TryBeginRecoveryProbe("a", out _));
    }

    private static DroppedServerTracker CreateTracker(
        Func<DateTime> utcNow,
        TimeSpan? maximumProbeDelay = null)
    {
        return new DroppedServerTracker(
            InitialProbeDelay,
            maximumProbeDelay ?? MaximumProbeDelay,
            utcNow);
    }
}