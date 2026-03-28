using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using MediaPlayer.Controls.Audio;
using MediaPlayer.Controls.Backends;
using MediaPlayer.Controls.Rendering;
using MediaPlayer.Native.Abstractions;
using MediaPlayer.Native.Interop;

namespace MediaPlayer.Controls;

public sealed class GpuMediaPlayer : OpenGlControlBase, IDisposable
{
    private static readonly IReadOnlyList<MediaAudioDeviceInfo> s_emptyAudioDevices = Array.Empty<MediaAudioDeviceInfo>();
    private static readonly MediaAudioRouteState s_defaultAudioRouteState = new(string.Empty, string.Empty, LoopbackEnabled: false);

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

    public static readonly StyledProperty<double> PlaybackRateProperty =
        AvaloniaProperty.Register<GpuMediaPlayer, double>(nameof(PlaybackRate), 1d);

    public static readonly StyledProperty<VideoLayoutMode> LayoutModeProperty =
        AvaloniaProperty.Register<GpuMediaPlayer, VideoLayoutMode>(nameof(LayoutMode), VideoLayoutMode.Fit);

    public static readonly StyledProperty<bool> PreferDirectGpuTextureUploadProperty =
        AvaloniaProperty.Register<GpuMediaPlayer, bool>(nameof(PreferDirectGpuTextureUpload), true);

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

    public static readonly DirectProperty<GpuMediaPlayer, double> FrameRateProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, double>(
            nameof(FrameRate),
            o => o.FrameRate);

