using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ArcfinSync.Api;
using Jellyfin.Plugin.ArcfinSync.Api.Hidden;
using Jellyfin.Plugin.ArcfinSync.Api.Integrations;
using Jellyfin.Plugin.ArcfinSync.Api.Limits;
using Jellyfin.Plugin.ArcfinSync.Api.Recommendations;
using Jellyfin.Plugin.ArcfinSync.Api.Security;
using Jellyfin.Plugin.ArcfinSync.Api.WatchParty;
using Xunit;

namespace Jellyfin.Plugin.ArcfinSync.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arcfin-sync-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}

public class SecretVaultTests
{
    private static Func<string, string?> NoEnv => _ => null;

    [Fact]
    public void SecretsRoundTripAndAreNotStoredInPlainText()
    {
        using var dir = new TempFolder();
        var vault = new SecretVault(dir.Path, NoEnv);
        vault.Set("seerr.apiKey", "super-secret-value");
        Assert.Equal("super-secret-value", new SecretVault(dir.Path, NoEnv).Get("seerr.apiKey"));
        Assert.DoesNotContain("super-secret-value", File.ReadAllText(Path.Combine(dir.Path, "vault.json")), StringComparison.Ordinal);
        Assert.Equal("generated", vault.KeySource);
    }

    [Fact]
    public void FilesAreOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempFolder();
        new SecretVault(dir.Path, NoEnv).Set("a", "b");
        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, File.GetUnixFileMode(Path.Combine(dir.Path, "vault.json")));
        Assert.Equal(expected, File.GetUnixFileMode(Path.Combine(dir.Path, "vault.key")));
    }

    [Fact]
    public void AnotherMasterKeyCantReadIt()
    {
        using var dir = new TempFolder();
        new SecretVault(dir.Path, NoEnv).Set("a", "b");
        var other = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var vault = new SecretVault(dir.Path, n => n == SecretVault.MasterKeyVariable ? other : null);
        Assert.Equal("environment", vault.KeySource);
        Assert.True(vault.Has("a"));
        Assert.Null(vault.Get("a"));
    }

    [Fact]
    public void SecretsAreBoundToTheirName()
    {
        using var dir = new TempFolder();
        var vault = new SecretVault(dir.Path, NoEnv);
        vault.Set("a", "one");
        vault.Set("b", "two");
        var path = Path.Combine(dir.Path, "vault.json");
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        var entries = doc.RootElement.GetProperty("Entries");
        var swapped = $"{{\"Version\":1,\"Entries\":{{\"a\":{entries.GetProperty("b").GetRawText()},\"b\":{entries.GetProperty("a").GetRawText()}}}}}";
        File.WriteAllText(path, swapped);
        Assert.Null(vault.Get("a"));
    }

    [Fact]
    public void EmptyValueRemoves()
    {
        using var dir = new TempFolder();
        var vault = new SecretVault(dir.Path, NoEnv);
        vault.Set("a", "b");
        vault.Set("a", string.Empty);
        Assert.False(vault.Has("a"));
    }

    [Fact]
    public void ABadMasterKeyIsRefused()
    {
        using var dir = new TempFolder();
        Assert.Throws<InvalidOperationException>(() => new SecretVault(dir.Path, n => n == SecretVault.MasterKeyVariable ? "short" : null));
    }
}

public class CommandSignerTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc);

    private static CommandSigner Signer(TempFolder dir) => new(dir.Path, new SecretVault(dir.Path, _ => null));

    [Fact]
    public void NoKeyNoCommands()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        Assert.Null(signer.PublicKey);
        Assert.Null(signer.Issue(CommandKind.Lock, Guid.NewGuid(), "server", Now));
    }

    [Fact]
    public void CommandsVerifyWithThePublicKey()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        var key = signer.Generate();
        var user = Guid.NewGuid();
        var command = signer.Issue(CommandKind.ResetPin, user, "server", Now)!;
        Assert.True(CommandSigner.Verify(command, key.PublicKey));

        var payload = JsonDocument.Parse(Convert.FromBase64String(command.Payload)).RootElement;
        Assert.Equal("resetPin", payload.GetProperty("kind").GetString());
        Assert.Equal(user.ToString("N"), payload.GetProperty("user").GetString());
        Assert.Equal(1, payload.GetProperty("counter").GetInt64());
        Assert.Equal(64, Convert.FromBase64String(command.Signature).Length);
    }

    [Fact]
    public void ATamperedPayloadFails()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        var key = signer.Generate();
        var command = signer.Issue(CommandKind.Unlock, Guid.NewGuid(), "server", Now)!;
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(command.Payload)).Replace("unlock", "lock  ", StringComparison.Ordinal);
        command.Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Assert.False(CommandSigner.Verify(command, key.PublicKey));
    }

    [Fact]
    public void LockAndUnlockReplaceEachOther()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        signer.Generate();
        var user = Guid.NewGuid();
        signer.Issue(CommandKind.Lock, user, "s", Now);
        Assert.True(signer.IsLocked(user, Now));
        signer.Issue(CommandKind.ResetPin, user, "s", Now);
        signer.Issue(CommandKind.Unlock, user, "s", Now);
        Assert.False(signer.IsLocked(user, Now));
        Assert.Equal(new[] { CommandKind.ResetPin, CommandKind.Unlock }, signer.CommandsFor(user, Now).Select(c => c.Kind));
        Assert.True(signer.IsLocked(user, Now) == false);
    }

    [Fact]
    public void PinResetsExpireButLocksStay()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        signer.Generate();
        var user = Guid.NewGuid();
        signer.Issue(CommandKind.ResetPin, user, "s", Now);
        signer.Issue(CommandKind.Lock, user, "s", Now);
        var later = Now + CommandSigner.Lifetime + TimeSpan.FromDays(1);
        Assert.Equal(new[] { CommandKind.Lock }, signer.CommandsFor(user, later).Select(c => c.Kind));
    }

    [Fact]
    public void CountersOnlyGoUp()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        signer.Generate();
        var a = signer.Issue(CommandKind.Lock, Guid.NewGuid(), "s", Now)!;
        var b = signer.Issue(CommandKind.Lock, Guid.NewGuid(), "s", Now)!;
        Assert.True(b.Counter > a.Counter);
    }

    [Fact]
    public void ANewKeyIsSignedByTheOldOne()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        var first = signer.Generate();
        var second = signer.Generate();
        Assert.Equal(first.PublicKey, second.PreviousPublicKey);
        using var old = ECDsa.Create();
        old.ImportSubjectPublicKeyInfo(Convert.FromBase64String(first.PublicKey), out _);
        Assert.True(old.VerifyData(Convert.FromBase64String(second.PublicKey), Convert.FromBase64String(second.RotationSignature!),
                                   HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void ImportsAnAdminsP256Key()
    {
        using var dir = new TempFolder();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var info = Signer(dir).Import(key.ExportPkcs8PrivateKeyPem());
        Assert.Equal(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), info.PublicKey);
    }

    [Fact]
    public void RefusesOtherCurvesAndJunk()
    {
        using var dir = new TempFolder();
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => Signer(dir).Import(p384.ExportPkcs8PrivateKeyPem()));
        Assert.Throws<ArgumentException>(() => Signer(dir).Import("not a key"));
    }

    [Fact]
    public void ThePrivateKeyIsOnlyInTheVault()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        signer.Generate();
        var plain = File.ReadAllText(Path.Combine(dir.Path, "signing.json"));
        Assert.DoesNotContain("PRIVATE", plain, StringComparison.Ordinal);
        Assert.True(new SecretVault(dir.Path, _ => null).Has(CommandSigner.PrivateKeyName));
    }
}

