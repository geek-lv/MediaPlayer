using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using LibVlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using MediaPlayer.Controls.Audio;

namespace MediaPlayer.Controls.Backends;

internal sealed class LibVlcMediaBackend : IMediaBackend, IMediaAudioCapabilityProvider, IMediaAudioPlaybackController, IMediaAudioDeviceController
{
    private const int BufferAlignment = 32;
    private static readonly object s_coreInitLock = new();
    private static bool s_coreInitialized;
    private static readonly IReadOnlyList<MediaAudioDeviceInfo> s_defaultInputDevices = MediaAudioDeviceCatalog.CreateDefaultInputDevices("libvlc");
    private static readonly IReadOnlyList<MediaAudioDeviceInfo> s_defaultOutputDevices = MediaAudioDeviceCatalog.CreateDefaultOutputDevices("libvlc");

    private readonly LibVlcPlatformProfile _profile;
    private readonly LibVLC _libVlc;
    private readonly LibVlcMediaPlayer _mediaPlayer;
    private readonly object _frameGate = new();
    private readonly LibVlcMediaPlayer.LibVLCVideoLockCb _videoLockCallback;
    private readonly LibVlcMediaPlayer.LibVLCVideoUnlockCb _videoUnlockCallback;
    private readonly LibVlcMediaPlayer.LibVLCVideoDisplayCb _videoDisplayCallback;
    private readonly LibVlcMediaPlayer.LibVLCVideoFormatCb _videoFormatCallback;
    private readonly LibVlcMediaPlayer.LibVLCVideoCleanupCb _videoCleanupCallback;
    private readonly object _audioRouteGate = new();

    private Media? _currentMedia;
    private IntPtr _frameBufferRaw;
    private IntPtr _frameBufferAligned;
    private int _frameBufferBytes;
    private uint _frameWidth;
    private uint _frameHeight;
    private uint _framePitch;
    private long _latestFrameSequence;
    private bool _looping;
    private float _volume = 85f;
    private bool _muted;
    private double _playbackRate = 1d;
    private bool _disposed;
    private string _selectedInputDeviceId = MediaAudioDeviceCatalog.PlatformDefaultInputDeviceId;
    private string _selectedOutputDeviceId = MediaAudioDeviceCatalog.PlatformDefaultOutputDeviceId;

    public LibVlcMediaBackend()
    {
        EnsureCoreInitialized();
        _profile = LibVlcPlatformProfileResolver.Resolve();
        _libVlc = new LibVLC(_profile.LibVlcOptions);
        _mediaPlayer = new LibVlcMediaPlayer(_libVlc);

        _videoLockCallback = OnVideoLock;
        _videoUnlockCallback = OnVideoUnlock;
        _videoDisplayCallback = OnVideoDisplay;
        _videoFormatCallback = OnVideoFormat;
        _videoCleanupCallback = OnVideoCleanup;

        _mediaPlayer.SetVideoFormatCallbacks(_videoFormatCallback, _videoCleanupCallback);
        _mediaPlayer.SetVideoCallbacks(_videoLockCallback, _videoUnlockCallback, _videoDisplayCallback);

        _mediaPlayer.Playing += OnPlaybackStateChanged;
        _mediaPlayer.Paused += OnPlaybackStateChanged;
        _mediaPlayer.Stopped += OnPlaybackStateChanged;
        _mediaPlayer.EndReached += OnEndReached;
        _mediaPlayer.TimeChanged += OnTimelineChanged;
        _mediaPlayer.LengthChanged += OnTimelineChanged;
        _mediaPlayer.PositionChanged += OnTimelineChanged;
        _mediaPlayer.EncounteredError += OnEncounteredError;
        _volume = Math.Clamp(_mediaPlayer.Volume, 0, 100);
        _muted = _mediaPlayer.Mute;
    }

    public event EventHandler? FrameReady;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? TimelineChanged;
    public event EventHandler<string>? ErrorOccurred;

    public string ActiveProfileName => _profile.Name;

    public string ActiveDecodeApi => _profile.NativeDecodeApi;

    public string ActiveRenderPath => _profile.NativeRenderPipeline;

    public bool IsPlaying => !_disposed && _mediaPlayer.IsPlaying;

