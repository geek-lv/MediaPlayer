using MediaPlayer.Controls;

namespace MediaPlayer.Controls.Tests;

public sealed class TimelineSeekLatencyIntegrationTests
{
    [Fact]
    public void FastMode_DragSeekLatencyP95_StaysWithinBudget()
    {
        var samples = BuildSamples(count: 180, interval: TimeSpan.FromMilliseconds(8), startSeconds: 2d, deltaSeconds: 0.12d);
        var result = TimelineSeekLatencyHarness.Run(samples, isSlowMode: false, releaseDelay: TimeSpan.FromMilliseconds(12));

        Assert.True(result.DispatchCount > 20, "Expected enough dispatched seek samples for p95 calculation.");
        Assert.InRange(result.P95.TotalMilliseconds, 0d, 40d);
    }

    [Fact]
    public void SlowMode_DragSeekLatencyP95_StaysWithinSlowPathBudget()
    {
        var samples = BuildSamples(count: 120, interval: TimeSpan.FromMilliseconds(14), startSeconds: 10d, deltaSeconds: 0.16d);
        var result = TimelineSeekLatencyHarness.Run(samples, isSlowMode: true, releaseDelay: TimeSpan.FromMilliseconds(16));

        Assert.True(result.DispatchCount > 12, "Expected enough dispatched seek samples for p95 calculation.");
        Assert.InRange(result.P95.TotalMilliseconds, 0d, 100d);
    }

    [Fact]
    public void PendingSeek_FlushesWhenPointerStopsMoving()
    {
        var samples = new[]
        {
            new SeekInputSample(TimeSpan.Zero, 4d),
            new SeekInputSample(TimeSpan.FromMilliseconds(10), 5d)
        };

        var result = TimelineSeekLatencyHarness.Run(samples, isSlowMode: false, releaseDelay: TimeSpan.FromMilliseconds(120));
        Assert.True(result.TryGetLatencyForValue(5d, out var latency));
        Assert.InRange(latency.TotalMilliseconds, 0d, 40d);
    }

    private static SeekInputSample[] BuildSamples(int count, TimeSpan interval, double startSeconds, double deltaSeconds)
    {
        var samples = new SeekInputSample[count];
        var offset = TimeSpan.Zero;
        var value = startSeconds;
        for (var index = 0; index < count; index++)
        {
            samples[index] = new SeekInputSample(offset, value);
            offset += interval;
            value += deltaSeconds;
        }

        return samples;
    }

    private readonly record struct SeekInputSample(TimeSpan Offset, double Value);

    private readonly record struct SeekLatencyResult(TimeSpan P95, int DispatchCount, Dictionary<long, TimeSpan> LatencyByValue)
    {
        public bool TryGetLatencyForValue(double value, out TimeSpan latency)
        {
            return LatencyByValue.TryGetValue(ToKey(value), out latency);
        }
    }

    private sealed class TimelineSeekLatencyHarness
    {
        private readonly Dictionary<long, TimeSpan> _submitTimes = new();
        private readonly Dictionary<long, TimeSpan> _latencyByValue = new();
        private readonly List<long> _latencyTicks = new();
        private readonly TimeSpan _fastInterval = TimeSpan.FromMilliseconds(24);
        private readonly TimeSpan _slowInterval = TimeSpan.FromMilliseconds(60);
        private readonly TimelineSeekController _controller;
        private DateTime _utcNow = DateTime.UnixEpoch;
        private TimeSpan _elapsed;
        private bool _timerEnabled;
        private TimeSpan _timerInterval;
        private TimeSpan _nextTimerTick;

        private TimelineSeekLatencyHarness(bool isSlowMode)
        {
            _controller = new TimelineSeekController(
                OnSeekDispatched,
                seconds => Math.Clamp(seconds, 0d, 10_000d),
                () => isSlowMode,
                _fastInterval,
                _slowInterval,
                fastMinDeltaSeconds: 0.03d,
                slowMinDeltaSeconds: 0.08d,
                utcNow: () => _utcNow);
        }