public class WatchLimitTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static DateTime At(int hour, int minute = 0, DayOfWeek day = DayOfWeek.Tuesday)
    {
        var date = new DateTime(2026, 11, 24);
        while (date.DayOfWeek != day)
        {
            date = date.AddDays(1);
        }

        return date.AddHours(hour).AddMinutes(minute);
    }

    [Fact]
    public void NoRuleMeansNoLimit() => Assert.True(WatchLimits.Evaluate(null, 10000, At(3)).Allowed);

    [Fact]
    public void DailyLimitRunsOut()
    {
        var rule = new WatchLimitRule { UserId = User, DailyMinutes = 60 };
        Assert.Equal(15, WatchLimits.Evaluate(rule, 45, At(12)).MinutesLeft);
        var state = WatchLimits.Evaluate(rule, 60, At(12));
        Assert.False(state.Allowed);
        Assert.Equal(WatchLimits.DailyLimit, state.Reason);
        Assert.Equal(At(0).AddDays(1), state.Until);
    }

    [Fact]
    public void WeekendsCanHaveTheirOwnAllowance()
    {
        var rule = new WatchLimitRule { UserId = User, DailyMinutes = 60, WeekendMinutes = 180 };
        Assert.True(WatchLimits.Evaluate(rule, 120, At(12, day: DayOfWeek.Saturday)).Allowed);
        Assert.False(WatchLimits.Evaluate(rule, 120, At(12, day: DayOfWeek.Monday)).Allowed);
    }

    [Fact]
    public void BedtimeAcrossMidnight()
    {
        var rule = new WatchLimitRule { UserId = User, BedtimeStart = "21:00", BedtimeEnd = "07:00" };
        Assert.True(WatchLimits.Evaluate(rule, 0, At(20, 59)).Allowed);
        var late = WatchLimits.Evaluate(rule, 0, At(21));
        Assert.False(late.Allowed);
        Assert.Equal(WatchLimits.Bedtime, late.Reason);
        Assert.Equal(At(7).AddDays(1), late.Until);
        Assert.False(WatchLimits.Evaluate(rule, 0, At(6, 59)).Allowed);
        Assert.True(WatchLimits.Evaluate(rule, 0, At(7)).Allowed);
    }

    [Fact]
    public void BedtimeInsideTheDay()
    {
        var rule = new WatchLimitRule { UserId = User, BedtimeStart = "13:00", BedtimeEnd = "14:00" };
        Assert.False(WatchLimits.Evaluate(rule, 0, At(13, 30)).Allowed);
        Assert.True(WatchLimits.Evaluate(rule, 0, At(14)).Allowed);
    }

    [Fact]
    public void MinutesLeftStopAtBedtime()
    {
        var rule = new WatchLimitRule { UserId = User, DailyMinutes = 240, BedtimeStart = "21:00", BedtimeEnd = "07:00" };
        Assert.Equal(30, WatchLimits.Evaluate(rule, 0, At(20, 30)).MinutesLeft);
    }

    [Fact]
    public void Validation()
    {
        Assert.True(WatchLimits.IsValid(new WatchLimitRule { DailyMinutes = 90, BedtimeStart = "21:30", BedtimeEnd = "06:00" }));
        Assert.False(WatchLimits.IsValid(new WatchLimitRule { DailyMinutes = -1 }));
        Assert.False(WatchLimits.IsValid(new WatchLimitRule { BedtimeStart = "21:00" }));
        Assert.False(WatchLimits.IsValid(new WatchLimitRule { BedtimeStart = "25:00", BedtimeEnd = "07:00" }));
    }

    [Fact]
    public void UsageAddsUpPerDay()
    {
        using var dir = new TempFolder();
        var usage = new UsageStore(dir.Path);
        var day = new DateTime(2026, 11, 24);
        usage.Add(User, day, 10);
        usage.Add(User, day, 5.5);
        usage.Add(User, day.AddDays(1), 3);
        Assert.Equal(15.5, usage.Used(User, day));
        Assert.Equal(3, usage.Used(User, day.AddDays(1)));
        usage.Add(User, day.AddDays(10), 1);
        Assert.Equal(0, usage.Used(User, day));
    }
}

