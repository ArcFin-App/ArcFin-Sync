using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Limits;

/// <summary>
/// A user's watch-time rule: minutes a day, and a bedtime when nothing plays. Times are the
/// server's local time, so every device follows the same clock.
/// </summary>
public sealed class WatchLimitRule
{
    public Guid UserId { get; set; }

    /// <summary>Minutes a day; 0 for no daily limit.</summary>
    public int DailyMinutes { get; set; }

    /// <summary>Minutes on Saturdays and Sundays, or null for the same as other days.</summary>
    public int? WeekendMinutes { get; set; }

    /// <summary>"HH:mm", or null for no bedtime.</summary>
    public string? BedtimeStart { get; set; }

    /// <summary>"HH:mm"; before <see cref="BedtimeStart"/> means bedtime runs past midnight.</summary>
    public string? BedtimeEnd { get; set; }

    /// <summary>This person's own time zone (IANA or Windows id), or null for the one set for everyone.</summary>
    public string? TimeZone { get; set; }
}

public sealed record LimitState(bool Allowed, string? Reason, int? MinutesLeft, DateTime? Until);

public static class WatchLimits
{
    public const string Bedtime = "bedtime";
    public const string DailyLimit = "dailyLimit";

    /// <summary>Whether the user may watch at <paramref name="now"/> (server local time).</summary>
    public static LimitState Evaluate(WatchLimitRule? rule, double usedMinutes, DateTime now)
    {
        if (rule is null)
        {
            return new LimitState(true, null, null, null);
        }

        if (BedtimeWindow(rule, now) is { } until)
        {
            return new LimitState(false, Bedtime, 0, until);
        }

        var allowance = Allowance(rule, now);
        if (allowance <= 0)
        {
            return new LimitState(true, null, MinutesUntilBedtime(rule, now), null);
        }

        var left = (int)Math.Floor(allowance - usedMinutes);
        if (left <= 0)
        {
            return new LimitState(false, DailyLimit, 0, now.Date.AddDays(1));
        }

        var toBed = MinutesUntilBedtime(rule, now);
        return new LimitState(true, null, toBed is { } bed ? Math.Min(left, bed) : left, null);
    }

    public static int Allowance(WatchLimitRule rule, DateTime now)
    {
        var weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return weekend && rule.WeekendMinutes is { } w ? w : rule.DailyMinutes;
    }

    /// <summary>When bedtime ends, if <paramref name="now"/> is inside it.</summary>
    public static DateTime? BedtimeWindow(WatchLimitRule rule, DateTime now)
    {
        if (Parse(rule.BedtimeStart) is not { } start || Parse(rule.BedtimeEnd) is not { } end || start == end)
        {
            return null;
        }

        var time = now.TimeOfDay;
        if (start < end)
        {
            return time >= start && time < end ? now.Date + end : null;
        }

        if (time >= start)
        {
            return now.Date.AddDays(1) + end;
        }

        return time < end ? now.Date + end : null;
    }

    public static int? MinutesUntilBedtime(WatchLimitRule rule, DateTime now)
    {
        if (Parse(rule.BedtimeStart) is not { } start || Parse(rule.BedtimeEnd) is null)
        {
            return null;
        }

        var next = now.Date + start;
        if (next <= now)
        {
            next = next.AddDays(1);
        }

        return (int)Math.Ceiling((next - now).TotalMinutes);
    }

    public static TimeSpan? Parse(string? text)
    {
        return TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var value) && value < TimeSpan.FromDays(1) ? value : null;
    }

    /// <summary>
    /// The warning to give now, if any: the first of 10 and 5 minutes left that's been reached
    /// and not yet given (<paramref name="given"/> holds those already said today).
    /// </summary>
    public static int? WarningDue(LimitState state, ISet<int> given)
    {
        if (!state.Allowed || state.MinutesLeft is not { } left)
        {
            return null;
        }

        foreach (var at in new[] { 5, 10 })
        {
            if (left <= at && !given.Contains(at))
            {
                return at;
            }
        }

        return null;
    }

    public static bool IsValid(WatchLimitRule rule)
    {
        return rule.DailyMinutes is >= 0 and <= 1440
               && rule.WeekendMinutes is null or (>= 0 and <= 1440)
               && (rule.BedtimeStart is null) == (rule.BedtimeEnd is null)
               && (rule.BedtimeStart is null || (Parse(rule.BedtimeStart) is not null && Parse(rule.BedtimeEnd) is not null));
    }
}

/// <summary>Minutes watched per user per day, kept for a week.</summary>
public sealed class UsageStore
{
    private static readonly object Gate = new();
    private readonly string _path;

    public UsageStore(string folder)
    {
        _path = Path.Combine(folder, "watch-usage.json");
    }

    public static UsageStore Current => new(Plugin.Instance!.DataFolderPath);

    public double Used(Guid userId, DateTime day)
    {
        lock (Gate)
        {
            var file = JsonFile.Read<Dictionary<string, double>>(_path);
            return file is not null && file.TryGetValue(Key(userId, day), out var minutes) ? minutes : 0;
        }
    }

    public double Add(Guid userId, DateTime day, double minutes)
    {
        lock (Gate)
        {
            var file = JsonFile.Read<Dictionary<string, double>>(_path) ?? new Dictionary<string, double>();
            var key = Key(userId, day);
            file[key] = (file.TryGetValue(key, out var used) ? used : 0) + minutes;
            var oldest = day.Date.AddDays(-7).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            foreach (var stale in file.Keys.Where(k => string.CompareOrdinal(k[^8..], oldest) < 0).ToList())
            {
                file.Remove(stale);
            }

            JsonFile.Write(_path, file);
            return file[key];
        }
    }

    private static string Key(Guid userId, DateTime day) => $"{userId:N}:" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
