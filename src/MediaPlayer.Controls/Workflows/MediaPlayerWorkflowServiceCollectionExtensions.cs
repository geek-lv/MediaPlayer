using System;
using System.Runtime.InteropServices;
using MediaPlayer.Native.Abstractions;
using MediaPlayer.Native.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediaPlayer.Controls.Workflows;

public static class MediaPlayerWorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddMediaPlayerWorkflows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddMediaPlayerWorkflows(_ => { });
    }

    public static IServiceCollection AddMediaPlayerWorkflows(
        this IServiceCollection services,
        Action<MediaWorkflowServiceRegistrationOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new MediaWorkflowServiceRegistrationOptions();
        configureOptions(options);

        services.RemoveAll<IMediaWorkflowService>();
        services.RemoveAll<IMediaWorkflowProviderDiagnostics>();
        services.RemoveAll<IInteropMediaWorkflowProvider>();
        services.TryAddSingleton<FfmpegMediaWorkflowService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IInteropMediaWorkflowProvider, WavInteropMediaWorkflowProvider>());
        services.TryAddSingleton<InteropMediaWorkflowService>();
        services.AddSingleton<MediaWorkflowProviderDiagnostics>();
        services.AddSingleton<IMediaWorkflowProviderDiagnostics>(provider => provider.GetRequiredService<MediaWorkflowProviderDiagnostics>());
        services.AddSingleton<IMediaWorkflowService>(serviceProvider =>
        {
            var fallback = serviceProvider.GetRequiredService<FfmpegMediaWorkflowService>();
            var interop = serviceProvider.GetRequiredService<InteropMediaWorkflowService>();
            var diagnostics = serviceProvider.GetRequiredService<MediaWorkflowProviderDiagnostics>();
            if (!options.PreferNativePlatformServices)
            {
                diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                    options.NativeProviderMode,
                    MediaPlayerNativeProviderKind.FfmpegFallback,
                    "Native workflow providers are disabled via registration options."));
                return fallback;
            }

            switch (options.NativeProviderMode)
            {
                case MediaPlayerNativeProviderMode.InteropOnly:
                    {
                        var workflowProvider = ResolveInteropWorkflowProvider();
                        diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                            options.NativeProviderMode,
                            workflowProvider.providerKind,
                            workflowProvider.message));
                        return workflowProvider.service;
                    }
                case MediaPlayerNativeProviderMode.NativeBindingsOnly:
                    diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                        options.NativeProviderMode,
                        MediaPlayerNativeProviderKind.Interop,
                        "NativeBindingsOnly workflow mode is configured, but native binding workflow provider is not implemented yet. Using interop workflow provider fallback."));
                    return interop;
                case MediaPlayerNativeProviderMode.AutoPreferInterop:
                    {
                        var workflowProvider = ResolveInteropWorkflowProvider();
                        diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                            options.NativeProviderMode,
                            workflowProvider.providerKind,
                            workflowProvider.message));
                        return workflowProvider.service;
                    }
                case MediaPlayerNativeProviderMode.AutoPreferBindings:
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                            options.NativeProviderMode,
                            MediaPlayerNativeProviderKind.LegacyHelper,
                            "AutoPreferBindings mode is configured, but native binding workflow provider is not implemented yet. Using legacy native workflow helper."));
                        return new MacOsNativeMediaWorkflowService(fallback);
                    }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                            options.NativeProviderMode,
                            MediaPlayerNativeProviderKind.LegacyHelper,
                            "AutoPreferBindings mode is configured, but native binding workflow provider is not implemented yet. Using legacy native workflow helper."));
                        return new WindowsNativeMediaWorkflowService(fallback, options.WindowsNativeWorkflowHelperPath);
                    }

                    diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                        options.NativeProviderMode,
                        MediaPlayerNativeProviderKind.Interop,
                        "Native workflow provider mode is not supported on this platform. Using managed interop workflow provider."));
                    return interop;
                case MediaPlayerNativeProviderMode.LegacyHelpers:
                default:
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                            options.NativeProviderMode,
                            MediaPlayerNativeProviderKind.LegacyHelper,
                            string.Empty));
                        return new MacOsNativeMediaWorkflowService(fallback);
                    }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                            options.NativeProviderMode,
                            MediaPlayerNativeProviderKind.LegacyHelper,
                            string.Empty));
                        return new WindowsNativeMediaWorkflowService(fallback, options.WindowsNativeWorkflowHelperPath);
                    }

                    diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                        options.NativeProviderMode,
                        MediaPlayerNativeProviderKind.Interop,
                        "Legacy workflow helpers are not available on this platform. Using managed interop workflow provider."));
                    return interop;
            }

            (IMediaWorkflowService service, MediaPlayerNativeProviderKind providerKind, string message) ResolveInteropWorkflowProvider()
            {
                var providers = MediaPlayerInteropWorkflowProviderCatalog.GetWorkflowProviders();
                var hasCatalogInteropFallback = false;
                for (var index = 0; index < providers.Count; index++)
                {
                    var provider = providers[index];
                    if (!provider.IsAvailable)
                    {
                        continue;
                    }

                    if (interop.HasProvider(provider.Id))
                    {
                        return (interop, provider.ProviderKind, string.Empty);
                    }

                    if (provider.Id == MediaPlayerInteropWorkflowProviderId.FfmpegManagedInterop)
                    {
                        hasCatalogInteropFallback = true;
                    }
                }

                if (hasCatalogInteropFallback)
                {
                    if (interop.HasRegisteredInteropProviders)
                    {
                        return (interop, MediaPlayerNativeProviderKind.Interop, "No direct interop workflow provider matched catalog selection. Using interop workflow fallback.");
                    }

                    return (interop, MediaPlayerNativeProviderKind.Interop, "No direct interop workflow provider is currently available. Using FFmpeg-backed interop workflow fallback.");
                }

                return (fallback, MediaPlayerNativeProviderKind.FfmpegFallback, "No interop workflow provider is currently available. Using FFmpeg workflow fallback.");
            }
        });
        return services;
    }

    public static IServiceCollection AddMediaPlayerWorkflows(
        this IServiceCollection services,
        Func<IServiceProvider, IMediaWorkflowService> implementationFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(implementationFactory);

        services.RemoveAll<IMediaWorkflowService>();
        services.RemoveAll<IMediaWorkflowProviderDiagnostics>();
        services.AddSingleton<IMediaWorkflowService>(implementationFactory);
        services.AddSingleton<IMediaWorkflowProviderDiagnostics>(_ =>
        {
            var diagnostics = new MediaWorkflowProviderDiagnostics();
            diagnostics.Update(new MediaPlayerNativeProviderDiagnostics(
                MediaPlayerNativeProviderMode.AutoPreferInterop,
                MediaPlayerNativeProviderKind.Unknown,
                "Workflow provider diagnostics are unavailable for custom factory registration."));
            return diagnostics;
        });
        return services;
    }
}
