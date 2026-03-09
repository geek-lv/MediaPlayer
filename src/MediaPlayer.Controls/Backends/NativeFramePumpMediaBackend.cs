using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaPlayer.Controls.Audio;

namespace MediaPlayer.Controls.Backends;

internal abstract class NativeFramePumpMediaBackend : IMediaBackend, IMediaAudioCapabilityProvider, IMediaAudioPlaybackController, IMediaAudioDeviceController
{
    private static readonly IReadOnlyList<MediaTrackInfo> s_emptyTracks = Array.Empty<MediaTrackInfo>();
    private static readonly IReadOnlyList<MediaAudioDeviceInfo> s_defaultInputDevices = MediaAudioDeviceCatalog.CreateDefaultInputDevices("native-helper");
    private static readonly IReadOnlyList<MediaAudioDeviceInfo> s_defaultOutputDevices = MediaAudioDeviceCatalog.CreateDefaultOutputDevices("native-helper");
    private const int DefaultAudioTrackId = 0;
    private static readonly IReadOnlyList<MediaTrackInfo> s_defaultAudioTrackSelected =
    [
        new MediaTrackInfo(DefaultAudioTrackId, "Default", true)
    ];
    private static readonly IReadOnlyList<MediaTrackInfo> s_defaultAudioTrackUnselected =
    [
        new MediaTrackInfo(DefaultAudioTrackId, "Default", false)
    ];
    private readonly object _frameGate = new();
    private readonly object _stateGate = new();
    private readonly object _processGate = new();

    private Process? _playbackProcess;
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackReadTask;
    private CancellationTokenSource? _previewCts;
    private byte[]? _frameBuffer;
    private GCHandle _pinnedFrameBuffer;
    private int _frameWidth;
    private int _frameHeight;
    private int _frameStride;
    private bool _disposed;
    private bool _looping;
    private float _volume = 85f;
    private bool _muted;
    private int _selectedAudioTrackId = DefaultAudioTrackId;
    private Uri? _source;
    private TimeSpan _duration;
    private TimeSpan _position;
    private TimeSpan _positionAtPlayStart;
    private long _playbackStartedTimestamp;
    private bool _isPlaying;
    private long _latestFrameSequence;
    private double _frameRate;
    private double _playbackRate = 1d;
    private string _selectedInputDeviceId = MediaAudioDeviceCatalog.PlatformDefaultInputDeviceId;
    private string _selectedOutputDeviceId = MediaAudioDeviceCatalog.PlatformDefaultOutputDeviceId;

    protected NativeFramePumpMediaBackend()
    {
        if (!TryEnsureHelperReady(out var helperError))
        {
            throw new InvalidOperationException(helperError);
        }
    }

    public event EventHandler? FrameReady;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? TimelineChanged;
    public event EventHandler<string>? ErrorOccurred;

    public string ActiveProfileName => ProfileName;

    public string ActiveDecodeApi => NativeDecodeApi;

    public string ActiveRenderPath => NativeRenderPath;

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
        MediaAudioCapabilities.VolumeControl
        | MediaAudioCapabilities.MuteControl
        | MediaAudioCapabilities.AudioTrackEnumeration
        | MediaAudioCapabilities.AudioTrackSelection
        | MediaAudioCapabilities.InputDeviceEnumeration
        | MediaAudioCapabilities.OutputDeviceEnumeration;

    public bool SupportsVolumeControl => true;

    public bool SupportsMuteControl => true;

    public float Volume
    {
        get
        {
            lock (_stateGate)
            {
                return _volume;
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_stateGate)
            {
                return _muted;
            }
        }
    }

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
        CancelPreviewFrameRequest();
        StopPlaybackProcess(resetPosition: false);
        ReleaseFrameBuffer();

        if (!TryEnsureHelperReady(out var helperError))
        {
            throw new InvalidOperationException(helperError);
        }

        if (!TryProbeSource(source, out var width, out var height, out var duration, out var frameRate, out var probeError))
        {
            throw new InvalidOperationException(probeError);
        }

        _source = source;
        lock (_stateGate)
        {
            _duration = duration;
            _position = TimeSpan.Zero;
            _positionAtPlayStart = TimeSpan.Zero;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
            _isPlaying = false;
            _frameRate = frameRate;
            _selectedAudioTrackId = DefaultAudioTrackId;
        }

        _frameWidth = width;
        _frameHeight = height;
        _frameStride = width * 4;
        AllocateFrameBuffer();

