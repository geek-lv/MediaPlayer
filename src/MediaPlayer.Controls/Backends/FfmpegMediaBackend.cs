using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaPlayer.Controls.Audio;

namespace MediaPlayer.Controls.Backends;

internal class FfmpegMediaBackend : IMediaBackend, IMediaAudioCapabilityProvider, IMediaAudioPlaybackController, IMediaAudioDeviceController
{
    private static readonly IReadOnlyList<MediaTrackInfo> s_emptyTracks = Array.Empty<MediaTrackInfo>();
    private static readonly IReadOnlyList<MediaAudioDeviceInfo> s_defaultInputDevices = MediaAudioDeviceCatalog.CreateDefaultInputDevices("ffmpeg");
    private static readonly IReadOnlyList<MediaAudioDeviceInfo> s_defaultOutputDevices = MediaAudioDeviceCatalog.CreateDefaultOutputDevices("ffmpeg");
    private readonly object _frameGate = new();
    private readonly object _processGate = new();
    private readonly object _stateGate = new();
    private readonly object _trackGate = new();
    private static readonly bool s_canSuspendProcesses =
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private Process? _videoProcess;
    private Process? _audioProcess;
    private CancellationTokenSource? _decodeCts;
    private Task? _decodeTask;
    private CancellationTokenSource? _previewCts;
    private byte[]? _frameBuffer;
    private GCHandle _pinnedFrameBuffer;
    private int _frameWidth;
    private int _frameHeight;
    private int _frameStride;
    private bool _looping;
    private bool _disposed;

    private Uri? _source;
    private TimeSpan _duration;
    private TimeSpan _position;
    private long _playbackStartedTimestamp;
    private TimeSpan _positionAtPlayStart;
    private bool _isPlaying;
    private long _latestFrameSequence;
    private readonly bool _ffplayAvailable;
    private readonly FfmpegBackendProfile _profile;
    private DecodeMode _decodeMode;
    private bool _cpuFallbackApplied;
    private bool _processesSuspended;
    private float _volume = 85f;
    private bool _muted;
    private double _playbackRate = 1d;
    private double _frameRate;
    private readonly List<TrackDescriptor> _audioTracks = [];
    private readonly List<TrackDescriptor> _subtitleTracks = [];
    private int _selectedAudioTrackId = -1;
    private int _selectedSubtitleTrackId = -1;
    private string _selectedInputDeviceId = MediaAudioDeviceCatalog.PlatformDefaultInputDeviceId;
    private string _selectedOutputDeviceId = MediaAudioDeviceCatalog.PlatformDefaultOutputDeviceId;

    private enum DecodeMode
    {
        Hardware,
        Software
    }

    private readonly record struct TrackDescriptor(int Id, string Name, int StreamIndex, int StreamOrdinal);

    public FfmpegMediaBackend()
        : this(FfmpegBackendProfiles.GenericFallback())
    {
    }

    protected FfmpegMediaBackend(FfmpegBackendProfile profile)
    {
        _profile = profile;
        _decodeMode = profile.SupportsHardwareAcceleration ? DecodeMode.Hardware : DecodeMode.Software;
        _ffplayAvailable = IsToolAvailable(ProcessCommandResolver.ResolveFfplayExecutable());
    }

    public event EventHandler? FrameReady;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? TimelineChanged;
    public event EventHandler<string>? ErrorOccurred;

    public string ActiveProfileName => _profile.ProfileName;

    public string ActiveDecodeApi
    {
        get
        {
            lock (_stateGate)
            {
                return _decodeMode == DecodeMode.Hardware
                    ? _profile.HardwareDecodeApi
                    : _profile.SoftwareDecodeApi;
            }
        }
    }

    public string ActiveRenderPath => _profile.RenderPath;

    public bool IsPlaying
    {
        get
        {
            lock (_stateGate)
            {
                return _isPlaying;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_stateGate)
            {
                if (!_isPlaying)
                {
                    return _position;
                }

                var elapsedSeconds = GetElapsedSeconds(_playbackStartedTimestamp);
                var current = _positionAtPlayStart + TimeSpan.FromSeconds(elapsedSeconds * _playbackRate);
                if (_duration > TimeSpan.Zero && current > _duration)
                {
                    current = _duration;
                }

                return current;
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_stateGate)
            {
                return _duration;
            }
        }
    }

    public int VideoWidth => _frameWidth;

    public int VideoHeight => _frameHeight;

    public double FrameRate
    {
        get
        {
            lock (_stateGate)
            {
                return _frameRate;
            }
        }
    }

    public double PlaybackRate
    {
        get
        {
            lock (_stateGate)
            {
                return _playbackRate;
            }
        }
    }

    public long LatestFrameSequence => Interlocked.Read(ref _latestFrameSequence);

    public MediaAudioCapabilities AudioCapabilities =>
        (_ffplayAvailable
            ? MediaAudioCapabilities.VolumeControl
              | MediaAudioCapabilities.MuteControl
              | MediaAudioCapabilities.AudioTrackEnumeration
              | MediaAudioCapabilities.AudioTrackSelection
            : MediaAudioCapabilities.None)
        | MediaAudioCapabilities.InputDeviceEnumeration
        | MediaAudioCapabilities.OutputDeviceEnumeration;

    public bool SupportsVolumeControl => _ffplayAvailable;

