using System.Buffers;
using System.IO;
using System.Text;
using MediaPlayer.Native.Abstractions;
using MediaPlayer.Native.Interop;

namespace MediaPlayer.Controls.Workflows;

internal sealed class WavInteropMediaWorkflowProvider : IInteropMediaWorkflowProvider
{
    private const uint RiffTag = 0x46464952; // "RIFF"
    private const uint WaveTag = 0x45564157; // "WAVE"
    private const uint FmtTag = 0x20746D66; // "fmt "
    private const uint DataTag = 0x61746164; // "data"
    private const short PcmFormat = 1;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public MediaPlayerInteropWorkflowProviderId ProviderId => MediaPlayerInteropWorkflowProviderId.ManagedPcmWaveInterop;

    public string Name => "Managed PCM WAV Interop";

    public MediaPlayerNativeProviderKind ProviderKind => MediaPlayerNativeProviderKind.Interop;

    public bool IsAvailable => true;

    public string UnavailableReason => string.Empty;

    public Task<MediaWorkflowResult?> TrimAsync(
        Uri source,
        TimeSpan startTime,
        TimeSpan endTime,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (endTime <= startTime)
            {
                return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail("Trim duration must be positive."));
            }

            if (!IsWavePath(outputPath))
            {
                return Task.FromResult<MediaWorkflowResult?>(null);
            }

            if (!TryResolveSupportedSource(source, outputPath, out var sourcePath, out var sourceValidationError))
            {
                return Task.FromResult<MediaWorkflowResult?>(sourceValidationError is null ? null : MediaWorkflowResult.Fail(sourceValidationError));
            }

