using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>A text subtitle stream of a file. <paramref name="Relative"/> is its place among the file's own subtitle streams, for ffmpeg's 0:s:N.</summary>
public sealed record SubtitleChoice(int Index, int? Relative, string Lang, bool External, string? Path, bool Forced, bool Default);

/// <summary>Which subtitle streams of a title go in the dialogue index.</summary>
public static class DialogueSelection
{
    public const int MaxTracks = 3;

    /// <summary>
    /// One stream per wanted language (full subtitles over forced ones, the default first), in the
    /// order given, at most three. With none of them there, the title's best stream.
    /// </summary>
    public static List<SubtitleChoice> Pick(IReadOnlyList<SubtitleChoice> streams, IReadOnlyList<string> wanted)
    {
        IEnumerable<SubtitleChoice> Best(IEnumerable<SubtitleChoice> from) => from.OrderBy(s => s.Forced).ThenByDescending(s => s.Default).ThenBy(s => s.Index);
        var picked = new List<SubtitleChoice>();
        foreach (var language in wanted)
        {
            if (picked.Count == MaxTracks)
            {
                break;
            }

            if (Best(streams.Where(s => LanguageCodes.Same(s.Lang, language))).FirstOrDefault() is { } stream && !picked.Contains(stream))
            {
                picked.Add(stream);
            }
        }

        if (picked.Count == 0 && Best(streams).FirstOrDefault() is { } any)
        {
            picked.Add(any);
        }

        return picked;
    }
}

/// <summary>Takes text subtitles out of files with Jellyfin's ffmpeg, as SRT.</summary>
public static class DialogueExtractor
{
    /// <summary>
    /// The SRT of each of <paramref name="relatives"/> (0:s:N) in one file, read in a single ffmpeg
    /// run. A stream that couldn't be read comes back null.
    /// </summary>
    public static async Task<List<string?>> ExtractAsync(string ffmpeg, string input, IReadOnlyList<int> relatives, ILogger logger, CancellationToken cancellationToken)
    {
        var result = relatives.Select(_ => (string?)null).ToList();
        if (relatives.Count == 0)
        {
            return result;
        }

        var folder = Path.Combine(Path.GetTempPath(), "arcfin-dialogue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", input })
            {
                start.ArgumentList.Add(arg);
            }

            for (var i = 0; i < relatives.Count; i++)
            {
                foreach (var arg in new[] { "-map", $"0:s:{relatives[i].ToString(CultureInfo.InvariantCulture)}", "-c:s", "subrip", "-f", "srt", Path.Combine(folder, $"{i}.srt") })
                {
                    start.ArgumentList.Add(arg);
                }
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return result;
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

                logger.LogInformation("Arcfin Sync Arcs: reading subtitles from {Path} took too long", input);
                return result;
            }