    public TimeSpan Position => !_disposed
        ? TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time))
        : TimeSpan.Zero;

    public TimeSpan Duration => !_disposed
        ? TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Length))
        : TimeSpan.Zero;

    public int VideoWidth => !_disposed ? (int)_frameWidth : 0;

    public int VideoHeight => !_disposed ? (int)_frameHeight : 0;

    public double FrameRate => !_disposed ? Math.Max(0d, _mediaPlayer.Fps) : 0d;

    public double PlaybackRate => _playbackRate;

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

    public float Volume => _volume;

    public bool IsMuted => _muted;

    public IReadOnlyList<MediaAudioDeviceInfo> GetAudioInputDevices() => s_defaultInputDevices;

    public IReadOnlyList<MediaAudioDeviceInfo> GetAudioOutputDevices() => s_defaultOutputDevices;

    public MediaAudioRouteState GetAudioRouteState()
    {
        lock (_audioRouteGate)
        {
            return new(_selectedInputDeviceId, _selectedOutputDeviceId, LoopbackEnabled: false);
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

        lock (_audioRouteGate)
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

        lock (_audioRouteGate)
        {
            _selectedOutputDeviceId = canonical;
        }

        return true;
    }

    public void Open(Uri source)
    {
        ThrowIfDisposed();

        Stop();
        _currentMedia?.Dispose();

        _currentMedia = new Media(_libVlc, source);
        _mediaPlayer.Media = _currentMedia;
        TimelineChanged?.Invoke(this, EventArgs.Empty);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Play()
    {
        ThrowIfDisposed();

        if (_mediaPlayer.Media is null)
        {
            return;
        }

        _mediaPlayer.Play();
        _mediaPlayer.SetRate((float)_playbackRate);
    }

    public void Pause()
    {
        ThrowIfDisposed();

        if (_mediaPlayer.Media is null || !_mediaPlayer.IsPlaying)
        {
            return;
        }

        _mediaPlayer.Pause();
    }

    public void Stop()
    {
        ThrowIfDisposed();
        _mediaPlayer.Stop();
    }

    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();

        if (_mediaPlayer.Media is null)
        {
            return;
        }

        var durationMs = Math.Max(0, _mediaPlayer.Length);
        var clamped = Math.Clamp((long)position.TotalMilliseconds, 0, durationMs);
        _mediaPlayer.Time = clamped;
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVolume(float volume)
    {
        ThrowIfDisposed();
        _mediaPlayer.Volume = (int)Math.Clamp(Math.Round(volume), 0, 100);
        _volume = Math.Clamp(_mediaPlayer.Volume, 0, 100);
    }

    public void SetMuted(bool muted)
    {
        ThrowIfDisposed();
        _mediaPlayer.Mute = muted;
        _muted = _mediaPlayer.Mute;
    }

    public void SetLooping(bool looping)
    {
        ThrowIfDisposed();
        _looping = looping;
    }

    public void SetPlaybackRate(double rate)
    {
        ThrowIfDisposed();
        var clamped = Math.Clamp(rate, 0.1d, 16d);
        var success = _mediaPlayer.SetRate((float)clamped) == 0;
        if (success)
        {
            _playbackRate = clamped;
        }
        else
        {
            var current = (double)_mediaPlayer.Rate;
            if (current > 0d)
            {
                _playbackRate = current;
            }
        }

        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MediaTrackInfo> GetAudioTracks()
    {
        ThrowIfDisposed();
        return BuildTrackInfos(_mediaPlayer.AudioTrackDescription, _mediaPlayer.AudioTrack, includeOffOption: false);
    }

    public IReadOnlyList<MediaTrackInfo> GetSubtitleTracks()
    {
        ThrowIfDisposed();
        return BuildTrackInfos(_mediaPlayer.SpuDescription, _mediaPlayer.Spu, includeOffOption: true);
    }

    public bool SetAudioTrack(int trackId)
    {
        ThrowIfDisposed();
        var success = _mediaPlayer.SetAudioTrack(trackId);
        if (success)
        {
            TimelineChanged?.Invoke(this, EventArgs.Empty);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        return success;
    }

    public bool SetSubtitleTrack(int trackId)
    {
        ThrowIfDisposed();
        var success = _mediaPlayer.SetSpu(trackId);
        if (success)
        {
            TimelineChanged?.Invoke(this, EventArgs.Empty);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        return success;
    }

    public bool TryAcquireFrame(out MediaFrameLease frame)
    {
        frame = default;

        if (_disposed)
        {
            return false;
        }

        Monitor.Enter(_frameGate);
        if (_frameBufferAligned == IntPtr.Zero || _frameWidth == 0 || _frameHeight == 0 || _framePitch == 0)
        {
            Monitor.Exit(_frameGate);
            return false;
        }

        frame = new MediaFrameLease(
            _frameGate,
            _frameBufferAligned,
            (int)_frameWidth,
            (int)_frameHeight,
            (int)_framePitch,
            MediaFramePixelFormat.Bgra32,
            Interlocked.Read(ref _latestFrameSequence));
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _mediaPlayer.Playing -= OnPlaybackStateChanged;
        _mediaPlayer.Paused -= OnPlaybackStateChanged;
        _mediaPlayer.Stopped -= OnPlaybackStateChanged;
        _mediaPlayer.EndReached -= OnEndReached;
        _mediaPlayer.TimeChanged -= OnTimelineChanged;
        _mediaPlayer.LengthChanged -= OnTimelineChanged;
        _mediaPlayer.PositionChanged -= OnTimelineChanged;
        _mediaPlayer.EncounteredError -= OnEncounteredError;

        try
        {
            _mediaPlayer.Stop();
        }
        catch
        {
            // Best effort teardown.
        }

        _currentMedia?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        ReleaseFrameBuffer();
    }

    private static void EnsureCoreInitialized()
    {
        lock (s_coreInitLock)
        {
            if (s_coreInitialized)
            {
                return;
            }

            Exception? lastError = null;
            var attempted = new List<string>();

            foreach (var candidate in EnumerateLibVlcProbePaths())
            {
                var label = candidate ?? "<default-search-paths>";
                attempted.Add(label);

                try
                {
                    if (candidate is null)
                    {
                        Core.Initialize();
                    }
                    else
                    {
                        Core.Initialize(candidate);
                    }

                    s_coreInitialized = true;
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException(
                "Unable to initialize LibVLC native runtime. " +
                $"Attempted paths: {string.Join(", ", attempted)}",
                lastError);
        }
    }

    private static IEnumerable<string?> EnumerateLibVlcProbePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        yield return null; // Let LibVLCSharp try package default search first.

        foreach (var candidate in EnumerateCandidateRoots())
        {
            if (TryNormalizeLibVlcRoot(candidate, out var normalized)
                && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateRoots()
    {
        var appBase = AppContext.BaseDirectory;
        var envPath = Environment.GetEnvironmentVariable("MEDIAPLAYER_LIBVLC_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            yield return envPath;
        }

        if (!string.IsNullOrWhiteSpace(appBase))
        {
            yield return appBase;
            yield return Path.Combine(appBase, "libvlc");
            yield return Path.Combine(appBase, "lib");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/VLC.app/Contents/MacOS/lib";
            yield return "/Applications/VLC.app/Contents/MacOS";
            yield return "/opt/homebrew/lib";
            yield return "/usr/local/lib";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(appBase, "libvlc", "win-x64");
            yield return Path.Combine(appBase, "libvlc", "win-x86");
        }
        else
        {
            yield return "/usr/lib";
            yield return "/usr/lib64";
            yield return "/usr/local/lib";
            yield return "/snap/vlc/current/usr/lib";
        }
    }

    private static bool TryNormalizeLibVlcRoot(string candidate, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var directory = candidate;
        if (File.Exists(candidate))
        {
            directory = Path.GetDirectoryName(candidate) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        if (ContainsLibVlcBinary(directory))
        {
            normalized = directory;
            return true;
        }

        var nestedLib = Path.Combine(directory, "lib");
        if (Directory.Exists(nestedLib) && ContainsLibVlcBinary(nestedLib))
        {
            normalized = nestedLib;
            return true;
        }

        return false;
    }

    private static bool ContainsLibVlcBinary(string directory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return File.Exists(Path.Combine(directory, "libvlc.dll"))
                   && File.Exists(Path.Combine(directory, "libvlccore.dll"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return File.Exists(Path.Combine(directory, "libvlc.dylib"))
                   && File.Exists(Path.Combine(directory, "libvlccore.dylib"));
        }

        return File.Exists(Path.Combine(directory, "libvlc.so"))
               || File.Exists(Path.Combine(directory, "libvlc.so.5"))
               || File.Exists(Path.Combine(directory, "libvlc.so.9"));
    }

    private static IReadOnlyList<MediaTrackInfo> BuildTrackInfos(
        IEnumerable<TrackDescription>? descriptions,
        int selectedTrackId,
        bool includeOffOption)
    {
        var tracks = new List<MediaTrackInfo>();
        if (includeOffOption)
        {
            tracks.Add(new MediaTrackInfo(-1, "Off", selectedTrackId < 0));
        }

        if (descriptions is null)
        {
            return tracks;
        }

        foreach (var description in descriptions)
        {
            var name = string.IsNullOrWhiteSpace(description.Name)
                ? $"Track {description.Id}"
                : description.Name;
            tracks.Add(new MediaTrackInfo(description.Id, name, description.Id == selectedTrackId));
        }

        return tracks;
    }

    private static void WriteFourCc(IntPtr chroma, string fourCc)
    {
        if (fourCc.Length != 4)
        {
            throw new ArgumentException("FourCC must contain exactly 4 characters.", nameof(fourCc));
        }

        Marshal.WriteByte(chroma, 0, (byte)fourCc[0]);
        Marshal.WriteByte(chroma, 1, (byte)fourCc[1]);
        Marshal.WriteByte(chroma, 2, (byte)fourCc[2]);
        Marshal.WriteByte(chroma, 3, (byte)fourCc[3]);
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        ErrorOccurred?.Invoke(this, "libVLC signaled an unrecoverable playback error.");
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        if (_looping && !_disposed && _mediaPlayer.Media is not null)
        {
            _mediaPlayer.Position = 0f;
            _mediaPlayer.Play();
        }
        else
        {
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_frameGate);

        if (_frameBufferAligned == IntPtr.Zero)
        {
            EnsureFallbackBufferNoLock();
        }

        Marshal.WriteIntPtr(planes, _frameBufferAligned);
        return IntPtr.Zero;
    }

    private void OnVideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        Interlocked.Increment(ref _latestFrameSequence);
        Monitor.Exit(_frameGate);
    }

    private void OnVideoDisplay(IntPtr opaque, IntPtr picture)
    {
        FrameReady?.Invoke(this, EventArgs.Empty);
    }

    private uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        if (width == 0 || height == 0)
        {
            return 0;
        }

        WriteFourCc(chroma, "RV32");

        var pitch = width * 4;
        var lineCount = height;

        pitches = pitch;
        lines = lineCount;

        AllocateFrameBuffer(width, height, pitch, lineCount);
        TimelineChanged?.Invoke(this, EventArgs.Empty);
        return 1;
    }

    private void OnVideoCleanup(ref IntPtr opaque)
    {
        ReleaseFrameBuffer();
    }

    private void AllocateFrameBuffer(uint width, uint height, uint pitch, uint lines)
    {
        var requiredBytes = checked((int)(pitch * lines + BufferAlignment));

        Monitor.Enter(_frameGate);
        try
        {
            if (_frameBufferRaw != IntPtr.Zero
                && requiredBytes <= _frameBufferBytes
                && width == _frameWidth
                && height == _frameHeight
                && pitch == _framePitch)
            {
                return;
            }

            ReleaseFrameBufferNoLock();

            _frameBufferRaw = Marshal.AllocHGlobal(requiredBytes);
            _frameBufferAligned = AlignPointer(_frameBufferRaw, BufferAlignment);
            _frameBufferBytes = requiredBytes;
            _frameWidth = width;
            _frameHeight = height;
            _framePitch = pitch;
            Interlocked.Exchange(ref _latestFrameSequence, 0);
        }
        finally
        {
            Monitor.Exit(_frameGate);
        }
    }

    private void EnsureFallbackBufferNoLock()
    {
        if (_frameBufferRaw != IntPtr.Zero)
        {
            return;
        }

        _frameBufferRaw = Marshal.AllocHGlobal(BufferAlignment + 4);
        _frameBufferAligned = AlignPointer(_frameBufferRaw, BufferAlignment);
        _frameBufferBytes = BufferAlignment + 4;
        _frameWidth = 1;
        _frameHeight = 1;
        _framePitch = 4;
    }

    private void ReleaseFrameBuffer()
    {
        Monitor.Enter(_frameGate);
        try
        {
            ReleaseFrameBufferNoLock();
        }
        finally
        {
            Monitor.Exit(_frameGate);
        }
    }

    private void ReleaseFrameBufferNoLock()
    {
        if (_frameBufferRaw != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frameBufferRaw);
            _frameBufferRaw = IntPtr.Zero;
        }

        _frameBufferAligned = IntPtr.Zero;
        _frameBufferBytes = 0;
        _frameWidth = 0;
        _frameHeight = 0;
        _framePitch = 0;
    }

    private static IntPtr AlignPointer(IntPtr pointer, int alignment)
    {
        var address = unchecked((nuint)pointer);
        var aligned = (address + (uint)(alignment - 1)) & ~((nuint)alignment - 1u);
        return (IntPtr)aligned;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
