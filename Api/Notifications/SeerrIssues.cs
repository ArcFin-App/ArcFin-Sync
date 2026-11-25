using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Notifications;

public sealed record SeerrPerson(Guid? JellyfinUserId, string? Name);

public sealed record SeerrComment(int Id, string Message, SeerrPerson By, DateTime? Created);

/// <summary>A Seerr issue. <see cref="Comments"/> is null when Seerr's list left them out.</summary>
public sealed record SeerrIssue(int Id, int IssueType, int Status, int? ProblemSeason, int? ProblemEpisode, SeerrPerson ReportedBy,
                                SeerrPerson ModifiedBy, int? TmdbId, string? MediaType, DateTime? Updated, IReadOnlyList<SeerrComment>? Comments);

/// <summary>What changed on an issue, for its event: the comment for a new comment, otherwise null.</summary>
public sealed record IssueChange(SeerrIssue Issue, string Kind, SeerrComment? Comment, int Count);

public static class SeerrIssues
{
    public const int Open = 1;
    public const int Resolved = 2;

    public static List<SeerrIssue> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return new List<SeerrIssue>();
        }

        return results.EnumerateArray().Select(ParseOne).Where(i => i is not null).Select(i => i!).ToList();
    }

    public static SeerrIssue? ParseOne(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Int(e, "id") is not { } id)
        {
            return null;
        }

        var media = e.TryGetProperty("media", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        List<SeerrComment>? comments = null;
        if (e.TryGetProperty("comments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            comments = new List<SeerrComment>();
            foreach (var c in list.EnumerateArray())
            {
                if (Int(c, "id") is { } commentId)
                {
                    comments.Add(new SeerrComment(commentId, Text(c, "message") ?? string.Empty, Person(c, "user"), Date(c, "createdAt")));
                }
            }
        }

        return new SeerrIssue(id, Int(e, "issueType") ?? 4, Int(e, "status") ?? Open, Positive(Int(e, "problemSeason")), Positive(Int(e, "problemEpisode")),
                              Person(e, "createdBy"), Person(e, "modifiedBy"), Int(media, "tmdbId"), Text(media, "mediaType"), Date(e, "updatedAt"), comments);
    }

    private static int? Positive(int? value) => value > 0 ? value : null;

    private static SeerrPerson Person(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object)
        {
            return new SeerrPerson(null, null);
        }

        Guid? id = Text(p, "jellyfinUserId") is { } raw && Guid.TryParse(raw, out var parsed) ? parsed : null;
        return new SeerrPerson(id, Text(p, "displayName"));
    }

    private static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;

    private static string? Text(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement e, string name)
        => Text(e, name) is { } raw && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>
/// Each issue's last seen status, update time and newest comment. The first check only records
/// them; an issue seen for the first time later is a new report.
/// </summary>
public sealed class IssueStateFile
{
    private const int Limit = 2000;
    private static readonly object Gate = new();
    private readonly string _path;

    public IssueStateFile(string folder)
    {
        _path = Path.Combine(folder, "seerr-issues.json");
    }

    /// <summary>Issues whose comments need reading: changed since last time, or new, or named by a webhook.</summary>
    public HashSet<int> NeedingComments(IEnumerable<SeerrIssue> issues, IEnumerable<int> named)
    {
        lock (Gate)
        {
            var saved = Read();
            var ids = new HashSet<int>(named);
            if (saved is null)
            {
                return ids;
            }

            foreach (var issue in issues)
            {
                if (!saved.TryGetValue(Key(issue.Id), out var before) || before.Updated != issue.Updated)
                {
                    ids.Add(issue.Id);
                }
            }

            return ids;
        }
    }

