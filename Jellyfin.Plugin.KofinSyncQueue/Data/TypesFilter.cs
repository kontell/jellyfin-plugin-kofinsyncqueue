using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.KofinSyncQueue.Data;

/// <summary>
/// The ?types= include-list parser. Unknown tokens are ignored — nothing
/// ever silently defaults to a media type (the official plugin's
/// Enum.TryParse("") → Movies foot-gun dies here).
/// </summary>
public static class TypesFilter
{
    /// <summary>All media-type classes the queue records.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        "movies",
        "tvshows",
        "boxsets",
        "musicvideos",
        "music",
    };

    /// <summary>
    /// Parse the include list. Null/empty means "no filtering" and returns
    /// null; otherwise the known tokens, deduped. Unknown tokens are
    /// reported through <paramref name="unknown"/> for the caller to log.
    /// </summary>
    /// <param name="types">The raw query value.</param>
    /// <param name="unknown">Tokens that matched no known class.</param>
    /// <returns>The include set, or null for "all".</returns>
    public static IReadOnlySet<string>? Parse(string? types, out IReadOnlyList<string> unknown)
    {
        if (string.IsNullOrWhiteSpace(types))
        {
            unknown = Array.Empty<string>();
            return null;
        }

        var tokens = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .ToList();

        unknown = tokens.Where(token => !All.Contains(token)).ToList();

        return tokens.Where(All.Contains).ToHashSet();
    }
}
