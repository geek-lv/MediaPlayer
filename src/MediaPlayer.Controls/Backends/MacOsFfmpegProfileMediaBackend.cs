using System;
using System.Runtime.InteropServices;

namespace MediaPlayer.Controls.Backends;

internal sealed class MacOsFfmpegProfileMediaBackend : FfmpegMediaBackend
{
    public MacOsFfmpegProfileMediaBackend() : base(FfmpegBackendProfiles.MacOsNative())
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            throw new PlatformNotSupportedException("MacOsFfmpegProfileMediaBackend can only be used on macOS.");
        }
    }
}
