using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api;

/// <summary>
/// One audio track of a file, as AAC in an .m4a, for a converted download that keeps more than
/// one track. Jellyfin's conversion keeps a single track and its audio-only streams can't be
/// told which one, so Arcfin fetches each extra track here and adds it to the file itself.
/// </summary>
public static class AudioTrackExtractor
{
    // Each run reads the whole file; two at a time keeps the disk free for playback.
    private static readonly SemaphoreSlim Gate = new(2, 2);

    /// <summary>Writes the track to a new temp file and returns its path, or null if ffmpeg failed.</summary>
    public static async Task<string?> ExtractAsync(string ffmpeg, string tempDirectory, string sourcePath, MediaStream stream,
                                                  ILogger logger, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(tempDirectory);
        var output = Path.Combine(tempDirectory, $"arcfin-audio-{Guid.NewGuid():N}.m4a");
        var input = stream.IsExternal && !string.IsNullOrEmpty(stream.Path) ? stream.Path : sourcePath;
        var map = stream.IsExternal ? "0:a:0" : "0:" + stream.Index.ToString(CultureInfo.InvariantCulture);

        var args = new List<string> { "-hide_banner", "-nostdin", "-i", input, "-map", map, "-vn", "-sn", "-dn",
                                      "-c:a", "aac", "-b:a", "320k" };
        // The AAC encoder's channel layouts stop at 5.1.
        if (stream.Channels is > 6)
        {
            args.AddRange(new[] { "-ac", "6" });
        }

        if (!string.IsNullOrEmpty(stream.Language))
        {
            args.AddRange(new[] { "-metadata:s:a:0", "language=" + stream.Language });
        }

        args.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", "-y", output });

        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                File.Delete(output);
                throw;
            }

            if (process.ExitCode != 0)
            {
                logger.LogWarning("Arcfin Sync audio track {Index} of {Path} failed: {Errors}", stream.Index, sourcePath, await errors.ConfigureAwait(false));
                File.Delete(output);
                return null;
            }

            return output;
        }
        finally
        {
            Gate.Release();
        }
    }
}
