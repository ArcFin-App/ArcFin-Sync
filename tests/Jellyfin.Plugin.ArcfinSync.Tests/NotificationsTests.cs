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

        var changes = file.Update(new[] { new SeerrRequest(1, 2, 3, 10, "movie", user), new SeerrRequest(2, 2, 3, 11, "movie", user) });
        Assert.Equal(new[] { (1, "approved") }, changes.Select(c => (c.Request.Id, c.Kind)));
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
    [InlineData("MEDIA_PENDING", null)]
    [InlineData("TEST_NOTIFICATION", null)]
    [InlineData(null, null)]
    public void WebhookTypesMapToChanges(string? type, string? kind)
    {
        Assert.Equal(kind, SeerrRequests.KindOf(type));
    }

    [Fact]
    public void RequestsWithoutAJellyfinAccountAreNotAnnounced()
    {
        using var dir = new TempFolder();
        var file = new SeerrStateFile(dir.Path);
        file.Update(new[] { new SeerrRequest(1, 1, 2, 10, "movie", null) });
        Assert.Empty(file.Update(new[] { new SeerrRequest(1, 2, 3, 10, "movie", null) }));
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
        Assert.Equal((type, requestId), NotificationsController.ReadPayload(body));
    }

    [Fact]
    public void TheSuggestedPayloadIsValidJsonWithBothFields()
    {
        var filled = NotificationsController.SeerrPayload.Replace("{{notification_type}}", "MEDIA_DECLINED", StringComparison.Ordinal)
            .Replace("{{request_id}}", "5", StringComparison.Ordinal);
        Assert.Equal(("MEDIA_DECLINED", 5), NotificationsController.ReadPayload(filled));
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
