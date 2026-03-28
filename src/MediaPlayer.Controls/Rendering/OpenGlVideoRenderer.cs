using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Avalonia.OpenGL;
using MediaPlayer.Controls.Backends;
using static Avalonia.OpenGL.GlConsts;

namespace MediaPlayer.Controls.Rendering;

internal sealed class OpenGlVideoRenderer
{
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlClampToEdge = 0x812F;
    private const int GlDynamicDraw = 0x88E8;

    private int _program;
    private int _vertexShader;
    private int _fragmentShader;
    private int _vertexBuffer;
    private int _texture;
    private int _vertexArray;
    private int _samplerUniformLocation;
    private int _videoWidth;
    private int _videoHeight;
    private int _textureWidth;
    private int _textureHeight;
    private MediaFramePixelFormat _texturePixelFormat;
    private int _quadViewportWidth;
    private int _quadViewportHeight;
    private int _quadVideoWidth;
    private int _quadVideoHeight;
    private readonly float[] _quadVertices = new float[24];
    private bool _quadDirty = true;
    private VideoLayoutMode _layoutMode = VideoLayoutMode.Fit;
    // Two-phase staging buffer: see CopyFrameToStagingBuffer XML doc for full explanation.
    // Required to prevent AMD GPU driver (atio6axx.dll) crash caused by async AVX2 reads
    // of the pixels pointer passed to glTexSubImage2D after the GCHandle pin is released.
    private int _stagedWidth;
    private int _stagedHeight;
    private MediaFramePixelFormat _stagedPixelFormat;
    private bool _stagedFrameReady;
    private byte[]? _strideCopyBuffer;
    private bool _initialized;
    private GlVersion _glVersion;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void GlGenVertexArraysDelegate(int n, uint* arrays);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GlBindVertexArrayDelegate(uint array);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void GlDeleteVertexArraysDelegate(int n, uint* arrays);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GlUniform1iDelegate(int location, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GlTexSubImage2DDelegate(
        int target,
        int level,
        int xoffset,
        int yoffset,
        int width,
        int height,
        int format,
        int type,
        IntPtr pixels);

    private GlGenVertexArraysDelegate? _glGenVertexArrays;
    private GlBindVertexArrayDelegate? _glBindVertexArray;
    private GlDeleteVertexArraysDelegate? _glDeleteVertexArrays;
    private GlUniform1iDelegate? _glUniform1i;
    private GlTexSubImage2DDelegate? _glTexSubImage2D;

    public void Initialize(GlInterface gl, GlVersion glVersion)
    {
        if (_initialized)
        {
            return;
        }

        _glVersion = glVersion;
        _vertexShader = CompileShader(gl, GL_VERTEX_SHADER, GetVertexShaderSource(glVersion));
        _fragmentShader = CompileShader(gl, GL_FRAGMENT_SHADER, GetFragmentShaderSource(glVersion));

        _program = gl.CreateProgram();
        gl.BindAttribLocationString(_program, 0, "aPos");
        gl.BindAttribLocationString(_program, 1, "aTex");
        gl.AttachShader(_program, _vertexShader);
        gl.AttachShader(_program, _fragmentShader);

        var linkError = gl.LinkProgramAndGetError(_program);
        if (!string.IsNullOrWhiteSpace(linkError))
        {
            throw new InvalidOperationException($"Failed to link video shader program. {linkError}");
        }

        _samplerUniformLocation = gl.GetUniformLocationString(_program, "uTex");
        _glUniform1i = TryLoadDelegate<GlUniform1iDelegate>(gl, "glUniform1i");
        _glTexSubImage2D = TryLoadDelegate<GlTexSubImage2DDelegate>(gl, "glTexSubImage2D");

        _vertexBuffer = gl.GenBuffer();
        _texture = gl.GenTexture();

        _glGenVertexArrays = TryLoadDelegate<GlGenVertexArraysDelegate>(gl, "glGenVertexArrays");
        _glBindVertexArray = TryLoadDelegate<GlBindVertexArrayDelegate>(gl, "glBindVertexArray");
        _glDeleteVertexArrays = TryLoadDelegate<GlDeleteVertexArraysDelegate>(gl, "glDeleteVertexArrays");

        if (_glGenVertexArrays is not null && _glBindVertexArray is not null)
        {
            unsafe
            {
                uint vao = 0;
                _glGenVertexArrays(1, &vao);
                _vertexArray = unchecked((int)vao);
                _glBindVertexArray(vao);
            }
        }

        gl.BindTexture(GL_TEXTURE_2D, _texture);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GlTextureWrapS, GlClampToEdge);
        gl.TexParameteri(GL_TEXTURE_2D, GlTextureWrapT, GlClampToEdge);
        gl.BindTexture(GL_TEXTURE_2D, 0);

        _initialized = true;
    }

