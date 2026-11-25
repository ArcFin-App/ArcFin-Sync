using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.Notifications;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ArcfinSync.Tests;

public class EventStoreTests
{
    [Fact]
    public void EventsAreNumberedAndOnlyGoToTheirPerson()
    {
        using var dir = new TempFolder();
        var store = new EventStore(dir.Path);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var now = DateTime.UtcNow;
        Assert.Equal(0, store.Cursor);

        store.Add(new[] { new ServerEvent { Id = "a", UserId = alice }, new ServerEvent { Id = "everyone" } }, now);
        store.Add(new[] { new ServerEvent { Id = "b", UserId = bob } }, now);

        Assert.Equal(3, store.Cursor);
        Assert.Equal(new[] { "a", "everyone" }, store.After(0, alice).Select(e => e.Id));
        Assert.Equal(new[] { "everyone", "b" }, store.After(1, bob).Select(e => e.Id));
        Assert.Equal(new[] { "b" }, store.After(2, bob).Select(e => e.Id));
        Assert.Empty(store.After(3, alice));
    }

    [Fact]
    public void OldEventsAreDroppedButNumbersKeepGoing()
    {
        using var dir = new TempFolder();
        var store = new EventStore(dir.Path);
        var start = DateTime.UtcNow;
        store.Add(new[] { new ServerEvent { Id = "old" } }, start);
        store.Add(new[] { new ServerEvent { Id = "new" } }, start + EventStore.KeepFor + TimeSpan.FromMinutes(1));

        Assert.Equal(2, store.Cursor);
        Assert.Equal(new[] { "new" }, store.After(0, Guid.NewGuid()).Select(e => e.Id));
    }
}

public class SeerrWatchTests
{
    private const string Json = """
        {"pageInfo":{"results":2},"results":[
          {"id":7,"status":2,"media":{"tmdbId":550,"mediaType":"movie","status":3},"requestedBy":{"id":3,"jellyfinUserId":"0f8fad5bd9cb469fa16570867728950e"}},
          {"id":8,"status":1,"media":{"tmdbId":1399,"mediaType":"tv","status":2},"requestedBy":{"id":4}}
        ]}
        """;

    [Fact]
    public void ParsesSeerrsRequestList()
    {
        var requests = SeerrRequests.Parse(Json);
        Assert.Equal(2, requests.Count);
        Assert.Equal(new SeerrRequest(7, 2, 3, 550, "movie", Guid.Parse("0f8fad5bd9cb469fa16570867728950e")), requests[0]);
        Assert.Null(requests[1].JellyfinUserId);
    }

    [Theory]
    [InlineData("1-2", 2, 3, "approved")]
    [InlineData("1-2", 3, 2, "declined")]
    [InlineData("2-3", 2, 5, "available")]
    [InlineData("1-3", 2, 5, "available")]
    [InlineData("2-3", 2, 3, null)]
    [InlineData("2-5", 2, 5, null)]
    public void DecidesLikeTheApp(string before, int status, int media, string? expected)
    {
        Assert.Equal(expected, SeerrRequests.Change(before, new SeerrRequest(1, status, media, null, null, null)));
    }

    [Fact]
    public void TheFirstCheckOnlyRecords()
    {
        using var dir = new TempFolder();
        var file = new SeerrStateFile(dir.Path);
        var user = Guid.NewGuid();
        Assert.Empty(file.Update(new[] { new SeerrRequest(1, 1, 2, 10, "movie", user) }));

        var changes = file.Update(new[] { new SeerrRequest(1, 2, 3, 10, "movie", user), new SeerrRequest(2, 1, 2, 11, "movie", user), new SeerrRequest(3, 2, 3, 12, "tv", user) });
        Assert.Equal(new[] { (1, "approved"), (2, "pending"), (3, "auto-approved") }, changes.Select(c => (c.Request.Id, c.Kind)));
    }

    [Fact]
    public void AFailedRequestIsAChange()
    {
        Assert.Equal("failed", SeerrRequests.Change("2-3", new SeerrRequest(1, 4, 3, null, null, null)));
    }

