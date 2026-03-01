using System;

namespace MediaPlayer.Controls.Audio;

public readonly record struct MediaAudioLevels(
    DateTimeOffset Timestamp,
    float PeakLeft,
    float PeakRight,
    float RmsLeft,
    float RmsRight);
