using GatewayService.Resilience;

namespace HotelsBooking.Tests;

public class RingBufferTests
{
    private static readonly DateTimeOffset FirstTimeout = new(2026, 11, 20, 3, 14, 27, TimeSpan.FromHours(3));

    private static DateTimeOffset At(int secondsAfterFirst) => FirstTimeout.AddSeconds(secondsAfterFirst);

    [Fact]
    public void ThreeTimeouts_FillBuffer_InOrderTheyHappened()
    {
        var failures = new RingBuffer<DateTimeOffset>(3);
        failures.Add(At(0));
        failures.Add(At(2));
        failures.Add(At(5));

        Assert.True(failures.IsFull);
        Assert.Equal([At(0), At(2), At(5)], failures.Items());
        Assert.Equal(At(0), failures.Oldest);
        Assert.Equal(At(5), failures.Newest);
    }

    [Fact]
    public void FreshTimeout_OverwritesOldestOne_WhenBufferIsFull()
    {
        var failures = new RingBuffer<DateTimeOffset>(3);
        foreach (var second in new[] { 0, 2, 5, 9, 14 })
            failures.Add(At(second));

        Assert.Equal(3, failures.Count);
        Assert.Equal([At(5), At(9), At(14)], failures.Items());
    }

    [Fact]
    public void ExpiredTimeoutsRemoved_NewOnesWrapAroundArray()
    {
        var failures = new RingBuffer<DateTimeOffset>(3);
        failures.Add(At(0));
        failures.Add(At(2));
        failures.Add(At(5));

        Assert.Equal(At(0), failures.RemoveOldest());
        Assert.Equal(At(2), failures.RemoveOldest());

        failures.Add(At(41));
        failures.Add(At(43));

        Assert.Equal([At(5), At(41), At(43)], failures.Items());
    }

    [Fact]
    public void Clear_ForgetsAllTimeouts_AfterLoyaltyRecovered()
    {
        var failures = new RingBuffer<DateTimeOffset>(3);
        failures.Add(At(0));
        failures.Add(At(7));

        failures.Clear();

        Assert.True(failures.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => failures.Oldest);
    }

    [Fact]
    public void BufferWithoutCapacity_CannotBeCreated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<DateTimeOffset>(0));
    }
}
