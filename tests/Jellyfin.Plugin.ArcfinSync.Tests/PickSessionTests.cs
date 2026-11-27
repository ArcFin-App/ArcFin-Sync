using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.PickTogether;
using Xunit;

namespace Jellyfin.Plugin.ArcfinSync.Tests;

public class PickSessionTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc);

    private static List<PickTitle> Pool(int count) =>
        Enumerable.Range(0, count).Select(i => new PickTitle(Guid.NewGuid(), i % 10, Now.AddDays(-i))).ToList();

    private static PickSession Session(List<PickTitle> pool, PickRule rule = PickRule.Everyone, bool sameOrder = false, PickOrder order = PickOrder.TopRated) =>
        new("ABC123", "a", new PickOptions(true, false, Array.Empty<string>(), false, order, rule, sameOrder), pool, Now, new Random(7));

    private static HashSet<Guid> All(List<PickTitle> pool) => pool.Select(t => t.Id).ToHashSet();

    [Fact]
    public void EveryoneMustLikeATitleForAMatch()
    {
        var pool = Pool(10);
        var s = Session(pool);
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        s.Join("b", Guid.NewGuid(), "B", All(pool), Now);
        s.Join("c", Guid.NewGuid(), "C", All(pool), Now);
        var id = s.Deck[0];
        s.Swipe("a", id, true, Now);
        s.Swipe("b", id, true, Now);
        Assert.Empty(s.Matches);
        s.Swipe("c", id, true, Now);
        Assert.Single(s.Matches);
    }

    [Fact]
    public void MostNeedsMoreThanHalf()
    {
        var pool = Pool(10);
        var s = Session(pool, PickRule.Most);
        foreach (var k in new[] { "a", "b", "c", "d" })
        {
            s.Join(k, Guid.NewGuid(), k, All(pool), Now);
        }

        var id = s.Deck[0];
        s.Swipe("a", id, true, Now);
        s.Swipe("b", id, true, Now);
        Assert.Empty(s.Matches);
        s.Swipe("c", id, true, Now);
        Assert.Single(s.Matches);
    }

    [Fact]
    public void OnePersonIsNeverAMatch()
    {
        var pool = Pool(5);
        var s = Session(pool);
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        s.Swipe("a", s.Deck[0], true, Now);
        Assert.Empty(s.Matches);
    }

    [Fact]
    public void SomeoneAwayNoLongerHoldsUpEveryone()
    {
        var pool = Pool(10);
        var s = Session(pool);
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        s.Join("b", Guid.NewGuid(), "B", All(pool), Now);
        s.Join("c", Guid.NewGuid(), "C", All(pool), Now);
        var id = s.Deck[0];
        var later = Now + PickSession.AwayAfter + TimeSpan.FromSeconds(1);
        s.Touch("a", later);
        s.Touch("b", later);
        s.Swipe("a", id, true, later);
        s.Swipe("b", id, true, later);
        Assert.Single(s.Matches);
    }

    [Fact]
    public void AnotherRoundNeverRepeatsATitle()
    {
        var pool = Pool(PickSession.DeckSize * 2 + 5);
        var s = Session(pool);
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        var first = s.Deck.ToHashSet();
        s.Restart(Now);
        Assert.DoesNotContain(s.Deck, first.Contains);
        var second = s.Deck.ToHashSet();
        s.Restart(Now);
        Assert.Equal(5, s.Deck.Count);
        Assert.DoesNotContain(s.Deck, id => first.Contains(id) || second.Contains(id));
        s.Restart(Now);
        Assert.Empty(s.Deck);
    }

    [Fact]
    public void ATitleOnlyOneOfThemCanSeeIsNeverDealtToTheOther()
    {
        var pool = Pool(10);
        var hidden = pool[0].Id;
        var s = Session(pool);
        s.Join("adult", Guid.NewGuid(), "A", All(pool), Now);
        s.Join("kid", Guid.NewGuid(), "K", All(pool).Where(id => id != hidden).ToHashSet(), Now);
        Assert.DoesNotContain(hidden, s.Next("adult", 20, Now));
        Assert.DoesNotContain(hidden, s.Next("kid", 20, Now));
    }

    [Fact]
    public void EachPersonGetsTheirOwnOrderUnlessAskedForTheSame()
    {
        var pool = Pool(40);
        var own = Session(pool, order: PickOrder.Random);
        own.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        own.Join("b", Guid.NewGuid(), "B", All(pool), Now);
        Assert.NotEqual(own.People["a"].Order, own.People["b"].Order);
        Assert.Equal(own.People["a"].Order.ToHashSet(), own.People["b"].Order.ToHashSet());

        var same = Session(pool, sameOrder: true, order: PickOrder.Random);
        same.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        same.Join("b", Guid.NewGuid(), "B", All(pool), Now);
        Assert.Equal(same.People["a"].Order, same.People["b"].Order);
    }

    [Fact]
    public void TopRatedLeansToHigherRatings()
    {
        var pool = Pool(200);
        var s = Session(pool);
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        var ratings = pool.ToDictionary(t => t.Id, t => t.Rating);
        var firstTen = s.People["a"].Order.Take(10).Average(id => ratings[id]);
        var lastTen = s.People["a"].Order.TakeLast(10).Average(id => ratings[id]);
        Assert.True(firstTen > lastTen);
    }

    /// <summary>Two people who each like about a quarter of titles should agree well inside a deck.</summary>
    [Fact]
    public void TwoPeopleUsuallyMatchWellInsideADeck()
    {
        var random = new Random(3);
        var swipesToMatch = new List<int>();
        for (var run = 0; run < 200; run++)
        {
            var pool = Pool(500);
            var s = Session(pool);
            var taste = pool.ToDictionary(t => t.Id, _ => (A: random.NextDouble() < 0.25, B: random.NextDouble() < 0.25));
            s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
            s.Join("b", Guid.NewGuid(), "B", All(pool), Now);
            var swipes = 0;
            while (s.Matches.Count == 0 && swipes < PickSession.DeckSize)
            {
                var a = s.Next("a", 1, Now).Single();
                var b = s.Next("b", 1, Now).Single();
                s.Swipe("a", a, taste[a].A, Now);
                s.Swipe("b", b, taste[b].B, Now);
                swipes++;
            }

            swipesToMatch.Add(swipes);
        }

        swipesToMatch.Sort();
        Assert.True(swipesToMatch[swipesToMatch.Count / 2] < 30, $"median {swipesToMatch[swipesToMatch.Count / 2]} swipes");
    }

    [Fact]
    public void WatchingTogetherNeedsEveryoneHereAndOnlyTheLeaderSetsItUp()
    {
        var pool = Pool(10);
        var s = Session(pool);
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        s.Join("b", Guid.NewGuid(), "B", All(pool), Now);
        var id = s.Deck[0];
        s.JoinTogether("a", id, Now);
        Assert.Null(s.Together);
        s.Swipe("a", id, true, Now);
        s.Swipe("b", id, true, Now);
        s.JoinTogether("a", id, Now);
        Assert.False(s.TogetherReady(Now));
        Assert.False(s.SetTogetherGroup("a", "g", id, 0, Now));
        s.JoinTogether("b", id, Now);
        Assert.True(s.TogetherReady(Now));
        Assert.False(s.SetTogetherGroup("b", "g", id, 0, Now));
        Assert.True(s.SetTogetherGroup("a", "g", id, 600, Now));
        Assert.Equal("g", s.Together!.GroupId);
    }

    [Fact]
    public void TheLeaderLeavingHandsItOn()
    {
        var pool = Pool(10);
        var s = Session(pool);
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        s.Join("b", Guid.NewGuid(), "B", All(pool), Now);
        var id = s.Deck[0];
        s.Swipe("a", id, true, Now);
        s.Swipe("b", id, true, Now);
        s.JoinTogether("a", id, Now);
        s.JoinTogether("b", id, Now);
        s.LeaveTogether("a", Now);
        Assert.Equal("b", s.Together!.Leader);
        s.LeaveTogether("b", Now);
        Assert.Null(s.Together);
    }

    [Fact]
    public void OnlyTheHostRemovesPeopleAndARemovedDeviceCantComeBack()
    {
        var pool = Pool(10);
        var s = Session(pool);
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        s.Join("b", Guid.NewGuid(), "B", All(pool), Now);
        s.Join("c", Guid.NewGuid(), "C", All(pool), Now);
        Assert.False(s.Remove("b", "c", Now));
        Assert.False(s.Remove("a", "a", Now));
        Assert.True(s.Remove("a", "c", Now));
        Assert.False(s.People.ContainsKey("c"));
        Assert.Throws<InvalidOperationException>(() => s.Join("c", Guid.NewGuid(), "C", All(pool), Now));
    }

    [Fact]
    public void ALockedSessionTakesNoOneNewButKeepsWhoIsIn()
    {
        var pool = Pool(10);
        var s = Session(pool);
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        s.Locked = true;
        Assert.Throws<InvalidOperationException>(() => s.Join("b", Guid.NewGuid(), "B", All(pool), Now));
        s.Join("a", Guid.NewGuid(), "A", All(pool), Now);
        Assert.Single(s.People);
    }

    [Fact]
    public void TwoDevicesOnOneProfileAreTwoPeople()
    {
        var pool = Pool(10);
        var s = Session(pool);
        var user = Guid.NewGuid();
        s.Join("a", user, "Pat", All(pool), Now);
        s.Join("a2", user, "Pat", All(pool), Now);
        var id = s.Deck[0];
        s.Swipe("a", id, true, Now);
        Assert.Empty(s.Matches);
        s.Swipe("a2", id, true, Now);
        Assert.Single(s.Matches);
    }
}
