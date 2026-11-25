using System;
using System.IO;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Notifications;

/// <summary>
/// The last call Seerr's webhook made, so the dashboard can say whether Seerr's Test button got
/// through. Calls with the wrong secret are recorded at most every ten seconds.
/// </summary>
public sealed class WebhookLog
{
    private static readonly object Gate = new();
    private readonly string _path;

    public WebhookLog(string folder)
    {
        _path = Path.Combine(folder, "seerr-webhook.json");
    }

    public static WebhookLog Current => new(Plugin.Instance!.DataFolderPath);

    public WebhookCall? Last
    {
        get
        {
            lock (Gate)
            {
                return JsonFile.Read<WebhookCall>(_path);
            }
        }
    }

    /// <param name="result">test, ok, unreadable (no notification_type), missingSecret or wrongSecret.</param>
    public void Record(string result, string? type, DateTime now)
    {
        lock (Gate)
        {
            var last = JsonFile.Read<WebhookCall>(_path);
            if (result is "missingSecret" or "wrongSecret" && last?.Result == result && now - last.Time < TimeSpan.FromSeconds(10))
            {
                return;
            }

            JsonFile.Write(_path, new WebhookCall { Time = now, Result = result, Type = type?.Length > 40 ? type[..40] : type });
        }
    }
}

public sealed class WebhookCall
{
    public DateTime Time { get; set; }

    public string Result { get; set; } = string.Empty;

    public string? Type { get; set; }
}