public class SeerrRouteTests
{
    [Theory]
    [InlineData("GET", "discover/trending")]
    [InlineData("GET", "discover/movies/genre/28")]
    [InlineData("GET", "discover/tv/network/213")]
    [InlineData("GET", "discover/movies/language/en")]
    [InlineData("GET", "discover/genreslider/movie")]
    [InlineData("GET", "discover/watchlist")]
    [InlineData("GET", "search")]
    [InlineData("GET", "movie/603")]
    [InlineData("GET", "movie/603/ratingscombined")]
    [InlineData("GET", "tv/1399/similar")]
    [InlineData("GET", "person/287/combined_credits")]
    [InlineData("GET", "settings/public")]
    [InlineData("GET", "auth/me")]
    [InlineData("GET", "user/4/quota")]
    [InlineData("GET", "request")]
    [InlineData("POST", "request")]
    [InlineData("POST", "request/12/approve")]
    [InlineData("DELETE", "request/12")]
    [InlineData("POST", "watchlist")]
    [InlineData("DELETE", "watchlist/603")]
    public void AppCallsArePassedOn(string method, string path) => Assert.True(SeerrRoutes.IsAllowed(method, path));

    [Theory]
    [InlineData("GET", "settings/main")]
    [InlineData("POST", "settings/main")]
    [InlineData("GET", "user")]
    [InlineData("POST", "user/import-from-jellyfin")]
    [InlineData("DELETE", "user/4")]
    [InlineData("POST", "auth/jellyfin")]
    [InlineData("POST", "auth/logout")]
    [InlineData("PUT", "request/12")]
    [InlineData("GET", "movie/603/../../settings/main")]
    [InlineData("GET", "movie/603%2F..")]
    [InlineData("GET", "")]
    [InlineData("POST", "movie/603")]
    public void EverythingElseIsNot(string method, string path) => Assert.False(SeerrRoutes.IsAllowed(method, path));

    [Fact]
    public void OnlyNewRequestsAreQueued()
    {
        Assert.True(SeerrRoutes.IsNewRequest("POST", "request"));
        Assert.False(SeerrRoutes.IsNewRequest("POST", "request/1/approve"));
        Assert.False(SeerrRoutes.IsNewRequest("GET", "request"));
    }

    [Fact]
    public void QueueKeepsBodies()
    {
        using var dir = new TempFolder();
        var queue = new SeerrQueue(dir.Path);
        var user = Guid.NewGuid();
        var entry = queue.Add(user, Encoding.UTF8.GetBytes("{\"mediaId\":603}"), DateTime.UtcNow);
        var all = queue.All();
        Assert.Single(all);
        Assert.Equal("{\"mediaId\":603}", Encoding.UTF8.GetString(Convert.FromBase64String(all[0].Body)));
        queue.Update(list => list.Where(e => e.Id != entry.Id).ToList());
        Assert.Empty(queue.All());
    }
}

public class HiddenStoreTests
{
    [Fact]
    public void HideAndUnhidePerRow()
    {
        using var dir = new TempFolder();
        var store = new HiddenStore(dir.Path);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var now = DateTime.UtcNow;
        store.Hide(user, HiddenStore.NextUp, item, now);
        store.Hide(user, HiddenStore.NextUp, item, now);
        Assert.Single(store.Load(user).NextUp);
        Assert.Empty(store.Load(user).ContinueWatching);
        store.Unhide(user, HiddenStore.NextUp, item);
        Assert.Empty(store.Load(user).NextUp);
        Assert.Empty(store.Load(Guid.NewGuid()).NextUp);
    }

    [Fact]
    public void PlayingAgainBringsItBack()
    {
        using var dir = new TempFolder();
        var store = new HiddenStore(dir.Path);
        var user = Guid.NewGuid();
        var replayed = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        var hiddenAt = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        store.Hide(user, HiddenStore.ContinueWatching, replayed, hiddenAt);
        store.Hide(user, HiddenStore.ContinueWatching, untouched, hiddenAt);
        var lists = store.DropReplayed(user, id => id == replayed ? hiddenAt.AddDays(1) : hiddenAt.AddDays(-1));
        Assert.Equal(new[] { untouched }, lists.ContinueWatching.Select(e => e.ItemId));
    }
}

public class WatchPartyTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc);

    private static WatchPartySession Party()
    {
        var party = new WatchPartySession("ABC123", "host", Guid.NewGuid(), Now);
        party.Join("host", Guid.NewGuid(), "Host", "iPhone", Now);
        return party;
    }

    [Fact]
    public void OnlyTheHostStartsAndPostsTheGroup()
    {
        var party = Party();
        party.Join("guest", Guid.NewGuid(), "Guest", "iPad", Now);
        Assert.Throws<UnauthorizedAccessException>(() => party.Start("guest", 0, Now));
        party.Start("host", 1234, Now);
        Assert.Equal(PartyPhase.Starting, party.Phase);
        Assert.Throws<UnauthorizedAccessException>(() => party.SetGroup("guest", "g", Now));
        party.SetGroup("host", "group-1", Now);
        Assert.Equal(PartyPhase.Started, party.Phase);
        Assert.Equal("group-1", party.GroupId);
        Assert.Equal(1234, party.StartTicks);
    }

    [Fact]
    public void RemovedPeopleCantComeBack()
    {
        var party = Party();
        party.Join("guest", Guid.NewGuid(), "Guest", "iPad", Now);
        party.Remove("host", "guest", Now);
        Assert.Throws<InvalidOperationException>(() => party.Join("guest", Guid.NewGuid(), "Guest", "iPad", Now));
        Assert.Throws<InvalidOperationException>(() => party.Remove("host", "host", Now));
    }

    [Fact]
    public void LockedPartiesTakeNoNewPeople()
    {
        var party = Party();
        party.SetLocked("host", true);
        Assert.Throws<InvalidOperationException>(() => party.Join("late", Guid.NewGuid(), "Late", "Mac", Now));
        Assert.Throws<UnauthorizedAccessException>(() => party.SetLocked("late", false));
    }

    [Fact]
    public void TheHostLeavingHandsItOn()
    {
        var party = Party();
        party.Join("guest", Guid.NewGuid(), "Guest", "iPad", Now);
        party.Leave("host", Now);
        Assert.Equal("guest", party.HostKey);
        Assert.True(party.People["guest"].Ready);
    }

    [Fact]
    public void ResetGoesBackToTheLobby()
    {
        var party = Party();
        party.Join("guest", Guid.NewGuid(), "Guest", "iPad", Now);
        party.SetReady("guest", true, Now);
        party.Start("host", 0, Now);
        party.Reset("host", Now);
        Assert.Equal(PartyPhase.Lobby, party.Phase);
        Assert.False(party.People["guest"].Ready);
        Assert.Null(party.GroupId);
    }

    [Fact]
    public void IsFull()
    {
        var party = Party();
        for (var i = 1; i < WatchPartySession.MaxPeople; i++)
        {
            party.Join("p" + i, Guid.NewGuid(), "P", "D", Now);
        }

        Assert.Throws<InvalidOperationException>(() => party.Join("extra", Guid.NewGuid(), "X", "D", Now));
    }
}

