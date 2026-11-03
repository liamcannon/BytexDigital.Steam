using BytexDigital.Steam.ContentDelivery;

namespace BytexDigital.Steam.Tests.ContentDelivery;

public class DroppedServerTrackerTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    [Fact]
    public void NothingIsDroppedByDefault()
    {
        var tracker = new DroppedServerTracker(Cooldown, () => Start);

        Assert.False(tracker.IsDropped("CDN:a.example.com"));
    }

    [Fact]
    public void DroppedServerStaysDroppedDuringTheCooldown()
    {
        var now = Start;
        var tracker = new DroppedServerTracker(Cooldown, () => now);

        tracker.Drop("CDN:a.example.com");
        now = Start.AddMinutes(4);

        Assert.True(tracker.IsDropped("CDN:a.example.com"));
    }

    [Fact]
    public void DroppedServerIsAllowedAgainAfterTheCooldown()
    {
        var now = Start;
        var tracker = new DroppedServerTracker(Cooldown, () => now);

        tracker.Drop("CDN:a.example.com");
        now = Start.AddMinutes(5).AddSeconds(1);

        Assert.False(tracker.IsDropped("CDN:a.example.com"));
    }

    [Fact]
    public void DroppingAgainRestartsTheCooldown()
    {
        var now = Start;
        var tracker = new DroppedServerTracker(Cooldown, () => now);

        tracker.Drop("CDN:a.example.com");
        now = Start.AddMinutes(4);
        tracker.Drop("CDN:a.example.com");
        now = Start.AddMinutes(8);

        Assert.True(tracker.IsDropped("CDN:a.example.com"));
    }

    [Fact]
    public void OtherServersAreNotAffected()
    {
        var tracker = new DroppedServerTracker(Cooldown, () => Start);

        tracker.Drop("CDN:a.example.com");

        Assert.False(tracker.IsDropped("CDN:b.example.com"));
        Assert.False(tracker.IsDropped("SteamCache:a.example.com"));
    }

    [Fact]
    public void WithoutDroppedRemovesDroppedItemsAndKeepsTheOrder()
    {
        var tracker = new DroppedServerTracker(Cooldown, () => Start);
        tracker.Drop("b");

        var result = tracker.WithoutDropped(new[] { "a", "b", "c" }, item => item);

        Assert.Equal(new[] { "a", "c" }, result);
    }

    [Fact]
    public void WithoutDroppedUsesEverythingWhenAllAreDropped()
    {
        var tracker = new DroppedServerTracker(Cooldown, () => Start);
        tracker.Drop("a");
        tracker.Drop("b");

        var result = tracker.WithoutDropped(new[] { "a", "b" }, item => item);

        Assert.Equal(new[] { "a", "b" }, result);
        // The cooldowns were forgotten, not just ignored
        Assert.False(tracker.IsDropped("a"));
        Assert.False(tracker.IsDropped("b"));
    }

    [Fact]
    public void WithoutDroppedOfNothingIsNothing()
    {
        var tracker = new DroppedServerTracker(Cooldown, () => Start);

        Assert.Empty(tracker.WithoutDropped(Array.Empty<string>(), item => item));
    }
}