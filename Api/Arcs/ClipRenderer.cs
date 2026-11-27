using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>A text subtitle to burn in: a stream inside the file (by its place among the file's subtitle streams) or a file beside it.</summary>
public sealed record BurnSubtitle(string EscapedPath, int? RelativeIndex);

/// <summary>The source's dynamic range: HDR10 and Dolby Vision use PQ, HLG its own curve. Dolby Vision on its own has no base layer to label.</summary>
public enum ClipHdr
{
    None,
    Pq,
    Hlg,
    DolbyVision
}

/// <summary>How ffmpeg can bring HDR down to standard range: Jellyfin's own tone mapper, the stock filters, or not at all.</summary>
public enum ToneMapper
{
    Tonemapx,
    Zscale,
    None
}

/// <summary>Where a clip is decoded, scaled and encoded: in software, or on an NVIDIA card (NVDEC, CUDA filters, NVENC).</summary>
public enum ClipEncoder
{
    Cpu,
    Nvenc
}

/// <summary>Jellyfin's NVENC setting as it applies to clips: which source codecs the card may decode.</summary>
public sealed record ClipGpu(IReadOnlyCollection<string> DecodeCodecs, bool Hevc10Bit, bool Vp910Bit);

public sealed record ClipRenderRequest(string SourcePath, long StartTicks, int Seconds, int Height, BurnSubtitle? Subtitle, ClipHdr Hdr = ClipHdr.None,
                                       string? Codec = null, int BitDepth = 8);

/// <summary>What every render needs from the server: its ffmpeg, the cache and its size, and the GPU if Jellyfin uses one.</summary>
public sealed record ClipRenderContext(string Ffmpeg, string CacheFolder, long CacheBytes, ClipGpu? Gpu);

/// <summary>Remembers that the GPU path failed, so the next few minutes of clips go straight to the CPU.</summary>
public sealed class GpuHealth
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
    private long _downUntil;

    public bool IsDown(DateTime now) => now.Ticks < Interlocked.Read(ref _downUntil);

    /// <summary>Marks a failure. True when the GPU was working until now, so the caller logs it once.</summary>
    public bool MarkFailed(DateTime now)
    {
        var wasDown = IsDown(now);
        Interlocked.Exchange(ref _downUntil, (now + Cooldown).Ticks);
        return !wasDown;
    }
}

/// <summary>
/// Cuts clips with the server's own ffmpeg into H.264 and AAC, kept in the plugin's data folder and
/// pruned oldest-used first to the admin's size. The feed streams each clip as HLS while it's being
/// made; shared clips are whole MP4s. Two renders run at once; asking for a clip that's already
/// being made joins that render rather than starting another.
/// </summary>
public static partial class ClipRenderer
{
    public const int HighHeight = 720;
    public const int LowHeight = 480;
    public const string PlaylistName = "index.m3u8";
    public const string InitName = "init.mp4";
    public const int SegmentSeconds = 2;
    private const string ClipMarker = ".clip";
    private const string HlsFolderName = "hls";
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> Running = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> HlsRunning = new(StringComparer.Ordinal);
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan GpuFirstSegmentWithin = TimeSpan.FromSeconds(8);

    // CUDA compiles the scale and tone-mapping filters the first time a process uses them, about
    // 15 s on older cards, and keeps the result only where it can write. Jellyfin's home in its
    // container usually can't be written, so every clip paid it. Clips get their own cache, filled
    // once by a warm-up; until then they use the CPU rather than wait.
    private static int _gpuWarm;

    /// <summary>Where CUDA keeps its compiled filters for clip renders; set before the warm-up.</summary>
    public static string? CudaCachePath { get; set; }

    /// <summary>The warm-up finished, so a GPU render starts within a second or two.</summary>
    public static bool GpuReady => Volatile.Read(ref _gpuWarm) == 2;