    [Fact]
    public void AWebhookAnnouncesARequestSeenForTheFirstTimeOnlyWhenSeerrAgrees()
    {
        using var dir = new TempFolder();
        var file = new SeerrStateFile(dir.Path);
        var user = Guid.NewGuid();
        var named = new Dictionary<int, string> { [1] = "approved", [2] = "available" };
        var changes = file.Update(new[] { new SeerrRequest(1, 2, 3, 10, "movie", user), new SeerrRequest(2, 2, 3, 11, "movie", user) }, named);
        Assert.Equal(new[] { (1, "approved") }, changes.Select(c => (c.Request.Id, c.Kind)));
    }

    [Theory]
    [InlineData("MEDIA_APPROVED", "approved")]
    [InlineData("MEDIA_DECLINED", "declined")]
    [InlineData("MEDIA_AVAILABLE", "available")]
    [InlineData("MEDIA_PENDING", "pending")]
    [InlineData("MEDIA_AUTO_APPROVED", "auto-approved")]
    [InlineData("MEDIA_FAILED", "failed")]
    [InlineData("ISSUE_CREATED", null)]
    [InlineData("TEST_NOTIFICATION", null)]
    [InlineData(null, null)]
    public void WebhookTypesMapToChanges(string? type, string? kind)
    {
        Assert.Equal(kind, SeerrRequests.KindOf(type));
    }

    [Fact]
    public void RequestsWithoutAJellyfinAccountTellNoRequester()
    {
        var request = new SeerrRequest(1, 2, 3, 10, "movie", null);
        Assert.Empty(SeerrRecipients.ForRequest("approved", request, new Dictionary<Guid, int>()));
    }
}

public class NotificationFeedTests
{
    [Fact]
    public void WebhookSecretMustMatchExactly()
    {
        var secret = NotificationsController.NewSecret();
        Assert.Equal(64, secret.Length);
        Assert.True(NotificationsController.Matches(secret, secret));
        Assert.True(NotificationsController.Matches($" {secret} ", secret));
        Assert.False(NotificationsController.Matches(secret[..^1], secret));
        Assert.False(NotificationsController.Matches(string.Empty, secret));
    }

    [Theory]
    [InlineData("{\"notification_type\":\"MEDIA_APPROVED\",\"request_id\":\"42\"}", "MEDIA_APPROVED", 42)]
    [InlineData("{\"notification_type\":\"MEDIA_AVAILABLE\",\"request_id\":7}", "MEDIA_AVAILABLE", 7)]
    [InlineData("{\"notification_type\":\"TEST_NOTIFICATION\",\"request_id\":\"\"}", "TEST_NOTIFICATION", null)]
    [InlineData("not json", null, null)]
    [InlineData("[]", null, null)]
    public void ReadsSeerrsPayload(string body, string? type, int? requestId)
    {
        Assert.Equal((type, requestId, (int?)null), NotificationsController.ReadPayload(body));
    }

    [Fact]
    public void TheSuggestedPayloadIsValidJsonWithBothFields()
    {
        var filled = NotificationsController.SeerrPayload.Replace("{{notification_type}}", "MEDIA_DECLINED", StringComparison.Ordinal)
            .Replace("{{request_id}}", "5", StringComparison.Ordinal).Replace("{{issue_id}}", string.Empty, StringComparison.Ordinal);
        Assert.Equal(("MEDIA_DECLINED", 5, (int?)null), NotificationsController.ReadPayload(filled));

        var issue = NotificationsController.SeerrPayload.Replace("{{notification_type}}", "ISSUE_COMMENT", StringComparison.Ordinal)
            .Replace("{{request_id}}", string.Empty, StringComparison.Ordinal).Replace("{{issue_id}}", "12", StringComparison.Ordinal);
        Assert.Equal(("ISSUE_COMMENT", (int?)null, 12), NotificationsController.ReadPayload(issue));
    }

    [Fact]
    public void AnnouncementsShowWhileActiveAndMaintenanceADayAhead()
    {
        var now = DateTime.UtcNow;
        var me = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            Announcements = new List<Announcement>
            {
                new() { Title = "Now", Starts = now.AddHours(-1) },
                new() { Title = "Later", Starts = now.AddHours(1) },
                new() { Title = "Someone else", Users = new List<Guid> { Guid.NewGuid() } }
            },
            Maintenance = new List<MaintenanceWindow>
            {
                new() { Starts = now.AddHours(5), Ends = now.AddHours(6), Message = "Disk swap" },
                new() { Starts = now.AddDays(3), Ends = now.AddDays(3).AddHours(1) }
            }
        };