            var output = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                // What it wrote before failing may be cut short.
                logger.LogDebug("Arcfin Sync Arcs: reading subtitles from {Path} failed: {Errors}", input, output.Length > 2000 ? output[^2000..] : output);
                return result;
            }

            for (var i = 0; i < relatives.Count; i++)
            {
                var file = Path.Combine(folder, $"{i}.srt");
                if (File.Exists(file) && new FileInfo(file).Length > 0)
                {
                    result[i] = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogDebug("Arcfin Sync Arcs: reading subtitles from {Path} didn't run: {Message}", input, error.Message);
            return result;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// "Arcs dialogue index": reads the text subtitles of films and episodes (files beside them and
/// streams inside them) for dialogue search. Only titles that are new or changed since the last run
/// are read again.
/// </summary>
public sealed class ArcsDialogueIndexTask : IScheduledTask
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(7);
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _config;
    private readonly ILogger<ArcsDialogueIndexTask> _logger;

    public ArcsDialogueIndexTask(ILibraryManager libraryManager, IUserManager userManager, IMediaEncoder mediaEncoder, IConfigurationManager config,
                                 ILogger<ArcsDialogueIndexTask> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaEncoder = mediaEncoder;
        _config = config;
        _logger = logger;
    }

    public string Name => "Arcs dialogue index";

    public string Key => "ArcfinSyncArcsDialogueIndex";

    public string Description => "Reads the text subtitles of films and episodes so Arcfin's Arcs can find scenes by a line of dialogue.";

    public string Category => "Arcfin Sync";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks } };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var settings = plugin?.Configuration.Arcs;
        if (plugin is null || settings is null || !settings.Enabled || !settings.DialogueSearch)
        {
            progress.Report(100);
            return;
        }

        var dataFolder = plugin.DataFolderPath;
        var wanted = WantedLanguages();
        var existing = DialogueIndex.Load(dataFolder);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
            AncestorIds = ArcsService.AncestorIds(_libraryManager, settings.Libraries),
        }).OfType<Video>().ToList();
        var kept = new Dictionary<Guid, DialogueEntry>();
        var done = 0;
        var changed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * done++ / Math.Max(1, items.Count));
            var source = item.GetMediaSources(false).FirstOrDefault();
            if (source?.Path is not { Length: > 0 } path || !File.Exists(path))
            {
                // A drive that's away for now: keep what was read before while Jellyfin still has the title.
                if (existing.TryGetValue(item.Id, out var away))
                {
                    kept[item.Id] = away;
                }

                continue;
            }

            var embedded = source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal).OrderBy(s => s.Index).ToList();
            var streams = source.MediaStreams
                .Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream && (!s.IsExternal || (!string.IsNullOrEmpty(s.Path) && File.Exists(s.Path))))
                .Select(s => new SubtitleChoice(s.Index, s.IsExternal ? null : embedded.IndexOf(s), LanguageCodes.Canonical(s.Language), s.IsExternal, s.Path, s.IsForced, s.IsDefault))
                .ToList();
            var picked = DialogueSelection.Pick(streams, wanted);
            var signature = Signature(path, picked);
            if (existing.TryGetValue(item.Id, out var entry) && entry.Signature == signature && (entry.FailedAt is not { } failed || DateTime.UtcNow - failed < RetryAfter))
            {
                kept[item.Id] = entry;
                continue;
            }

            var tracks = new List<(string Lang, List<SubtitleCue> Cues)>();
            var inside = picked.Where(p => !p.External).ToList();
            var srt = await DialogueExtractor.ExtractAsync(_mediaEncoder.EncoderPath, path, inside.Select(p => p.Relative!.Value).ToList(), _logger, cancellationToken).ConfigureAwait(false);
            var texts = inside.Zip(srt).ToDictionary(x => x.First, x => x.Second);
            foreach (var external in picked.Where(p => p.External))
            {
                texts[external] = (await DialogueExtractor.ExtractAsync(_mediaEncoder.EncoderPath, external.Path!, new[] { 0 }, _logger, cancellationToken).ConfigureAwait(false))[0];
            }

            if (picked.Count > 0 && texts.Values.All(t => t is null))
            {
                // ffmpeg couldn't read any of them: keep what was read before, and try again in a week rather than every night.
                kept[item.Id] = entry is not null && entry.Tracks.Count > 0
                    ? new DialogueEntry { ItemId = item.Id, Signature = signature, Tracks = entry.Tracks, FailedAt = DateTime.UtcNow }
                    : new DialogueEntry { ItemId = item.Id, Signature = signature, FailedAt = DateTime.UtcNow };
                changed++;
                continue;
            }

            foreach (var choice in picked)
            {
                if (texts.GetValueOrDefault(choice) is { } text)
                {
                    tracks.Add((choice.Lang, DialogueText.ParseSrt(text)));
                }
            }

            kept[item.Id] = DialogueIndex.SaveItem(dataFolder, item.Id, signature, tracks);
            if (++changed % 25 == 0)
            {
                DialogueIndex.SaveList(dataFolder, existing.Values.Where(e => !kept.ContainsKey(e.ItemId)).Concat(kept.Values));
            }
        }

        foreach (var gone in existing.Keys.Where(id => !kept.ContainsKey(id)))
        {
            DialogueIndex.RemoveItem(dataFolder, gone);
        }

        DialogueIndex.SaveList(dataFolder, kept.Values);
        progress.Report(100);
    }

    /// <summary>The languages people here read subtitles in, the most used first, then the server's metadata language.</summary>
    private List<string> WantedLanguages()
    {
        var languages = _userManager.GetUsers().Select(u => u.SubtitleLanguagePreference).Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(LanguageCodes.Canonical).GroupBy(l => l).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        if (_config.CommonConfiguration is ServerConfiguration server && LanguageCodes.Canonical(server.PreferredMetadataLanguage) is var metadata && metadata != "und"
            && !languages.Contains(metadata))
        {
            languages.Add(metadata);
        }

        return languages.Where(l => l != "und").ToList();
    }

    private static string Signature(string path, IReadOnlyList<SubtitleChoice> picked)
    {
        var text = new StringBuilder();
        text.Append(File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture));
        foreach (var choice in picked)
        {
            text.Append('|').Append(choice.Index.ToString(CultureInfo.InvariantCulture)).Append(':').Append(choice.Lang);
            if (choice.External && choice.Path is { } sub)
            {
                text.Append(':').Append(File.GetLastWriteTimeUtc(sub).Ticks.ToString(CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }
}