    /// <summary>
    /// Phase 1 of the two-phase frame upload. Copies all pixel data from the backend frame buffer
    /// into a renderer-owned staging buffer (<c>_strideCopyBuffer</c>) while the
    /// <see cref="MediaFrameLease"/> is still held. No GL calls are made here.
    /// <para>
    /// <b>Why this is required (AMD GPU driver crash fix):</b><br/>
    /// Certain AMD driver versions (<c>atio6axx.dll</c>) process <c>glTexSubImage2D</c>
    /// asynchronously — the driver returns from the call but continues reading the <c>pixels</c>
    /// CPU pointer on an internal driver thread using AVX2 instructions. If the frame lease is
    /// released before the driver finishes its async read, <c>ReleaseFrameBuffer()</c> can
    /// un-pin the managed <c>byte[]</c>, letting the GC move it. The driver's background read
    /// then hits the stale (now-invalid) address, producing an access violation at
    /// <c>0x0000000000000008</c> inside <c>atio6axx!boost::serialization::singleton::is_destroyed</c>.
    /// By copying into a renderer-owned buffer here (while the pin is still active) and only
    /// calling GL in <see cref="UploadStagedFrame"/> after the lease is disposed, the AMD driver's
    /// async read targets memory that is never freed or GC-moved during an active upload.
    /// </para>
    /// <para>
    /// <b>BGRA→RGBA conversion (Intel integrated graphics / ANGLE fix):</b><br/>
    /// When the frame pixel format is <see cref="MediaFramePixelFormat.Bgra32"/>, the B and R
    /// channels are swapped during the copy so that the staging buffer always holds RGBA data.
    /// The staged format is then reported as <see cref="MediaFramePixelFormat.Rgba32"/>, which
    /// causes <see cref="UploadStagedFrame"/> to use <c>GL_RGBA</c> rather than <c>GL_BGRA</c>
    /// for the texture upload.<br/>
    /// <c>GL_BGRA</c> as an external format for <c>glTexImage2D</c>/<c>glTexSubImage2D</c> is not
    /// part of the OpenGL ES core specification. On systems where Avalonia runs through ANGLE
    /// (which translates OpenGL ES calls to Direct3D 11) — common on Intel integrated graphics —
    /// passing <c>GL_BGRA</c> produces a silent <c>GL_INVALID_ENUM</c> error and the texture
    /// remains black. Converting to RGBA on the CPU (which is a cheap in-cache byte swap over
    /// already-touched staging memory) makes the upload path universally compatible across all
    /// GPU vendors, driver versions, and OpenGL profiles.
    /// </para>
    /// </summary>
    public unsafe void CopyFrameToStagingBuffer(in MediaFrameLease frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Data == IntPtr.Zero)
        {
            _stagedFrameReady = false;
            return;
        }

        if (_videoWidth != frame.Width || _videoHeight != frame.Height)
        {
            _videoWidth = frame.Width;
            _videoHeight = frame.Height;
            _quadDirty = true;
        }

        var tightStride = frame.Width * 4;
        var requiredBytes = checked(frame.Height * tightStride);
        if (_strideCopyBuffer is null || _strideCopyBuffer.Length < requiredBytes)
        {
            _strideCopyBuffer = new byte[requiredBytes];
        }

        var srcBase = (byte*)frame.Data;
        fixed (byte* dstBase = _strideCopyBuffer)
        {
            if (frame.PixelFormat == MediaFramePixelFormat.Bgra32)
            {
                // Convert BGRA → RGBA while copying so that glTexSubImage2D can always use the
                // universally-supported GL_RGBA external format instead of GL_BGRA. GL_BGRA is
                // absent from OpenGL ES core and causes silent GL_INVALID_ENUM failures (black
                // screen) on Intel integrated graphics running through ANGLE / Direct3D 11.
                CopyBgraToRgba(srcBase, frame.Stride, dstBase, tightStride, frame.Width, frame.Height);
            }
            else if (frame.Stride == tightStride)
            {
                Buffer.MemoryCopy(srcBase, dstBase, requiredBytes, requiredBytes);
            }
            else
            {
                for (var y = 0; y < frame.Height; y++)
                {
                    Buffer.MemoryCopy(srcBase + (y * frame.Stride), dstBase + (y * tightStride), tightStride, tightStride);
                }
            }
        }