        public static SeekLatencyResult Run(IReadOnlyList<SeekInputSample> samples, bool isSlowMode, TimeSpan releaseDelay)
        {
            var harness = new TimelineSeekLatencyHarness(isSlowMode);
            return harness.Execute(samples, releaseDelay);
        }

        private SeekLatencyResult Execute(IReadOnlyList<SeekInputSample> samples, TimeSpan releaseDelay)
        {
            _controller.BeginDrag();
            _timerInterval = _controller.CurrentInterval;

            for (var index = 0; index < samples.Count; index++)
            {
                var sample = samples[index];
                AdvanceTo(sample.Offset);
                Submit(sample.Value);
            }

            var releaseAt = samples.Count == 0
                ? releaseDelay
                : samples[samples.Count - 1].Offset + releaseDelay;
            AdvanceTo(releaseAt);
            if (samples.Count > 0)
            {
                _controller.EndDrag(samples[samples.Count - 1].Value, commitFinalSeek: true);
            }

            _timerEnabled = false;
            var p95 = ComputeP95(_latencyTicks);
            return new SeekLatencyResult(p95, _latencyTicks.Count, _latencyByValue);
        }

        private void Submit(double value)
        {
            _submitTimes[ToKey(value)] = _elapsed;
            _ = _controller.Submit(value);
            UpdateTimerInterval(_controller.CurrentInterval);

            if (_controller.HasPendingSeek)
            {
                if (!_timerEnabled)
                {
                    _timerEnabled = true;
                    _nextTimerTick = _elapsed + _timerInterval;
                }
            }
            else if (_timerEnabled)
            {
                _timerEnabled = false;
            }
        }

        private void AdvanceTo(TimeSpan target)
        {
            while (_timerEnabled && _nextTimerTick <= target)
            {
                SetNow(_nextTimerTick);
                OnTimerTick();
            }

            SetNow(target);
        }

        private void OnTimerTick()
        {
            if (!_controller.IsDragging)
            {
                _timerEnabled = false;
                return;
            }

            UpdateTimerInterval(_controller.CurrentInterval);
            if (!_controller.HasPendingSeek)
            {
                _timerEnabled = false;
                return;
            }

            _ = _controller.FlushPending();
            if (!_controller.HasPendingSeek)
            {
                _timerEnabled = false;
                return;
            }

            _nextTimerTick = _elapsed + _timerInterval;
        }

        private void UpdateTimerInterval(TimeSpan interval)
        {
            if (_timerInterval == interval)
            {
                return;
            }

            _timerInterval = interval;
            if (_timerEnabled)
            {
                _nextTimerTick = _elapsed + _timerInterval;
            }
        }

        private void OnSeekDispatched(double seconds)
        {
            var key = ToKey(seconds);
            if (_submitTimes.TryGetValue(key, out var submittedAt))
            {
                var latency = _elapsed - submittedAt;
                if (latency < TimeSpan.Zero)
                {
                    latency = TimeSpan.Zero;
                }

                _latencyByValue[key] = latency;
                _latencyTicks.Add(latency.Ticks);
            }
        }

        private void SetNow(TimeSpan elapsed)
        {
            _elapsed = elapsed;
            _utcNow = DateTime.UnixEpoch + elapsed;
        }

        private static TimeSpan ComputeP95(List<long> ticks)
        {
            if (ticks.Count == 0)
            {
                return TimeSpan.Zero;
            }

            ticks.Sort();
            var p95Index = (int)Math.Ceiling(ticks.Count * 0.95d) - 1;
            p95Index = Math.Clamp(p95Index, 0, ticks.Count - 1);
            return TimeSpan.FromTicks(ticks[p95Index]);
        }
    }

    private static long ToKey(double value)
    {
        return BitConverter.DoubleToInt64Bits(value);
    }
}
