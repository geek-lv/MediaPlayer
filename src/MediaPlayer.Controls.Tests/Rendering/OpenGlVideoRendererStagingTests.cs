using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MediaPlayer.Controls.Backends;
using MediaPlayer.Controls.Rendering;

namespace MediaPlayer.Controls.Tests.Rendering;

/// <summary>
/// Tests for <c>OpenGlVideoRenderer.CopyFrameToStagingBuffer</c> BGRA→RGBA channel-swap behavior.
/// The conversion was introduced to fix Intel integrated-graphics black-screen issues caused by
/// <c>GL_BGRA</c> being unsupported in OpenGL ES / ANGLE (Direct3D 11 back-end) environments.
/// </summary>
public sealed class OpenGlVideoRendererStagingTests
{
    // InternalsVisibleTo("MediaPlayer.Controls.Tests") is declared in the production AssemblyInfo.cs,
    // so OpenGlVideoRenderer, MediaFrameLease and MediaFramePixelFormat are referenced directly here,
    // giving compile-time safety if these types are ever renamed or moved.
    // Private staging fields (_strideCopyBuffer, _stagedPixelFormat, …) still require reflection
    // because InternalsVisibleTo only exposes internal members, not private ones.

    [Fact]
    public void CopyFrameToStagingBuffer_Bgra32Frame_SwapsRedAndBlueChannels()
    {
        // A 1×1 BGRA pixel [B=0x11, G=0x22, R=0x33, A=0xFF] must become RGBA [R=0x33, G=0x22, B=0x11, A=0xFF].
        var pixelData = new byte[] { 0x11, 0x22, 0x33, 0xFF };
        var handle = GCHandle.Alloc(pixelData, GCHandleType.Pinned);
        try
        {
            var renderer = new OpenGlVideoRenderer();
            CopyFrame(renderer, handle.AddrOfPinnedObject(), width: 1, height: 1, stride: 4, MediaFramePixelFormat.Bgra32);

            var staged = GetFieldValue<byte[]?>(renderer, "_strideCopyBuffer");
            Assert.NotNull(staged);
            Assert.Equal(0x33, staged![0]); // R
            Assert.Equal(0x22, staged[1]);  // G
            Assert.Equal(0x11, staged[2]);  // B
            Assert.Equal(0xFF, staged[3]);  // A
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void CopyFrameToStagingBuffer_Bgra32Frame_SetsStagedFormatToRgba32()
    {
        // The staged pixel format must be promoted from Bgra32 to Rgba32 after the channel swap
        // so that UploadStagedFrame uses GL_RGBA (universally supported) instead of GL_BGRA.
        var pixelData = new byte[4];
        var handle = GCHandle.Alloc(pixelData, GCHandleType.Pinned);
        try
        {
            var renderer = new OpenGlVideoRenderer();
            CopyFrame(renderer, handle.AddrOfPinnedObject(), width: 1, height: 1, stride: 4, MediaFramePixelFormat.Bgra32);

            var stagedFormat = GetFieldValue<MediaFramePixelFormat>(renderer, "_stagedPixelFormat");
            Assert.Equal(MediaFramePixelFormat.Rgba32, stagedFormat);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void CopyFrameToStagingBuffer_Bgra32Frame_SetsStagedReadyAndDimensions()
    {
        var pixelData = new byte[2 * 3 * 4]; // 2×3 frame
        var handle = GCHandle.Alloc(pixelData, GCHandleType.Pinned);
        try
        {
            var renderer = new OpenGlVideoRenderer();
            CopyFrame(renderer, handle.AddrOfPinnedObject(), width: 2, height: 3, stride: 8, MediaFramePixelFormat.Bgra32);

            Assert.True(GetFieldValue<bool>(renderer, "_stagedFrameReady"));
            Assert.Equal(2, GetFieldValue<int>(renderer, "_stagedWidth"));
            Assert.Equal(3, GetFieldValue<int>(renderer, "_stagedHeight"));
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void CopyFrameToStagingBuffer_Bgra32Frame_WithPaddedStride_StripsStrideAndSwaps()
    {
        // 2×1 BGRA pixels with 2-byte row padding (stride=10, tightStride=8).
        // Padding bytes must not appear in the output; R/B channels must be swapped.
        const int stride = 10;
        var pixelData = new byte[stride];
        // Pixel 0: B=0x10, G=0x20, R=0x30, A=0xFF
        pixelData[0] = 0x10; pixelData[1] = 0x20; pixelData[2] = 0x30; pixelData[3] = 0xFF;
        // Pixel 1: B=0x40, G=0x50, R=0x60, A=0xFF
        pixelData[4] = 0x40; pixelData[5] = 0x50; pixelData[6] = 0x60; pixelData[7] = 0xFF;
        // bytes 8-9 are row padding

        var handle = GCHandle.Alloc(pixelData, GCHandleType.Pinned);
        try
        {
            var renderer = new OpenGlVideoRenderer();
            CopyFrame(renderer, handle.AddrOfPinnedObject(), width: 2, height: 1, stride: stride, MediaFramePixelFormat.Bgra32);

            var staged = GetFieldValue<byte[]?>(renderer, "_strideCopyBuffer");
            Assert.NotNull(staged);
            // Pixel 0 -> RGBA (tightly packed)
            Assert.Equal(0x30, staged![0]); Assert.Equal(0x20, staged[1]); Assert.Equal(0x10, staged[2]); Assert.Equal(0xFF, staged[3]);
            // Pixel 1 -> RGBA (no padding in output)
            Assert.Equal(0x60, staged[4]); Assert.Equal(0x50, staged[5]); Assert.Equal(0x40, staged[6]); Assert.Equal(0xFF, staged[7]);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void CopyFrameToStagingBuffer_Bgra32Frame_FourPixels_CorrectlySwapsAll()
    {
        // Four pixels wide exercises the SIMD path (Vector128 processes 4 pixels = 16 bytes at a time).
        // Each pixel has a distinct, non-zero B/R pair to detect any per-pixel addressing error.
        var pixelData = new byte[]
        {
            // [B,    G,    R,    A]
            0x01, 0x02, 0x03, 0xFF,
            0x11, 0x12, 0x13, 0xFF,
            0x21, 0x22, 0x23, 0xFF,
            0x31, 0x32, 0x33, 0xFF,
        };
        var handle = GCHandle.Alloc(pixelData, GCHandleType.Pinned);
        try
        {
            var renderer = new OpenGlVideoRenderer();
            CopyFrame(renderer, handle.AddrOfPinnedObject(), width: 4, height: 1, stride: 16, MediaFramePixelFormat.Bgra32);

            var staged = GetFieldValue<byte[]?>(renderer, "_strideCopyBuffer");
            Assert.NotNull(staged);
            // Each pixel: [R, G, B, A]
            Assert.Equal(new byte[] { 0x03, 0x02, 0x01, 0xFF }, staged![0..4]);
            Assert.Equal(new byte[] { 0x13, 0x12, 0x11, 0xFF }, staged[4..8]);
            Assert.Equal(new byte[] { 0x23, 0x22, 0x21, 0xFF }, staged[8..12]);
            Assert.Equal(new byte[] { 0x33, 0x32, 0x31, 0xFF }, staged[12..16]);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void CopyFrameToStagingBuffer_Rgba32Frame_PreservesChannelOrderAndFormat()
    {
        // Non-BGRA frames must pass through without any channel swap, and the staged
        // format must remain Rgba32 (not be changed to Bgra32 or anything else).
        var pixelData = new byte[] { 0xAA, 0xBB, 0xCC, 0xFF };
        var handle = GCHandle.Alloc(pixelData, GCHandleType.Pinned);
        try
        {
            var renderer = new OpenGlVideoRenderer();
            CopyFrame(renderer, handle.AddrOfPinnedObject(), width: 1, height: 1, stride: 4, MediaFramePixelFormat.Rgba32);

            var staged = GetFieldValue<byte[]?>(renderer, "_strideCopyBuffer");
            Assert.NotNull(staged);
            Assert.Equal(0xAA, staged![0]);
            Assert.Equal(0xBB, staged[1]);
            Assert.Equal(0xCC, staged[2]);
            Assert.Equal(0xFF, staged[3]);

            var stagedFormat = GetFieldValue<MediaFramePixelFormat>(renderer, "_stagedPixelFormat");
            Assert.Equal(MediaFramePixelFormat.Rgba32, stagedFormat);
        }
        finally
        {
            handle.Free();
        }
    }

    // -- Helpers --------------------------------------------------------------

    private static void CopyFrame(
        OpenGlVideoRenderer renderer,
        IntPtr data,
        int width,
        int height,
        int stride,
        MediaFramePixelFormat pixelFormat)
    {
        // MediaFrameLease.Dispose() calls Monitor.Exit on the gate; enter the lock first
        // so that the disposal can release it.  The try-finally ensures the lock is always
        // released even if CopyFrameToStagingBuffer throws unexpectedly.
        var gate = new object();
        Monitor.Enter(gate);
        var leaseDisposed = false;
        var lease = new MediaFrameLease(gate, data, width, height, stride, pixelFormat, sequence: 0);
        try
        {
            renderer.CopyFrameToStagingBuffer(in lease);
        }
        finally
        {
            if (!leaseDisposed)
            {
                lease.Dispose(); // calls Monitor.Exit(gate)
                leaseDisposed = true;
            }
        }
    }

    private static T GetFieldValue<T>(object instance, string fieldName)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                return (T)field.GetValue(instance)!;
            }
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found on '{instance.GetType().FullName}'.");
    }
}
