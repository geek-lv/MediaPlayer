using MediaPlayer.Native.Abstractions;
using MediaPlayer.Native.Interop;

namespace MediaPlayer.Controls.Tests.Native;

public sealed class MediaPlayerInteropPlaybackProviderCatalogTests
{
    [Fact]
    public void GetPlaybackProviders_ReturnsLibVlcInteropDescriptor()
    {
        var providers = MediaPlayerInteropPlaybackProviderCatalog.GetPlaybackProviders();

        var descriptor = Assert.Single(providers, candidate => candidate.Id == MediaPlayerInteropPlaybackProviderId.LibVlcManagedInterop);
        Assert.Equal(MediaPlayerNativeProviderKind.Interop, descriptor.ProviderKind);
    }

    [Fact]
    public void GetPlaybackProviders_ExposesAvailabilityReason_WhenUnavailable()
    {
        var providers = MediaPlayerInteropPlaybackProviderCatalog.GetPlaybackProviders();
        foreach (var provider in providers)
        {
            if (!provider.IsAvailable)
            {
                Assert.False(string.IsNullOrWhiteSpace(provider.UnavailableReason));
            }
        }
    }
}
