using System;

namespace MediaPlayer.Controls;

/// <summary>
/// Coordinates interactive timeline seeking with debounced realtime updates while dragging
/// and a guaranteed final seek commit when drag completes.
/// </summary>
public sealed class TimelineSeekController
{
    private readonly Action<double> _seekSeconds;
    private readonly Func<double, double> _clampSeconds;
    private readonly Func<bool> _isSlowMode;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _fastInterval;
    private readonly TimeSpan _slowInterval;
    private readonly double _fastMinDeltaSeconds;
    private readonly double _slowMinDeltaSeconds;

    private bool _isDragging;
    private bool _hasPendingSeek;
    private double _pendingSeekSeconds;
    private double _lastSeekSeconds = double.NaN;
    private DateTime _lastSeekUtc = DateTime.MinValue;

    /// <summary>
    /// Creates a controller used by timeline sliders to provide smooth, backend-aware seek behavior.
    /// </summary>
    /// <param name="seekSeconds">Invoked when a seek should be applied in seconds.</param>
    /// <param name="clampSeconds">Clamps incoming seconds to a valid timeline range.</param>
    /// <param name="isSlowMode">Returns <c>true</c> for slower seek backends.</param>
    /// <param name="fastInterval">Debounce interval used for fast backends.</param>
    /// <param name="slowInterval">Debounce interval used for slower backends.</param>
    /// <param name="fastMinDeltaSeconds">Minimum seek delta for fast backends.</param>
    /// <param name="slowMinDeltaSeconds">Minimum seek delta for slower backends.</param>
    /// <param name="utcNow">Optional clock for deterministic tests.</param>
    public TimelineSeekController(
        Action<double> seekSeconds,
        Func<double, double> clampSeconds,
        Func<bool> isSlowMode,
        TimeSpan fastInterval,
        TimeSpan slowInterval,
        double fastMinDeltaSeconds,
        double slowMinDeltaSeconds,
        Func<DateTime>? utcNow = null)
    {
        _seekSeconds = seekSeconds ?? throw new ArgumentNullException(nameof(seekSeconds));
        _clampSeconds = clampSeconds ?? throw new ArgumentNullException(nameof(clampSeconds));
        _isSlowMode = isSlowMode ?? throw new ArgumentNullException(nameof(isSlowMode));
        _fastInterval = fastInterval < TimeSpan.Zero ? TimeSpan.Zero : fastInterval;
        _slowInterval = slowInterval < TimeSpan.Zero ? TimeSpan.Zero : slowInterval;
        _fastMinDeltaSeconds = Math.Max(0d, fastMinDeltaSeconds);
        _slowMinDeltaSeconds = Math.Max(0d, slowMinDeltaSeconds);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Gets whether a pointer drag interaction is currently active.
    /// </summary>
    public bool IsDragging => _isDragging;

    /// <summary>
    /// Gets whether there is a pending seek value waiting for debounce flush.
    /// </summary>
    public bool HasPendingSeek => _hasPendingSeek;

    /// <summary>
    /// Gets the current debounce interval for the active backend mode.
    /// </summary>
    public TimeSpan CurrentInterval => _isSlowMode() ? _slowInterval : _fastInterval;

    /// <summary>
    /// Starts a drag interaction and resets debounce state.
    /// </summary>
    public void BeginDrag()
    {
        _isDragging = true;
        _hasPendingSeek = false;
        _pendingSeekSeconds = 0d;
        _lastSeekSeconds = double.NaN;
        _lastSeekUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Submits slider value changes. During drag, values are debounced; outside drag, seek is committed immediately.
    /// </summary>
    /// <param name="seconds">Slider value in seconds.</param>
    /// <returns><c>true</c> when a seek was dispatched immediately.</returns>
    public bool Submit(double seconds)
    {
        var clamped = _clampSeconds(seconds);
        if (!_isDragging)
        {
            _hasPendingSeek = false;
            return DispatchSeek(clamped, _utcNow(), allowDebounce: false);
        }

        var now = _utcNow();
        if (DispatchSeek(clamped, now, allowDebounce: true))
        {
            _hasPendingSeek = false;
            return true;
        }

        _pendingSeekSeconds = clamped;
        _hasPendingSeek = true;
        return false;
    }

    /// <summary>
    /// Attempts to flush pending drag seek value when debounce conditions are satisfied.
    /// </summary>
    /// <returns><c>true</c> when a pending seek was dispatched.</returns>
    public bool FlushPending()
    {
        if (!_isDragging || !_hasPendingSeek)
        {
            return false;
        }

        var dispatched = DispatchSeek(_pendingSeekSeconds, _utcNow(), allowDebounce: true);
        if (dispatched)
        {
            _hasPendingSeek = false;
        }

        return dispatched;
    }

    /// <summary>
    /// Ends drag and optionally commits the final slider value immediately.
    /// </summary>
    /// <param name="seconds">Final slider value in seconds.</param>
    /// <param name="commitFinalSeek">When true, commits the final seek value without debounce.</param>
    /// <returns><c>true</c> when final seek was dispatched.</returns>
    public bool EndDrag(double seconds, bool commitFinalSeek)
    {
        bool dispatched = false;
        if (commitFinalSeek)
        {
            var clamped = _clampSeconds(seconds);
            dispatched = DispatchSeek(clamped, _utcNow(), allowDebounce: false);
        }

        _isDragging = false;
        _hasPendingSeek = false;
        return dispatched;
    }

    private bool DispatchSeek(double targetSeconds, DateTime utcNow, bool allowDebounce)
    {
        if (double.IsNaN(targetSeconds) || double.IsInfinity(targetSeconds))
        {
            return false;
        }

        if (allowDebounce && !CanDispatchWithDebounce(targetSeconds, utcNow))
        {
            return false;
        }

        if (!double.IsNaN(_lastSeekSeconds) && Math.Abs(targetSeconds - _lastSeekSeconds) < 0.0005d)
        {
            _lastSeekUtc = utcNow;
            return false;
        }

        _seekSeconds(targetSeconds);
        _lastSeekSeconds = targetSeconds;
        _lastSeekUtc = utcNow;
        return true;
    }

    private bool CanDispatchWithDebounce(double targetSeconds, DateTime utcNow)
    {
        if (double.IsNaN(_lastSeekSeconds))
        {
            return true;
        }

        var interval = CurrentInterval;
        if (utcNow - _lastSeekUtc < interval)
        {
            return false;
        }

        var minDelta = _isSlowMode() ? _slowMinDeltaSeconds : _fastMinDeltaSeconds;
        return Math.Abs(targetSeconds - _lastSeekSeconds) >= minDelta;
    }
}