        _stagedWidth = frame.Width;
        _stagedHeight = frame.Height;
        // BGRA data has been converted to RGBA above; always stage as Rgba32 so that
        // UploadStagedFrame uploads with GL_RGBA (universally supported) rather than GL_BGRA.
        _stagedPixelFormat = frame.PixelFormat == MediaFramePixelFormat.Bgra32
            ? MediaFramePixelFormat.Rgba32
            : frame.PixelFormat;
        _stagedFrameReady = true;
    }

    /// <summary>
    /// Phase 2 of the two-phase frame upload. Uploads the pixel data previously copied by
    /// <see cref="CopyFrameToStagingBuffer"/> into the GL texture. Must be called <b>after</b>
    /// the <see cref="MediaFrameLease"/> has been disposed so that the AMD driver's async reads
    /// target renderer-owned memory rather than the backend's GC-pinned frame buffer.
    /// </summary>
    public unsafe void UploadStagedFrame(GlInterface gl)
    {
        EnsureInitialized();

        if (!_stagedFrameReady)
        {
            return;
        }

        _stagedFrameReady = false;

        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GL_TEXTURE_2D, _texture);
        var pixelFormat = _stagedPixelFormat == MediaFramePixelFormat.Bgra32 ? GL_BGRA : GL_RGBA;

        if (_textureWidth != _stagedWidth || _textureHeight != _stagedHeight || _texturePixelFormat != _stagedPixelFormat)
        {
            gl.TexImage2D(
                GL_TEXTURE_2D,
                level: 0,
                internalFormat: GL_RGBA,
                width: _stagedWidth,
                height: _stagedHeight,
                border: 0,
                format: pixelFormat,
                type: GL_UNSIGNED_BYTE,
                data: IntPtr.Zero);

            _textureWidth = _stagedWidth;
            _textureHeight = _stagedHeight;
            _texturePixelFormat = _stagedPixelFormat;
        }

        fixed (byte* ptr = _strideCopyBuffer)
        {
            UploadTextureData(gl, _stagedWidth, _stagedHeight, pixelFormat, new IntPtr(ptr));
        }

        gl.BindTexture(GL_TEXTURE_2D, 0);
    }

    public unsafe void Render(GlInterface gl, int framebuffer, int width, int height)
    {
        EnsureInitialized();

        gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer);
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(GL_COLOR_BUFFER_BIT);

        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return;
        }

        if (_quadDirty
            || _quadViewportWidth != width
            || _quadViewportHeight != height
            || _quadVideoWidth != _videoWidth
            || _quadVideoHeight != _videoHeight)
        {
            UpdateQuadForLayoutMode(width, height, _videoWidth, _videoHeight, _layoutMode);
            _quadDirty = true;
        }

        gl.BindBuffer(GL_ARRAY_BUFFER, _vertexBuffer);
        if (_quadDirty)
        {
            fixed (float* ptr = _quadVertices)
            {
                gl.BufferData(
                    GL_ARRAY_BUFFER,
                    new IntPtr(_quadVertices.Length * sizeof(float)),
                    new IntPtr(ptr),
                    GlDynamicDraw);
            }

            _quadDirty = false;
        }

        gl.UseProgram(_program);
        if (_vertexArray != 0 && _glBindVertexArray is not null)
        {
            _glBindVertexArray(unchecked((uint)_vertexArray));
        }

        gl.ActiveTexture(GL_TEXTURE0);
        if (_samplerUniformLocation >= 0 && _glUniform1i is not null)
        {
            _glUniform1i(_samplerUniformLocation, 0);
        }

        gl.BindTexture(GL_TEXTURE_2D, _texture);

        var stride = 4 * sizeof(float);
        gl.VertexAttribPointer(index: 0, size: 2, type: GL_FLOAT, normalized: 0, stride: stride, pointer: IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(index: 1, size: 2, type: GL_FLOAT, normalized: 0, stride: stride, pointer: new IntPtr(2 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.DrawArrays(GL_TRIANGLES, first: 0, count: new IntPtr(6));

        gl.BindTexture(GL_TEXTURE_2D, 0);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
        if (_vertexArray != 0 && _glBindVertexArray is not null)
        {
            _glBindVertexArray(0);
        }

        gl.UseProgram(0);
    }

    public void Dispose(GlInterface gl)
    {
        if (!_initialized)
        {
            return;
        }

        if (_texture != 0)
        {
            gl.DeleteTexture(_texture);
            _texture = 0;
        }

        if (_vertexBuffer != 0)
        {
            gl.DeleteBuffer(_vertexBuffer);
            _vertexBuffer = 0;
        }

        if (_vertexArray != 0 && _glDeleteVertexArrays is not null)
        {
            unsafe
            {
                uint vao = unchecked((uint)_vertexArray);
                _glDeleteVertexArrays(1, &vao);
            }

            _vertexArray = 0;
        }

        if (_program != 0)
        {
            gl.DeleteProgram(_program);
            _program = 0;
        }

        if (_vertexShader != 0)
        {
            gl.DeleteShader(_vertexShader);
            _vertexShader = 0;
        }

        if (_fragmentShader != 0)
        {
            gl.DeleteShader(_fragmentShader);
            _fragmentShader = 0;
        }

        _videoWidth = 0;
        _videoHeight = 0;
        _textureWidth = 0;
        _textureHeight = 0;
        _quadViewportWidth = 0;
        _quadViewportHeight = 0;
        _quadVideoWidth = 0;
        _quadVideoHeight = 0;
        _quadDirty = true;
        _strideCopyBuffer = null;
        _stagedFrameReady = false;
        _initialized = false;
    }

    public void ResetFrameState()
    {
        _videoWidth = 0;
        _videoHeight = 0;
        _textureWidth = 0;
        _textureHeight = 0;
        _quadDirty = true;
        _stagedFrameReady = false;
    }

    public void SetLayoutMode(VideoLayoutMode mode)
    {
        if (_layoutMode == mode)
        {
            return;
        }

        _layoutMode = mode;
        _quadDirty = true;
    }

    public void SetPreferDirectGpuTextureUpload(bool enabled)
    {
        // No-op: the two-phase staging buffer approach (CopyFrameToStagingBuffer +
        // UploadStagedFrame) is always used. Direct GPU texture upload was removed to
        // fix the AMD driver (atio6axx.dll) async AVX2 read crash.
    }

    private static T? TryLoadDelegate<T>(GlInterface gl, string proc) where T : class
    {
        var address = gl.GetProcAddress(proc);
        if (address == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
    }

    private void UploadTextureData(GlInterface gl, int width, int height, int pixelFormat, IntPtr data)
    {
        if (_glTexSubImage2D is not null)
        {
            _glTexSubImage2D(
                GL_TEXTURE_2D,
                level: 0,
                xoffset: 0,
                yoffset: 0,
                width: width,
                height: height,
                format: pixelFormat,
                type: GL_UNSIGNED_BYTE,
                pixels: data);
            return;
        }

        // Fallback for minimal GL bindings: full upload when glTexSubImage2D is unavailable.
        gl.TexImage2D(
            GL_TEXTURE_2D,
            level: 0,
            internalFormat: GL_RGBA,
            width: width,
            height: height,
            border: 0,
            format: pixelFormat,
            type: GL_UNSIGNED_BYTE,
            data: data);
    }

    private void UpdateQuadForLayoutMode(
        int viewportWidth,
        int viewportHeight,
        int videoWidth,
        int videoHeight,
        VideoLayoutMode mode)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || videoWidth <= 0 || videoHeight <= 0)
        {
            return;
        }

        _quadViewportWidth = viewportWidth;
        _quadViewportHeight = viewportHeight;
        _quadVideoWidth = videoWidth;
        _quadVideoHeight = videoHeight;

        var viewportAspect = (float)viewportWidth / viewportHeight;
        var videoAspect = (float)videoWidth / videoHeight;

        var scaleX = 1f;
        var scaleY = 1f;
        var fillScaleX = 1f;
        var fillScaleY = 1f;

        if (videoAspect > viewportAspect)
        {
            scaleY = viewportAspect / videoAspect;
            fillScaleX = videoAspect / viewportAspect;
        }
        else
        {
            scaleX = videoAspect / viewportAspect;
            fillScaleY = viewportAspect / videoAspect;
        }

        if (mode == VideoLayoutMode.Fill)
        {
            scaleX = fillScaleX;
            scaleY = fillScaleY;
        }
        else if (mode == VideoLayoutMode.Panoramic)
        {
            // Panoramic blends fit and fill for a subtle center-focused crop similar to movie player panoramic view.
            const float panoramicBlend = 0.42f;
            scaleX += (fillScaleX - scaleX) * panoramicBlend;
            scaleY += (fillScaleY - scaleY) * panoramicBlend;
        }

        _quadVertices[0] = -scaleX; _quadVertices[1] = -scaleY; _quadVertices[2] = 0f; _quadVertices[3] = 1f;
        _quadVertices[4] = scaleX; _quadVertices[5] = -scaleY; _quadVertices[6] = 1f; _quadVertices[7] = 1f;
        _quadVertices[8] = scaleX; _quadVertices[9] = scaleY; _quadVertices[10] = 1f; _quadVertices[11] = 0f;
        _quadVertices[12] = -scaleX; _quadVertices[13] = -scaleY; _quadVertices[14] = 0f; _quadVertices[15] = 1f;
        _quadVertices[16] = scaleX; _quadVertices[17] = scaleY; _quadVertices[18] = 1f; _quadVertices[19] = 0f;
        _quadVertices[20] = -scaleX; _quadVertices[21] = scaleY; _quadVertices[22] = 0f; _quadVertices[23] = 0f;
    }

    private static int CompileShader(GlInterface gl, int shaderType, string source)
    {
        var shader = gl.CreateShader(shaderType);
        var compileError = gl.CompileShaderAndGetError(shader, source);
        if (!string.IsNullOrWhiteSpace(compileError))
        {
            throw new InvalidOperationException($"Failed to compile OpenGL shader. {compileError}");
        }

        return shader;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("OpenGlVideoRenderer must be initialized before rendering.");
        }
    }

    private static string GetVertexShaderSource(GlVersion glVersion)
    {
        if (glVersion.Type == GlProfileType.OpenGLES)
        {
            return
                """
                attribute vec2 aPos;
                attribute vec2 aTex;
                varying vec2 vTex;

                void main()
                {
                    vTex = aTex;
                    gl_Position = vec4(aPos, 0.0, 1.0);
                }
                """;
        }

        return
            """
            #version 150
            in vec2 aPos;
            in vec2 aTex;
            out vec2 vTex;

            void main()
            {
                vTex = aTex;
                gl_Position = vec4(aPos, 0.0, 1.0);
            }
            """;
    }

    private static string GetFragmentShaderSource(GlVersion glVersion)
    {
        if (glVersion.Type == GlProfileType.OpenGLES)
        {
            return
                """
                precision mediump float;
                varying vec2 vTex;
                uniform sampler2D uTex;

                void main()
                {
                    vec4 c = texture2D(uTex, vTex);
                    gl_FragColor = vec4(c.rgb, 1.0);
                }
                """;
        }

        return
            """
            #version 150
            in vec2 vTex;
            out vec4 outColor;
            uniform sampler2D uTex;

            void main()
            {
                vec4 c = texture(uTex, vTex);
                outColor = vec4(c.rgb, 1.0);
            }
            """;
    }

    /// <summary>
    /// Copies <paramref name="width"/>×<paramref name="height"/> pixels from <paramref name="src"/>
    /// to <paramref name="dst"/>, swapping the R and B channels (BGRA→RGBA) for every pixel.
    /// Uses hardware-accelerated SIMD (e.g., SSSE3 on x64, NEON on ARM64) when available,
    /// processing four pixels (16 bytes) per iteration; remaining pixels are handled by a scalar loop.
    /// </summary>
    private static unsafe void CopyBgraToRgba(
        byte* src, int srcStride,
        byte* dst, int dstStride,
        int width, int height)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            // Shuffle mask: within each 4-byte BGRA pixel, swap byte 0 (B) with byte 2 (R).
            // Applied to 16 bytes (4 pixels): [2,1,0,3, 6,5,4,7, 10,9,8,11, 14,13,12,15]
            var shuffleMask = Vector128.Create(
                (byte)2, 1, 0, 3,
                      6, 5, 4, 7,
                     10, 9, 8, 11,
                     14, 13, 12, 15);

            for (var y = 0; y < height; y++)
            {
                var s = src + (y * srcStride);
                var d = dst + (y * dstStride);
                var remaining = width;

                while (remaining >= 4)
                {
                    // Vector128.Load / Store use Unsafe.ReadUnaligned / WriteUnaligned internally,
                    // so no 16-byte alignment requirement is imposed on s or d.
                    Vector128.Store(Vector128.Shuffle(Vector128.Load<byte>(s), shuffleMask), d);
                    s += 16;
                    d += 16;
                    remaining -= 4;
                }

                while (remaining-- > 0)
                {
                    d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = s[3];
                    s += 4;
                    d += 4;
                }
            }
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                var s = src + (y * srcStride);
                var d = dst + (y * dstStride);
                for (var x = 0; x < width; x++, s += 4, d += 4)
                {
                    d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = s[3];
                }
            }
        }
    }
}
