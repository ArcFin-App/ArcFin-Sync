using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ArcfinSync.Api.Integrations;

/// <summary>
/// The Seerr calls the server makes for the apps, and nothing else: browsing, details, requests
/// and the watchlist. Settings, user management and sign-in are never passed through. Seerr
/// still checks each call against the person's own Seerr permissions, since every call is made
/// as them.
/// </summary>
public static class SeerrRoutes
{
    private static readonly Regex[] Reads =
    {
        Rx(@"discover(/[a-z]+)*(/\d+)?(/[a-z]+)?"),
        Rx(@"discover/(movies|tv)/language/[a-zA-Z-]{2,10}"),
        Rx(@"discover/keyword/\d+/movies"),
        Rx(@"search"),
        Rx(@"(movie|tv)/\d+(/(recommendations|similar|ratings|ratingscombined))?"),
        Rx(@"tv/\d+/season/\d+"),
        Rx(@"person/\d+(/combined_credits)?"),
        Rx(@"collection/\d+"),
        Rx(@"watchproviders/(regions|movies|tv)"),
        Rx(@"genres/(movie|tv)"),
        Rx(@"settings/public"),
        Rx(@"auth/me"),
        Rx(@"user/\d+/(quota|watchlist|requests)"),
        Rx(@"request(/count)?"),
        Rx(@"request/\d+"),
    };

    private static readonly Regex[] Creates =
    {
        Rx(@"request"),
        Rx(@"request/\d+/(approve|decline|retry)"),
        Rx(@"watchlist"),
    };

    private static readonly Regex[] Removes =
    {
        Rx(@"request/\d+"),
        Rx(@"watchlist/\d+"),
    };

    /// <summary>Whether <paramref name="path"/> (after "api/v1/") may be sent with <paramref name="method"/>.</summary>
    public static bool IsAllowed(string method, string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains("..", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal)
            || path.Contains('%', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var list = method.ToUpperInvariant() switch
        {
            "GET" => Reads,
            "POST" => Creates,
            "DELETE" => Removes,
            _ => Array.Empty<Regex>()
        };
        return list.Any(r => r.IsMatch(path));
    }

    /// <summary>A new request: the one call kept and sent again later when Seerr can't be reached.</summary>
    public static bool IsNewRequest(string method, string path) => method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path == "request";

    private static Regex Rx(string pattern) => new("^" + pattern + "$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
}