public class RecommenderTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc);

    private static TitleFeatures Title(string name, string[] genres, string[]? people = null, double rating = 7, bool series = false, int year = 2010) =>
        new(Guid.NewGuid(), name, series, genres, people ?? Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), rating, year, Now);

    [Fact]
    public void ScoresFollowWhatWasWatchedAndLiked()
    {
        var watched = Title("Heat", new[] { "Crime", "Thriller" }, new[] { "Michael Mann" });
        var crime = Title("Collateral", new[] { "Crime", "Thriller" }, new[] { "Michael Mann" });
        var musical = Title("Musical", new[] { "Music", "Romance" });
        var profile = Recommender.Profile(new[] { new Signal(watched, true, true, false, Now.AddDays(-3)) }, Now);
        Assert.True(Recommender.Score(profile, crime) > Recommender.Score(profile, musical));
    }

    [Fact]
    public void DislikesPushTheirFeaturesDown()
    {
        var disliked = Title("Bad Horror", new[] { "Horror" });
        var horror = Title("Other Horror", new[] { "Horror" });
        var drama = Title("Drama", new[] { "Drama" });
        var profile = Recommender.Profile(new[] { new Signal(disliked, true, false, false, Now) }, Now);
        Assert.True(Recommender.Score(profile, horror) < Recommender.Score(profile, drama));
    }

    [Fact]
    public void RecentWatchingCountsMore()
    {
        var recent = new Signal(Title("A", new[] { "X" }), true, null, false, Now.AddDays(-1));
        var old = new Signal(Title("B", new[] { "X" }), true, null, false, Now.AddDays(-700));
        Assert.True(Recommender.SignalWeight(recent, Now) > Recommender.SignalWeight(old, Now));
    }

    [Fact]
    public void SimilarityPrefersSharedPeopleAndGenres()
    {
        var seed = Title("Seed", new[] { "Sci-Fi" }, new[] { "Director A" });
        var close = Title("Close", new[] { "Sci-Fi" }, new[] { "Director A" });
        var far = Title("Far", new[] { "Comedy" }, new[] { "Someone" });
        Assert.True(Recommender.Similarity(seed, close) > Recommender.Similarity(seed, far));
    }

    [Fact]
    public void TopKeepsAMixOfGenres()
    {
        var scored = Enumerable.Range(0, 20).Select(i => (Title("Action " + i, new[] { "Action" }), 10.0 - i * 0.1))
            .Concat(Enumerable.Range(0, 5).Select(i => (Title("Drama " + i, new[] { "Drama" }), 1.0)));
        var top = Recommender.Top(scored, 9);
        Assert.Equal(3, top.Count(t => t.Genres.Contains("Action")));
        Assert.Contains(top, t => t.Genres.Contains("Drama"));
    }
}

public class SeerrPublicUrlTests
{
    [Fact]
    public void PublicUrlIsEveryonesSeerrUrlUnlessThePolicySetsOne()
    {
        var user = Guid.NewGuid();
        var empty = new Jellyfin.Plugin.ArcfinSync.Configuration.SettingsPolicy();
        var rules = SettingsRules.RulesForUser(empty, user, "https://requests.example.com");
        Assert.Equal("https://requests.example.com", rules.Single(r => r.Key == "seerrURL").Value);

        var set = new Jellyfin.Plugin.ArcfinSync.Configuration.SettingsPolicy();
        set.Settings.Add(new Jellyfin.Plugin.ArcfinSync.Configuration.PolicySetting { Key = "seerrURL", DefaultValue = "https://other.example.com", Locked = true });
        var kept = SettingsRules.RulesForUser(set, user, "https://requests.example.com").Single(r => r.Key == "seerrURL");
        Assert.Equal("https://other.example.com", kept.Value);
        Assert.True(kept.Locked);
        Assert.DoesNotContain(SettingsRules.RulesForUser(empty, user), r => r.Key == "seerrURL");
    }

    [Fact]
    public void QuickConnectLockedForcesTheJellyfinUsername()
    {
        Assert.True(SettingsRules.IsSeerrUsernameForced(new[] { new EffectiveRule("seerrAuthMethod", "quickConnect", true) }));
        Assert.False(SettingsRules.IsSeerrUsernameForced(new[] { new EffectiveRule("seerrAuthMethod", "local", true) }));
    }
}

