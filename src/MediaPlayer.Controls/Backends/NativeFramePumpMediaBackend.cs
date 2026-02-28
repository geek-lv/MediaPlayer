using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaPlayer.Controls.Backends;

internal abstract class NativeFramePumpMediaBackend : IMediaBackend
{
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
    private Uri? _source;
    private TimeSpan _duration;
    private TimeSpan _position;
    private TimeSpan _positionAtPlayStart;
    private DateTime _playbackStartedUtc;
    private bool _isPlaying;
    private long _latestFrameSequence;

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

                var elapsed = DateTime.UtcNow - _playbackStartedUtc;
                var current = _positionAtPlayStart + elapsed;
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

    public long LatestFrameSequence => Interlocked.Read(ref _latestFrameSequence);

    public void Open(Uri source)
    {
        ThrowIfDisposed();
        StopPlaybackProcess(resetPosition: false);
        ReleaseFrameBuffer();

        if (!TryEnsureHelperReady(out var helperError))
        {
            throw new InvalidOperationException(helperError);
        }

        if (!TryProbeSource(source, out var width, out var height, out var duration, out var probeError))
        {
            throw new InvalidOperationException(probeError);
        }

        _source = source;
        lock (_stateGate)
        {
            _duration = duration;
            _position = TimeSpan.Zero;
            _positionAtPlayStart = TimeSpan.Zero;
            _playbackStartedUtc = DateTime.UtcNow;
            _isPlaying = false;
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
            _playbackStartedUtc = DateTime.UtcNow;
            _isPlaying = true;
        }

        CancelPreviewFrameRequest();
        StartPlaybackProcess(source, startPosition);
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
            _playbackStartedUtc = DateTime.UtcNow;
            shouldRestartPlayback = _isPlaying;
            position = clamped;
        }

        if (shouldRestartPlayback)
        {
            StartPlaybackProcess(source, position);
        }
        else
        {
            RequestPreviewFrame(source, position);
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVolume(float volume)
    {
        // Native helper backends currently do not expose volume control.
    }

    public void SetMuted(bool muted)
    {
        // Native helper backends currently do not expose mute control.
    }

    public void SetLooping(bool looping)
    {
        _looping = looping;
    }

    public bool TryAcquireFrame(out MediaFrameLease frame)
    {
        frame = default;

        if (_disposed || !_pinnedFrameBuffer.IsAllocated || _frameBuffer is null)
        {
            return false;
        }

        Monitor.Enter(_frameGate);
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

    protected abstract ProcessStartInfo CreatePlaybackProcess(Uri source, TimeSpan startPosition);

    protected abstract ProcessStartInfo CreateSingleFrameProcess(Uri source, TimeSpan position);

    protected virtual TimeSpan PlaybackReadJoinTimeout => TimeSpan.FromMilliseconds(35);

    private bool TryProbeSource(Uri source, out int width, out int height, out TimeSpan duration, out string error)
    {
        width = 0;
        height = 0;
        duration = TimeSpan.Zero;
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
            process.WaitForExit(15000);

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
        StopPlaybackProcess(resetPosition: false);

        var process = Process.Start(CreatePlaybackProcess(source, startPosition))
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
                _playbackStartedUtc = DateTime.UtcNow;
                _isPlaying = true;
            }

            StartPlaybackProcess(_source, TimeSpan.Zero);
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

    protected static ProcessStartInfo CreateToolProcess(string fileName)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
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