    /// <summary>Starts the warm-up once: a tiny HDR and SDR render on the GPU, run in the background.</summary>
    public static void EnsureGpuWarm(string ffmpeg, ILogger logger)
    {
        if (Interlocked.CompareExchange(ref _gpuWarm, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var ok = await WarmGpuAsync(ffmpeg, logger).ConfigureAwait(false);
            Volatile.Write(ref _gpuWarm, ok ? 2 : 3);
            if (!ok)
            {
                logger.LogWarning("Arcfin Sync Arcs: the GPU warm-up failed, so clips use the CPU");
            }
        });
    }

    public static IEnumerable<string[]> WarmupArguments()
    {
        string[] common = { "-hide_banner", "-nostdin", "-loglevel", "error", "-init_hw_device", "cuda=cu:0", "-filter_hw_device", "cu",
                            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24:duration=0.25" };
        yield return common.Concat(new[]
        {
            "-vf", "format=p010le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,hwupload,scale_cuda=640:360:format=p010,"
                   + "tonemap_cuda=format=yuv420p:p=bt709:t=bt709:m=bt709:tonemap=bt2390:peak=100:desat=0",
            "-c:v", "h264_nvenc", "-preset", "p4", "-f", "null", "-"
        }).ToArray();
        yield return common.Concat(new[] { "-vf", "format=yuv420p,hwupload,scale_cuda=640:360:format=yuv420p", "-c:v", "h264_nvenc", "-preset", "p4", "-f", "null", "-" }).ToArray();
    }

    private static async Task<bool> WarmGpuAsync(string ffmpeg, ILogger logger)
    {
        foreach (var arguments in WarmupArguments())
        {
            var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            ApplyCudaCache(start);
            foreach (var arg in arguments)
            {
                start.ArgumentList.Add(arg);
            }

            try
            {
                using var process = Process.Start(start);
                if (process is null)
                {
                    return false;
                }

                var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
                _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    logger.LogDebug("Arcfin Sync Arcs: GPU warm-up: {Errors}", await errors.ConfigureAwait(false));
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Arcfin Sync Arcs: GPU warm-up couldn't run");
                return false;
            }
        }

        return true;
    }

    internal static void ApplyCudaCache(ProcessStartInfo start)
    {
        if (CudaCachePath is not { Length: > 0 } path)
        {
            return;
        }

        Directory.CreateDirectory(path);
        start.Environment["CUDA_CACHE_PATH"] = path;
        start.Environment["CUDA_CACHE_MAXSIZE"] = "1073741824";
    }
    private static readonly object PruneGate = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> FilterLists = new(StringComparer.Ordinal);

    /// <summary>Bumped when the way clips are made changes, so cached ones made the old way aren't served.</summary>
    private const string RenderVersion = "r3";

    public static GpuHealth Gpu { get; } = new();

    public static string CacheFolder(string dataFolder) => Path.Combine(dataFolder, "arcs", "cache");

    public static string HlsFolder(string cacheFolder, string key) => Path.Combine(cacheFolder, HlsFolderName, key);

    /// <summary>The cache name for a clip at a length, quality and subtitle, as an MP4 or as HLS.</summary>
    public static string CacheKey(string clipId, int seconds, string quality, int? subtitle, string format = "mp4")
    {
        var raw = string.Create(CultureInfo.InvariantCulture, $"{clipId}|{seconds}|{quality}|{subtitle?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{format}|{RenderVersion}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32].ToLowerInvariant();
    }

    // ----- Arguments -----

    /// <summary>
    /// The ffmpeg arguments for an MP4. The seek is on the input, so decoding starts at the nearest
    /// keyframe and the output starts at 0. Burned subtitles need the source's own timeline, so the
    /// frames are moved back to it for the subtitles filter and to 0 again after.
    /// </summary>
    public static List<string> Arguments(ClipRenderRequest request, string output, ToneMapper mapper = ToneMapper.Tonemapx, ClipEncoder encoder = ClipEncoder.Cpu)
    {
        var args = VideoArguments(request, mapper, encoder, burnSubtitle: true, segmented: false);
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-movflags", "+faststart", "-f", "mp4", "-y", output });
        return args;
    }

    /// <summary>
    /// The ffmpeg arguments for HLS, run in the clip's own folder: an EVENT playlist that grows by a
    /// two-second fMP4 segment at a time and ends with ENDLIST. A keyframe every two seconds, so
    /// segments are cut exactly there. Subtitles are never burned in; the feed shows its own.
    /// </summary>
    public static List<string> HlsArguments(ClipRenderRequest request, ToneMapper mapper = ToneMapper.Tonemapx, ClipEncoder encoder = ClipEncoder.Cpu)
    {
        var args = VideoArguments(request, mapper, encoder, burnSubtitle: false, segmented: true);
        args.AddRange(new[]
        {
            "-c:a", "aac", "-b:a", "128k", "-ac", "2",
            "-f", "hls", "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture), "-hls_list_size", "0",
            "-hls_playlist_type", "event", "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", InitName,
            "-hls_segment_filename", "seg_%05d.m4s", "-hls_flags", "temp_file+independent_segments",
            "-y", PlaylistName
        });
        return args;
    }

    private static List<string> VideoArguments(ClipRenderRequest request, ToneMapper mapper, ClipEncoder encoder, bool burnSubtitle, bool segmented)
    {
        var start = (request.StartTicks / (double)ClipPicker.TicksPerSecond).ToString("0.###", CultureInfo.InvariantCulture);
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        bool toneMapped;
        string filters;
        if (encoder == ClipEncoder.Nvenc)
        {
            args.AddRange(new[] { "-init_hw_device", "cuda=cu:0", "-filter_hw_device", "cu", "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
            toneMapped = request.Hdr != ClipHdr.None;
            filters = string.Join(',', GpuFilters(request));
        }
        else
        {
            toneMapped = request.Hdr != ClipHdr.None && mapper != ToneMapper.None;
            filters = string.Join(',', CpuFilters(request, mapper, burnSubtitle ? request.Subtitle : null, start));
        }

        args.AddRange(new[]
        {
            "-ss", start, "-i", request.SourcePath,
            "-t", request.Seconds.ToString(CultureInfo.InvariantCulture),
            "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn", "-map_chapters", "-1", "-map_metadata", "-1",
            "-vf", filters
        });
        if (encoder == ClipEncoder.Nvenc)
        {
            // Forced keyframes are only IDR frames (where HLS can cut) with forced-idr.
            args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-profile:v", "high", "-forced-idr", "1" });
        }
        else
        {
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-profile:v", "high", "-pix_fmt", "yuv420p" });
        }

        if (segmented)
        {
            args.AddRange(new[] { "-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})" });
        }

        if (toneMapped)
        {
            // Labelled as what it now is, so players don't read the old HDR tags off it.
            args.AddRange(new[] { "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv" });
        }

        return args;
    }

    private static List<string> CpuFilters(ClipRenderRequest request, ToneMapper mapper, BurnSubtitle? subtitle, string start)
    {
        // Scaled first, so the tone mapping works on the small picture; subtitles go on after, in standard range.
        var filters = new List<string> { $"scale=-2:{HeightExpression(request.Height)}" };
        if (request.Hdr != ClipHdr.None && mapper != ToneMapper.None)
        {
            filters.AddRange(ToneMapFilters(request.Hdr, mapper));
        }

        if (subtitle is not null)
        {
            var text = $"subtitles=f='{subtitle.EscapedPath}'";
            if (subtitle.RelativeIndex is { } si)
            {
                text += ":si=" + si.ToString(CultureInfo.InvariantCulture);
            }

            filters.Add($"setpts=PTS+{start}/TB");
            filters.Add(text);
            filters.Add("setpts=PTS-STARTPTS");
        }

        filters.Add("format=yuv420p");
        return filters;
    }

    /// <summary>Decoded frames stay on the card: scaled there, and for HDR kept 10-bit until the tone mapper brings them to 8-bit BT.709.</summary>
    private static IEnumerable<string> GpuFilters(ClipRenderRequest request)
    {
        var height = HeightExpression(request.Height);
        if (request.Hdr == ClipHdr.None)
        {
            yield return $"scale_cuda=-2:{height}:format=yuv420p";
            yield break;
        }

        yield return $"scale_cuda=-2:{height}:format=p010";
        yield return "tonemap_cuda=format=yuv420p:p=bt709:t=bt709:m=bt709:tonemap=bt2390:peak=100:desat=0";
    }

    private static string HeightExpression(int height) => $"trunc(min({height.ToString(CultureInfo.InvariantCulture)}\\,ih)/2)*2";

    /// <summary>
    /// HDR to standard range, as Jellyfin's own software transcode does it: the source labelled with its
    /// curve and colours (some files leave them out), then Jellyfin's tone mapper, which also reads
    /// Dolby Vision's metadata. Without it, the stock zscale and tonemap filters.
    /// </summary>
    public static IEnumerable<string> ToneMapFilters(ClipHdr hdr, ToneMapper mapper)
    {
        var transfer = hdr == ClipHdr.Hlg ? "arib-std-b67" : "smpte2084";
        // Dolby Vision on its own isn't PQ in BT.2020 until its metadata is applied; the tone mapper does that.
        if (hdr != ClipHdr.DolbyVision)
        {
            yield return $"setparams=color_primaries=bt2020:color_trc={transfer}:colorspace=bt2020nc";
        }

        if (mapper == ToneMapper.Tonemapx)
        {
            yield return "tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p";
            yield break;
        }

        yield return "zscale=t=linear:npl=100";
        yield return "format=gbrpf32le";
        yield return "zscale=p=bt709";
        yield return "tonemap=tonemap=hable:desat=0";
        yield return "zscale=t=bt709:m=bt709:r=tv";
    }

    /// <summary>
    /// The GPU only takes clips it can do start to finish: a source codec Jellyfin lets it decode, no
    /// burned subtitles (a software filter), and no Dolby Vision without a base layer (its metadata is
    /// applied on the CPU). After a GPU failure it sits out for a few minutes.
    /// </summary>
    public static ClipEncoder EncoderFor(ClipRenderRequest request, ClipGpu? gpu, IReadOnlySet<string> filters, GpuHealth health, DateTime now)
    {
        if (gpu is null || health.IsDown(now) || request.Subtitle is not null || request.Hdr == ClipHdr.DolbyVision
            || !filters.Contains("scale_cuda") || (request.Hdr != ClipHdr.None && !filters.Contains("tonemap_cuda")))
        {
            return ClipEncoder.Cpu;
        }

        return CanDecode(gpu, request.Codec, request.BitDepth) ? ClipEncoder.Nvenc : ClipEncoder.Cpu;
    }

    private static bool CanDecode(ClipGpu gpu, string? codec, int bitDepth)
    {
        if (string.IsNullOrEmpty(codec) || bitDepth > 10)
        {
            return false;
        }

        codec = codec.ToLowerInvariant() switch
        {
            "h265" => "hevc",
            "mpeg2" => "mpeg2video",
            var c => c
        };
        if (!gpu.DecodeCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return bitDepth <= 8 || codec switch
        {
            "hevc" => gpu.Hevc10Bit,
            "vp9" => gpu.Vp910Bit,
            "av1" => true,
            _ => false
        };
    }

    // ----- Filters this ffmpeg has -----

    /// <summary>The filter names this ffmpeg has, asked once per ffmpeg.</summary>
    public static IReadOnlySet<string> Filters(string ffmpeg, ILogger logger)
    {
        return FilterLists.GetOrAdd(ffmpeg, path =>
        {
            try
            {
                var info = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var arg in new[] { "-hide_banner", "-filters" })
                {
                    info.ArgumentList.Add(arg);
                }

                using var process = Process.Start(info);
                if (process is null)
                {
                    return new HashSet<string>(StringComparer.Ordinal);
                }

                _ = process.StandardError.ReadToEndAsync();
                var text = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10000);
                return ParseFilterNames(text);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Arcs couldn't list ffmpeg's filters; HDR clips won't be tone mapped and the GPU won't be used");
                return new HashSet<string>(StringComparer.Ordinal);
            }
        });
    }

    /// <summary>Which tone mapper this ffmpeg has.</summary>
    public static ToneMapper FindToneMapper(string ffmpeg, ILogger logger) => ToneMapperOf(Filters(ffmpeg, logger));

    /// <summary>The tone mapper named in `ffmpeg -filters` output.</summary>
    public static ToneMapper ParseToneMapper(string filters) => ToneMapperOf(ParseFilterNames(filters));

    public static HashSet<string> ParseFilterNames(string filters)
    {
        return filters.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length > 1).Select(p => p[1]).ToHashSet(StringComparer.Ordinal);
    }

    private static ToneMapper ToneMapperOf(IReadOnlySet<string> names)
    {
        if (names.Contains("tonemapx"))
        {
            return ToneMapper.Tonemapx;
        }

        return names.Contains("zscale") && names.Contains("tonemap") ? ToneMapper.Zscale : ToneMapper.None;
    }

    // ----- MP4 -----

    /// <summary>The clip's MP4, rendering it first if it isn't cached. Null when ffmpeg failed.</summary>
    public static async Task<string?> GetAsync(ClipRenderContext context, string key, ClipRenderRequest request, ILogger logger, CancellationToken cancellationToken)
    {
        var path = Path.Combine(context.CacheFolder, key + ".mp4");
        if (File.Exists(path))
        {
            Touch(path);
            return path;
        }

        var render = Running.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => RenderOnceAsync(context, key, path, request, logger)));
        return await render.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool IsRendering => !Running.IsEmpty || !HlsRunning.IsEmpty;

    private static async Task<string?> RenderOnceAsync(ClipRenderContext context, string key, string path, ClipRenderRequest request, ILogger logger)
    {
        try
        {
            return await RenderAsync(context, path, request, logger).ConfigureAwait(false);
        }
        finally
        {
            Running.TryRemove(key, out _);
        }
    }

    private static async Task<string?> RenderAsync(ClipRenderContext context, string path, ClipRenderRequest request, ILogger logger)
    {
        // Each render has its own time limit; one caller going away mustn't stop it for the others waiting.
        using var timeout = new CancellationTokenSource(RenderTimeout);
        try
        {
            await Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (File.Exists(path))
            {
                Touch(path);
                return path;
            }

            Directory.CreateDirectory(context.CacheFolder);
            var temp = Path.Combine(context.CacheFolder, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var encoder = EncoderFor(request, context.Gpu, Filters(context.Ffmpeg, logger), Gpu, DateTime.UtcNow);
                logger.LogDebug("Arcfin Sync Arcs: making an MP4 of {Path} ({Codec}, {Hdr}) on the {Encoder}", request.SourcePath, request.Codec, request.Hdr, encoder);
                var made = await TryMp4Async(context.Ffmpeg, request, temp, encoder, logger, timeout.Token).ConfigureAwait(false);
                if (!made && encoder == ClipEncoder.Nvenc && !timeout.IsCancellationRequested)
                {
                    made = await TryMp4Async(context.Ffmpeg, request, temp, ClipEncoder.Cpu, logger, timeout.Token).ConfigureAwait(false);
                }

                if (!made)
                {
                    return null;
                }

                File.Move(temp, path, overwrite: true);
                Touch(path);
            }
            catch (IOException error)
            {
                logger.LogWarning("Arcfin Sync Arcs: a clip of {Path} couldn't be kept: {Message}", request.SourcePath, error.Message);
                return null;
            }
            finally
            {
                TryDelete(temp);
            }

            Prune(context.CacheFolder, context.CacheBytes);
            return File.Exists(path) ? path : null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> TryMp4Async(string ffmpeg, ClipRenderRequest request, string temp, ClipEncoder encoder, ILogger logger, CancellationToken timeout)
    {
        var mapper = encoder == ClipEncoder.Cpu && request.Hdr != ClipHdr.None ? FindToneMapper(ffmpeg, logger) : ToneMapper.None;
        var (ok, errors) = await RunAsync(ffmpeg, Arguments(request, temp, mapper, encoder), null, null, request, logger, timeout).ConfigureAwait(false);
        ok = ok && File.Exists(temp) && new FileInfo(temp).Length > 0;
        if (!ok)
        {
            ReportFailure(encoder, request, errors, logger);
            TryDelete(temp);
        }

        return ok;
    }

    // ----- HLS -----

    /// <summary>
    /// The clip's HLS playlist as ffmpeg wrote it, starting or joining its render. Waits up to
    /// <paramref name="wait"/> for the first segment; null when there's none by then or the render failed.
    /// A finished playlist (with ENDLIST) is served from the cache as is.
    /// </summary>
    public static async Task<string?> PlaylistAsync(ClipRenderContext context, string key, string clipId, ClipRenderRequest request, ILogger logger,
                                                    TimeSpan wait, CancellationToken cancellationToken)
    {
        var folder = HlsFolder(context.CacheFolder, key);
        var playlist = Path.Combine(folder, PlaylistName);
        if (!HlsRunning.ContainsKey(key) && ReadText(playlist) is { } cached && IsComplete(cached))
        {
            Touch(playlist);
            return cached;
        }

        var render = StartHls(context, key, clipId, request, logger);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var done = render.IsCompleted;
            if (ReadText(playlist) is { } text && IsReady(text))
            {
                return text;
            }

            if (done || DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Makes the clip's HLS if it isn't cached, and waits for the whole render. For prewarming.</summary>
    public static Task<bool> EnsureHlsAsync(ClipRenderContext context, string key, string clipId, ClipRenderRequest request, ILogger logger)
    {
        var playlist = Path.Combine(HlsFolder(context.CacheFolder, key), PlaylistName);
        if (!HlsRunning.ContainsKey(key) && ReadText(playlist) is { } cached && IsComplete(cached))
        {
            return Task.FromResult(true);
        }

        return StartHls(context, key, clipId, request, logger);
    }

    private static Task<bool> StartHls(ClipRenderContext context, string key, string clipId, ClipRenderRequest request, ILogger logger)
    {
        return HlsRunning.GetOrAdd(key, _ => new Lazy<Task<bool>>(() => HlsOnceAsync(context, key, clipId, request, logger))).Value;
    }

    /// <summary>A playlist with at least one finished segment listed.</summary>
    public static bool IsReady(string playlist) => playlist.Contains("#EXTINF:", StringComparison.Ordinal);

    /// <summary>A playlist ffmpeg finished: nothing more will be added.</summary>
    public static bool IsComplete(string playlist) => playlist.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);

    /// <summary>
    /// The playlist as the app gets it. It's served from Clips/{clipId}/Stream.m3u8, so each file is
    /// pointed at hls/{key}/ beside it. Playback starts at the beginning even while the clip is still
    /// being made. Otherwise as ffmpeg wrote it: a finished one stays EVENT with ENDLIST, as an EVENT
    /// playlist may only grow.
    /// </summary>
    public static string ServedPlaylist(string playlist, string key)
    {
        var prefix = $"{HlsFolderName}/{key}/";
        var lines = new List<string>();
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                lines.Add(MapUri().Replace(line, m => $"URI=\"{prefix}{m.Groups[1].Value}\""));
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                lines.Add(prefix + line);
            }
            else
            {
                lines.Add(line);
            }

            if (line == "#EXTM3U")
            {
                lines.Add("#EXT-X-START:TIME-OFFSET=0");
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The file for a segment request, or null: only the init segment and numbered segments ffmpeg
    /// wrote, in a folder made for this very clip (any clip's key can be worked out, so the key alone
    /// proves nothing).
    /// </summary>
    public static string? HlsFile(string cacheFolder, string key, string clipId, string file)
    {
        if (!KeyPattern().IsMatch(key) || !(file == InitName || SegmentPattern().IsMatch(file)))
        {
            return null;
        }

        var folder = HlsFolder(cacheFolder, key);
        if (ReadText(Path.Combine(folder, ClipMarker)) != clipId)
        {
            return null;
        }

        var path = Path.Combine(folder, file);
        return File.Exists(path) ? path : null;
    }

    private static async Task<bool> HlsOnceAsync(ClipRenderContext context, string key, string clipId, ClipRenderRequest request, ILogger logger)
    {
        try
        {
            var folder = HlsFolder(context.CacheFolder, key);
            lock (PruneGate)
            {
                // Left half made by a render that died (a restart, a timeout): nothing will finish it, so start again.
                if (!(ReadText(Path.Combine(folder, PlaylistName)) is { } text && IsComplete(text)))
                {
                    TryDeleteFolder(folder);
                }
            }

            return await HlsRenderAsync(context, folder, clipId, request, logger).ConfigureAwait(false);
        }
        finally
        {
            HlsRunning.TryRemove(key, out _);
        }
    }

    private static async Task<bool> HlsRenderAsync(ClipRenderContext context, string folder, string clipId, ClipRenderRequest request, ILogger logger)
    {
        using var timeout = new CancellationTokenSource(RenderTimeout);
        try
        {
            await Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            var playlist = Path.Combine(folder, PlaylistName);
            if (ReadText(playlist) is { } existing && IsComplete(existing))
            {
                return true;
            }

            var encoder = EncoderFor(request, context.Gpu, Filters(context.Ffmpeg, logger), Gpu, DateTime.UtcNow);
            logger.LogDebug("Arcfin Sync Arcs: making HLS of {Path} ({Codec}, {Hdr}) on the {Encoder}", request.SourcePath, request.Codec, request.Hdr, encoder);
            var made = await TryHlsAsync(context.Ffmpeg, request, folder, clipId, encoder, logger, timeout.Token).ConfigureAwait(false);
            if (!made && encoder == ClipEncoder.Nvenc && !timeout.IsCancellationRequested)
            {
                made = await TryHlsAsync(context.Ffmpeg, request, folder, clipId, ClipEncoder.Cpu, logger, timeout.Token).ConfigureAwait(false);
            }

            if (!made)
            {
                lock (PruneGate)
                {
                    TryDeleteFolder(folder);
                }

                return false;
            }

            Touch(playlist);
            Prune(context.CacheFolder, context.CacheBytes);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> TryHlsAsync(string ffmpeg, ClipRenderRequest request, string folder, string clipId, ClipEncoder encoder, ILogger logger,
                                                CancellationToken timeout)
    {
        var playlist = Path.Combine(folder, PlaylistName);
        try
        {
            lock (PruneGate)
            {
                TryDeleteFolder(folder);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, ClipMarker), clipId);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Arcfin Sync Arcs: couldn't make a folder for a clip of {Path}: {Message}", request.SourcePath, error.Message);
            return false;
        }

        var mapper = encoder == ClipEncoder.Cpu && request.Hdr != ClipHdr.None ? FindToneMapper(ffmpeg, logger) : ToneMapper.None;
        // A GPU that can't decode or filter this file usually fails at once, but it can also just hang; either way the CPU gets it.
        Func<bool>? started = encoder == ClipEncoder.Nvenc ? () => ReadText(playlist) is { } t && IsReady(t) : null;
        var (ok, errors) = await RunAsync(ffmpeg, HlsArguments(request, mapper, encoder), folder, started, request, logger, timeout).ConfigureAwait(false);
        ok = ok && ReadText(playlist) is { } text && IsComplete(text);
        if (!ok)
        {
            ReportFailure(encoder, request, errors, logger);
        }

        return ok;
    }

    // ----- Running ffmpeg -----

    private static async Task<(bool Ok, string Errors)> RunAsync(string ffmpeg, List<string> arguments, string? workingDirectory, Func<bool>? started,
                                                                 ClipRenderRequest request, ILogger logger, CancellationToken timeout)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        ApplyCudaCache(start);

        foreach (var arg in arguments)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return (false, "ffmpeg didn't start");
            }

            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                if (started is not null)
                {
                    var within = DateTime.UtcNow + GpuFirstSegmentWithin;
                    while (!process.HasExited && !started())
                    {
                        if (DateTime.UtcNow >= within)
                        {
                            process.Kill(true);
                            return (false, "no first segment in time");
                        }

                        await Task.Delay(100, timeout).ConfigureAwait(false);
                    }
                }

                await process.WaitForExitAsync(timeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                logger.LogWarning("Arcfin Sync Arcs: a clip of {Path} took too long and was stopped", request.SourcePath);
                return (false, "timed out");
            }

            return (process.ExitCode == 0, await errors.ConfigureAwait(false));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return (false, error.Message);
        }
    }

    private static void ReportFailure(ClipEncoder encoder, ClipRenderRequest request, string errors, ILogger logger)
    {
        if (encoder == ClipEncoder.Nvenc)
        {
            if (Gpu.MarkFailed(DateTime.UtcNow))
            {
                logger.LogWarning("Arcfin Sync Arcs: the GPU couldn't make a clip of {Path}, so clips use the CPU for the next {Minutes} minutes: {Errors}",
                                  request.SourcePath, GpuHealth.Cooldown.TotalMinutes, errors);
            }

            return;
        }

        logger.LogWarning("Arcfin Sync Arcs: a clip of {Path} failed: {Errors}", request.SourcePath, errors);
    }

    // ----- Cache -----

    private sealed record CacheEntry(string Path, bool IsFolder, long Bytes, DateTime UsedAt);

    /// <summary>Every finished MP4 and HLS folder, or with <paramref name="includeRendering"/> also the ones being written.</summary>
    private static List<CacheEntry> Entries(string cacheFolder, bool includeRendering)
    {
        var entries = new List<CacheEntry>();
        if (!Directory.Exists(cacheFolder))
        {
            return entries;
        }

        foreach (var file in new DirectoryInfo(cacheFolder).EnumerateFiles("*.mp4"))
        {
            entries.Add(new CacheEntry(file.FullName, false, file.Length, file.LastAccessTimeUtc));
        }

        var hls = new DirectoryInfo(Path.Combine(cacheFolder, HlsFolderName));
        if (hls.Exists)
        {
            foreach (var folder in hls.EnumerateDirectories())
            {
                if (!includeRendering && HlsRunning.ContainsKey(folder.Name))
                {
                    continue;
                }

                try
                {
                    var files = folder.EnumerateFiles().ToList();
                    var playlist = files.FirstOrDefault(f => f.Name == PlaylistName);
                    entries.Add(new CacheEntry(folder.FullName, true, files.Sum(f => f.Length), playlist?.LastAccessTimeUtc ?? folder.LastWriteTimeUtc));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return entries;
    }

    /// <summary>Deletes every cached clip not being written right now.</summary>
    public static void Clear(string cacheFolder)
    {
        lock (PruneGate)
        {
            foreach (var entry in Entries(cacheFolder, includeRendering: false))
            {
                Delete(entry);
            }
        }
    }

    public static long CacheSize(string cacheFolder) => Entries(cacheFolder, includeRendering: true).Sum(e => e.Bytes);

    /// <summary>Removes the least recently used clips until the cache fits in <paramref name="limitBytes"/>. Never one being written.</summary>
    public static void Prune(string cacheFolder, long limitBytes)
    {
        lock (PruneGate)
        {
            var all = Entries(cacheFolder, includeRendering: true);
            var total = all.Sum(e => e.Bytes);
            foreach (var entry in all.Where(e => !(e.IsFolder && HlsRunning.ContainsKey(Path.GetFileName(e.Path)))).OrderBy(e => e.UsedAt))
            {
                if (total <= limitBytes)
                {
                    break;
                }

                if (Delete(entry))
                {
                    total -= entry.Bytes;
                }
            }
        }
    }

    private static bool Delete(CacheEntry entry) => entry.IsFolder ? TryDeleteFolder(entry.Path) : TryDelete(entry.Path);

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^seg_[0-9]{5,6}\.m4s$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("URI=\"([^\"]*)\"")]
    private static partial Regex MapUri();
}