            if (!TryReadMetadata(sourcePath, out var metadata, out var error))
            {
                return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail(error));
            }

            var startOffset = AlignDownToBlock(
                (long)Math.Floor(Math.Max(0d, startTime.TotalSeconds) * metadata.ByteRate),
                metadata.BlockAlign);
            var endOffset = AlignDownToBlock(
                (long)Math.Ceiling(Math.Max(0d, endTime.TotalSeconds) * metadata.ByteRate),
                metadata.BlockAlign);

            startOffset = Math.Clamp(startOffset, 0, metadata.DataLength);
            endOffset = Math.Clamp(endOffset, 0, metadata.DataLength);
            var copyLength = endOffset - startOffset;
            if (copyLength <= 0)
            {
                return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail("Trim produced an empty range."));
            }

            WriteWaveSlice(metadata, sourcePath, outputPath, startOffset, copyLength, cancellationToken);
            return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Ok());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail("Operation canceled."));
        }
        catch (Exception ex)
        {
            return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail(ex.Message));
        }
    }

    public async Task<MediaWorkflowResult?> SplitAsync(
        Uri source,
        TimeSpan splitTime,
        TimeSpan duration,
        string partOnePath,
        string partTwoPath,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return MediaWorkflowResult.Fail("Split requires known media duration.");
        }

        if (splitTime <= TimeSpan.Zero || splitTime >= duration)
        {
            return MediaWorkflowResult.Fail("Split time must be inside media duration.");
        }

        var first = await TrimAsync(source, TimeSpan.Zero, splitTime, partOnePath, cancellationToken).ConfigureAwait(false);
        if (first is null || !first.Value.Success)
        {
            return first;
        }

        var second = await TrimAsync(source, splitTime, duration, partTwoPath, cancellationToken).ConfigureAwait(false);
        if (second is null || !second.Value.Success)
        {
            return second;
        }

        return MediaWorkflowResult.Ok();
    }

    public Task<MediaWorkflowResult?> CombineAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (inputPaths.Count < 2)
            {
                return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail("At least two clips are required."));
            }

            if (!IsWavePath(outputPath))
            {
                return Task.FromResult<MediaWorkflowResult?>(null);
            }

            var clips = new List<WavMetadata>(inputPaths.Count);
            for (var index = 0; index < inputPaths.Count; index++)
            {
                var inputPath = inputPaths[index];
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail($"Clip not found: {inputPath}"));
                }

                if (!TryReadMetadata(inputPath, out var metadata, out _))
                {
                    return Task.FromResult<MediaWorkflowResult?>(null);
                }

                if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), PathComparison))
                {
                    return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail("Output file must be different from input clips."));
                }

                if (clips.Count > 0 && !HasMatchingFormat(clips[0], metadata))
                {
                    return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail("All WAV clips must share the same PCM format."));
                }

                clips.Add(metadata);
            }

            WriteCombinedWave(clips, inputPaths, outputPath, cancellationToken);
            return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Ok());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail("Operation canceled."));
        }
        catch (Exception ex)
        {
            return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail(ex.Message));
        }
    }

    public Task<MediaWorkflowResult?> RemoveAudioAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<MediaWorkflowResult?>(null);
    }

    public Task<MediaWorkflowResult?> RemoveVideoAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<MediaWorkflowResult?>(null);
    }

    public Task<MediaWorkflowResult?> TransformAsync(
        Uri source,
        string outputPath,
        MediaVideoTransform transform,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<MediaWorkflowResult?>(null);
    }

    public Task<MediaWorkflowResult?> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (preset != MediaExportPreset.AudioOnly)
            {
                return Task.FromResult<MediaWorkflowResult?>(null);
            }

            if (!IsWavePath(outputPath))
            {
                return Task.FromResult<MediaWorkflowResult?>(null);
            }

            if (!TryResolveSupportedSource(source, outputPath, out var sourcePath, out var sourceValidationError))
            {
                return Task.FromResult<MediaWorkflowResult?>(sourceValidationError is null ? null : MediaWorkflowResult.Fail(sourceValidationError));
            }

            if (!TryReadMetadata(sourcePath, out _, out var error))
            {
                return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail(error));
            }

            EnsureParentDirectory(outputPath);
            File.Copy(sourcePath, outputPath, overwrite: true);
            return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Ok());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail("Operation canceled."));
        }
        catch (Exception ex)
        {
            return Task.FromResult<MediaWorkflowResult?>(MediaWorkflowResult.Fail(ex.Message));
        }
    }

    public Task<MediaWorkflowResult?> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaExportOptions options,
        CancellationToken cancellationToken = default)
    {
        if (HasExportOverrides(options))
        {
            return Task.FromResult<MediaWorkflowResult?>(null);
        }

        return ExportAsync(
            source,
            outputPath,
            preset,
            options.QualityProfile ?? MediaWorkflowQualityProfile.Balanced,
            cancellationToken);
    }

    public Task<MediaWorkflowResult?> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<MediaWorkflowResult?>(null);
    }

    public Task<MediaWorkflowResult?> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaRecordingOptions options,
        CancellationToken cancellationToken = default)
    {
        if (HasRecordingOverrides(options))
        {
            return Task.FromResult<MediaWorkflowResult?>(null);
        }

        return RecordAsync(
            preset,
            outputPath,
            duration,
            options.QualityProfile ?? MediaWorkflowQualityProfile.Balanced,
            cancellationToken);
    }

    private static bool HasExportOverrides(MediaExportOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.AudioCodec)
               || options.AudioBitrateKbps > 0
               || options.AudioFormat.HasAnyValue
               || options.NormalizeLoudness;
    }

    private static bool HasRecordingOverrides(MediaRecordingOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.InputDeviceId)
               || !string.IsNullOrWhiteSpace(options.OutputDeviceId)
               || options.EnableSystemLoopback
               || options.EnableAcousticEchoCancellation
               || options.EnableNoiseSuppression
               || options.TargetAudioFormat.HasAnyValue;
    }

    private static bool TryResolveSupportedSource(
        Uri source,
        string outputPath,
        out string sourcePath,
        out string? validationError)
    {
        sourcePath = string.Empty;
        validationError = null;
        if (!source.IsFile)
        {
            return false;
        }

        sourcePath = source.LocalPath;
        if (!File.Exists(sourcePath))
        {
            validationError = $"Source file not found: {sourcePath}";
            return false;
        }

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), PathComparison))
        {
            validationError = "Output file must be different from source file.";
            return false;
        }

        return true;
    }

    private static bool IsWavePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".wave", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadMetadata(string path, out WavMetadata metadata, out string error)
    {
        metadata = default;
        error = string.Empty;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 44)
            {
                error = "WAV file is too small.";
                return false;
            }

            var riff = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var wave = reader.ReadUInt32();
            if (riff != RiffTag || wave != WaveTag)
            {
                error = "Source is not a RIFF/WAVE file.";
                return false;
            }

            var foundFmt = false;
            var foundData = false;
            short blockAlign = 0;
            int byteRate = 0;
            byte[] fmtBytes = Array.Empty<byte>();
            long dataOffset = 0;
            long dataLength = 0;

            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = reader.ReadUInt32();
                var chunkSize = reader.ReadInt32();
                if (chunkSize < 0)
                {
                    error = "WAV chunk size is invalid.";
                    return false;
                }

                var chunkStart = stream.Position;
                var chunkEnd = chunkStart + chunkSize;
                if (chunkEnd > stream.Length)
                {
                    error = "WAV chunk exceeds file bounds.";
                    return false;
                }

                if (chunkId == FmtTag)
                {
                    if (chunkSize < 16)
                    {
                        error = "WAV format chunk is invalid.";
                        return false;
                    }

                    fmtBytes = reader.ReadBytes(chunkSize);
                    using var formatStream = new MemoryStream(fmtBytes, writable: false);
                    using var formatReader = new BinaryReader(formatStream, Encoding.UTF8, leaveOpen: false);
                    var formatTag = formatReader.ReadInt16();
                    _ = formatReader.ReadInt16();
                    _ = formatReader.ReadInt32();
                    byteRate = formatReader.ReadInt32();
                    blockAlign = formatReader.ReadInt16();
                    _ = formatReader.ReadInt16();

                    if (formatTag != PcmFormat)
                    {
                        error = "Only PCM WAV is currently supported by interop workflow provider.";
                        return false;
                    }

                    if (blockAlign <= 0 || byteRate <= 0)
                    {
                        error = "WAV format values are invalid.";
                        return false;
                    }

                    foundFmt = true;
                }
                else if (chunkId == DataTag)
                {
                    dataOffset = chunkStart;
                    dataLength = chunkSize;
                    stream.Position = chunkEnd;
                    foundData = true;
                }
                else
                {
                    stream.Position = chunkEnd;
                }

                if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                {
                    stream.Position++;
                }

                if (foundFmt && foundData)
                {
                    break;
                }
            }

            if (!foundFmt || !foundData)
            {
                error = "Required WAV chunks were not found.";
                return false;
            }

            metadata = new WavMetadata(path, fmtBytes, blockAlign, byteRate, dataOffset, dataLength);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Unable to parse WAV metadata: {ex.Message}";
            return false;
        }
    }

    private static bool HasMatchingFormat(in WavMetadata first, in WavMetadata second)
    {
        if (first.BlockAlign != second.BlockAlign
            || first.ByteRate != second.ByteRate
            || first.FormatChunkBytes.Length != second.FormatChunkBytes.Length)
        {
            return false;
        }

        for (var index = 0; index < first.FormatChunkBytes.Length; index++)
        {
            if (first.FormatChunkBytes[index] != second.FormatChunkBytes[index])
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteCombinedWave(
        IReadOnlyList<WavMetadata> clips,
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var totalDataLength = 0L;
        for (var index = 0; index < clips.Count; index++)
        {
            totalDataLength += clips[index].DataLength;
        }

        EnsureParentDirectory(outputPath);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteWaveHeader(output, clips[0].FormatChunkBytes, totalDataLength);

        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(81920);
            for (var index = 0; index < clips.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = new FileStream(inputPaths[index], FileMode.Open, FileAccess.Read, FileShare.Read);
                input.Position = clips[index].DataOffset;
                CopyExact(input, output, clips[index].DataLength, rented, cancellationToken);
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        if ((totalDataLength & 1) != 0)
        {
            output.WriteByte(0);
        }
    }

    private static void WriteWaveSlice(
        in WavMetadata metadata,
        string sourcePath,
        string outputPath,
        long dataOffset,
        long dataLength,
        CancellationToken cancellationToken)
    {
        EnsureParentDirectory(outputPath);
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteWaveHeader(output, metadata.FormatChunkBytes, dataLength);

        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(81920);
            input.Position = metadata.DataOffset + dataOffset;
            CopyExact(input, output, dataLength, rented, cancellationToken);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        if ((dataLength & 1) != 0)
        {
            output.WriteByte(0);
        }
    }

    private static void WriteWaveHeader(Stream output, byte[] formatChunkBytes, long dataLength)
    {
        if (dataLength < 0 || dataLength > uint.MaxValue)
        {
            throw new InvalidOperationException("WAV output data length exceeds RIFF limits.");
        }

        var fmtChunkLength = formatChunkBytes.Length;
        var fmtPadding = fmtChunkLength & 1;
        var dataPadding = (int)(dataLength & 1);
        var riffSize = 4L + (8L + fmtChunkLength + fmtPadding) + (8L + dataLength + dataPadding);
        if (riffSize > uint.MaxValue)
        {
            throw new InvalidOperationException("WAV output exceeds RIFF size limits.");
        }

        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(RiffTag);
        writer.Write((uint)riffSize);
        writer.Write(WaveTag);
        writer.Write(FmtTag);
        writer.Write(fmtChunkLength);
        writer.Write(formatChunkBytes);
        if (fmtPadding != 0)
        {
            writer.Write((byte)0);
        }

        writer.Write(DataTag);
        writer.Write((uint)dataLength);
    }

    private static void CopyExact(
        Stream input,
        Stream output,
        long length,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = remaining > buffer.Length ? buffer.Length : (int)remaining;
            var read = input.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of source WAV stream.");
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static long AlignDownToBlock(long value, int blockAlign)
    {
        if (blockAlign <= 1)
        {
            return Math.Max(0, value);
        }

        var clamped = Math.Max(0, value);
        return clamped - (clamped % blockAlign);
    }

    private static void EnsureParentDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private readonly record struct WavMetadata(
        string Path,
        byte[] FormatChunkBytes,
        short BlockAlign,
        int ByteRate,
        long DataOffset,
        long DataLength);
}
