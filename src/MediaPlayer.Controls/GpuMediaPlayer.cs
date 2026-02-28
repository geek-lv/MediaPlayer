using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using MediaPlayer.Controls.Backends;
using MediaPlayer.Controls.Rendering;

namespace MediaPlayer.Controls;

public sealed class GpuMediaPlayer : OpenGlControlBase, IDisposable
{
    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<GpuMediaPlayer, Uri?>(nameof(Source));

    public static readonly StyledProperty<bool> AutoPlayProperty =
        AvaloniaProperty.Register<GpuMediaPlayer, bool>(nameof(AutoPlay), true);

    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<GpuMediaPlayer, double>(nameof(Volume), 85d);

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<GpuMediaPlayer, bool>(nameof(IsMuted));

    public static readonly StyledProperty<bool> IsLoopingProperty =
        AvaloniaProperty.Register<GpuMediaPlayer, bool>(nameof(IsLooping));

    public static readonly DirectProperty<GpuMediaPlayer, bool> IsPlayingProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, bool>(
            nameof(IsPlaying),
            o => o.IsPlaying);

    public static readonly DirectProperty<GpuMediaPlayer, TimeSpan> PositionProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, TimeSpan>(
            nameof(Position),
            o => o.Position);

    public static readonly DirectProperty<GpuMediaPlayer, TimeSpan> DurationProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, TimeSpan>(
            nameof(Duration),
            o => o.Duration);

    public static readonly DirectProperty<GpuMediaPlayer, int> VideoWidthProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, int>(
            nameof(VideoWidth),
            o => o.VideoWidth);

    public static readonly DirectProperty<GpuMediaPlayer, int> VideoHeightProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, int>(
            nameof(VideoHeight),
            o => o.VideoHeight);

