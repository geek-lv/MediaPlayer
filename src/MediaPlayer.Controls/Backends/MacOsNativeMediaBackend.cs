using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MediaPlayer.Controls.Backends;

internal sealed class MacOsNativeMediaBackend : NativeFramePumpMediaBackend
{
    private static readonly object s_helperBuildGate = new();
    private static readonly object s_cacheGate = new();
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };
    private static string? s_helperPath;
    private static string? s_helperBuildError;

    protected override string ProfileName => "macOS Native AVFoundation";

    protected override string NativeDecodeApi => "AVFoundation/VideoToolbox (native GPU decode, CPU fallback by platform)";

    protected override string NativeRenderPath => "AVFoundation frame output -> managed BGRA frame pump -> Avalonia OpenGL texture";

    public MacOsNativeMediaBackend()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            throw new PlatformNotSupportedException("MacOsNativeMediaBackend can only be used on macOS.");
        }
    }

    protected override bool TryEnsureHelperReady(out string error)
    {
        lock (s_helperBuildGate)
        {
            if (!string.IsNullOrWhiteSpace(s_helperPath) && File.Exists(s_helperPath))
            {
                error = string.Empty;
                return true;
            }

            var helperRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MediaPlayer",
                "native-helpers",
                "macos");
            Directory.CreateDirectory(helperRoot);

            var sourcePath = Path.Combine(helperRoot, "avfoundation-frame-pump.swift");
            var binaryPath = Path.Combine(helperRoot, "avfoundation-frame-pump");
            File.WriteAllText(sourcePath, SwiftHelperSource);

            if (!File.Exists(binaryPath)
                || File.GetLastWriteTimeUtc(binaryPath) < File.GetLastWriteTimeUtc(sourcePath))
            {
                if (!TryCompileSwiftHelper(sourcePath, binaryPath, out var buildError))
                {
                    s_helperBuildError = buildError;
                    error = buildError;
                    return false;
                }
            }

            s_helperPath = binaryPath;
            s_helperBuildError = string.Empty;
            error = string.Empty;
            return true;
        }
    }

    protected override ProcessStartInfo CreateProbeProcess(Uri source)
    {
        var psi = CreateToolProcess(s_helperPath!);
        psi.ArgumentList.Add("probe");
        psi.ArgumentList.Add("--source");
        psi.ArgumentList.Add(ResolveMediaSource(source));
        return psi;
    }

    protected override ProcessStartInfo CreatePlaybackProcess(Uri source, TimeSpan startPosition)
    {
        var psi = CreateToolProcess(s_helperPath!);
        psi.ArgumentList.Add("play");
        psi.ArgumentList.Add("--source");
        psi.ArgumentList.Add(ResolveMediaSource(source));
        psi.ArgumentList.Add("--start");
        psi.ArgumentList.Add(FormatSeconds(startPosition));
        return psi;
    }

    protected override ProcessStartInfo CreateSingleFrameProcess(Uri source, TimeSpan position)
    {
        var psi = CreateToolProcess(s_helperPath!);
        psi.ArgumentList.Add("frame");
        psi.ArgumentList.Add("--source");
        psi.ArgumentList.Add(ResolveMediaSource(source));
        psi.ArgumentList.Add("--time");
        psi.ArgumentList.Add(FormatSeconds(position));
        return psi;
    }

    private static string ResolveMediaSource(Uri source)
    {
        if (source.IsFile)
        {
            return source.LocalPath;
        }

        if (source.Scheme == Uri.UriSchemeHttp || source.Scheme == Uri.UriSchemeHttps)
        {
            return EnsureRemoteMediaCached(source);
        }

        return source.ToString();
    }

    private static string EnsureRemoteMediaCached(Uri source)
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MediaPlayer",
            "native-helpers",
            "macos",
            "cache");
        Directory.CreateDirectory(cacheRoot);

        var key = source.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var extension = Path.GetExtension(source.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            extension = ".mp4";
        }

        var destinationPath = Path.Combine(cacheRoot, $"{hash}{extension}");
        lock (s_cacheGate)
        {
            if (File.Exists(destinationPath))
            {
                return destinationPath;
            }
        }

        var tempPath = destinationPath + ".part";
        using var response = s_httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var sourceStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using (var target = File.Create(tempPath))
        {
            sourceStream.CopyTo(target);
        }

        lock (s_cacheGate)
        {
            if (!File.Exists(destinationPath))
            {
                File.Move(tempPath, destinationPath, overwrite: true);
            }
            else
            {
                File.Delete(tempPath);
            }
        }

        return destinationPath;
    }

    private static bool TryCompileSwiftHelper(string sourcePath, string outputPath, out string error)
    {
        error = string.Empty;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xcrun",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("swiftc");
            psi.ArgumentList.Add("-O");
            psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add("-framework");
            psi.ArgumentList.Add("AVFoundation");
            psi.ArgumentList.Add("-framework");
            psi.ArgumentList.Add("CoreVideo");
            psi.ArgumentList.Add("-framework");
            psi.ArgumentList.Add("CoreMedia");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputPath);

            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "Unable to start xcrun for AVFoundation helper build.";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? $"xcrun swiftc failed. {stdout}".Trim()
                    : stderr.Trim();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Unable to compile macOS native helper: {ex.Message}";
            return false;
        }
    }

    private const string SwiftHelperSource = """
import Foundation
import AVFoundation
import CoreMedia
import CoreVideo
import QuartzCore

struct ProbeInfo: Codable {
    let width: Int
    let height: Int
    let duration: Double
}

enum HelperError: Error {
    case invalidArgs(String)
    case mediaOpen(String)
    case readerInit(String)
    case streamUnavailable(String)
}

func value(for flag: String, args: [String]) -> String? {
    guard let idx = args.firstIndex(of: flag), idx + 1 < args.count else {
        return nil
    }
    return args[idx + 1]
}

func mediaURL(from source: String) -> URL {
    if source.hasPrefix("http://") || source.hasPrefix("https://") || source.hasPrefix("file://") {
        return URL(string: source) ?? URL(fileURLWithPath: source)
    }
    return URL(fileURLWithPath: source)
}

func waitForAsset(_ asset: AVAsset, keys: [String]) throws {
    let semaphore = DispatchSemaphore(value: 0)
    asset.loadValuesAsynchronously(forKeys: keys) {
        semaphore.signal()
    }
    semaphore.wait()

    for key in keys {
        var error: NSError?
        let status = asset.statusOfValue(forKey: key, error: &error)
        if status != .loaded {
            throw error ?? HelperError.mediaOpen("Asset key \(key) failed to load.")
        }
    }
}

func loadVideoTrack(_ asset: AVAsset) throws -> AVAssetTrack {
    try waitForAsset(asset, keys: ["tracks", "duration"])
    guard let track = asset.tracks(withMediaType: .video).first else {
        throw HelperError.mediaOpen("No video track found.")
    }
    return track
}

func transformedSize(for track: AVAssetTrack) -> (Int, Int) {
    let transformed = track.naturalSize.applying(track.preferredTransform)
    let width = max(1, Int(abs(transformed.width).rounded()))
    let height = max(1, Int(abs(transformed.height).rounded()))
    return (width, height)
}

func probe(source: String) throws {
    let url = mediaURL(from: source)
    let asset = AVURLAsset(url: url)
    let track = try loadVideoTrack(asset)
    let (width, height) = transformedSize(for: track)
    let durationSeconds = max(0.0, CMTimeGetSeconds(asset.duration))
    let payload = ProbeInfo(width: width, height: height, duration: durationSeconds)
    let data = try JSONEncoder().encode(payload)
    FileHandle.standardOutput.write(data)
}

func createReader(asset: AVAsset, track: AVAssetTrack, startSeconds: Double) throws -> (AVAssetReader, AVAssetReaderTrackOutput) {
    let reader = try AVAssetReader(asset: asset)
    let settings: [String: Any] = [
        kCVPixelBufferPixelFormatTypeKey as String: Int(kCVPixelFormatType_32BGRA)
    ]
    let output = AVAssetReaderTrackOutput(track: track, outputSettings: settings)
    output.alwaysCopiesSampleData = false
    guard reader.canAdd(output) else {
        throw HelperError.readerInit("Unable to add track output.")
    }
    reader.add(output)

    if startSeconds > 0.0 {
        let start = CMTime(seconds: startSeconds, preferredTimescale: 600)
        let remaining = CMTimeSubtract(asset.duration, start)
        if CMTimeCompare(remaining, .zero) > 0 {
            reader.timeRange = CMTimeRange(start: start, duration: remaining)
        }
    }

    guard reader.startReading() else {
        throw HelperError.readerInit("Reader failed to start.")
    }

    return (reader, output)
}

func writeSample(_ sample: CMSampleBuffer, width: Int, height: Int, reusableBuffer: inout Data) throws {
    guard let pixelBuffer = CMSampleBufferGetImageBuffer(sample) else {
        throw HelperError.streamUnavailable("No image buffer in sample.")
    }

    CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
    defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }

    guard let baseAddress = CVPixelBufferGetBaseAddress(pixelBuffer) else {
        throw HelperError.streamUnavailable("No base address in pixel buffer.")
    }

    let srcWidth = CVPixelBufferGetWidth(pixelBuffer)
    let srcHeight = CVPixelBufferGetHeight(pixelBuffer)
    let srcStride = CVPixelBufferGetBytesPerRow(pixelBuffer)
    let targetStride = width * 4
    let copyWidthBytes = min(width, srcWidth) * 4
    let copyRows = min(height, srcHeight)

    if reusableBuffer.count != targetStride * height {
        reusableBuffer = Data(count: targetStride * height)
    }

    reusableBuffer.withUnsafeMutableBytes { dstRaw in
        guard let dstBase = dstRaw.baseAddress else { return }
        for y in 0..<copyRows {
            let src = baseAddress.advanced(by: y * srcStride)
            let dst = dstBase.advanced(by: y * targetStride)
            memcpy(dst, src, copyWidthBytes)

            if copyWidthBytes < targetStride {
                memset(dst.advanced(by: copyWidthBytes), 0, targetStride - copyWidthBytes)
            }
        }

        if copyRows < height {
            let tail = dstBase.advanced(by: copyRows * targetStride)
            memset(tail, 0, (height - copyRows) * targetStride)
        }
    }

    FileHandle.standardOutput.write(reusableBuffer)
}

func streamFrames(source: String, startSeconds: Double, singleFrameOnly: Bool) throws {
    let url = mediaURL(from: source)
    let asset = AVURLAsset(url: url)
    let track = try loadVideoTrack(asset)
    let (width, height) = transformedSize(for: track)
    let (_, output) = try createReader(asset: asset, track: track, startSeconds: startSeconds)

    var reusable = Data()
    var firstPts: Double?
    let wallStart = CACurrentMediaTime()

    while let sample = output.copyNextSampleBuffer() {
        let pts = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sample))
        if !singleFrameOnly {
            if let first = firstPts {
                let target = pts - first
                let elapsed = CACurrentMediaTime() - wallStart
                if target > elapsed {
                    Thread.sleep(forTimeInterval: target - elapsed)
                }
            } else {
                firstPts = pts
            }
        }

        try writeSample(sample, width: width, height: height, reusableBuffer: &reusable)
        if singleFrameOnly {
            break
        }
    }
}

func run() throws {
    let args = CommandLine.arguments
    guard args.count >= 2 else {
        throw HelperError.invalidArgs("Missing command.")
    }

    let command = args[1].lowercased()
    let source = value(for: "--source", args: args) ?? ""
    if source.isEmpty {
        throw HelperError.invalidArgs("Missing --source argument.")
    }

    switch command {
    case "probe":
        try probe(source: source)
    case "play":
        let start = Double(value(for: "--start", args: args) ?? "0") ?? 0
        try streamFrames(source: source, startSeconds: max(0, start), singleFrameOnly: false)
    case "frame":
        let at = Double(value(for: "--time", args: args) ?? "0") ?? 0
        try streamFrames(source: source, startSeconds: max(0, at), singleFrameOnly: true)
    default:
        throw HelperError.invalidArgs("Unknown command: \(command)")
    }
}

do {
    try run()
} catch {
    let message = String(describing: error)
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(1)
}
""";
}