        RequestPreviewFrame(source, TimeSpan.Zero);
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
        if (!TryStartPlaybackProcess(
                source,
                startPosition,
                "Unable to start native playback process.",
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

        StopPlaybackProcess(resetPosition: false);
        if (_source is not null)
        {
            RequestPreviewFrame(_source, Position);
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

        StopPlaybackProcess(resetPosition: false);
        if (_source is not null)
        {
            RequestPreviewFrame(_source, TimeSpan.Zero);
        }

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
            if (!TryStartPlaybackProcess(
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
        bool wasPlaying;
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

        if (wasPlaying
            && !TryStartPlaybackProcess(
                source,
                restartPosition,
                "Volume update restart failed.",
                markNotPlayingOnFailure: true))
        {
            return;
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMuted(bool muted)
    {
        ThrowIfDisposed();
        bool changed;
        Uri? source;
        bool wasPlaying;
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

        if (wasPlaying
            && !TryStartPlaybackProcess(
                source,
                restartPosition,
                "Mute update restart failed.",
                markNotPlayingOnFailure: true))
        {
            return;
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
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
        TimeSpan restartPosition;
        bool wasPlaying;

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
            if (!TryStartPlaybackProcess(
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
            RequestPreviewFrame(source, restartPosition);
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MediaTrackInfo> GetAudioTracks()
    {
        lock (_stateGate)
        {
            if (_source is null)
            {
                return s_emptyTracks;
            }

            return _selectedAudioTrackId == DefaultAudioTrackId
                ? s_defaultAudioTrackSelected
                : s_defaultAudioTrackUnselected;
        }
    }

    public IReadOnlyList<MediaTrackInfo> GetSubtitleTracks() => s_emptyTracks;

    public bool SetAudioTrack(int trackId)
    {
        ThrowIfDisposed();
        if (trackId != DefaultAudioTrackId)
        {
            return false;
        }

        Uri? source;
        bool changed;
        bool wasPlaying;
        TimeSpan restartPosition;
        lock (_stateGate)
        {
            source = _source;
            if (source is null)
            {
                return false;
            }

            changed = _selectedAudioTrackId != trackId;
            _selectedAudioTrackId = trackId;
            wasPlaying = _isPlaying;
            restartPosition = _isPlaying ? Position : _position;
            _position = restartPosition;
            _positionAtPlayStart = restartPosition;
            _playbackStartedTimestamp = Stopwatch.GetTimestamp();
        }

        if (!changed)
        {
            return true;
        }

        if (wasPlaying)
        {
            if (!TryStartPlaybackProcess(
                    source,
                    restartPosition,
                    "Audio track update restart failed.",
                    markNotPlayingOnFailure: true))
            {
                return false;
            }
        }
        else
        {
            RequestPreviewFrame(source, restartPosition);
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool SetSubtitleTrack(int trackId) => false;

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
            MediaFramePixelFormat.Bgra32,
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
        StopPlaybackProcess(resetPosition: false);
        ReleaseFrameBuffer();
    }

    protected abstract string ProfileName { get; }

    protected abstract string NativeDecodeApi { get; }

    protected abstract string NativeRenderPath { get; }

    protected abstract bool TryEnsureHelperReady(out string error);

    protected abstract ProcessStartInfo CreateProbeProcess(Uri source);

    protected abstract ProcessStartInfo CreatePlaybackProcess(
        Uri source,
        TimeSpan startPosition,
        double playbackRate,
        float volume,
        bool muted);

    protected abstract ProcessStartInfo CreateSingleFrameProcess(Uri source, TimeSpan position);

    protected virtual TimeSpan PlaybackReadJoinTimeout => TimeSpan.FromMilliseconds(35);

    private bool TryProbeSource(Uri source, out int width, out int height, out TimeSpan duration, out double frameRate, out string error)
    {
        width = 0;
        height = 0;
        duration = TimeSpan.Zero;
        frameRate = 0d;
        error = string.Empty;

        try
        {
            using var process = Process.Start(CreateProbeProcess(source));
            if (process is null)
            {
                error = "Unable to start native probe helper.";
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
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
                    // Best effort timeout cleanup.
                }

                error = "Native probe helper timed out.";
                return false;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? "Native probe helper failed."
                    : stderr.Trim();
                return false;
            }

            using var json = JsonDocument.Parse(stdout);
            var root = json.RootElement;
            width = root.TryGetProperty("width", out var widthProp) ? widthProp.GetInt32() : 0;
            height = root.TryGetProperty("height", out var heightProp) ? heightProp.GetInt32() : 0;
            var durationSeconds = root.TryGetProperty("duration", out var durationProp)
                                  && durationProp.ValueKind is JsonValueKind.Number
                ? durationProp.GetDouble()
                : 0d;
            duration = durationSeconds > 0d ? TimeSpan.FromSeconds(durationSeconds) : TimeSpan.Zero;
            frameRate = root.TryGetProperty("frameRate", out var frameRateProp)
                        && frameRateProp.ValueKind is JsonValueKind.Number
                ? Math.Max(0d, frameRateProp.GetDouble())
                : 0d;

            if (width <= 0 || height <= 0)
            {
                error = "Native probe helper returned invalid dimensions.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Native probe helper failed: {ex.Message}";
            return false;
        }
    }

    private void StartPlaybackProcess(Uri source, TimeSpan startPosition)
    {
        CancelPreviewFrameRequest();
        StopPlaybackProcess(resetPosition: false);

        double playbackRate;
        float volume;
        bool muted;
        lock (_stateGate)
        {
            playbackRate = _playbackRate;
            volume = _volume;
            muted = _muted;
        }

        var process = Process.Start(CreatePlaybackProcess(source, startPosition, playbackRate, volume, muted))
            ?? throw new InvalidOperationException("Unable to start native playback helper.");
        var cts = new CancellationTokenSource();

        lock (_processGate)
        {
            _playbackProcess = process;
            _playbackCts = cts;
            _playbackReadTask = Task.Run(() => ReadFramesLoopAsync(process, cts.Token), cts.Token);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    ErrorOccurred?.Invoke(this, stderr.Trim());
                }
            }
            catch
            {
                // Ignore stderr races during teardown.
            }
        });
    }

    private bool TryStartPlaybackProcess(
        Uri source,
        TimeSpan startPosition,
        string failurePrefix,
        bool markNotPlayingOnFailure)
    {
        try
        {
            StartPlaybackProcess(source, startPosition);
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

    private async Task ReadFramesLoopAsync(Process process, CancellationToken cancellationToken)
    {
        var frameBytes = checked(_frameStride * _frameHeight);
        var stream = process.StandardOutput.BaseStream;
        var scratch = new byte[frameBytes];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await ReadExactlyAsync(stream, scratch, frameBytes, cancellationToken).ConfigureAwait(false);
            if (read < frameBytes)
            {
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
                _isPlaying = true;
            }

            if (!TryStartPlaybackProcess(
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

    private void StopPlaybackProcess(bool resetPosition)
    {
        Process? process;
        CancellationTokenSource? cts;
        Task? readTask;

        lock (_processGate)
        {
            process = _playbackProcess;
            cts = _playbackCts;
            readTask = _playbackReadTask;
            _playbackProcess = null;
            _playbackCts = null;
            _playbackReadTask = null;
        }

        cts?.Cancel();
        KillProcess(process);

        if (readTask is not null && readTask.Id != Task.CurrentId)
        {
            try
            {
                readTask.Wait(PlaybackReadJoinTimeout);
            }
            catch
            {
                // Ignore shutdown races.
            }
        }

        cts?.Dispose();

        if (resetPosition)
        {
            lock (_stateGate)
            {
                _position = TimeSpan.Zero;
            }
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

    private void DecodePreviewFrame(Uri source, TimeSpan position, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            process = Process.Start(CreateSingleFrameProcess(source, position))
                ?? throw new InvalidOperationException("Unable to start native single-frame helper.");
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
            // Ignore canceled preview operations.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ErrorOccurred?.Invoke(this, $"Native preview decode failed: {ex.Message}");
            }
        }
        finally
        {
            KillProcess(process);
        }
    }

    private void CancelPreviewFrameRequest()
    {
        CancellationTokenSource? cts;
        lock (_processGate)
        {
            cts = _previewCts;
            _previewCts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
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

    protected static ProcessStartInfo CreateToolProcess(string fileName)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ProcessCommandResolver.ConfigureTool(startInfo, fileName);
        return startInfo;
    }

    protected static string FormatMediaSource(Uri source)
    {
        if (source.IsFile)
        {
            return source.LocalPath;
        }

        return source.ToString();
    }

    protected static string FormatSeconds(TimeSpan value)
    {
        var seconds = Math.Max(0d, value.TotalSeconds);
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
