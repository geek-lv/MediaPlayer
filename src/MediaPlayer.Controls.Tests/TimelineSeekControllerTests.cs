using MediaPlayer.Controls;

namespace MediaPlayer.Controls.Tests;

public sealed class TimelineSeekControllerTests
{
    [Fact]
    public void Submit_OutsideDrag_DispatchesImmediately()
    {
        var clock = new TestClock();
        var seeks = new List<double>();
        var controller = CreateController(seeks, clock, isSlowMode: false);

        var dispatched = controller.Submit(12.5d);

        Assert.True(dispatched);
        Assert.False(controller.HasPendingSeek);
        Assert.Equal([12.5d], seeks);
    }

    [Fact]
    public void DragSubmit_Debounces_AndFlushesWhenIntervalElapsed()
    {
        var clock = new TestClock();
        var seeks = new List<double>();
        var controller = CreateController(seeks, clock, isSlowMode: false);
        controller.BeginDrag();

        var firstDispatched = controller.Submit(5d);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        var secondDispatched = controller.Submit(6d);

        Assert.True(firstDispatched);
        Assert.False(secondDispatched);
        Assert.True(controller.HasPendingSeek);

        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.False(controller.FlushPending());
        Assert.Equal([5d], seeks);

        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(controller.FlushPending());
        Assert.Equal([5d, 6d], seeks);
    }

    [Fact]
    public void EndDrag_FinalCommitBypassesMinDeltaThreshold()
    {
        var clock = new TestClock();
        var seeks = new List<double>();
        var controller = new TimelineSeekController(
            seeks.Add,
            seconds => Math.Clamp(seconds, 0d, 1000d),
            () => false,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(60),
            fastMinDeltaSeconds: 0.5d,
            slowMinDeltaSeconds: 1d,
            utcNow: clock.UtcNow);

        controller.BeginDrag();
        Assert.True(controller.Submit(10d));

        clock.Advance(TimeSpan.FromMilliseconds(30));
        Assert.False(controller.Submit(10.2d));
        Assert.True(controller.HasPendingSeek);
        Assert.False(controller.FlushPending());

        Assert.True(controller.EndDrag(10.2d, commitFinalSeek: true));
        Assert.False(controller.IsDragging);
        Assert.False(controller.HasPendingSeek);
        Assert.Equal([10d, 10.2d], seeks);
    }

    [Fact]
    public void SlowMode_UsesSlowIntervalThreshold()
    {
        var clock = new TestClock();
        var seeks = new List<double>();
        var controller = CreateController(seeks, clock, isSlowMode: true);
        controller.BeginDrag();

        Assert.True(controller.Submit(1d));
        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert.False(controller.Submit(2d));
        Assert.True(controller.HasPendingSeek);

        clock.Advance(TimeSpan.FromMilliseconds(40));
        Assert.False(controller.FlushPending());

        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(controller.FlushPending());
        Assert.Equal([1d, 2d], seeks);
    }

    private static TimelineSeekController CreateController(List<double> seeks, TestClock clock, bool isSlowMode)
    {
        return new TimelineSeekController(
            seeks.Add,
            seconds => Math.Clamp(seconds, 0d, 1000d),
            () => isSlowMode,
            TimeSpan.FromMilliseconds(24),
            TimeSpan.FromMilliseconds(100),
            fastMinDeltaSeconds: 0.03d,
            slowMinDeltaSeconds: 0.08d,
            utcNow: clock.UtcNow);
    }

    private sealed class TestClock
    {
        private DateTime _utcNow = DateTime.UnixEpoch;

        public DateTime UtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }
    }
}
