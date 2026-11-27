using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

public sealed class ArcsAvailabilityDto
{
    public bool Enabled { get; set; }

    public string Sharing { get; set; } = string.Empty;

    public string Watermark { get; set; } = string.Empty;

    public int MinSeconds { get; set; }

    public int MaxSeconds { get; set; }

    public int DefaultSeconds { get; set; }

    public string Spoilers { get; set; } = string.Empty;

    public bool ClipOfTheDay { get; set; }

    /// <summary>Everyone or Off.</summary>
    public string Sending { get; set; } = string.Empty;

    /// <summary>Whether dialogue search is on.</summary>
    public bool DialogueSearch { get; set; }

    /// <summary>Whether request clips can come into the feed for this person (admin on, Seerr set up).</summary>
    public bool Requests { get; set; }

    /// <summary>Whether request clips may play YouTube trailers (the admin's switch).</summary>
    public bool YouTubeTrailers { get; set; }

    /// <summary>Scenes sent to this person they haven't watched yet, that their spoiler setting allows.</summary>
    public int Unseen { get; set; }
}

public sealed class ArcFeedDto
{
    public List<ArcClipDto> Clips { get; set; } = new();

    public string? Cursor { get; set; }
}

public sealed class ArcClipDto
{
    public string Id { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string? MediaSourceId { get; set; }

    /// <summary>Movie or Episode.</summary>
    public string Type { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string? ChapterName { get; set; }

    public string? Overview { get; set; }

    public int? Year { get; set; }

    public string? OfficialRating { get; set; }

    public float? CommunityRating { get; set; }

    public List<string> Genres { get; set; } = new();

    public long StartTicks { get; set; }

    public long EndTicks { get; set; }

    public long RuntimeTicks { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public List<ArcSubtitleDto> Subtitles { get; set; } = new();

    public ArcReasonDto Reason { get; set; } = new();

    public bool Liked { get; set; }

    public bool Disliked { get; set; }

    public bool IsFavorite { get; set; }

    public int LikeCount { get; set; }

    /// <summary>scene, or request for a title not on the server.</summary>
    public string Kind { get; set; } = ArcClipKinds.Scene;

    public bool IsColdOpen { get; set; }

    public string? MyReaction { get; set; }

    /// <summary>Always funny, wow and sad; a count is 0 unless at least two people chose it.</summary>
    public Dictionary<string, int> Reactions { get; set; } = ArcReactionKinds.Empty();

    public string? MatchedLine { get; set; }

    public long? MatchedTicks { get; set; }

    public string? SentBy { get; set; }

    public string? SentNote { get; set; }

    public int? TmdbId { get; set; }

    /// <summary>movie or tv.</summary>
    public string? MediaType { get; set; }

    public string? TrailerYouTubeKey { get; set; }

    public string? PosterPath { get; set; }

    public string? BackdropPath { get; set; }
}

public static class ArcClipKinds
{
    public const string Scene = "scene";
    public const string Request = "request";
}

public static class ArcReactionKinds
{
    public const string Funny = "funny";
    public const string Wow = "wow";
    public const string Sad = "sad";
    public const string None = "none";

    public static readonly string[] All = { Funny, Wow, Sad };

    public static Dictionary<string, int> Empty() => new(StringComparer.Ordinal) { [Funny] = 0, [Wow] = 0, [Sad] = 0 };
}

public sealed class ArcSubtitleDto
{
    public int Index { get; set; }

    public string? Language { get; set; }

    public string? Title { get; set; }

    public bool IsDefault { get; set; }
}

public sealed class ArcReasonDto
{
    public string Kind { get; set; } = ArcReasons.Top;

    public string? SeedName { get; set; }
}

public sealed class ArcEventsBody
{
    public List<ArcEventDto>? Events { get; set; }
}

public sealed class ArcEventDto
{
    public string? ClipId { get; set; }

    public string? ItemId { get; set; }

    public string? SeriesId { get; set; }

    public string? Kind { get; set; }

    public double? WatchedSeconds { get; set; }

    public double? ClipSeconds { get; set; }

    public string? Feed { get; set; }

    public DateTime? At { get; set; }

    /// <summary>For react: funny, wow, sad, or none to clear.</summary>
    public string? Reaction { get; set; }
}

public sealed class ArcHiddenListDto
{
    public List<ArcHiddenItemDto> Items { get; set; } = new();
}

public sealed class ArcHiddenItemDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>hide, fewer or notNow.</summary>
    public string Kind { get; set; } = string.Empty;

    public DateTime? Until { get; set; }
}

public sealed class ArcsStatsDto
{
    /// <summary>Different clips people have seen.</summary>
    public int Clips { get; set; }

    public int IndexedTitles { get; set; }

    public double CacheMb { get; set; }

    public int Likes { get; set; }

    public int Shares { get; set; }

    public int WatchFromHere { get; set; }

    public List<ArcClipDto> TopLiked { get; set; } = new();

    public List<ArcClipDto> TopWatchFromHere { get; set; } = new();

    public int DialogueTitles { get; set; }
}

public sealed class ArcSearchDto
{
    public List<ArcClipDto> Clips { get; set; } = new();
}

public sealed class ArcPeopleDto
{
    public List<ArcPersonDto> People { get; set; } = new();
}

public sealed class ArcPersonDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class ArcFriendsDto
{
    public string Code { get; set; } = string.Empty;

    public List<ArcPersonDto> Friends { get; set; } = new();
}

public sealed class ArcFriendCodeBody
{
    public string? Code { get; set; }
}

public sealed class ArcFriendCodeDto
{
    public string Code { get; set; } = string.Empty;
}

public sealed class ArcSendBody
{
    public string? ClipId { get; set; }

    public string? ToUserId { get; set; }

    public string? Note { get; set; }
}

public sealed class ArcMyStatsDto
{
    public int Scenes { get; set; }

    public int Minutes { get; set; }

    public int Likes { get; set; }

    public int Shares { get; set; }

    public int WatchFromHere { get; set; }

    public List<ArcTopTitleDto> TopTitles { get; set; } = new();

    public ArcClipDto? MostRewatched { get; set; }

    public string? TopReaction { get; set; }

    public DateTime? Since { get; set; }
}

public sealed class ArcTopTitleDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Scenes { get; set; }
}