    public bool SupportsMuteControl => _ffplayAvailable;

    public float Volume => _volume;

    public bool IsMuted => _muted;

    public IReadOnlyList<MediaAudioDeviceInfo> GetAudioInputDevices() => s_defaultInputDevices;

    public IReadOnlyList<MediaAudioDeviceInfo> GetAudioOutputDevices() => s_defaultOutputDevices;

    public MediaAudioRouteState GetAudioRouteState()
    {
        lock (_stateGate)
        {
            return new MediaAudioRouteState(_selectedInputDeviceId, _selectedOutputDeviceId, LoopbackEnabled: false);
        }
    }

    public bool TrySetAudioInputDevice(string deviceId)
    {
        ThrowIfDisposed();
        var normalized = MediaAudioDeviceCatalog.NormalizeDeviceId(deviceId, MediaAudioDeviceCatalog.PlatformDefaultInputDeviceId);
        if (!MediaAudioDeviceCatalog.TryGetCanonicalDeviceId(s_defaultInputDevices, normalized, out var canonical))
        {
            return false;
        }

        lock (_stateGate)
        {
            _selectedInputDeviceId = canonical;
        }

        return true;
    }

    public bool TrySetAudioOutputDevice(string deviceId)
    {
        ThrowIfDisposed();
        var normalized = MediaAudioDeviceCatalog.NormalizeDeviceId(deviceId, MediaAudioDeviceCatalog.PlatformDefaultOutputDeviceId);
        if (!MediaAudioDeviceCatalog.TryGetCanonicalDeviceId(s_defaultOutputDevices, normalized, out var canonical))
        {
            return false;
        }

        lock (_stateGate)
        {
            _selectedOutputDeviceId = canonical;
        }

        return true;
    }