public class ScheduledPartyTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);

    private static WatchPartySession Scheduled(DateTime at)
    {
        var party = new WatchPartySession("ABC123", "host", Guid.NewGuid(), Now, at);
        party.Join("host", Guid.NewGuid(), "Host", "iPhone", Now);
        return party;
    }

    [Fact]
    public void TheLobbyOpensFifteenMinutesBefore()
    {
        var at = Now.AddHours(2);
        var party = Scheduled(at);
        party.Join("guest", Guid.NewGuid(), "Guest", "iPad", Now);
        Assert.True(party.IsWaiting(Now));
        Assert.Throws<InvalidOperationException>(() => party.SetReady("guest", true, Now));
        var open = at - WatchPartySession.LobbyOpensBefore;
        Assert.False(party.IsWaiting(open));
        party.SetReady("guest", true, open);
        Assert.True(party.People["guest"].Ready);
    }

    [Fact]
    public void TheHostCanStartEarly()
    {
        var party = Scheduled(Now.AddDays(1));
        party.Start("host", 0, Now);
        Assert.Equal(PartyPhase.Starting, party.Phase);
        Assert.Null(party.ScheduledFor);
        Assert.False(party.IsScheduled);
    }

    [Fact]
    public void AnUnstartedPartyEndsAnHourAfterItsTime()
    {
        var at = Now.AddHours(3);
        var party = Scheduled(at);
        Assert.False(party.IsOver(Now.AddHours(2)));
        Assert.False(party.IsOver(at + TimeSpan.FromMinutes(59)));
        Assert.True(party.IsOver(at + TimeSpan.FromMinutes(61)));
    }

    [Fact]
    public void ScheduledPartiesAreNotForgottenWhileQuiet()
    {
        var party = Scheduled(Now.AddDays(5));
        Assert.False(party.IsOver(Now.AddDays(2)));
    }

    [Fact]
    public void OnlyTheNextThirtyDays()
    {
        Assert.Throws<InvalidOperationException>(() => WatchPartySession.CheckSchedule(Now.AddDays(31), Now));
        Assert.Throws<InvalidOperationException>(() => WatchPartySession.CheckSchedule(Now.AddHours(-1), Now));
        WatchPartySession.CheckSchedule(Now.AddDays(29), Now);
        WatchPartySession.CheckSchedule(null, Now);
    }

    [Fact]
    public void OnlyTheHostReschedules()
    {
        var party = Scheduled(Now.AddHours(5));
        party.Join("guest", Guid.NewGuid(), "Guest", "iPad", Now);
        Assert.Throws<UnauthorizedAccessException>(() => party.Schedule("guest", Now.AddHours(6), Now));
        party.Schedule("host", Now.AddHours(6), Now);
        Assert.Equal(Now.AddHours(6), party.ScheduledFor);
        party.Schedule("host", null, Now);
        Assert.False(party.IsScheduled);
    }

    [Fact]
    public void ASavedPartyComesBackTheSame()
    {
        var party = Scheduled(Now.AddDays(2));
        party.Join("guest", Guid.NewGuid(), "Guest", "iPad", Now);
        party.Join("gone", Guid.NewGuid(), "Gone", "Mac", Now);
        party.Remove("host", "gone", Now);
        var json = System.Text.Json.JsonSerializer.Serialize(party.Save());
        var back = WatchPartySession.Restore(System.Text.Json.JsonSerializer.Deserialize<SavedParty>(json)!);
        Assert.Equal(party.Code, back.Code);
        Assert.Equal(party.ScheduledFor, back.ScheduledFor);
        Assert.Equal(new[] { "host", "guest" }, back.People.Values.OrderBy(p => p.Order).Select(p => p.Key));
        Assert.Contains("gone", back.Removed);
        Assert.Throws<InvalidOperationException>(() => back.Join("gone", Guid.NewGuid(), "Gone", "Mac", Now));
    }
}

public class HistoryTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);

    private static Jellyfin.Plugin.ArcfinSync.Api.History.HistoryEntry Entry(DateTime ended, string kind = "watchParty") => new() { Kind = kind, Ended = ended, Outcome = "watched" };

    [Fact]
    public void OldEntriesGoAfterTheChosenDays()
    {
        using var dir = new TempFolder();
        var store = new Jellyfin.Plugin.ArcfinSync.Api.History.HistoryStore(dir.Path);
        store.Add(Entry(Now.AddDays(-40)), null, Now);
        store.Add(Entry(Now.AddDays(-10)), null, Now);
        store.Add(Entry(Now.AddDays(-1), "pickTogether"), null, Now);
        Assert.Equal(3, store.All(null, Now).Count);
        var kept = store.All(30, Now);
        Assert.Equal(2, kept.Count);
        Assert.Equal("pickTogether", kept[0].Kind);
        Assert.Equal(2, store.All(null, Now).Count);
    }

    [Fact]
    public void ClearEmptiesIt()
    {
        using var dir = new TempFolder();
        var store = new Jellyfin.Plugin.ArcfinSync.Api.History.HistoryStore(dir.Path);
        store.Add(Entry(Now), 90, Now);
        store.Clear();
        Assert.Empty(store.All(null, Now));
    }

    [Fact]
    public void APartyRemembersEveryoneAndHowItWent()
    {
        var party = new WatchPartySession("ABC123", "host", Guid.NewGuid(), Now, Now.AddHours(1));
        party.Join("host", Guid.NewGuid(), "Host", "iPhone", Now);
        party.Join("guest", Guid.NewGuid(), "Guest", "iPad", Now);
        party.Leave("guest", Now);
        var notStarted = PartyHistory.Entry(party, "notStarted", Now);
        Assert.Equal(new[] { "Host", "Guest" }, notStarted.People);
        Assert.Equal("notStarted", notStarted.Outcome);
        Assert.Equal(Now.AddHours(1), notStarted.ScheduledFor);
        party.Start("host", 0, Now);
        party.SetGroup("host", "g", Now);
        var watched = PartyHistory.Entry(party, "ended", Now);
        Assert.Equal("watched", watched.Outcome);
        Assert.Equal(Now.AddHours(1), watched.ScheduledFor);
        Assert.Equal("Host", watched.HostName);
    }
}

