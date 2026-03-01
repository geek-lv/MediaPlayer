using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace MediaPlayer.Controls.Tests.Backends;

public sealed class BackendFrameLeaseTests
{
    [Fact]
    public void FfmpegBackend_TryAcquireFrame_DoesNotThrow_WhenBufferInvalidatesBeforeLockEntry()
    {
        var backend = CreateBackend("MediaPlayer.Controls.Backends.FfmpegMediaBackend");
        try
        {
            InitializeFrameBuffer(backend, width: 4, height: 4);
            AssertBufferInvalidationRaceHandledWithoutThrow(backend);
        }
        finally
        {
            DisposeBackend(backend);
        }
    }

    [Fact]
    public void NativeBackend_TryAcquireFrame_DoesNotThrow_WhenBufferInvalidatesBeforeLockEntry()
    {
        var backendTypeName = ResolveNativeBackendTypeName();
        if (backendTypeName is null)
        {
            return;
        }

        object? backend = null;
        try
        {
            backend = CreateBackend(backendTypeName);
            InitializeFrameBuffer(backend, width: 4, height: 4);
            AssertBufferInvalidationRaceHandledWithoutThrow(backend);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is PlatformNotSupportedException)
        {
            // Skip on environments where platform-specific helper backend can't be constructed.
        }
        finally
        {
            if (backend is not null)
            {
                DisposeBackend(backend);
            }
        }
    }

    private static string? ResolveNativeBackendTypeName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "MediaPlayer.Controls.Backends.MacOsNativeMediaBackend";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "MediaPlayer.Controls.Backends.WindowsNativeMediaBackend";
        }

        return null;
    }

    private static object CreateBackend(string typeName)
    {
        var assembly = typeof(MediaPlayer.Controls.GpuMediaPlayer).Assembly;
        var backendType = assembly.GetType(typeName, throwOnError: true)
            ?? throw new InvalidOperationException($"Type '{typeName}' was not found.");
        return Activator.CreateInstance(backendType, nonPublic: true)
            ?? throw new InvalidOperationException($"Unable to create backend instance '{typeName}'.");
    }

    private static void InitializeFrameBuffer(object backend, int width, int height)
    {
        SetFieldValue(backend, "_frameWidth", width);
        SetFieldValue(backend, "_frameHeight", height);
        SetFieldValue(backend, "_frameStride", width * 4);
        InvokeMethod(backend, "AllocateFrameBuffer");
    }

    private static void AssertBufferInvalidationRaceHandledWithoutThrow(object backend)
    {
        var frameGate = GetFieldValue<object>(backend, "_frameGate");
        Exception? workerException = null;
        var acquired = false;
        var started = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            try
            {
                started.Set();
                acquired = InvokeTryAcquireFrame(backend);
            }
            catch (Exception ex)
            {
                workerException = ex;
            }
        })
        {
            IsBackground = true
        };

        lock (frameGate)
        {
            worker.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(1)));

            var blocked = SpinWait.SpinUntil(
                () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(1));
            Assert.True(blocked, "Expected worker to block on frame gate.");

            var handle = GetFieldValue<GCHandle>(backend, "_pinnedFrameBuffer");
            if (handle.IsAllocated)
            {
                handle.Free();
            }

            SetFieldValue(backend, "_pinnedFrameBuffer", default(GCHandle));
            SetFieldValue<byte[]?>(backend, "_frameBuffer", null);
        }

        Assert.True(worker.Join(TimeSpan.FromSeconds(1)));
        Assert.Null(workerException);
        Assert.False(acquired);
    }

    private static bool InvokeTryAcquireFrame(object backend)
    {
        var method = GetMethodInfo(backend.GetType(), "TryAcquireFrame");
        var arguments = new object?[] { null };
        var result = (bool)method.Invoke(backend, arguments)!;

        if (result && arguments[0] is { } lease)
        {
            // MediaFrameLease is internal, so dispose it via reflection.
            var disposeMethod = lease.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public);
            disposeMethod?.Invoke(lease, null);
        }

        return result;
    }

    private static void DisposeBackend(object backend)
    {
        if (backend is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static void InvokeMethod(object instance, string methodName)
    {
        var method = GetMethodInfo(instance.GetType(), methodName);
        method.Invoke(instance, null);
    }

    private static FieldInfo GetFieldInfo(Type type, string fieldName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field;
            }
        }

        throw new InvalidOperationException($"Field '{fieldName}' was not found on '{type.FullName}'.");
    }

    private static MethodInfo GetMethodInfo(Type type, string methodName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method is not null)
            {
                return method;
            }
        }

        throw new InvalidOperationException($"Method '{methodName}' was not found on '{type.FullName}'.");
    }

    private static T GetFieldValue<T>(object instance, string fieldName)
    {
        var field = GetFieldInfo(instance.GetType(), fieldName);
        return (T)field.GetValue(instance)!;
    }

    private static void SetFieldValue<T>(object instance, string fieldName, T value)
    {
        var field = GetFieldInfo(instance.GetType(), fieldName);
        field.SetValue(instance, value);
    }
}
