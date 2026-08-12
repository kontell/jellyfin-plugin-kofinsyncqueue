using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.KofinSyncQueue.Data;

/// <summary>
/// The ?libraries= include-list parser: collection folder ids, the same ones
/// clients whitelist and /Items/{id}/Ancestors reports. Same stance as
/// <see cref="TypesFilter"/> — unparseable tokens are reported, never guessed
/// at, and an all-unparseable list matches nothing rather than everything.
/// </summary>
public static class LibrariesFilter
{
    /// <summary>
    /// Parse the include list. Null/empty means "no filtering" and returns
    /// null; otherwise the parsed ids, deduped. Tokens that are not Guids are
    /// reported through <paramref name="unparsed"/> for the caller to log.
    /// </summary>
    /// <param name="libraries">The raw query value.</param>
    /// <param name="unparsed">Tokens that were not readable as an id.</param>
    /// <returns>The include set, or null for "all".</returns>
    public static IReadOnlySet<Guid>? Parse(string? libraries, out IReadOnlyList<string> unparsed)
    {
        if (string.IsNullOrWhiteSpace(libraries))
        {
            unparsed = Array.Empty<string>();
            return null;
        }

        var tokens = libraries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new HashSet<Guid>();
        var rejected = new List<string>();

        foreach (var token in tokens)
        {
            if (Guid.TryParse(token, out var id) && !id.Equals(default))
            {
                parsed.Add(id);
            }
            else
            {
                rejected.Add(token);
            }
        }

        unparsed = rejected;
        return parsed;
    }

    /// <summary>
    /// Whether a record survives the filter. Absent record libraries mean
    /// unknown — a record stored before the dimension existed, or an item
    /// with no library to name — and unknown is always served, so the
    /// decision falls back to the client rather than being made wrongly here.
    /// </summary>
    /// <param name="recordLibraries">The record's collection folders, if known.</param>
    /// <param name="filter">The parsed include set, or null for "all".</param>
    /// <returns>True when the record should be served.</returns>
    public static bool Matches(IReadOnlyCollection<Guid>? recordLibraries, IReadOnlySet<Guid>? filter)
    {
        if (filter is null || recordLibraries is null || recordLibraries.Count == 0)
        {
            return true;
        }

        return recordLibraries.Any(filter.Contains);
    }
}
