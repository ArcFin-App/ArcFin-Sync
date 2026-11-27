using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

public sealed class SceneEntry
{
    public Guid ItemId { get; set; }

    /// <summary>The file's modified time when it was scanned; a changed file is scanned again.</summary>
    public DateTime Modified { get; set; }

    public List<long> Starts { get; set; } = new();
}

/// <summary>Clip starts found by scene-change scans, for titles without chapters.</summary>
public static class SceneIndex
{
    private static readonly object Gate = new();
    private static readonly Regex PtsTime = new(@"pts_time:\s*(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
    private static (string Path, DateTime Written, Dictionary<Guid, SceneEntry> Entries)? _cache;

    public static string PathIn(string dataFolder) => Path.Combine(dataFolder, "arcs", "scenes.json");

    public static Dictionary<Guid, SceneEntry> Load(string dataFolder)
    {
        var path = PathIn(dataFolder);
        lock (Gate)
        {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_cache is { } cached && cached.Path == path && cached.Written == written)
            {
                return cached.Entries;
            }

            var entries = (JsonFile.Read<List<SceneEntry>>(path) ?? new List<SceneEntry>()).GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.Last());
            _cache = (path, written, entries);
            return entries;
        }
    }

    public static void Save(string dataFolder, IEnumerable<SceneEntry> entries)
    {
        lock (Gate)
        {
            JsonFile.Write(PathIn(dataFolder), entries.ToList());
            _cache = null;
        }
    }

    /// <summary>The times, in seconds, of the scene changes in ffmpeg's showinfo output.</summary>
    public static List<double> ParseShowInfo(string output)
    {
        return PtsTime.Matches(output).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>
    /// The ffmpeg arguments for a scan: keyframes only (dropped before decoding, so this holds for
    /// every codec), scaled right down, on the GPU when one is given. Encoders put keyframes at
    /// cuts, and clips start on keyframes, so they're good starts.
    /// </summary>
    public static List<string> ScanArguments(string path, bool gpu)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "info" };
        if (gpu)
        {
            args.AddRange(new[] { "-init_hw_device", "cuda=cu:0", "-filter_hw_device", "cu", "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
        }

        args.AddRange(new[] { "-discard", "nokey", "-skip_frame", "nokey", "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn", "-vf" });
        var select = $"select='gt(scene,{SceneThreshold.ToString(CultureInfo.InvariantCulture)})',showinfo";
        args.Add(gpu ? "scale_cuda=160:-2:format=nv12,hwdownload,format=nv12," + select : "scale=160:-2," + select);
        args.AddRange(new[] { "-fps_mode", "passthrough", "-f", "null", "-" });
        return args;
    }

    /// <summary>Lower than for every frame: keyframes are seconds apart, so a cut between two is a smaller change.</summary>
    public const double SceneThreshold = 0.25;

    /// <summary>Scene changes in a file, on the GPU when there is one and on the CPU when that fails. Null when neither worked.</summary>
    public static async Task<List<double>?> ScanAsync(string ffmpeg, string path, bool gpu, ILogger logger, CancellationToken cancellationToken)
    {
        if (gpu)
        {
            var found = await RunScanAsync(ffmpeg, path, true, logger, cancellationToken).ConfigureAwait(false);
            if (found is not null)
            {
                return found;
            }
        }

        return await RunScanAsync(ffmpeg, path, false, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<double>?> RunScanAsync(string ffmpeg, string path, bool gpu, ILogger logger, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in ScanArguments(path, gpu))
        {
            start.ArgumentList.Add(arg);
        }

        if (gpu)
        {
            ClipRenderer.ApplyCudaCache(start);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            ClipRenderer.Background(process);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                logger.LogInformation("Arcfin Sync Arcs: scene scan of {Path} took too long", path);
                return null;
            }

            var output = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                logger.LogDebug("Arcfin Sync Arcs: {Where} scene scan of {Path} failed: {Errors}", gpu ? "GPU" : "CPU", path, output.Length > 2000 ? output[^2000..] : output);
                return null;
            }

            return ParseShowInfo(output);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug("Arcfin Sync Arcs: scene scan of {Path} didn't run: {Message}", path, error.Message);
            return null;
        }
    }
}

