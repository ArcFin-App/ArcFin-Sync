using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api;

/// <summary>
/// Which video encoders the server can really use with its hardware acceleration setting.
/// Jellyfin only reports what ffmpeg was built with; an AV1 encoder can be built in and still
/// fail on the card (Pascal NVENC, for one). So each codec gets a one-frame test encode with
/// the server's own ffmpeg and device, and the result is kept until the setting changes.
/// </summary>
public static class EncoderProbe
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static EncodersDto? _cached;
    private static string? _cachedFor;

    public static async Task<EncodersDto> GetAsync(IMediaEncoder encoder, IConfigurationManager config, ILogger logger, bool refresh)
    {
        var options = config.GetEncodingOptions();
        var signature = $"{options.HardwareAccelerationType}|{options.VaapiDevice}|{options.QsvDevice}|{encoder.EncoderPath}";
        if (!refresh && _cached is not null && _cachedFor == signature)
        {
            return _cached;
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!refresh && _cached is not null && _cachedFor == signature)
            {
                return _cached;
            }

            var accel = options.HardwareAccelerationType;
            var result = new EncodersDto
            {
                HardwareAcceleration = accel.ToString(),
                ProbedAt = DateTime.UtcNow,
            };
            foreach (var codec in new[] { "h264", "hevc", "av1" })
            {
                var name = EncoderName(accel, codec);
                var works = name is not null && encoder.SupportsEncoder(name)
                    && await TryEncodeAsync(encoder.EncoderPath, accel, options.VaapiDevice, options.QsvDevice, name, logger).ConfigureAwait(false);
                result.Encoders[codec] = works;
                logger.LogInformation("Arcfin Sync encoder probe: {Codec} via {Encoder} on {Accel}: {Result}", codec, name ?? "none", accel, works ? "works" : "unavailable");
            }

            _cached = result;
            _cachedFor = signature;
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The ffmpeg encoder Jellyfin uses for a codec with an acceleration type.</summary>
    private static string? EncoderName(HardwareAccelerationType accel, string codec) => accel switch
    {
        HardwareAccelerationType.nvenc => $"{codec}_nvenc",
        HardwareAccelerationType.qsv => $"{codec}_qsv",
        HardwareAccelerationType.vaapi => $"{codec}_vaapi",
        HardwareAccelerationType.amf => $"{codec}_amf",
        HardwareAccelerationType.videotoolbox => codec == "av1" ? null : $"{codec}_videotoolbox",
        HardwareAccelerationType.rkmpp => codec == "av1" ? null : $"{codec}_rkmpp",
        HardwareAccelerationType.v4l2m2m => codec == "av1" ? null : $"{codec}_v4l2m2m",
        _ => codec switch { "h264" => "libx264", "hevc" => "libx265", _ => "libsvtav1" },
    };

    private static async Task<bool> TryEncodeAsync(string ffmpeg, HardwareAccelerationType accel, string? vaapiDevice, string? qsvDevice, string encoderName, ILogger logger)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        var filter = "format=nv12";
        switch (accel)
        {
            case HardwareAccelerationType.vaapi:
                args.AddRange(new[] { "-init_hw_device", $"vaapi=va:{(string.IsNullOrEmpty(vaapiDevice) ? "/dev/dri/renderD128" : vaapiDevice)}", "-filter_hw_device", "va" });
                filter = "format=nv12,hwupload";
                break;
            case HardwareAccelerationType.qsv:
                args.AddRange(new[] { "-init_hw_device", string.IsNullOrEmpty(qsvDevice) ? "qsv=hw" : $"qsv=hw:{qsvDevice}", "-filter_hw_device", "hw" });
                filter = "format=nv12,hwupload=extra_hw_frames=64";
                break;
        }

        // A tiny black frame, long enough for encoders with a minimum size.
        args.AddRange(new[] { "-f", "lavfi", "-i", "color=c=black:s=640x360:r=25", "-frames:v", "5", "-vf", filter, "-c:v", encoderName, "-f", "null", "-" });

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

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                logger.LogDebug("Arcfin Sync encoder probe: {Encoder} failed: {Errors}", encoderName, await errors.ConfigureAwait(false));
            }

            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug("Arcfin Sync encoder probe: {Encoder} didn't run: {Message}", encoderName, error.Message);
            return false;
        }
    }
}

public class EncodersDto
{
    /// <summary>The server's hardware acceleration setting, e.g. nvenc or none.</summary>
    public string HardwareAcceleration { get; set; } = string.Empty;

    /// <summary>h264, hevc and av1: whether a test encode worked.</summary>
    public Dictionary<string, bool> Encoders { get; set; } = new();

    public DateTime ProbedAt { get; set; }
}