        var events = NotificationsController.CurrentNotices(config, me, now).ToList();
        Assert.Equal(new[] { "announcement", "maintenance" }, events.Select(e => e.Kind));
        Assert.Equal("Now", events[0].Title);
        Assert.Equal("Disk swap", events[1].Body);
        Assert.StartsWith("announcement.", events[0].Id, StringComparison.Ordinal);
    }
}

public class WebhookLogTests
{
    [Fact]
    public void KeepsTheLastCallAndThrottlesRejectedOnes()
    {
        using var dir = new TempFolder();
        var log = new WebhookLog(dir.Path);
        var now = DateTime.UtcNow;
        Assert.Null(log.Last);

        log.Record("wrongSecret", null, now);
        log.Record("wrongSecret", null, now.AddSeconds(3));
        Assert.Equal(now, log.Last!.Time);

        log.Record("test", "TEST_NOTIFICATION", now.AddSeconds(4));
        Assert.Equal(("test", "TEST_NOTIFICATION"), (log.Last!.Result, log.Last.Type));
    }
}

public class SeerrRecipientTests
{
    private static readonly Guid Admin = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid RequestManager = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid IssueManager = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid Viewer = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    private static readonly Dictionary<Guid, int> Permissions = new()
    {
        [Admin] = SeerrRecipients.Admin,
        [RequestManager] = SeerrRecipients.ManageRequests | 32,
        [IssueManager] = SeerrRecipients.ManageIssues,
        [Viewer] = 32
    };

    [Fact]
    public void RequestsGoToTheRequesterOrThePeopleWhoManageThem()
    {
        var mine = new SeerrRequest(1, 1, 2, 10, "movie", RequestManager);
        Assert.Equal(new[] { RequestManager }, SeerrRecipients.ForRequest("approved", mine, Permissions));
        Assert.Equal(new[] { Admin }, SeerrRecipients.ForRequest("pending", mine, Permissions));
        Assert.Equal(new[] { Admin, RequestManager }, SeerrRecipients.ForRequest("failed", mine, Permissions));
        Assert.Equal(new[] { Admin, RequestManager }, SeerrRecipients.ForRequest("auto-approved", mine with { JellyfinUserId = Viewer }, Permissions));
    }

    [Fact]
    public void IssuesGoToTheReporterAndTheirManagersButNotWhoeverActed()
    {
        var issue = new SeerrIssue(5, 1, SeerrIssues.Open, null, null, new SeerrPerson(Viewer, "Viewer"), new SeerrPerson(Admin, "Admin"), 10, "movie", null, null);
        Assert.Equal(new[] { Admin, IssueManager }, SeerrRecipients.ForIssue(new IssueChange(issue, "reported", null, 0), Permissions));
        Assert.Equal(new[] { Viewer, IssueManager }, SeerrRecipients.ForIssue(new IssueChange(issue, "resolved", null, 1), Permissions));
        var byReporter = new SeerrComment(9, "Still broken", new SeerrPerson(Viewer, "Viewer"), null);
        Assert.Equal(new[] { Admin, IssueManager }, SeerrRecipients.ForIssue(new IssueChange(issue, "comment", byReporter, 0), Permissions));
    }
}

public class SeerrIssueTests
{
    private const string Json = """
        {"pageInfo":{"results":1},"results":[
          {"id":3,"issueType":2,"status":1,"problemSeason":1,"problemEpisode":4,
           "createdBy":{"id":4,"displayName":"Sam","jellyfinUserId":"0f8fad5bd9cb469fa16570867728950e"},
           "modifiedBy":{"id":1,"displayName":"Admin"},
           "media":{"tmdbId":1399,"mediaType":"tv"},"updatedAt":"2026-11-25T08:00:00.000Z"}
        ]}
        """;

    private static readonly Guid Sam = Guid.Parse("0f8fad5bd9cb469fa16570867728950e");