/// <summary>"Arcs scene index": finds clip starts in films and episodes that have no chapters.</summary>
public sealed class ArcsSceneIndexTask : IScheduledTask
{
    private const int StartsPerTitle = 8;
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapters;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _config;
    private readonly ILogger<ArcsSceneIndexTask> _logger;

    public ArcsSceneIndexTask(ILibraryManager libraryManager, IChapterManager chapters, IMediaEncoder mediaEncoder, IConfigurationManager config,
                              ILogger<ArcsSceneIndexTask> logger)
    {
        _libraryManager = libraryManager;
        _chapters = chapters;
        _mediaEncoder = mediaEncoder;
        _config = config;
        _logger = logger;
    }

    public string Name => "Arcs scene index";

    public string Key => "ArcfinSyncArcsSceneIndex";

    public string Description => "Finds scene changes in films and episodes without chapters, for Arcfin's Arcs clips.";

    public string Category => "Arcfin Sync";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks } };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var settings = plugin?.Configuration.Arcs;
        if (plugin is null || settings is null || !settings.Enabled || !settings.UseSceneDetection)
        {
            progress.Report(100);
            return;
        }

        var dataFolder = plugin.DataFolderPath;
        var existing = SceneIndex.Load(dataFolder);
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
            AncestorIds = ArcsService.AncestorIds(_libraryManager, settings.Libraries),
        };
        var items = _libraryManager.GetItemList(query).OfType<Video>().ToList();

        // Sorted first, so the progress counts only the titles that are scanned.
        var kept = new ConcurrentDictionary<Guid, SceneEntry>();
        var toScan = new List<(Video Item, DateTime Modified)>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.RunTimeTicks is not > 0 || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path)
                || ClipPicker.ContentChapters(_chapters.GetChapters(item.Id).Select(c => new ChapterMark(c.StartPositionTicks, c.Name)).ToList(),
                                              item.RunTimeTicks.Value, 15).Count >= 3)
            {
                continue;
            }

            var modified = File.GetLastWriteTimeUtc(item.Path);
            if (existing.TryGetValue(item.Id, out var entry) && entry.Modified == modified)
            {
                kept[item.Id] = entry;
            }
            else
            {
                toScan.Add((item, modified));
            }
        }

        // Whether Jellyfin uses NVIDIA, not whether the clip warm-up has finished: a run started
        // just after the server does would otherwise scan everything on the CPU. A file the GPU
        // can't scan falls back to the CPU on its own.
        _ = ArcsService.WarmGpu(_config, _mediaEncoder, _logger); // sets the CUDA cache folder, and warms clips up
        var gpu = ArcsService.GpuFrom(_config.GetEncodingOptions(), _mediaEncoder.SupportsEncoder("h264_nvenc")) is not null;
        var parallel = ArcsSettingsRules.ClampSceneScans(settings.SceneScans);
        _logger.LogInformation("Arcfin Sync Arcs: scanning {Count} titles for scenes, {Parallel} at a time, on the {Where}", toScan.Count, parallel, gpu ? "GPU" : "CPU");
        // Titles already scanned count as done, so a run that picks up where one stopped shows it.
        var alreadyDone = kept.Count;
        var total = alreadyDone + toScan.Count;
        var done = 0;
        var saveGate = new object();
        progress.Report(100.0 * alreadyDone / Math.Max(1, total));
        await Parallel.ForEachAsync(toScan, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (next, token) =>
        {
            var scenes = await SceneIndex.ScanAsync(_mediaEncoder.EncoderPath, next.Item.Path, gpu, _logger, token).ConfigureAwait(false);
            if (scenes is not null)
            {
                var starts = ClipPicker.SpreadSceneStarts(scenes, next.Item.RunTimeTicks!.Value / (double)ClipPicker.TicksPerSecond, StartsPerTitle);
                kept[next.Item.Id] = new SceneEntry { ItemId = next.Item.Id, Modified = next.Modified, Starts = starts };
            }

            var count = Interlocked.Increment(ref done);
            progress.Report(100.0 * (alreadyDone + count) / Math.Max(1, total));
            if (count % 25 == 0)
            {
                lock (saveGate)
                {
                    SceneIndex.Save(dataFolder, existing.Values.Where(e => !kept.ContainsKey(e.ItemId)).Concat(kept.Values).ToList());
                }
            }
        }).ConfigureAwait(false);

        SceneIndex.Save(dataFolder, kept.Values.ToList());
        progress.Report(100);
    }
}