    public List<IssueChange> Update(IReadOnlyList<SeerrIssue> issues)
    {
        lock (Gate)
        {
            var saved = Read();
            var states = saved ?? new Dictionary<string, IssueState>();
            var changes = new List<IssueChange>();
            foreach (var issue in issues)
            {
                var key = Key(issue.Id);
                states.TryGetValue(key, out var before);
                var newest = issue.Comments?.Count > 0 ? issue.Comments.Max(c => c.Id) : (int?)null;
                var state = new IssueState
                {
                    Status = issue.Status,
                    Updated = issue.Updated,
                    LastComment = newest ?? before?.LastComment,
                    Changes = before?.Changes ?? 0
                };

                if (saved is not null && before is null)
                {
                    changes.Add(new IssueChange(issue, "reported", issue.Comments?.OrderBy(c => c.Id).FirstOrDefault(), 0));
                }
                else if (before is not null)
                {
                    if (before.Status != issue.Status && issue.Status is SeerrIssues.Open or SeerrIssues.Resolved)
                    {
                        state.Changes++;
                        changes.Add(new IssueChange(issue, issue.Status == SeerrIssues.Resolved ? "resolved" : "reopened", null, state.Changes));
                    }

                    foreach (var comment in NewComments(issue, before))
                    {
                        changes.Add(new IssueChange(issue, "comment", comment, 0));
                    }
                }

                states[key] = state;
            }

            if (states.Count > Limit)
            {
                states = states.OrderByDescending(p => int.TryParse(p.Key, out var n) ? n : 0).Take(Limit).ToDictionary(p => p.Key, p => p.Value);
            }

            JsonFile.Write(_path, states);
            return changes;
        }
    }

    /// <summary>
    /// Comments after the newest one seen. When none was seen yet (the list Seerr gave before
    /// had no comments), the ones written after the issue's last seen update.
    /// </summary>
    private static IEnumerable<SeerrComment> NewComments(SeerrIssue issue, IssueState before)
    {
        if (issue.Comments is null)
        {
            return Enumerable.Empty<SeerrComment>();
        }

        return issue.Comments.Where(c => before.LastComment is { } last ? c.Id > last : before.Updated is { } since && c.Created > since).OrderBy(c => c.Id);
    }

    private static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);

    private Dictionary<string, IssueState>? Read() => JsonFile.Read<Dictionary<string, IssueState>>(_path);

    public sealed class IssueState
    {
        public int Status { get; set; }

        public DateTime? Updated { get; set; }

        public int? LastComment { get; set; }

        /// <summary>How many times it's been resolved or reopened, so each time gets its own event.</summary>
        public int Changes { get; set; }
    }
}

/// <summary>Who Seerr would notify, by their Seerr permissions.</summary>
public static class SeerrRecipients
{
    public const int Admin = 2;
    public const int ManageRequests = 16;
    public const int ManageIssues = 1048576;

    /// <summary>Jellyfin users whose Seerr permissions include <paramref name="permission"/> (Admin counts for all), less <paramref name="except"/>.</summary>
    public static List<Guid> With(IReadOnlyDictionary<Guid, int> permissions, int permission, params Guid?[] except)
    {
        return permissions.Where(p => (p.Value & (Admin | permission)) != 0 && !except.Contains(p.Key)).Select(p => p.Key).OrderBy(id => id).ToList();
    }

    /// <summary>Everyone to tell about a request change: the requester, or the people who manage requests.</summary>
    public static List<Guid> ForRequest(string kind, SeerrRequest request, IReadOnlyDictionary<Guid, int> permissions) => kind switch
    {
        "approved" or "declined" or "available" => request.JellyfinUserId is { } requester ? new List<Guid> { requester } : new List<Guid>(),
        "pending" or "auto-approved" => With(permissions, ManageRequests, request.JellyfinUserId),
        "failed" => With(permissions, ManageRequests),
        _ => new List<Guid>()
    };

    /// <summary>Everyone to tell about an issue change: whoever reported it and the people who manage issues, less whoever did it.</summary>
    public static List<Guid> ForIssue(IssueChange change, IReadOnlyDictionary<Guid, int> permissions)
    {
        var actor = change.Kind switch
        {
            "reported" => change.Issue.ReportedBy.JellyfinUserId,
            "comment" => change.Comment?.By.JellyfinUserId,
            _ => change.Issue.ModifiedBy.JellyfinUserId
        };
        var people = With(permissions, ManageIssues, actor);
        if (change.Kind != "reported" && change.Issue.ReportedBy.JellyfinUserId is { } reporter && reporter != actor && !people.Contains(reporter))
        {
            people.Insert(0, reporter);
        }

        return people;
    }
}