    private static SeerrIssue Issue(int status, string updated, params SeerrComment[] comments)
        => new(3, 2, status, 1, 4, new SeerrPerson(Sam, "Sam"), new SeerrPerson(null, "Admin"), 1399, "tv", DateTime.Parse(updated, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime(), comments);

    [Fact]
    public void ParsesSeerrsIssueList()
    {
        var issue = Assert.Single(SeerrIssues.Parse(Json));
        Assert.Equal((3, 2, SeerrIssues.Open, 1, 4), (issue.Id, issue.IssueType, issue.Status, issue.ProblemSeason!.Value, issue.ProblemEpisode!.Value));
        Assert.Equal(Sam, issue.ReportedBy.JellyfinUserId);
        Assert.Equal("Admin", issue.ModifiedBy.Name);
        Assert.Null(issue.Comments);
        Assert.Equal((1399, "tv"), (issue.TmdbId!.Value, issue.MediaType));
    }

    [Fact]
    public void FirstCheckRecordsThenReportsResolvesReopensAndComments()
    {
        using var dir = new TempFolder();
        var file = new IssueStateFile(dir.Path);
        var first = new SeerrComment(1, "No sound", new SeerrPerson(Sam, "Sam"), null);
        Assert.Empty(file.Update(new[] { Issue(SeerrIssues.Open, "2026-11-25T08:00:00Z", first) }));

        var other = Issue(SeerrIssues.Open, "2026-11-25T08:01:00Z", first) with { Id = 4 };
        var reported = Assert.Single(file.Update(new[] { Issue(SeerrIssues.Open, "2026-11-25T08:00:00Z", first), other }));
        Assert.Equal(("reported", 4, "No sound"), (reported.Kind, reported.Issue.Id, reported.Comment!.Message));

        var reply = new SeerrComment(2, "Fixed the file", new SeerrPerson(null, "Admin"), null);
        var changes = file.Update(new[] { Issue(SeerrIssues.Resolved, "2026-11-25T09:00:00Z", first, reply) });
        Assert.Equal(new[] { ("resolved", 1), ("comment", 0) }, changes.Select(c => (c.Kind, c.Count)));
        Assert.Equal(2, changes[1].Comment!.Id);

        var reopened = Assert.Single(file.Update(new[] { Issue(SeerrIssues.Open, "2026-11-25T10:00:00Z", first, reply) }));
        Assert.Equal(("reopened", 2), (reopened.Kind, reopened.Count));
        var again = Assert.Single(file.Update(new[] { Issue(SeerrIssues.Resolved, "2026-11-25T11:00:00Z", first, reply) }));
        Assert.Equal(("resolved", 3), (again.Kind, again.Count));
    }

    [Fact]
    public void CommentsAreReadForChangedNewAndNamedIssuesOnly()
    {
        using var dir = new TempFolder();
        var file = new IssueStateFile(dir.Path);
        var known = Issue(SeerrIssues.Open, "2026-11-25T08:00:00Z") with { Comments = null };
        Assert.Equal(new[] { 9 }, file.NeedingComments(new[] { known }, new[] { 9 }));
        file.Update(new[] { known });

        var changed = known with { Updated = known.Updated!.Value.AddMinutes(1) };
        var fresh = known with { Id = 8 };
        Assert.Equal(new HashSet<int> { 3, 8 }, file.NeedingComments(new[] { changed, fresh }, Array.Empty<int>()));
        Assert.Empty(file.NeedingComments(new[] { known }, Array.Empty<int>()));
    }

    [Fact]
    public void WithoutASeenCommentOnlyLaterOnesCount()
    {
        using var dir = new TempFolder();
        var file = new IssueStateFile(dir.Path);
        file.Update(new[] { Issue(SeerrIssues.Open, "2026-11-25T08:00:00Z") with { Comments = null } });
        var old = new SeerrComment(1, "Old", new SeerrPerson(Sam, "Sam"), new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Utc));
        var fresh = new SeerrComment(2, "New", new SeerrPerson(null, "Admin"), new DateTime(2026, 11, 25, 8, 30, 0, DateTimeKind.Utc));
        var change = Assert.Single(file.Update(new[] { Issue(SeerrIssues.Open, "2026-11-25T08:30:00Z", old, fresh) }));
        Assert.Equal(2, change.Comment!.Id);
    }
}