    public static readonly DirectProperty<GpuMediaPlayer, string> ActiveDecodeApiProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(ActiveDecodeApi),
            o => o.ActiveDecodeApi);

    public static readonly DirectProperty<GpuMediaPlayer, string> ActiveRenderPathProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(ActiveRenderPath),
            o => o.ActiveRenderPath);

    public static readonly DirectProperty<GpuMediaPlayer, string> LastErrorProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(LastError),
            o => o.LastError);

    private readonly IMediaBackend _backend;
    private readonly OpenGlVideoRenderer _renderer = new();
    private bool _isPlaying;
    private TimeSpan _position;
    private TimeSpan _duration;
    private int _videoWidth;
    private int _videoHeight;
    private string _activeDecodeApi;
    private string _activeRenderPath;
    private string _lastError = string.Empty;
    private long _lastRenderedFrameSequence = -1;
    private bool _disposed;
    private int _renderRequestPending;
    private int _timelineDispatchPending;
    private int _playbackDispatchPending;
    private int _errorDispatchPending;
    private string _pendingErrorMessage = string.Empty;

    public GpuMediaPlayer()
    {
        _backend = CreateBackendWithFallback(out _lastError);
        _activeDecodeApi = _backend.ActiveDecodeApi;
        _activeRenderPath = _backend.ActiveRenderPath;

        _backend.FrameReady += OnFrameReady;
        _backend.PlaybackStateChanged += OnPlaybackStateChanged;
        _backend.TimelineChanged += OnTimelineChanged;
        _backend.ErrorOccurred += OnErrorOccurred;

        _backend.SetVolume((float)Volume);
        _backend.SetMuted(IsMuted);
        _backend.SetLooping(IsLooping);

    }

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool AutoPlay
    {
        get => GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    public double Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    public bool IsMuted
    {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public bool IsLooping
    {
        get => GetValue(IsLoopingProperty);
        set => SetValue(IsLoopingProperty, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetAndRaise(IsPlayingProperty, ref _isPlaying, value);
    }

    public TimeSpan Position
    {
        get => _position;
        private set => SetAndRaise(PositionProperty, ref _position, value);
    }

    public TimeSpan Duration
    {
        get => _duration;
        private set => SetAndRaise(DurationProperty, ref _duration, value);
    }

    public int VideoWidth
    {
        get => _videoWidth;
        private set => SetAndRaise(VideoWidthProperty, ref _videoWidth, value);
    }

    public int VideoHeight
    {
        get => _videoHeight;
        private set => SetAndRaise(VideoHeightProperty, ref _videoHeight, value);
    }

    public string ActiveDecodeApi
    {
        get => _activeDecodeApi;
        private set => SetAndRaise(ActiveDecodeApiProperty, ref _activeDecodeApi, value);
    }

    public string ActiveRenderPath
    {
        get => _activeRenderPath;
        private set => SetAndRaise(ActiveRenderPathProperty, ref _activeRenderPath, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetAndRaise(LastErrorProperty, ref _lastError, value);
    }

    public void Play()
    {
        EnsureNotDisposed();
        _backend.Play();
        RequestRender();
    }

    public void Pause()
    {
        EnsureNotDisposed();
        _backend.Pause();
    }

    public void Stop()
    {
        EnsureNotDisposed();
        _backend.Stop();
        RequestRender();
    }

    public void Seek(TimeSpan position)
    {
        EnsureNotDisposed();
        _backend.Seek(position);
        RequestRender();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend.FrameReady -= OnFrameReady;
        _backend.PlaybackStateChanged -= OnPlaybackStateChanged;
        _backend.TimelineChanged -= OnTimelineChanged;
        _backend.ErrorOccurred -= OnErrorOccurred;
        _backend.Dispose();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (_disposed)
        {
            base.OnPropertyChanged(change);
            return;
        }

        if (change.Property == SourceProperty)
        {
            ApplySource(change.GetNewValue<Uri?>());
        }
        else if (change.Property == VolumeProperty)
        {
            _backend.SetVolume((float)Math.Clamp(Volume, 0, 100));
        }
        else if (change.Property == IsMutedProperty)
        {
            _backend.SetMuted(IsMuted);
        }
        else if (change.Property == IsLoopingProperty)
        {
            _backend.SetLooping(IsLooping);
        }

        base.OnPropertyChanged(change);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _renderer.Initialize(gl, GlVersion);
        RequestRender();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer.Dispose(gl);
    }

    protected override void OnOpenGlLost()
    {
        _lastRenderedFrameSequence = -1;
        _renderer.ResetFrameState();
        base.OnOpenGlLost();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_disposed)
        {
            return;
        }

        var scale = VisualRoot?.RenderScaling ?? 1d;
        var pixelWidth = Math.Max(1, (int)(Bounds.Width * scale));
        var pixelHeight = Math.Max(1, (int)(Bounds.Height * scale));

        var latestSequence = _backend.LatestFrameSequence;
        if (latestSequence > _lastRenderedFrameSequence && _backend.TryAcquireFrame(out var frame))
        {
            using (frame)
            {
                _renderer.UploadFrame(gl, frame);
                _lastRenderedFrameSequence = frame.Sequence;
            }
        }

        _renderer.Render(gl, fb, pixelWidth, pixelHeight);

        // Rendering is event-driven from frame callbacks to avoid redraw loops when frame content doesn't change.
    }

    private void ApplySource(Uri? source)
    {
        LastError = string.Empty;
        _lastRenderedFrameSequence = -1;
        VideoWidth = 0;
        VideoHeight = 0;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        IsPlaying = false;
        _renderer.ResetFrameState();

        if (source is null)
        {
            _backend.Stop();
            RequestRender();
            return;
        }

        try
        {
            _backend.Open(source);

            if (AutoPlay)
            {
                _backend.Play();
            }

            RequestRender();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            try
            {
                _backend.Stop();
            }
            catch
            {
                // Best effort cleanup after failed open.
            }

            IsPlaying = false;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            RequestRender();
        }
    }

    private void OnFrameReady(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        RequestRender();
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _playbackDispatchPending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _playbackDispatchPending, 0);

            if (_disposed)
            {
                return;
            }

            IsPlaying = _backend.IsPlaying;
            ActiveDecodeApi = _backend.ActiveDecodeApi;
            ActiveRenderPath = _backend.ActiveRenderPath;
            RequestRender();
        }, DispatcherPriority.Background);
    }

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _timelineDispatchPending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _timelineDispatchPending, 0);

            if (_disposed)
            {
                return;
            }

            Position = _backend.Position;
            Duration = _backend.Duration;
            VideoWidth = _backend.VideoWidth;
            VideoHeight = _backend.VideoHeight;
            RequestRender();
        }, DispatcherPriority.Background);
    }

    private void OnErrorOccurred(object? sender, string message)
    {
        if (_disposed)
        {
            return;
        }

        _pendingErrorMessage = message;
        if (Interlocked.Exchange(ref _errorDispatchPending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _errorDispatchPending, 0);
            if (_disposed)
            {
                return;
            }

            LastError = _pendingErrorMessage;
        }, DispatcherPriority.Background);
    }

    private void RequestRender()
    {
        if (_disposed || Interlocked.Exchange(ref _renderRequestPending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _renderRequestPending, 0);
            if (_disposed)
            {
                return;
            }

            RequestNextFrameRendering();
        }, DispatcherPriority.Render);
    }

    private static IMediaBackend CreateBackendWithFallback(out string initializationMessage)
    {
        var failures = new List<string>();

        foreach (var candidate in EnumerateBackendCandidates())
        {
            try
            {
                var backend = candidate.Factory();
                initializationMessage = failures.Count == 0
                    ? string.Empty
                    : $"Backend fallback active ({candidate.Name}). Previous failures: {string.Join(" | ", failures)}";
                return backend;
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate.Name}: {ex.Message}");
            }
        }

        initializationMessage = failures.Count == 0
            ? "No backend candidates available."
            : $"All backends failed: {string.Join(" | ", failures)}";
        return new NullMediaBackend(initializationMessage);
    }

    private static IEnumerable<(string Name, Func<IMediaBackend> Factory)> EnumerateBackendCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return ("macOS Native Interop", static () => new MacOsNativeMediaBackend());
            yield return ("macOS FFmpeg native profile", static () => new MacOsFfmpegProfileMediaBackend());
            yield return ("LibVLC", static () => new LibVlcMediaBackend());
            yield return ("FFmpeg fallback", static () => new FfmpegMediaBackend());
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return ("Windows Native Interop", static () => new WindowsNativeMediaBackend());
            yield return ("Windows FFmpeg native profile", static () => new WindowsFfmpegProfileMediaBackend());
            yield return ("LibVLC", static () => new LibVlcMediaBackend());
            yield return ("FFmpeg fallback", static () => new FfmpegMediaBackend());
            yield break;
        }

        yield return ("LibVLC", static () => new LibVlcMediaBackend());
        yield return ("FFmpeg fallback", static () => new FfmpegMediaBackend());
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
