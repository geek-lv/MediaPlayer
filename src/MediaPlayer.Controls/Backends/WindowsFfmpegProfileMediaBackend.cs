using System;
using System.Runtime.InteropServices;

namespace MediaPlayer.Controls.Backends;

internal sealed class WindowsFfmpegProfileMediaBackend : FfmpegMediaBackend
{
    public WindowsFfmpegProfileMediaBackend() : base(FfmpegBackendProfiles.WindowsNative())
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WindowsFfmpegProfileMediaBackend can only be used on Windows.");
        }
    }
}