public class RotationTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);

    private static CommandSigner Signer(TempFolder dir) => new(dir.Path, new SecretVault(dir.Path, _ => null));

    [Fact]
    public void LocksStillVerifyAfterARotation()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        signer.Generate();
        var user = Guid.NewGuid();
        var lockCommand = signer.Issue(CommandKind.Lock, user, "s", Now)!;
        var next = signer.Generate();
        var after = signer.CommandsFor(user, Now).Single();
        Assert.Equal(lockCommand.Counter, after.Counter);
        Assert.Equal(lockCommand.Payload, after.Payload);
        Assert.True(CommandSigner.Verify(after, next.PublicKey));
    }

    [Fact]
    public void ADeviceCanFollowEveryRotationItMissed()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        var first = signer.Generate();
        var second = signer.Generate();
        var third = signer.Generate();
        var chain = signer.ChainFrom(first.Fingerprint);
        Assert.Equal(new[] { second.Fingerprint, third.Fingerprint }, chain.Select(k => k.Fingerprint));
        var pinned = first.PublicKey;
        foreach (var link in chain)
        {
            Assert.Equal(pinned, link.PreviousPublicKey);
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pinned), out _);
            Assert.True(key.VerifyData(Convert.FromBase64String(link.PublicKey), Convert.FromBase64String(link.RotationSignature!),
                                       System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            pinned = link.PublicKey;
        }

        Assert.Empty(signer.ChainFrom(third.Fingerprint));
        Assert.Empty(signer.ChainFrom("00:00"));
    }

    [Fact]
    public void DeviceLocksAreSeparateFromUserLocks()
    {
        using var dir = new TempFolder();
        var signer = Signer(dir);
        signer.Generate();
        var user = Guid.NewGuid();
        signer.Issue(CommandKind.Lock, user, "s", Now, "phone");
        Assert.True(signer.IsLocked(user, "phone", Now));
        Assert.False(signer.IsLocked(user, Now));
        signer.Issue(CommandKind.Lock, user, "s", Now);
        signer.Issue(CommandKind.Unlock, user, "s", Now, "phone");
        Assert.False(signer.IsLocked(user, "phone", Now));
        Assert.True(signer.IsLocked(user, Now));
        var payload = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(signer.CommandsFor(user, Now).Last().Payload)).RootElement;
        Assert.Equal("phone", payload.GetProperty("device").GetString());
    }

    [Fact]
    public void ImportedKeysAreMarked()
    {
        using var dir = new TempFolder();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Signer(dir).Import(key.ExportPkcs8PrivateKeyPem());
        Assert.True(Signer(dir).PublicKey!.Imported);
        Assert.Null(SecurityService.NextRotation(new Jellyfin.Plugin.ArcfinSync.Configuration.SecuritySettings { AutoRotate = true }, Signer(dir).PublicKey));
    }

    [Fact]
    public void NextRotationFollowsTheInterval()
    {
        var key = new PublicKeyInfo { Created = Now };
        Assert.Equal(Now.AddDays(3), SecurityService.NextRotation(new Jellyfin.Plugin.ArcfinSync.Configuration.SecuritySettings { AutoRotate = true, RotateEveryDays = 3 }, key));
        Assert.Null(SecurityService.NextRotation(new Jellyfin.Plugin.ArcfinSync.Configuration.SecuritySettings { AutoRotate = false }, key));
    }
}

public class StaleDeviceTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid User = Guid.NewGuid();

    private static Jellyfin.Plugin.ArcfinSync.Configuration.SecuritySettings Settings(string action = "signOut", int days = 7) => new() { StaleAction = action, StaleAfterDays = days };

    [Fact]
    public void BehindForLongerThanTheDaysIsStale()
    {
        var record = new DeviceKeyRecord { UserId = User, LastCheckIn = Now.AddDays(-8), LastCurrent = Now.AddDays(-8) };
        Assert.True(StalePolicy.IsStale(record, StalePolicy.For(Settings(), User), null, Now));
        Assert.False(StalePolicy.IsStale(record, StalePolicy.For(Settings(days: 10), User), null, Now));
    }

    [Fact]
    public void TurningItOnDoesntSignEveryoneOutAtOnce()
    {
        var record = new DeviceKeyRecord { UserId = User, LastCheckIn = Now.AddDays(-100), LastCurrent = Now.AddDays(-100) };
        Assert.False(StalePolicy.IsStale(record, StalePolicy.For(Settings(), User), Now.AddDays(-1), Now));
        Assert.True(StalePolicy.IsStale(record, StalePolicy.For(Settings(), User), Now.AddDays(-8), Now));
    }

    [Fact]
    public void OverridesWinForTheirUser()
    {
        var settings = Settings();
        settings.Users.Add(new Jellyfin.Plugin.ArcfinSync.Configuration.SecurityUserOverride { UserId = User, StaleAction = "off" });
        var other = Guid.NewGuid();
        settings.Users.Add(new Jellyfin.Plugin.ArcfinSync.Configuration.SecurityUserOverride { UserId = other, StaleAction = "inherit", StaleAfterDays = 30 });
        Assert.Equal("off", StalePolicy.For(settings, User).Action);
        Assert.Equal(new StaleRule("signOut", 30), StalePolicy.For(settings, other));
        Assert.Equal(new StaleRule("signOut", 7), StalePolicy.For(settings, Guid.NewGuid()));
    }

    [Fact]
    public void SignedOutDevicesArentCountedAgain()
    {
        var record = new DeviceKeyRecord { UserId = User, LastCheckIn = Now.AddDays(-30), SignedOut = Now.AddDays(-20) };
        Assert.False(StalePolicy.IsStale(record, StalePolicy.For(Settings(), User), null, Now));
    }

    [Fact]
    public void CheckingInWithTheCurrentKeyKeepsItUpToDate()
    {
        using var dir = new TempFolder();
        var store = new DeviceKeyStore(dir.Path);
        store.CheckIn(User, "phone", "iPhone", "AA", true, Now.AddDays(-10));
        store.CheckIn(User, "phone", "iPhone", "BB-old", false, Now);
        var record = store.All().Single();
        Assert.Equal(Now.AddDays(-10), record.LastCurrent);
        Assert.Equal(Now, record.LastCheckIn);
        Assert.True(StalePolicy.IsStale(record, StalePolicy.For(Settings(), User), null, Now));
    }
}

public class KeyResetTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AResetCutsTheChainButKeepsLocks()
    {
        using var dir = new TempFolder();
        var signer = new CommandSigner(dir.Path, new SecretVault(dir.Path, _ => null));
        var first = signer.Generate();
        signer.Generate();
        var user = Guid.NewGuid();
        signer.Issue(CommandKind.Lock, user, "s", Now);
        var fresh = signer.Reset();
        Assert.Null(fresh.PreviousPublicKey);
        Assert.Null(fresh.RotationSignature);
        Assert.NotNull(fresh.ResetAt);
        Assert.Empty(signer.ChainFrom(first.Fingerprint));
        Assert.True(CommandSigner.Verify(signer.CommandsFor(user, Now).Single(), fresh.PublicKey));
        Assert.False(CommandSigner.Verify(signer.CommandsFor(user, Now).Single(), first.PublicKey));
    }
}