    public void Open(Uri source)
    {
        ThrowIfDisposed();

        StopProcesses(resetPosition: false);
        ReleaseFrameBuffer();

        if (!TryProbeSource(
                source,
                out var width,
                out var height,
                out var duration,
                out var frameRate,
                out var audioTracks,
                out var subtitleTracks,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        _source = source;
        lock (_stateGate)
        {
            _duration = duration;
            _position = TimeSpan.Zero;
            _positionAtPlayStart = TimeSpan.Zero;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
            _isPlaying = false;
            _decodeMode = _profile.SupportsHardwareAcceleration ? DecodeMode.Hardware : DecodeMode.Software;
            _cpuFallbackApplied = false;
            _frameRate = frameRate;
        }

        lock (_trackGate)
        {
            _audioTracks.Clear();
            _audioTracks.AddRange(audioTracks);
            _subtitleTracks.Clear();
            _subtitleTracks.AddRange(subtitleTracks);
            _selectedAudioTrackId = _audioTracks.Count > 0 ? _audioTracks[0].Id : -1;
            _selectedSubtitleTrackId = -1;
        }

        _frameWidth = width;
        _frameHeight = height;
        _frameStride = width * 4;
        AllocateFrameBuffer();

        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Play()
    {
        ThrowIfDisposed();

        Uri? source;
        TimeSpan startPosition;
        lock (_stateGate)
        {
            source = _source;
            if (_isPlaying)
            {
                return;
            }

            if (source is null)
            {
                ErrorOccurred?.Invoke(this, "No source loaded.");
                return;
            }

            startPosition = _position;
            _positionAtPlayStart = _position;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
            _isPlaying = true;
        }

        CancelPreviewFrameRequest();
        if (!TryResumeProcesses()
            && !TryRestartProcesses(
                source,
                startPosition,
                "Unable to start FFmpeg playback process.",
                markNotPlayingOnFailure: true))
        {
            return;
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        ThrowIfDisposed();

        lock (_stateGate)
        {
            if (!_isPlaying)
            {
                return;
            }

            _position = Position;
            _isPlaying = false;
        }

        if (!TrySuspendProcesses())
        {
            StopProcesses(resetPosition: false);
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        ThrowIfDisposed();

        lock (_stateGate)
        {
            _isPlaying = false;
            _position = TimeSpan.Zero;
        }

        StopProcesses(resetPosition: false);
        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();

        Uri? source;
        bool shouldRestartPlayback;
        lock (_stateGate)
        {
            source = _source;
            if (source is null)
            {
                return;
            }

            var duration = _duration;
            var clamped = duration > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(Math.Clamp(position.TotalMilliseconds, 0, duration.TotalMilliseconds))
            : TimeSpan.FromMilliseconds(Math.Max(0, position.TotalMilliseconds));

            _position = clamped;
            _positionAtPlayStart = clamped;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
            shouldRestartPlayback = _isPlaying;
            position = clamped;
        }

        if (shouldRestartPlayback)
        {
            if (!TryRestartProcesses(
                    source,
                    position,
                    "Seek restart failed.",
                    markNotPlayingOnFailure: true))
            {
                return;
            }
        }
        else
        {
            var suspended = false;
            lock (_processGate)
            {
                suspended = _processesSuspended;
            }

            if (suspended)
            {
                StopProcesses(resetPosition: false);
            }

            RequestPreviewFrame(source, position);
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVolume(float volume)
    {
        ThrowIfDisposed();
        var clamped = Math.Clamp(volume, 0f, 100f);
        bool changed;
        Uri? source;
        bool isPlaying;
        TimeSpan restartPosition;
        lock (_stateGate)
        {
            changed = Math.Abs(_volume - clamped) > 0.01f;
            _volume = clamped;
            if (!changed)
            {
                return;
            }

            source = _source;
            isPlaying = _isPlaying;
            restartPosition = _isPlaying ? Position : _position;
            _position = restartPosition;
            _positionAtPlayStart = restartPosition;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
        }

        if (source is null || !_ffplayAvailable)
        {
            return;
        }

        ApplyAudioOutputChange(source, isPlaying, restartPosition, "Volume update restart failed.");
    }

    public void SetMuted(bool muted)
    {
        ThrowIfDisposed();
        bool changed;
        Uri? source;
        bool isPlaying;
        TimeSpan restartPosition;
        lock (_stateGate)
        {
            changed = _muted != muted;
            _muted = muted;
            if (!changed)
            {
                return;
            }

            source = _source;
            isPlaying = _isPlaying;
            restartPosition = _isPlaying ? Position : _position;
            _position = restartPosition;
            _positionAtPlayStart = restartPosition;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
        }

        if (source is null || !_ffplayAvailable)
        {
            return;
        }

        ApplyAudioOutputChange(source, isPlaying, restartPosition, "Mute update restart failed.");
    }

    public void SetLooping(bool looping)
    {
        _looping = looping;
    }

    public void SetPlaybackRate(double rate)
    {
        ThrowIfDisposed();
        var clamped = Math.Clamp(rate, 0.1d, 16d);
        Uri? source;
        bool wasPlaying;
        TimeSpan restartPosition;

        lock (_stateGate)
        {
            _playbackRate = clamped;
            source = _source;
            wasPlaying = _isPlaying;
            restartPosition = _isPlaying ? Position : _position;
            _position = restartPosition;
            _positionAtPlayStart = restartPosition;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
        }

        if (source is null)
        {
            return;
        }

        if (wasPlaying)
        {
            if (!TryRestartProcesses(
                    source,
                    restartPosition,
                    "Playback-rate restart failed.",
                    markNotPlayingOnFailure: true))
            {
                return;
            }
        }
        else
        {
            var suspended = false;
            lock (_processGate)
            {
                suspended = _processesSuspended;
            }

            if (suspended)
            {
                StopProcesses(resetPosition: false);
            }

            RequestPreviewFrame(source, restartPosition);
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MediaTrackInfo> GetAudioTracks()
    {
        if (!_ffplayAvailable)
        {
            return s_emptyTracks;
        }

        lock (_trackGate)
        {
            if (_audioTracks.Count == 0)
            {
                return s_emptyTracks;
            }

            var snapshot = new MediaTrackInfo[_audioTracks.Count];
            for (var i = 0; i < _audioTracks.Count; i++)
            {
                var track = _audioTracks[i];
                snapshot[i] = new MediaTrackInfo(track.Id, track.Name, track.Id == _selectedAudioTrackId);
            }

            return snapshot;
        }
    }

    public IReadOnlyList<MediaTrackInfo> GetSubtitleTracks()
    {
        lock (_stateGate)
        {
            if (_source is null || !_source.IsFile)
            {
                return new[] { new MediaTrackInfo(-1, "Off", true) };
            }
        }

        lock (_trackGate)
        {
            if (_subtitleTracks.Count == 0)
            {
                return new[] { new MediaTrackInfo(-1, "Off", true) };
            }

            var snapshot = new MediaTrackInfo[_subtitleTracks.Count + 1];
            snapshot[0] = new MediaTrackInfo(-1, "Off", _selectedSubtitleTrackId < 0);
            for (var i = 0; i < _subtitleTracks.Count; i++)
            {
                var track = _subtitleTracks[i];
                snapshot[i + 1] = new MediaTrackInfo(track.Id, track.Name, track.Id == _selectedSubtitleTrackId);
            }

            return snapshot;
        }
    }

    public bool SetAudioTrack(int trackId)
    {
        ThrowIfDisposed();
        if (!_ffplayAvailable)
        {
            return false;
        }

        bool changed;
        lock (_trackGate)
        {
            if (_audioTracks.Count == 0)
            {
                return false;
            }

            var exists = false;
            for (var i = 0; i < _audioTracks.Count; i++)
            {
                if (_audioTracks[i].Id == trackId)
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                return false;
            }

            changed = _selectedAudioTrackId != trackId;
            _selectedAudioTrackId = trackId;
        }

        if (!changed)
        {
            return true;
        }

        ApplyTrackSelectionChange(refreshPreviewFrame: false);
        return true;
    }

    public bool SetSubtitleTrack(int trackId)
    {
        ThrowIfDisposed();
        bool changed;
        Uri? source;
        lock (_stateGate)
        {
            source = _source;
        }

        if (trackId >= 0 && (source is null || !source.IsFile))
        {
            return false;
        }

        lock (_trackGate)
        {
            if (trackId >= 0)
            {
                var exists = false;
                for (var i = 0; i < _subtitleTracks.Count; i++)
                {
                    if (_subtitleTracks[i].Id == trackId)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    return false;
                }
            }

            changed = _selectedSubtitleTrackId != trackId;
            _selectedSubtitleTrackId = trackId;
        }

        if (!changed)
        {
            return true;
        }

        ApplyTrackSelectionChange(refreshPreviewFrame: true);
        return true;
    }

    public bool TryAcquireFrame(out MediaFrameLease frame)
    {
        frame = default;

        if (_disposed)
        {
            return false;
        }

        Monitor.Enter(_frameGate);
        if (_disposed || !_pinnedFrameBuffer.IsAllocated || _frameBuffer is null || _frameWidth <= 0 || _frameHeight <= 0 || _frameStride <= 0)
        {
            Monitor.Exit(_frameGate);
            return false;
        }

        frame = new MediaFrameLease(
            _frameGate,
            _pinnedFrameBuffer.AddrOfPinnedObject(),
            _frameWidth,
            _frameHeight,
            _frameStride,
            MediaFramePixelFormat.Rgba32,
            LatestFrameSequence);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPreviewFrameRequest();
        StopProcesses(resetPosition: false);
        ReleaseFrameBuffer();
    }

    private void ApplyTrackSelectionChange(bool refreshPreviewFrame)
    {
        Uri? source;
        bool isPlaying;
        TimeSpan targetPosition;
        lock (_stateGate)
        {
            source = _source;
            if (source is null)
            {
                return;
            }

            isPlaying = _isPlaying;
            targetPosition = isPlaying ? Position : _position;
            _position = targetPosition;
            _positionAtPlayStart = targetPosition;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
        }

        StopProcesses(resetPosition: false);
        if (isPlaying)
        {
            if (!TryRestartProcesses(
                    source,
                    targetPosition,
                    "Track-selection restart failed.",
                    markNotPlayingOnFailure: true))
            {
                return;
            }
        }
        else if (refreshPreviewFrame)
        {
            RequestPreviewFrame(source, targetPosition);
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAudioOutputChange(Uri source, bool isPlaying, TimeSpan restartPosition, string failurePrefix)
    {
        if (isPlaying)
        {
            if (!TryRestartAudioProcess(source, restartPosition))
            {
                ErrorOccurred?.Invoke(this, failurePrefix);
            }

            TimelineChanged?.Invoke(this, EventArgs.Empty);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var suspended = false;
        lock (_processGate)
        {
            suspended = _processesSuspended;
        }

        if (suspended)
        {
            StopProcesses(resetPosition: false);
            RequestPreviewFrame(source, restartPosition);
            TimelineChanged?.Invoke(this, EventArgs.Empty);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryRestartAudioProcess(Uri source, TimeSpan startPosition)
    {
        Process? previousAudioProcess;
        lock (_processGate)
        {
            previousAudioProcess = _audioProcess;
            _audioProcess = null;
        }

        KillProcess(previousAudioProcess);
        var restarted = TryStartAudioProcess(source, startPosition, out var failureReason);
        if (restarted is null)
        {
            if (!string.IsNullOrWhiteSpace(failureReason))
            {
                ErrorOccurred?.Invoke(this, failureReason);
            }

            return false;
        }

        lock (_processGate)
        {
            if (_disposed)
            {
                KillProcess(restarted);
                return false;
            }

            _audioProcess = restarted;
        }

        return true;
    }

    private void StartProcesses(Uri source, TimeSpan startPosition)
    {
        lock (_processGate)
        {
            _processesSuspended = false;
            var decodeCts = new CancellationTokenSource();
            var videoProcess = StartVideoProcess(source, startPosition);

            _decodeCts = decodeCts;
            _videoProcess = videoProcess;

            if (_ffplayAvailable)
            {
                _audioProcess = TryStartAudioProcess(source, startPosition, out var audioFailureReason);
                if (_audioProcess is null && !string.IsNullOrWhiteSpace(audioFailureReason))
                {
                    ErrorOccurred?.Invoke(this, audioFailureReason);
                }
            }

            _decodeTask = Task.Run(() => ReadFramesLoopAsync(videoProcess, decodeCts.Token));
        }
    }

    private void StopProcesses(bool resetPosition)
    {
        CancellationTokenSource? decodeCts;
        Process? videoProcess;
        Process? audioProcess;
        Task? decodeTask;

        lock (_processGate)
        {
            _processesSuspended = false;
            decodeCts = _decodeCts;
            videoProcess = _videoProcess;
            audioProcess = _audioProcess;
            decodeTask = _decodeTask;

            _videoProcess = null;
            _audioProcess = null;
            _decodeTask = null;
            _decodeCts = null;
        }

        CancelPreviewFrameRequest();
        decodeCts?.Cancel();
        KillProcess(videoProcess);
        KillProcess(audioProcess);

        if (decodeTask is not null && decodeTask.Id != Task.CurrentId)
        {
            try
            {
                decodeTask.Wait(TimeSpan.FromMilliseconds(16));
            }
            catch
            {
                // Ignore shutdown races.
            }
        }

        decodeCts?.Dispose();

        if (resetPosition)
        {
            lock (_stateGate)
            {
                _position = TimeSpan.Zero;
            }
        }
    }

    private bool TrySuspendProcesses()
    {
        if (!s_canSuspendProcesses)
        {
            return false;
        }

        lock (_processGate)
        {
            if (_processesSuspended)
            {
                return true;
            }

            if (_videoProcess is null || _videoProcess.HasExited)
            {
                return false;
            }

            if (!TrySignalProcess(_videoProcess, GetSuspendSignal()))
            {
                return false;
            }

            if (!TrySignalProcess(_audioProcess, GetSuspendSignal()))
            {
                TrySignalProcess(_videoProcess, GetResumeSignal());
                return false;
            }

            _processesSuspended = true;
            return true;
        }
    }

    private bool TryResumeProcesses()
    {
        if (!s_canSuspendProcesses)
        {
            return false;
        }

        lock (_processGate)
        {
            if (!_processesSuspended)
            {
                return false;
            }

            if (!TrySignalProcess(_videoProcess, GetResumeSignal()))
            {
                _processesSuspended = false;
                return false;
            }

            if (!TrySignalProcess(_audioProcess, GetResumeSignal()))
            {
                _processesSuspended = false;
                return false;
            }

            _processesSuspended = false;
            return true;
        }
    }

    private void RequestPreviewFrame(Uri source, TimeSpan position)
    {
        if (_disposed || _frameWidth <= 0 || _frameHeight <= 0 || _frameStride <= 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_processGate)
        {
            previous = _previewCts;
            _previewCts = cts;
        }

        previous?.Cancel();
        previous?.Dispose();

        _ = Task.Run(() => DecodePreviewFrame(source, position, cts.Token), cts.Token);
    }

    private void CancelPreviewFrameRequest()
    {
        CancellationTokenSource? previewCts;
        lock (_processGate)
        {
            previewCts = _previewCts;
            _previewCts = null;
        }

        previewCts?.Cancel();
        previewCts?.Dispose();
    }

    private void DecodePreviewFrame(Uri source, TimeSpan position, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            process = StartPreviewProcess(source, position);
            using var _ = cancellationToken.Register(static state => KillProcess((Process?)state), process);

            var frameBytes = checked(_frameStride * _frameHeight);
            var scratch = new byte[frameBytes];
            var read = ReadExactlyAsync(process.StandardOutput.BaseStream, scratch, frameBytes, cancellationToken)
                .GetAwaiter()
                .GetResult();

            if (read < frameBytes || cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            Monitor.Enter(_frameGate);
            try
            {
                if (_frameBuffer is not null)
                {
                    Buffer.BlockCopy(scratch, 0, _frameBuffer, 0, frameBytes);
                    Interlocked.Increment(ref _latestFrameSequence);
                }
            }
            finally
            {
                Monitor.Exit(_frameGate);
            }

            FrameReady?.Invoke(this, EventArgs.Empty);
            TimelineChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Ignore canceled preview seeks.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ErrorOccurred?.Invoke(this, $"Preview frame decode failed: {ex.Message}");
            }
        }
        finally
        {
            KillProcess(process);
        }
    }

    private Process StartVideoProcess(Uri source, TimeSpan startPosition)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ProcessCommandResolver.ConfigureTool(psi, ProcessCommandResolver.ResolveFfmpegExecutable());

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-fflags");
        psi.ArgumentList.Add("nobuffer");
        psi.ArgumentList.Add("-flags");
        psi.ArgumentList.Add("low_delay");

        DecodeMode decodeModeSnapshot;
        double playbackRateSnapshot;
        TrackDescriptor? selectedSubtitleTrack;
        int selectedSubtitleTrackId;
        lock (_stateGate)
        {
            decodeModeSnapshot = _decodeMode;
            playbackRateSnapshot = _playbackRate;
        }
        lock (_trackGate)
        {
            selectedSubtitleTrackId = _selectedSubtitleTrackId;
            selectedSubtitleTrack = FindTrackByIdNoLock(_subtitleTracks, _selectedSubtitleTrackId);
        }

        if (decodeModeSnapshot == DecodeMode.Hardware)
        {
            foreach (var arg in _profile.HardwareAccelerationArgs)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        if (startPosition > TimeSpan.Zero)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startPosition.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (Math.Abs(playbackRateSnapshot - 1d) < 0.0001d)
        {
            psi.ArgumentList.Add("-re");
        }
        else
        {
            psi.ArgumentList.Add("-readrate");
            psi.ArgumentList.Add(playbackRateSnapshot.ToString("0.###", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(source.IsFile ? source.LocalPath : source.ToString());
        psi.ArgumentList.Add("-an");
        if (selectedSubtitleTrack is null)
        {
            psi.ArgumentList.Add("-sn");
        }

        psi.ArgumentList.Add("-dn");

        if (TryBuildVideoFilterChain(source, selectedSubtitleTrack, out var filterChain, out var filterWarning))
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(filterChain);
        }

        if (!string.IsNullOrWhiteSpace(filterWarning))
        {
            ErrorOccurred?.Invoke(this, filterWarning);
            lock (_trackGate)
            {
                if (selectedSubtitleTrackId == _selectedSubtitleTrackId)
                {
                    _selectedSubtitleTrackId = -1;
                }
            }
        }

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("rgba");
        psi.ArgumentList.Add("pipe:1");

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Unable to start ffmpeg process.");

        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    ErrorOccurred?.Invoke(this, stderr.Trim());
                }
            }
            catch
            {
                // Ignore.
            }
        });

        return process;
    }

    private Process StartPreviewProcess(Uri source, TimeSpan position)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ProcessCommandResolver.ConfigureTool(psi, ProcessCommandResolver.ResolveFfmpegExecutable());

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-fflags");
        psi.ArgumentList.Add("nobuffer");
        psi.ArgumentList.Add("-flags");
        psi.ArgumentList.Add("low_delay");

        DecodeMode decodeModeSnapshot;
        TrackDescriptor? selectedSubtitleTrack;
        int selectedSubtitleTrackId;
        lock (_stateGate)
        {
            decodeModeSnapshot = _decodeMode;
        }
        lock (_trackGate)
        {
            selectedSubtitleTrackId = _selectedSubtitleTrackId;
            selectedSubtitleTrack = FindTrackByIdNoLock(_subtitleTracks, _selectedSubtitleTrackId);
        }

        if (decodeModeSnapshot == DecodeMode.Hardware)
        {
            foreach (var arg in _profile.HardwareAccelerationArgs)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        if (position > TimeSpan.Zero)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(source.IsFile ? source.LocalPath : source.ToString());
        psi.ArgumentList.Add("-an");
        if (selectedSubtitleTrack is null)
        {
            psi.ArgumentList.Add("-sn");
        }

        psi.ArgumentList.Add("-dn");
        if (TryBuildSubtitleFilter(source, selectedSubtitleTrack, out var subtitleFilter, out var filterWarning))
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(subtitleFilter);
        }

        if (!string.IsNullOrWhiteSpace(filterWarning))
        {
            ErrorOccurred?.Invoke(this, filterWarning);
            lock (_trackGate)
            {
                if (selectedSubtitleTrackId == _selectedSubtitleTrackId)
                {
                    _selectedSubtitleTrackId = -1;
                }
            }
        }

        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("rgba");
        psi.ArgumentList.Add("pipe:1");

        return Process.Start(psi)
            ?? throw new InvalidOperationException("Unable to start ffmpeg preview process.");
    }

    private Process? TryStartAudioProcess(Uri source, TimeSpan startPosition, out string? failureReason)
    {
        failureReason = null;
        try
        {
            double playbackRateSnapshot;
            float volumeSnapshot;
            bool mutedSnapshot;
            TrackDescriptor? selectedAudioTrack;
            lock (_stateGate)
            {
                playbackRateSnapshot = _playbackRate;
                volumeSnapshot = _volume;
                mutedSnapshot = _muted;
            }
            lock (_trackGate)
            {
                selectedAudioTrack = FindTrackByIdNoLock(_audioTracks, _selectedAudioTrackId);
            }

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };
            ProcessCommandResolver.ConfigureTool(psi, ProcessCommandResolver.ResolveFfplayExecutable());

            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("quiet");
            psi.ArgumentList.Add("-nodisp");
            psi.ArgumentList.Add("-autoexit");
            psi.ArgumentList.Add("-volume");
            psi.ArgumentList.Add(((int)Math.Clamp(Math.Round(mutedSnapshot ? 0f : volumeSnapshot), 0, 100)).ToString(CultureInfo.InvariantCulture));
            if (selectedAudioTrack is not null)
            {
                psi.ArgumentList.Add("-ast");
                psi.ArgumentList.Add($"a:{selectedAudioTrack.Value.StreamOrdinal}");
            }

            if (Math.Abs(playbackRateSnapshot - 1d) > 0.0001d)
            {
                psi.ArgumentList.Add("-af");
                psi.ArgumentList.Add(BuildAtempoFilter(playbackRateSnapshot));
            }

            if (startPosition > TimeSpan.Zero)
            {
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add(startPosition.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }

            psi.ArgumentList.Add(source.IsFile ? source.LocalPath : source.ToString());
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            failureReason = $"Audio playback process failed to start (ffplay): {ex.Message}";
            return null;
        }
    }

    private async Task ReadFramesLoopAsync(Process process, CancellationToken cancellationToken)
    {
        var frameBytes = checked(_frameStride * _frameHeight);
        var stream = process.StandardOutput.BaseStream;
        var scratch = new byte[frameBytes];
        var framesDecoded = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await ReadExactlyAsync(stream, scratch, frameBytes, cancellationToken).ConfigureAwait(false);
            if (read < frameBytes)
            {
                if (!cancellationToken.IsCancellationRequested && framesDecoded == 0 && TrySwitchToSoftwareDecode())
                {
                    return;
                }

                break;
            }

            Monitor.Enter(_frameGate);
            try
            {
                if (_frameBuffer is not null)
                {
                    Buffer.BlockCopy(scratch, 0, _frameBuffer, 0, frameBytes);
                    Interlocked.Increment(ref _latestFrameSequence);
                }
            }
            finally
            {
                Monitor.Exit(_frameGate);
            }

            framesDecoded++;
            FrameReady?.Invoke(this, EventArgs.Empty);
            TimelineChanged?.Invoke(this, EventArgs.Empty);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (_looping && _source is not null)
        {
            lock (_stateGate)
            {
                _position = TimeSpan.Zero;
                _positionAtPlayStart = TimeSpan.Zero;
                _playbackStartedTimestamp = Stopwatch.GetTimestamp();
            }

            if (!TryRestartProcesses(
                    _source,
                    TimeSpan.Zero,
                    "Loop restart failed.",
                    markNotPlayingOnFailure: true))
            {
                return;
            }

            return;
        }

        lock (_stateGate)
        {
            _isPlaying = false;
            _position = _duration > TimeSpan.Zero ? _duration : _position;
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int expected, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < expected && !cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, expected - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static double GetElapsedSeconds(long startedTimestamp)
    {
        if (startedTimestamp <= 0)
        {
            return 0d;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        if (elapsedTicks <= 0)
        {
            return 0d;
        }

        return elapsedTicks / (double)Stopwatch.Frequency;
    }

    private bool TrySwitchToSoftwareDecode()
    {
        Uri? source;
        TimeSpan restartPosition;

        lock (_stateGate)
        {
            if (!_profile.SupportsHardwareAcceleration
                || _decodeMode != DecodeMode.Hardware
                || _cpuFallbackApplied
                || !_isPlaying
                || _source is null)
            {
                return false;
            }

            _decodeMode = DecodeMode.Software;
            _cpuFallbackApplied = true;

            var current = Position;
            _position = current;
            _positionAtPlayStart = current;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
            source = _source;
            restartPosition = current;
        }

        if (!TryRestartProcesses(
                source,
                restartPosition,
                "CPU decode fallback restart failed.",
                markNotPlayingOnFailure: true))
        {
            return false;
        }

        ErrorOccurred?.Invoke(this, "Hardware decode path failed. Switched to CPU decode fallback.");
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        TimelineChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool TryRestartProcesses(
        Uri source,
        TimeSpan startPosition,
        string failurePrefix,
        bool markNotPlayingOnFailure)
    {
        try
        {
            StopProcesses(resetPosition: false);
            StartProcesses(source, startPosition);
            return true;
        }
        catch (Exception ex)
        {
            lock (_stateGate)
            {
                if (markNotPlayingOnFailure)
                {
                    _isPlaying = false;
                }

                _position = startPosition;
                _positionAtPlayStart = startPosition;
                _playbackStartedTimestamp = Stopwatch.GetTimestamp();
            }

            ErrorOccurred?.Invoke(this, $"{failurePrefix} {ex.Message}");
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            TimelineChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    private bool TryProbeSource(
        Uri source,
        out int width,
        out int height,
        out TimeSpan duration,
        out double frameRate,
        out List<TrackDescriptor> audioTracks,
        out List<TrackDescriptor> subtitleTracks,
        out string error)
    {
        width = 0;
        height = 0;
        duration = TimeSpan.Zero;
        frameRate = 0d;
        audioTracks = [];
        subtitleTracks = [];
        error = string.Empty;

        if (!IsToolAvailable(ProcessCommandResolver.ResolveFfprobeExecutable()))
        {
            error = "ffprobe is required for FFmpeg fallback backend.";
            return false;
        }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ProcessCommandResolver.ConfigureTool(psi, ProcessCommandResolver.ResolveFfprobeExecutable());

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=index,codec_type,width,height,avg_frame_rate,r_frame_rate:stream_tags=language,title:format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(source.IsFile ? source.LocalPath : source.ToString());

        using var process = Process.Start(psi);
        if (process is null)
        {
            error = "Unable to start ffprobe.";
            return false;
        }

        var output = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            error = string.IsNullOrWhiteSpace(stderr) ? "ffprobe failed to inspect media source." : stderr.Trim();
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;

            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            {
                error = "No media streams found.";
                return false;
            }

            var foundVideo = false;
            var audioOrdinal = 0;
            var subtitleOrdinal = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var codecTypeProp)
                    ? codecTypeProp.GetString()
                    : string.Empty;
                var streamIndex = stream.TryGetProperty("index", out var indexProp) ? indexProp.GetInt32() : -1;
                if (streamIndex < 0)
                {
                    continue;
                }

                if (!foundVideo && string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                {
                    width = stream.TryGetProperty("width", out var widthProp) ? widthProp.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var heightProp) ? heightProp.GetInt32() : 0;
                    frameRate = TryReadFrameRate(stream);
                    foundVideo = true;
                }
                else if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    audioOrdinal++;
                    audioTracks.Add(new TrackDescriptor(
                        streamIndex,
                        BuildTrackName(stream, "Audio", audioOrdinal),
                        streamIndex,
                        audioOrdinal - 1));
                }
                else if (string.Equals(codecType, "subtitle", StringComparison.OrdinalIgnoreCase))
                {
                    subtitleOrdinal++;
                    subtitleTracks.Add(new TrackDescriptor(
                        streamIndex,
                        BuildTrackName(stream, "Subtitle", subtitleOrdinal),
                        streamIndex,
                        subtitleOrdinal - 1));
                }
            }

            if (root.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durationProp)
                && double.TryParse(durationProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSec)
                && durationSec > 0)
            {
                duration = TimeSpan.FromSeconds(durationSec);
            }

            if (width <= 0 || height <= 0)
            {
                error = "Could not determine video dimensions.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Unable to parse ffprobe output: {ex.Message}";
            return false;
        }
    }

    private static double TryReadFrameRate(JsonElement stream)
    {
        if (stream.TryGetProperty("avg_frame_rate", out var avg)
            && TryParseFfprobeRatio(avg.GetString(), out var fpsAvg)
            && fpsAvg > 0d)
        {
            return fpsAvg;
        }

        if (stream.TryGetProperty("r_frame_rate", out var raw)
            && TryParseFfprobeRatio(raw.GetString(), out var fpsRaw)
            && fpsRaw > 0d)
        {
            return fpsRaw;
        }

        return 0d;
    }

    private static string BuildTrackName(JsonElement stream, string prefix, int ordinal)
    {
        var language = TryReadStreamTag(stream, "language");
        var title = TryReadStreamTag(stream, "title");

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(language))
        {
            return $"{title} ({language})";
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            return $"{prefix} {ordinal} ({language})";
        }

        return $"{prefix} {ordinal}";
    }

    private static string TryReadStreamTag(JsonElement stream, string key)
    {
        if (!stream.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Object
            || !tags.TryGetProperty(key, out var property))
        {
            return string.Empty;
        }

        return property.GetString() ?? string.Empty;
    }

    private static bool TryParseFfprobeRatio(string? value, out double ratio)
    {
        ratio = 0d;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var slash = value.IndexOf('/');
        if (slash <= 0 || slash >= value.Length - 1)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) && ratio > 0d;
        }

        var left = value[..slash];
        var right = value[(slash + 1)..];
        if (!double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || denominator == 0d)
        {
            return false;
        }

        ratio = numerator / denominator;
        return ratio > 0d;
    }

    private static TrackDescriptor? FindTrackByIdNoLock(List<TrackDescriptor> tracks, int trackId)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Id == trackId)
            {
                return tracks[i];
            }
        }

        return null;
    }

    private static bool TryBuildVideoFilterChain(
        Uri source,
        TrackDescriptor? selectedSubtitleTrack,
        out string filterChain,
        out string warning)
    {
        warning = string.Empty;
        filterChain = string.Empty;
        var filters = new List<string>();

        if (TryBuildSubtitleFilter(source, selectedSubtitleTrack, out var subtitleFilter, out var subtitleWarning))
        {
            filters.Add(subtitleFilter);
        }
        else if (!string.IsNullOrWhiteSpace(subtitleWarning))
        {
            warning = subtitleWarning;
        }

        if (filters.Count == 0)
        {
            return false;
        }

        filterChain = string.Join(",", filters);
        return true;
    }

    private static bool TryBuildSubtitleFilter(
        Uri source,
        TrackDescriptor? selectedSubtitleTrack,
        out string subtitleFilter,
        out string warning)
    {
        subtitleFilter = string.Empty;
        warning = string.Empty;

        if (selectedSubtitleTrack is null)
        {
            return false;
        }

        if (!source.IsFile)
        {
            warning = "Subtitle selection is currently supported only for local files on FFmpeg backend.";
            return false;
        }

        if (!File.Exists(source.LocalPath))
        {
            warning = "Cannot apply subtitle track: source file is unavailable.";
            return false;
        }

        var escapedPath = EscapeForFfmpegFilter(source.LocalPath);
        subtitleFilter = $"subtitles='{escapedPath}':si={selectedSubtitleTrack.Value.StreamOrdinal}";
        return true;
    }

    private static string EscapeForFfmpegFilter(string filePath)
    {
        var builder = new StringBuilder(filePath.Length + 16);
        for (var i = 0; i < filePath.Length; i++)
        {
            var c = filePath[i];
            if (c is '\\' or ':' or '\'')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsToolAvailable(string toolPathOrCommand)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ProcessCommandResolver.ConfigureTool(psi, toolPathOrCommand);
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(1000))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(200);
                    }
                }
                catch
                {
                    // Ignore cleanup races for timed-out probe process.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsAudioPlaybackAvailable()
    {
        return IsToolAvailable(ProcessCommandResolver.ResolveFfplayExecutable());
    }

    private static string BuildAtempoFilter(double playbackRate)
    {
        var rate = Math.Clamp(playbackRate, 0.1d, 16d);
        var factors = new List<double>();

        while (rate > 2d)
        {
            factors.Add(2d);
            rate /= 2d;
        }

        while (rate < 0.5d)
        {
            factors.Add(0.5d);
            rate /= 0.5d;
        }

        factors.Add(Math.Clamp(rate, 0.5d, 2d));

        var parts = new string[factors.Count];
        for (var i = 0; i < factors.Count; i++)
        {
            parts[i] = $"atempo={factors[i].ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        return string.Join(",", parts);
    }

    private void AllocateFrameBuffer()
    {
        lock (_frameGate)
        {
            ReleaseFrameBuffer();
            _frameBuffer = new byte[checked(_frameStride * _frameHeight)];
            _pinnedFrameBuffer = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);
            Interlocked.Exchange(ref _latestFrameSequence, 0);
        }
    }

    private void ReleaseFrameBuffer()
    {
        lock (_frameGate)
        {
            if (_pinnedFrameBuffer.IsAllocated)
            {
                _pinnedFrameBuffer.Free();
            }

            _frameBuffer = null;
        }
    }

    private static int GetSuspendSignal() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 17 : 19;

    private static int GetResumeSignal() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 19 : 18;

    private static bool TrySignalProcess(Process? process, int signal)
    {
        if (process is null)
        {
            return true;
        }

        try
        {
            if (process.HasExited)
            {
                return false;
            }

            return kill(process.Id, signal) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void KillProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(60);
            }
        }
        catch
        {
            // Best effort.
        }
        finally
        {
            process.Dispose();
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
