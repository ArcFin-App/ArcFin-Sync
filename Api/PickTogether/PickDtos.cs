using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ArcfinSync.Api.PickTogether;

public class PickAvailabilityDto
{
    public bool Enabled { get; set; }
}

public class PickCreateBody
{
    public bool Movies { get; set; } = true;

    public bool Shows { get; set; }

    public List<string>? Genres { get; set; }

    public bool IncludeWatched { get; set; }

    /// <summary>TopRated (default), Random or RecentlyAdded.</summary>
    public string? Order { get; set; }

    /// <summary>Everyone (default) or Most.</summary>
    public string? Rule { get; set; }

    /// <summary>Everyone sees the titles in the same order. Off by default: each person gets their own.</summary>
    public bool SameOrder { get; set; }
}

public class PickLockBody
{
    public bool Locked { get; set; }
}

public class PickSwipeBody
{
    public Guid ItemId { get; set; }

    public bool Like { get; set; }
}

public class PickOptionsDto
{
    public bool Movies { get; set; }

    public bool Shows { get; set; }

    public List<string> Genres { get; set; } = new();

    public bool IncludeWatched { get; set; }

    public string Order { get; set; } = string.Empty;

    public string Rule { get; set; } = string.Empty;

    public bool SameOrder { get; set; }
}

public class PickPersonDto
{
    /// <summary>Only for the host to remove this person; it says nothing about their device.</summary>
    public string Id { get; set; } = string.Empty;

    public bool IsHost { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsMe { get; set; }

    public bool IsAway { get; set; }

    public int Swiped { get; set; }

    public int Total { get; set; }
}

public class PickCardDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public int? Year { get; set; }

    public float? Rating { get; set; }

    public string? OfficialRating { get; set; }

    public string Overview { get; set; } = string.Empty;

    public int? RuntimeMinutes { get; set; }

    public List<string> Genres { get; set; } = new();
}

public class PickMatchDto
{
    public PickCardDto Item { get; set; } = new();

    public int Round { get; set; }

    public DateTime At { get; set; }
}

public class PickTogetherBody
{
    public Guid ItemId { get; set; }
}

public class PickTogetherGroupBody
{
    public string GroupId { get; set; } = string.Empty;

    public Guid PlayItemId { get; set; }

    public long StartTicks { get; set; }
}

/// <summary>Watching a match together over SyncPlay, as the caller sees it.</summary>
public class PickTogetherDto
{
    public Guid ItemId { get; set; }

    public bool IAmIn { get; set; }

    public bool IAmLeader { get; set; }

    /// <summary>Everyone here is in: the leader sets up the group.</summary>
    public bool Ready { get; set; }

    /// <summary>People here who haven't said yes yet.</summary>
    public List<string> Waiting { get; set; } = new();

    public string? GroupId { get; set; }

    public Guid? PlayItemId { get; set; }

    public long StartTicks { get; set; }
}

public class PickSessionDto
{
    public string Code { get; set; } = string.Empty;

    public int Round { get; set; }

    public bool IsHost { get; set; }

    /// <summary>No one new can join, even with the code.</summary>
    public bool Locked { get; set; }

    public PickOptionsDto Options { get; set; } = new();

    public List<PickPersonDto> People { get; set; } = new();

    public List<PickCardDto> Cards { get; set; } = new();

    public int Remaining { get; set; }

    public List<PickMatchDto> Matches { get; set; } = new();

    public PickTogetherDto? Together { get; set; }
}
