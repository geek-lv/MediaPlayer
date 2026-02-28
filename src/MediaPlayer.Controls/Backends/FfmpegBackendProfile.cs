namespace MediaPlayer.Controls.Backends;

internal sealed record FfmpegBackendProfile(
    string ProfileName,
    string HardwareDecodeApi,
    string SoftwareDecodeApi,
    string RenderPath,
    string[] HardwareAccelerationArgs)
{
    public bool SupportsHardwareAcceleration => HardwareAccelerationArgs.Length > 0;
}

internal static class FfmpegBackendProfiles
{
    public static FfmpegBackendProfile GenericFallback() =>
        new(
            ProfileName: "FFmpeg fallback",
            HardwareDecodeApi: "FFmpeg hwaccel auto",
            SoftwareDecodeApi: "FFmpeg software decode",
            RenderPath: "ffmpeg raw BGRA frames -> Avalonia OpenGL texture",
            HardwareAccelerationArgs: ["-hwaccel", "auto"]);

    public static FfmpegBackendProfile MacOsNative() =>
        new(
            ProfileName: "macOS Native Interop",
            HardwareDecodeApi: "VideoToolbox (native) via FFmpeg interop",
            SoftwareDecodeApi: "FFmpeg software decode (CPU fallback)",
            RenderPath: "VideoToolbox decode -> FFmpeg frame callbacks -> Avalonia OpenGL texture",
            HardwareAccelerationArgs: ["-hwaccel", "videotoolbox"]);

    public static FfmpegBackendProfile WindowsNative() =>
        new(
            ProfileName: "Windows Native Interop",
            HardwareDecodeApi: "Media Foundation D3D11VA via FFmpeg interop",
            SoftwareDecodeApi: "FFmpeg software decode (CPU fallback)",
            RenderPath: "D3D11VA decode -> FFmpeg frame callbacks -> Avalonia OpenGL texture",
            HardwareAccelerationArgs: ["-hwaccel", "d3d11va"]);
}
