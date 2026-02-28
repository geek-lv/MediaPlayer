using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaPlayer.Controls.Backends;

internal class FfmpegMediaBackend : IMediaBackend
{
    private readonly object _frameGate = new();
    private readonly object _processGate = new();
    private readonly object _stateGate = new();
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
    private DateTime _playbackStartedUtc;
    private TimeSpan _positionAtPlayStart;
    private bool _isPlaying;
    private long _latestFrameSequence;
    private readonly bool _ffplayAvailable;
    private readonly FfmpegBackendProfile _profile;
    private DecodeMode _decodeMode;
    private bool _cpuFallbackApplied;
    private bool _processesSuspended;

    private enum DecodeMode
    {
        Hardware,
        Software
    }

    public FfmpegMediaBackend()
        : this(FfmpegBackendProfiles.GenericFallback())
    {
    }

    protected FfmpegMediaBackend(FfmpegBackendProfile profile)
    {
        _profile = profile;
        _decodeMode = profile.SupportsHardwareAcceleration ? DecodeMode.Hardware : DecodeMode.Software;
        _ffplayAvailable = IsToolAvailable("ffplay");
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

        StopProcesses(resetPosition: false);
        ReleaseFrameBuffer();

        if (!TryProbeSource(source, out var width, out var height, out var duration, out var error))
        {
            throw new InvalidOperationException(error);
        }

        _source = source;
        lock (_stateGate)
        {
            _duration = duration;
            _position = TimeSpan.Zero;
            _positionAtPlayStart = TimeSpan.Zero;
            _playbackStartedUtc = DateTime.UtcNow;
            _isPlaying = false;
            _decodeMode = _profile.SupportsHardwareAcceleration ? DecodeMode.Hardware : DecodeMode.Software;
            _cpuFallbackApplied = false;
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
            _playbackStartedUtc = DateTime.UtcNow;
            _isPlaying = true;
        }

        CancelPreviewFrameRequest();
        if (!TryResumeProcesses())
        {
            StopProcesses(resetPosition: false);
            StartProcesses(source, startPosition);
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
            _playbackStartedUtc = DateTime.UtcNow;
            shouldRestartPlayback = _isPlaying;
            position = clamped;
        }

        if (shouldRestartPlayback)
        {
            StopProcesses(resetPosition: false);
            StartProcesses(source, position);
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
        // Not supported in ffmpeg pipe mode.
    }

    public void SetMuted(bool muted)
    {
        // Not supported in ffmpeg pipe mode.
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
                _audioProcess = TryStartAudioProcess(source, startPosition);
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
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-fflags");
        psi.ArgumentList.Add("nobuffer");
        psi.ArgumentList.Add("-flags");
        psi.ArgumentList.Add("low_delay");

        DecodeMode decodeModeSnapshot;
        lock (_stateGate)
        {
            decodeModeSnapshot = _decodeMode;
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

        psi.ArgumentList.Add("-re");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(source.IsFile ? source.LocalPath : source.ToString());
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-dn");
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
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-fflags");
        psi.ArgumentList.Add("nobuffer");
        psi.ArgumentList.Add("-flags");
        psi.ArgumentList.Add("low_delay");

        DecodeMode decodeModeSnapshot;
        lock (_stateGate)
        {
            decodeModeSnapshot = _decodeMode;
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
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-dn");
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

    private Process? TryStartAudioProcess(Uri source, TimeSpan startPosition)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffplay",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("quiet");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-nodisp");
            psi.ArgumentList.Add("-autoexit");

            if (startPosition > TimeSpan.Zero)
            {
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add(startPosition.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }

            psi.ArgumentList.Add(source.IsFile ? source.LocalPath : source.ToString());
            return Process.Start(psi);
        }
        catch
        {
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
                _playbackStartedUtc = DateTime.UtcNow;
            }

            StopProcesses(resetPosition: false);
            StartProcesses(_source, TimeSpan.Zero);
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
            _playbackStartedUtc = DateTime.UtcNow;
            source = _source;
            restartPosition = current;
        }

        StopProcesses(resetPosition: false);
        StartProcesses(source, restartPosition);
        ErrorOccurred?.Invoke(this, "Hardware decode path failed. Switched to CPU decode fallback.");
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        TimelineChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool TryProbeSource(Uri source, out int width, out int height, out TimeSpan duration, out string error)
    {
        width = 0;
        height = 0;
        duration = TimeSpan.Zero;
        error = string.Empty;

        if (!IsToolAvailable("ffprobe"))
        {
            error = "ffprobe is required for FFmpeg fallback backend.";
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "ffprobe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=width,height:format=duration");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("v:0");
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
                error = "No video streams found.";
                return false;
            }

            var stream = streams[0];
            width = stream.TryGetProperty("width", out var widthProp) ? widthProp.GetInt32() : 0;
            height = stream.TryGetProperty("height", out var heightProp) ? heightProp.GetInt32() : 0;

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

    private static bool IsToolAvailable(string toolName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = toolName,
                ArgumentList = { "-version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(1000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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
