using MediaPlayer.Controls.Backends;
using MediaPlayer.Native.Abstractions;
using MediaPlayer.Native.Interop;

namespace MediaPlayer.Controls.Tests.Backends;

public sealed class MediaBackendSelectionPolicyTests
{
    private static readonly IReadOnlyList<MediaPlayerInteropPlaybackProviderDescriptor> s_interopAvailable =
    [
        new(
            MediaPlayerInteropPlaybackProviderId.LibVlcManagedInterop,
            "Managed Interop (LibVLC)",
            MediaPlayerNativeProviderKind.Interop,
            IsAvailable: true,
            UnavailableReason: string.Empty)
    ];

    [Fact]
    public void Build_InteropOnly_MacOs_PrefersInteropThenLegacy()
    {
        var result = MediaBackendSelectionPolicy.Build(
            MediaPlayerNativeProviderMode.InteropOnly,
            MediaBackendSelectionPlatform.MacOs,
            s_interopAvailable);

        Assert.Equal(4, result.Candidates.Count);
        Assert.Equal(MediaBackendKind.LibVlcInterop, result.Candidates[0].BackendKind);
        Assert.Equal(MediaBackendKind.MacOsNativeHelper, result.Candidates[1].BackendKind);
        Assert.Equal(MediaBackendKind.MacOsFfmpegProfile, result.Candidates[2].BackendKind);
        Assert.Equal(MediaBackendKind.FfmpegFallback, result.Candidates[3].BackendKind);
    }

    [Fact]
    public void Build_AutoPreferInterop_Windows_PrefersInteropThenLegacy()
    {
        var result = MediaBackendSelectionPolicy.Build(
            MediaPlayerNativeProviderMode.AutoPreferInterop,
            MediaBackendSelectionPlatform.Windows,
            s_interopAvailable);

        Assert.Equal(4, result.Candidates.Count);
        Assert.Equal(MediaBackendKind.LibVlcInterop, result.Candidates[0].BackendKind);
        Assert.Equal(MediaBackendKind.WindowsNativeHelper, result.Candidates[1].BackendKind);
        Assert.Equal(MediaBackendKind.WindowsFfmpegProfile, result.Candidates[2].BackendKind);
        Assert.Equal(MediaBackendKind.FfmpegFallback, result.Candidates[3].BackendKind);
    }

    [Fact]
    public void Build_NativeBindingsOnly_Linux_FallsBackWithWarning()
    {
        var result = MediaBackendSelectionPolicy.Build(
            MediaPlayerNativeProviderMode.NativeBindingsOnly,
            MediaBackendSelectionPlatform.Linux,
            s_interopAvailable);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(MediaBackendKind.LibVlcInterop, result.Candidates[0].BackendKind);
        Assert.Equal(MediaBackendKind.FfmpegFallback, result.Candidates[1].BackendKind);
        Assert.Contains("NativeBindingsOnly", result.ModeSelectionWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_InteropOnly_WithoutInteropProvider_EmitsWarning()
    {
        IReadOnlyList<MediaPlayerInteropPlaybackProviderDescriptor> unavailableInterop =
        [
            new(
                MediaPlayerInteropPlaybackProviderId.LibVlcManagedInterop,
                "Managed Interop (LibVLC)",
                MediaPlayerNativeProviderKind.Interop,
                IsAvailable: false,
                UnavailableReason: "No interop provider found.")
        ];

        var result = MediaBackendSelectionPolicy.Build(
            MediaPlayerNativeProviderMode.InteropOnly,
            MediaBackendSelectionPlatform.MacOs,
            unavailableInterop);

        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(MediaBackendKind.MacOsNativeHelper, result.Candidates[0].BackendKind);
        Assert.Contains("No interop provider found.", result.ModeSelectionWarning, StringComparison.Ordinal);
        Assert.Contains("InteropOnly", result.ModeSelectionWarning, StringComparison.Ordinal);
    }
}