public class OlderAppTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OlderAppsGetADay()
    {
        Assert.False(OlderApps.ShouldSignOut(Now.AddHours(-23), null, false, Now));
        Assert.True(OlderApps.ShouldSignOut(Now.AddHours(-25), null, false, Now));
    }

    [Fact]
    public void TheDayCountsFromWhenTheRuleWasTurnedOn()
    {
        Assert.False(OlderApps.ShouldSignOut(Now.AddDays(-10), Now.AddHours(-2), false, Now));
    }

    [Fact]
    public void SomeoneLockedIsSignedOutAtOnce()
    {
        Assert.True(OlderApps.ShouldSignOut(Now, null, true, Now));
    }

    [Fact]
    public void FirstSeenIsRememberedUntilForgotten()
    {
        using var dir = new TempFolder();
        var store = new OlderAppStore(dir.Path);
        var user = Guid.NewGuid();
        Assert.Equal(Now.AddDays(-2), store.FirstSeen(user, "tv", Now.AddDays(-2)));
        Assert.Equal(Now.AddDays(-2), store.FirstSeen(user, "tv", Now));
        store.Forget(user, "tv");
        Assert.Equal(Now, store.FirstSeen(user, "tv", Now));
    }
}

public class HouseholdTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WhicheverCapIsReachedFirstStopsADownload()
    {
        var caps = new Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadCaps(10, 5_000_000_000);
        Assert.True(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.Fits(caps, 9, 1_000_000_000, 1_000_000_000));
        Assert.False(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.Fits(caps, 10, 0, 1));
        Assert.False(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.Fits(caps, 0, 4_500_000_000, 1_000_000_000));
        Assert.True(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.Fits(null, 1000, long.MaxValue / 2, 1));
    }

    [Fact]
    public void DownloadOverridesWin()
    {
        var user = Guid.NewGuid();
        var settings = new Jellyfin.Plugin.ArcfinSync.Configuration.DownloadLimitSettings { Enabled = true, MaxTitles = 20, MaxGigabytes = 50 };
        Assert.Equal(new Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadCaps(20, 50_000_000_000), Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.For(settings, user));
        settings.Users.Add(new Jellyfin.Plugin.ArcfinSync.Configuration.DownloadLimitOverride { UserId = user, Mode = "custom", MaxTitles = 5 });
        Assert.Equal(new Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadCaps(5, null), Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.For(settings, user));
        settings.Users[0].Mode = "off";
        Assert.Null(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.For(settings, user));
        settings.Enabled = false;
        Assert.Null(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.For(settings, Guid.NewGuid()));
    }

    [Fact]
    public void ATitleOnTwoDevicesCountsOnce()
    {
        using var dir = new TempFolder();
        var store = new Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadInventoryStore(dir.Path);
        var user = Guid.NewGuid();
        static Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadedTitle T(string id, long bytes) => new() { ItemId = id, Bytes = bytes };
        store.Report(user, "phone", new[] { T("aaa", 100), T("bbb", 50), T("ccc", 10) }, Now);
        store.Report(user, "ipad", new[] { T("AAA", 120), T("ddd", 5) }, Now);
        Assert.Equal((4, 185L), store.Totals(user));
        Assert.Equal(120L, store.Existing(user, "a-a-a"));
        Assert.Null(store.Existing(user, "zzz"));
        store.Report(user, "phone", new[] { T("bbb", 50) }, Now);
        Assert.Equal((3, 175L), store.Totals(user));
    }

    [Fact]
    public void ATitleAlreadyElsewhereNeedsNoNewTitle()
    {
        var caps = new Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadCaps(3, 1000);
        Assert.False(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.Fits(caps, 3, 500, 100));
        Assert.True(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.Fits(caps, 3, 500, 100, existing: 100));
        Assert.False(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.Fits(caps, 3, 950, 200, existing: 100));
    }

    [Fact]
    public void AnnouncementsShowInTheirWindowForTheirPeople()
    {
        var me = Guid.NewGuid();
        var list = new[]
        {
            new Jellyfin.Plugin.ArcfinSync.Configuration.Announcement { Title = "Everyone" },
            new Jellyfin.Plugin.ArcfinSync.Configuration.Announcement { Title = "Later", Starts = Now.AddHours(1) },
            new Jellyfin.Plugin.ArcfinSync.Configuration.Announcement { Title = "Over", Ends = Now.AddHours(-1) },
            new Jellyfin.Plugin.ArcfinSync.Configuration.Announcement { Title = "Someone else", Users = { Guid.NewGuid() } },
            new Jellyfin.Plugin.ArcfinSync.Configuration.Announcement { Title = "Me", Users = { me }, Style = "warning" }
        };
        Assert.Equal(new[] { "Me", "Everyone" }, Jellyfin.Plugin.ArcfinSync.Api.Household.Notices.ActiveFor(list, me, Now).Select(a => a.Title));
    }

    [Fact]
    public void MaintenanceThatsOverIsLeftOut()
    {
        var list = new[]
        {
            new Jellyfin.Plugin.ArcfinSync.Configuration.MaintenanceWindow { Starts = Now.AddHours(-3), Ends = Now.AddHours(-1) },
            new Jellyfin.Plugin.ArcfinSync.Configuration.MaintenanceWindow { Starts = Now.AddHours(5), Ends = Now.AddHours(6), Message = "Next" },
            new Jellyfin.Plugin.ArcfinSync.Configuration.MaintenanceWindow { Starts = Now.AddHours(-1), Ends = Now.AddHours(1), Message = "Now" }
        };
        Assert.Equal(new[] { "Now", "Next" }, Jellyfin.Plugin.ArcfinSync.Api.Household.Notices.Upcoming(list, Now).Select(m => m.Message));
    }

    [Fact]
    public void WarningsComeOnceEachAtTenAndFive()
    {
        var given = new HashSet<int>();
        Assert.Null(WatchLimits.WarningDue(new LimitState(true, null, 30, null), given));
        Assert.Equal(10, WatchLimits.WarningDue(new LimitState(true, null, 9, null), given));
        given.Add(10);
        Assert.Null(WatchLimits.WarningDue(new LimitState(true, null, 8, null), given));
        Assert.Equal(5, WatchLimits.WarningDue(new LimitState(true, null, 4, null), given));
        Assert.Null(WatchLimits.WarningDue(new LimitState(false, WatchLimits.DailyLimit, 0, null), new HashSet<int>()));
    }
}

