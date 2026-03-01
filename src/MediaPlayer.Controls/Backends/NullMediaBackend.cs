using System;
using System.Collections.Generic;
using MediaPlayer.Controls.Audio;

namespace MediaPlayer.Controls.Backends;

#pragma warning disable CS0067
internal sealed class NullMediaBackend(string reason) : IMediaBackend, IMediaAudioCapabilityProvider, IMediaAudioPlaybackController
{
    private readonly string _reason = reason;

    public event EventHandler? FrameReady;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? TimelineChanged;
    public event EventHandler<string>? ErrorOccurred;

    public string ActiveProfileName => "Unavailable";
    public string ActiveDecodeApi => "Unavailable";
    public string ActiveRenderPath => "Unavailable";
    public bool IsPlaying => false;
    public TimeSpan Position => TimeSpan.Zero;
    public TimeSpan Duration => TimeSpan.Zero;
    public int VideoWidth => 0;
    public int VideoHeight => 0;
    public double FrameRate => 0d;
    public double PlaybackRate => 1d;
    public long LatestFrameSequence => 0;
    public MediaAudioCapabilities AudioCapabilities => MediaAudioCapabilities.None;
    public bool SupportsVolumeControl => false;
    public bool SupportsMuteControl => false;
    public float Volume => 0f;
    public bool IsMuted => false;

    public void Open(Uri source) => ErrorOccurred?.Invoke(this, _reason);

    public void Play() => ErrorOccurred?.Invoke(this, _reason);

    public void Pause()
    {
    }

    public void Stop()
    {
    }

    public void Seek(TimeSpan position)
    {
    }

    public void SetPlaybackRate(double rate)
    {
    }

    public IReadOnlyList<MediaTrackInfo> GetAudioTracks() => Array.Empty<MediaTrackInfo>();

    public IReadOnlyList<MediaTrackInfo> GetSubtitleTracks() => Array.Empty<MediaTrackInfo>();

    public bool SetAudioTrack(int trackId) => false;

    public bool SetSubtitleTrack(int trackId) => false;

    public void SetVolume(float volume)
    {
    }

    public void SetMuted(bool muted)
    {
    }

    public void SetLooping(bool looping)
    {
    }

    public bool TryAcquireFrame(out MediaFrameLease frame)
    {
        frame = default;
        return false;
    }

    public void Dispose()
    {
    }
}
#pragma warning restore CS0067
