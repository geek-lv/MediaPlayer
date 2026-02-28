using System;
using System.Runtime.InteropServices;

namespace MediaPlayer.Controls.Backends;

internal sealed class WindowsNativeMediaBackend : FfmpegMediaBackend
{
    public WindowsNativeMediaBackend() : base(FfmpegBackendProfiles.WindowsNative())
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WindowsNativeMediaBackend can only be used on Windows.");
        }
    }
}
