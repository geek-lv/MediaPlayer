using System;
using System.Runtime.InteropServices;

namespace MediaPlayer.Controls.Backends;

internal sealed class MacOsNativeMediaBackend : FfmpegMediaBackend
{
    public MacOsNativeMediaBackend() : base(FfmpegBackendProfiles.MacOsNative())
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            throw new PlatformNotSupportedException("MacOsNativeMediaBackend can only be used on macOS.");
        }
    }
}