public class PolicyAndTrustTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PinRulesReachTheRightPeople()
    {
        var chosen = Guid.NewGuid();
        var settings = new Jellyfin.Plugin.ArcfinSync.Configuration.SecuritySettings
        {
            RequireHttpsAway = true,
            Pins = new Jellyfin.Plugin.ArcfinSync.Configuration.PinRules { RequiredFor = "chosen", Users = { chosen }, MinLength = 6, MaxTries = 5 }
        };
        var mine = UserPolicy.For(settings, chosen);
        Assert.Equal("true", mine["pinRequired"]);
        Assert.Equal("6", mine["pinMinLength"]);
        Assert.Equal("8", mine["pinMaxLength"]);
        Assert.Equal("5", mine["pinMaxTries"]);
        Assert.Equal("true", mine["requireHttps"]);
        Assert.Equal("false", UserPolicy.For(settings, Guid.NewGuid())["pinRequired"]);
        Assert.True(UserPolicy.Same(new Dictionary<string, string>(mine), mine));
        Assert.False(UserPolicy.Same(null, mine));
    }

    [Fact]
    public void TrustedDevicesFollowTheOverride()
    {
        var user = Guid.NewGuid();
        var settings = new Jellyfin.Plugin.ArcfinSync.Configuration.SecuritySettings { Enabled = true, RequireTrustedDevices = false };
        Assert.False(TrustStore.Required(settings, user));
        settings.Users.Add(new Jellyfin.Plugin.ArcfinSync.Configuration.SecurityUserOverride { UserId = user, TrustedDevices = "on" });
        Assert.True(TrustStore.Required(settings, user));
        settings.Enabled = false;
        Assert.False(TrustStore.Required(settings, user));
    }

    [Fact]
    public void AWaitingDeviceKeepsItsCode()
    {
        var file = new TrustFile();
        var user = Guid.NewGuid();
        var first = TrustStore.RequestFor(file, user, "tv", "Apple TV", Now);
        var again = TrustStore.RequestFor(file, user, "tv", "Apple TV", Now.AddMinutes(5));
        Assert.Equal(first.Code, again.Code);
        Assert.Equal(6, first.Code.Length);
        Assert.Single(file.Requests);
    }

    [Fact]
    public void PoliciesAndWipesAreSignedWithTheirData()
    {
        using var dir = new TempFolder();
        var signer = new CommandSigner(dir.Path, new SecretVault(dir.Path, _ => null));
        var key = signer.Generate();
        var user = Guid.NewGuid();
        signer.Issue(CommandKind.Policy, user, "s", Now, data: new Dictionary<string, string> { ["pinRequired"] = "true" });
        signer.Issue(CommandKind.Policy, user, "s", Now, data: new Dictionary<string, string> { ["pinRequired"] = "false" });
        signer.Issue(CommandKind.Wipe, user, "s", Now, "phone");
        var commands = signer.CommandsFor(user, Now);
        Assert.Single(commands, c => c.Kind == CommandKind.Policy);
        var policy = commands.Single(c => c.Kind == CommandKind.Policy);
        Assert.Equal("false", policy.Data!["pinRequired"]);
        Assert.True(CommandSigner.Verify(policy, key.PublicKey));
        var payload = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(policy.Payload)).RootElement;
        Assert.Equal("false", payload.GetProperty("data").GetProperty("pinRequired").GetString());
        Assert.Equal("phone", commands.Single(c => c.Kind == CommandKind.Wipe).DeviceId);
    }
}

public class MusicLimitTests
{
    private static Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadedTitle Song(string id, string album, long bytes) => new() { ItemId = id, AlbumId = album, Bytes = bytes };

    [Fact]
    public void SongsAndAlbumsCountOnceAcrossDevices()
    {
        using var dir = new TempFolder();
        var store = new Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadInventoryStore(dir.Path);
        var user = Guid.NewGuid();
        var now = DateTime.UtcNow;
        store.Report(user, "phone", Array.Empty<Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadedTitle>(), now, new[] { Song("s1", "a1", 10), Song("s2", "a1", 10), Song("s3", "a2", 5) });
        store.Report(user, "ipad", Array.Empty<Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadedTitle>(), now, new[] { Song("s1", "a1", 12) });
        var music = store.Music(user);
        Assert.Equal(3, music.Songs);
        Assert.Equal(2, music.Albums);
        Assert.Equal(27, music.Bytes);
        store.Report(user, "ipad", Array.Empty<Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadedTitle>(), now);
        Assert.Equal(3, store.Music(user).Songs);
    }

    [Fact]
    public void WhicheverMusicCapComesFirstStopsIt()
    {
        var used = new Jellyfin.Plugin.ArcfinSync.Api.Household.MusicTotals(9, 2, 100, new HashSet<string> { "s1" }, new HashSet<string> { "a1", "a2" });
        var caps = new Jellyfin.Plugin.ArcfinSync.Api.Household.MusicCaps(3, 10, 1000);
        Assert.True(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.MusicFits(caps, used, new[] { Song("s9", "a1", 10) }));
        Assert.False(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.MusicFits(caps, used, new[] { Song("s9", "a3", 10), Song("s10", "a3", 10) }));
        Assert.True(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.MusicFits(caps, used, new[] { Song("s1", "a1", 10) }));
        Assert.False(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.MusicFits(new(null, null, 105), used, new[] { Song("s9", "a1", 10) }));
    }

    [Fact]
    public void MusicOverridesWin()
    {
        var user = Guid.NewGuid();
        var settings = new Jellyfin.Plugin.ArcfinSync.Configuration.DownloadLimitSettings { MusicEnabled = true, MusicMaxSongs = 500 };
        Assert.Equal(new Jellyfin.Plugin.ArcfinSync.Api.Household.MusicCaps(null, 500, null), Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.MusicFor(settings, user));
        settings.Users.Add(new Jellyfin.Plugin.ArcfinSync.Configuration.DownloadLimitOverride { UserId = user, MusicMode = "custom", MusicMaxAlbums = 5 });
        Assert.Equal(new Jellyfin.Plugin.ArcfinSync.Api.Household.MusicCaps(5, null, null), Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.MusicFor(settings, user));
        settings.Users[0].MusicMode = "off";
        Assert.Null(Jellyfin.Plugin.ArcfinSync.Api.Household.DownloadLimits.MusicFor(settings, user));
    }
}