    public static readonly DirectProperty<GpuMediaPlayer, string> ActiveDecodeApiProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(ActiveDecodeApi),
            o => o.ActiveDecodeApi);

    public static readonly DirectProperty<GpuMediaPlayer, string> ActiveProfileNameProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(ActiveProfileName),
            o => o.ActiveProfileName);

    public static readonly DirectProperty<GpuMediaPlayer, string> ActiveRenderPathProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(ActiveRenderPath),
            o => o.ActiveRenderPath);

    public static readonly DirectProperty<GpuMediaPlayer, string> LastErrorProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(LastError),
            o => o.LastError);

    public static readonly DirectProperty<GpuMediaPlayer, string> ConfiguredNativeProviderModeProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(ConfiguredNativeProviderMode),
            o => o.ConfiguredNativeProviderMode);

    public static readonly DirectProperty<GpuMediaPlayer, string> ActiveNativePlaybackProviderProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(ActiveNativePlaybackProvider),
            o => o.ActiveNativePlaybackProvider);

    public static readonly DirectProperty<GpuMediaPlayer, string> NativePlaybackFallbackReasonProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(NativePlaybackFallbackReason),
            o => o.NativePlaybackFallbackReason);

    public static readonly DirectProperty<GpuMediaPlayer, string> BackendCapabilityTableProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, string>(
            nameof(BackendCapabilityTable),
            o => o.BackendCapabilityTable);

    public static readonly DirectProperty<GpuMediaPlayer, MediaAudioCapabilities> AudioCapabilitiesProperty =
        AvaloniaProperty.RegisterDirect<GpuMediaPlayer, MediaAudioCapabilities>(
            nameof(AudioCapabilities),
            o => o.AudioCapabilities);

    private readonly IMediaBackend _backend;
    private readonly OpenGlVideoRenderer _renderer = new();
    private bool _isPlaying;
    private TimeSpan _position;
    private TimeSpan _duration;
    private int _videoWidth;
    private int _videoHeight;
    private double _frameRate;
    private string _activeProfileName;
    private string _activeDecodeApi;
    private string _activeRenderPath;
    private string _lastError = string.Empty;
    private string _configuredNativeProviderMode = string.Empty;
    private string _activeNativePlaybackProvider = string.Empty;
    private string _nativePlaybackFallbackReason = string.Empty;
    private string _backendCapabilityTable = string.Empty;
    private MediaAudioCapabilities _audioCapabilities;
    private long _lastRenderedFrameSequence = -1;
    private bool _disposed;
    private int _renderRequestPending;
    private int _timelineDispatchPending;
    private int _playbackDispatchPending;
    private int _errorDispatchPending;
    private string _pendingErrorMessage = string.Empty;

    public GpuMediaPlayer()
    {
        var nativeOptions = MediaPlayerNativeRuntime.GetOptions();
        _configuredNativeProviderMode = nativeOptions.ProviderMode.ToString();
        _backend = CreateBackendWithFallback(
            nativeOptions.ProviderMode,
            out _lastError,
            out var activeNativeProviderKind,
            out var fallbackReason,
            out var backendCapabilityTable);
        _activeProfileName = _backend.ActiveProfileName;
        _activeDecodeApi = _backend.ActiveDecodeApi;
        _activeRenderPath = _backend.ActiveRenderPath;
        _activeNativePlaybackProvider = activeNativeProviderKind.ToString();
        _nativePlaybackFallbackReason = fallbackReason;
        _backendCapabilityTable = backendCapabilityTable;
        _audioCapabilities = ResolveAudioCapabilities(_backend);
        MediaPlayerNativeRuntime.ReportPlaybackProvider(
            nativeOptions.ProviderMode,
            activeNativeProviderKind,
            fallbackReason);

        _backend.FrameReady += OnFrameReady;
        _backend.PlaybackStateChanged += OnPlaybackStateChanged;
        _backend.TimelineChanged += OnTimelineChanged;
        _backend.ErrorOccurred += OnErrorOccurred;

        _backend.SetVolume((float)Math.Clamp(Volume, 0d, 100d));
        _backend.SetMuted(IsMuted);
        _backend.SetLooping(IsLooping);
        _backend.SetPlaybackRate(Math.Clamp(PlaybackRate, 0.1d, 16d));
        _renderer.SetLayoutMode(LayoutMode);
        _renderer.SetPreferDirectGpuTextureUpload(PreferDirectGpuTextureUpload);

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

    public double PlaybackRate
    {
        get => GetValue(PlaybackRateProperty);
        set => SetValue(PlaybackRateProperty, value);
    }

    public VideoLayoutMode LayoutMode
    {
        get => GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    public bool PreferDirectGpuTextureUpload
    {
        get => GetValue(PreferDirectGpuTextureUploadProperty);
        set => SetValue(PreferDirectGpuTextureUploadProperty, value);
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

    public double FrameRate
    {
        get => _frameRate;
        private set => SetAndRaise(FrameRateProperty, ref _frameRate, value);
    }

    public string ActiveDecodeApi
    {
        get => _activeDecodeApi;
        private set => SetAndRaise(ActiveDecodeApiProperty, ref _activeDecodeApi, value);
    }

    public string ActiveProfileName
    {
        get => _activeProfileName;
        private set => SetAndRaise(ActiveProfileNameProperty, ref _activeProfileName, value);
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

    public string ConfiguredNativeProviderMode
    {
        get => _configuredNativeProviderMode;
        private set => SetAndRaise(ConfiguredNativeProviderModeProperty, ref _configuredNativeProviderMode, value);
    }

    public string ActiveNativePlaybackProvider
    {
        get => _activeNativePlaybackProvider;
        private set => SetAndRaise(ActiveNativePlaybackProviderProperty, ref _activeNativePlaybackProvider, value);
    }

    public string NativePlaybackFallbackReason
    {
        get => _nativePlaybackFallbackReason;
        private set => SetAndRaise(NativePlaybackFallbackReasonProperty, ref _nativePlaybackFallbackReason, value);
    }

    public MediaAudioCapabilities AudioCapabilities
    {
        get => _audioCapabilities;
        private set => SetAndRaise(AudioCapabilitiesProperty, ref _audioCapabilities, value);
    }

    public string BackendCapabilityTable
    {
        get => _backendCapabilityTable;
        private set => SetAndRaise(BackendCapabilityTableProperty, ref _backendCapabilityTable, value);
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

    public IReadOnlyList<MediaTrackInfo> GetAudioTracks()
    {
        EnsureNotDisposed();
        return _backend.GetAudioTracks();
    }

    public IReadOnlyList<MediaTrackInfo> GetSubtitleTracks()
    {
        EnsureNotDisposed();
        return _backend.GetSubtitleTracks();
    }

    public bool SetAudioTrack(int trackId)
    {
        EnsureNotDisposed();
        var changed = _backend.SetAudioTrack(trackId);
        if (changed)
        {
            RequestRender();
        }

        return changed;
    }

    public bool SetSubtitleTrack(int trackId)
    {
        EnsureNotDisposed();
        var changed = _backend.SetSubtitleTrack(trackId);
        if (changed)
        {
            RequestRender();
        }

        return changed;
    }

    public IReadOnlyList<MediaAudioDeviceInfo> GetAudioInputDevices()
    {
        EnsureNotDisposed();
        return _backend is IMediaAudioDeviceController deviceController
            ? deviceController.GetAudioInputDevices()
            : s_emptyAudioDevices;
    }

    public IReadOnlyList<MediaAudioDeviceInfo> GetAudioOutputDevices()
    {
        EnsureNotDisposed();
        return _backend is IMediaAudioDeviceController deviceController
            ? deviceController.GetAudioOutputDevices()
            : s_emptyAudioDevices;
    }

    public MediaAudioRouteState GetAudioRouteState()
    {
        EnsureNotDisposed();
        return _backend is IMediaAudioDeviceController deviceController
            ? deviceController.GetAudioRouteState()
            : s_defaultAudioRouteState;
    }

    public bool TrySetAudioInputDevice(string deviceId)
    {
        EnsureNotDisposed();
        if (_backend is not IMediaAudioDeviceController deviceController)
        {
            return false;
        }

        return deviceController.TrySetAudioInputDevice(deviceId);
    }

    public bool TrySetAudioOutputDevice(string deviceId)
    {
        EnsureNotDisposed();
        if (_backend is not IMediaAudioDeviceController deviceController)
        {
            return false;
        }

        return deviceController.TrySetAudioOutputDevice(deviceId);
    }

    public bool TryGetAudioLevels(out MediaAudioLevels levels)
    {
        EnsureNotDisposed();
        if (_backend is IMediaAudioMetricsProvider metricsProvider)
        {
            return metricsProvider.TryGetAudioLevels(out levels);
        }

        levels = default;
        return false;
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
            var clampedVolume = Math.Clamp(Volume, 0d, 100d);
            if (Math.Abs(Volume - clampedVolume) > 0.0001d)
            {
                SetCurrentValue(VolumeProperty, clampedVolume);
            }
            else
            {
                _backend.SetVolume((float)clampedVolume);
            }
        }
        else if (change.Property == IsMutedProperty)
        {
            _backend.SetMuted(IsMuted);
        }
        else if (change.Property == IsLoopingProperty)
        {
            _backend.SetLooping(IsLooping);
        }
        else if (change.Property == PlaybackRateProperty)
        {
            var clampedRate = Math.Clamp(PlaybackRate, 0.1d, 16d);
            if (Math.Abs(PlaybackRate - clampedRate) > 0.0001d)
            {
                SetCurrentValue(PlaybackRateProperty, clampedRate);
            }
            else
            {
                _backend.SetPlaybackRate(clampedRate);
            }
        }
        else if (change.Property == LayoutModeProperty)
        {
            _renderer.SetLayoutMode(LayoutMode);
            RequestRender();
        }
        else if (change.Property == PreferDirectGpuTextureUploadProperty)
        {
            _renderer.SetPreferDirectGpuTextureUpload(PreferDirectGpuTextureUpload);
            RequestRender();
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
                // Phase 1: copy pixel data into renderer-owned staging buffer while the lease is held.
                // No GL calls are made here. See OpenGlVideoRenderer.CopyFrameToStagingBuffer for
                // the full explanation of why this two-phase approach is required (AMD GPU driver crash).
                _renderer.CopyFrameToStagingBuffer(frame);
                _lastRenderedFrameSequence = frame.Sequence;
            }
            // Phase 2: upload from staging buffer to GL texture after the lease is released.
            // The AMD driver's async AVX2 reads now target renderer-owned memory, not the backend
            // frame buffer that may be freed or GC-moved after the lease is disposed.
            _renderer.UploadStagedFrame(gl);
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
        FrameRate = 0d;
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
            ActiveProfileName = _backend.ActiveProfileName;
            ActiveDecodeApi = _backend.ActiveDecodeApi;
            ActiveRenderPath = _backend.ActiveRenderPath;
            AudioCapabilities = ResolveAudioCapabilities(_backend);
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
            FrameRate = _backend.FrameRate;
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

    private static IMediaBackend CreateBackendWithFallback(
        MediaPlayerNativeProviderMode configuredMode,
        out string initializationMessage,
        out MediaPlayerNativeProviderKind activeProviderKind,
        out string fallbackReason,
        out string backendCapabilityTable)
    {
        var candidates = BuildBackendCandidates(configuredMode, out var modeSelectionWarning);
        backendCapabilityTable = BuildBackendCapabilityTable(candidates);
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                var backend = candidate.Factory();
                activeProviderKind = candidate.Kind;
                fallbackReason = BuildFallbackMessage(
                    modeSelectionWarning,
                    failures,
                    $"Backend fallback active ({candidate.Name}). Previous failures: {string.Join(" | ", failures)}");
                initializationMessage = fallbackReason;
                return backend;
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate.Name}: {ex.Message}");
            }
        }

        activeProviderKind = MediaPlayerNativeProviderKind.Unknown;
        fallbackReason = BuildFallbackMessage(
            modeSelectionWarning,
            failures,
            failures.Count == 0 ? "No backend candidates available." : $"All backends failed: {string.Join(" | ", failures)}");
        initializationMessage = fallbackReason;
        return new NullMediaBackend(initializationMessage);
    }

    private static IReadOnlyList<(string Name, MediaPlayerNativeProviderKind Kind, MediaBackendKind BackendKind, Func<IMediaBackend> Factory)> BuildBackendCandidates(
        MediaPlayerNativeProviderMode mode,
        out string modeSelectionWarning)
    {
        var selection = MediaBackendSelectionPolicy.Build(
            mode,
            GetSelectionPlatform(),
            MediaPlayerInteropPlaybackProviderCatalog.GetPlaybackProviders());
        modeSelectionWarning = selection.ModeSelectionWarning;

        var candidates = new List<(string Name, MediaPlayerNativeProviderKind Kind, MediaBackendKind BackendKind, Func<IMediaBackend> Factory)>(selection.Candidates.Count);
        for (var i = 0; i < selection.Candidates.Count; i++)
        {
            var candidate = selection.Candidates[i];
            var factory = CreateBackendFactory(candidate.BackendKind);
            candidates.Add((candidate.Name, candidate.ProviderKind, candidate.BackendKind, factory));
        }

        if (!FfmpegMediaBackend.IsAudioPlaybackAvailable())
        {
            var hasNonFfmpegCandidate = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!IsFfmpegCandidate(candidates[i].BackendKind))
                {
                    hasNonFfmpegCandidate = true;
                    break;
                }
            }

            if (hasNonFfmpegCandidate)
            {
                var reordered = new List<(string Name, MediaPlayerNativeProviderKind Kind, MediaBackendKind BackendKind, Func<IMediaBackend> Factory)>(candidates.Count);
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (!IsFfmpegCandidate(candidates[i].BackendKind))
                    {
                        reordered.Add(candidates[i]);
                    }
                }

                for (var i = 0; i < candidates.Count; i++)
                {
                    if (IsFfmpegCandidate(candidates[i].BackendKind))
                    {
                        reordered.Add(candidates[i]);
                    }
                }

                candidates = reordered;
            }
        }

        return candidates;
    }

    private static bool IsFfmpegCandidate(MediaBackendKind backendKind)
    {
        return backendKind is MediaBackendKind.MacOsFfmpegProfile
            or MediaBackendKind.WindowsFfmpegProfile
            or MediaBackendKind.FfmpegFallback;
    }

    private static Func<IMediaBackend> CreateBackendFactory(MediaBackendKind backendKind)
    {
        return backendKind switch
        {
            MediaBackendKind.MacOsNativeHelper => static () => new MacOsNativeMediaBackend(),
            MediaBackendKind.WindowsNativeHelper => static () => new WindowsNativeMediaBackend(),
            MediaBackendKind.MacOsFfmpegProfile => static () => new MacOsFfmpegProfileMediaBackend(),
            MediaBackendKind.WindowsFfmpegProfile => static () => new WindowsFfmpegProfileMediaBackend(),
            MediaBackendKind.LibVlcInterop => static () => new LibVlcMediaBackend(),
            _ => static () => new FfmpegMediaBackend()
        };
    }

    private static MediaBackendSelectionPlatform GetSelectionPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return MediaBackendSelectionPlatform.MacOs;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return MediaBackendSelectionPlatform.Windows;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return MediaBackendSelectionPlatform.Linux;
        }

        return MediaBackendSelectionPlatform.Other;
    }

    private static string BuildFallbackMessage(
        string modeSelectionWarning,
        IReadOnlyList<string> backendFailures,
        string terminalMessage)
    {
        if (string.IsNullOrWhiteSpace(modeSelectionWarning) && backendFailures.Count == 0)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(modeSelectionWarning))
        {
            return terminalMessage;
        }

        if (backendFailures.Count == 0)
        {
            return modeSelectionWarning;
        }

        return $"{modeSelectionWarning} | {terminalMessage}";
    }

    private static MediaAudioCapabilities ResolveAudioCapabilities(IMediaBackend backend)
    {
        return backend is IMediaAudioCapabilityProvider capabilityProvider
            ? capabilityProvider.AudioCapabilities
            : MediaAudioCapabilities.None;
    }

    private static string BuildBackendCapabilityTable(
        IReadOnlyList<(string Name, MediaPlayerNativeProviderKind Kind, MediaBackendKind BackendKind, Func<IMediaBackend> Factory)> candidates)
    {
        if (candidates.Count == 0)
        {
            return "No backend candidates available.";
        }

        var parts = new string[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var capabilities = GetExpectedAudioCapabilities(candidate.BackendKind);
            var capabilityText = MediaAudioCapabilityFormatter.ToDisplayString(capabilities);
            parts[index] = $"{candidate.Name}: {capabilityText}";
        }

        return string.Join(" | ", parts);
    }

    private static MediaAudioCapabilities GetExpectedAudioCapabilities(MediaBackendKind backendKind)
    {
        const MediaAudioCapabilities deviceRoutingCapabilities =
            MediaAudioCapabilities.InputDeviceEnumeration
            | MediaAudioCapabilities.OutputDeviceEnumeration;

        return backendKind switch
        {
            MediaBackendKind.LibVlcInterop => MediaAudioCapabilities.VolumeControl
                                              | MediaAudioCapabilities.MuteControl
                                              | MediaAudioCapabilities.AudioTrackEnumeration
                                              | MediaAudioCapabilities.AudioTrackSelection
                                              | deviceRoutingCapabilities,
            MediaBackendKind.MacOsNativeHelper or MediaBackendKind.WindowsNativeHelper => MediaAudioCapabilities.VolumeControl
                | MediaAudioCapabilities.MuteControl
                | MediaAudioCapabilities.AudioTrackEnumeration
                | MediaAudioCapabilities.AudioTrackSelection
                | deviceRoutingCapabilities,
            MediaBackendKind.MacOsFfmpegProfile or MediaBackendKind.WindowsFfmpegProfile or MediaBackendKind.FfmpegFallback
                when FfmpegMediaBackend.IsAudioPlaybackAvailable() => MediaAudioCapabilities.VolumeControl
                                                                      | MediaAudioCapabilities.MuteControl
                                                                      | MediaAudioCapabilities.AudioTrackEnumeration
                                                                      | MediaAudioCapabilities.AudioTrackSelection
                                                                      | deviceRoutingCapabilities,
            MediaBackendKind.MacOsFfmpegProfile or MediaBackendKind.WindowsFfmpegProfile or MediaBackendKind.FfmpegFallback
                => deviceRoutingCapabilities,
            _ => MediaAudioCapabilities.None
        };
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
