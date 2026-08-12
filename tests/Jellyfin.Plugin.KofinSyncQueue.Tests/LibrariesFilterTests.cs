using System;
using System.Collections.Generic;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public class LibrariesFilterTests
{
    private static readonly Guid Movies = Guid.NewGuid();
    private static readonly Guid Shows = Guid.NewGuid();
    private static readonly Guid Bench = Guid.NewGuid();

    [Fact]
    public void AbsentMeansAll()
    {
        Assert.Null(LibrariesFilter.Parse(null, out _));
        Assert.Null(LibrariesFilter.Parse("", out _));
        Assert.Null(LibrariesFilter.Parse("   ", out _));
    }

    [Fact]
    public void IdsParseInEitherFormat()
    {
        var filter = LibrariesFilter.Parse(
            $"{Movies:N}, {Shows:D}",
            out var unparsed);

        Assert.NotNull(filter);
        Assert.Equal(2, filter!.Count);
        Assert.Contains(Movies, filter);
        Assert.Contains(Shows, filter);
        Assert.Empty(unparsed);
    }

    [Fact]
    public void UnreadableTokensAreReportedNeverGuessed()
    {
        var filter = LibrariesFilter.Parse($"{Movies:N},not-an-id,", out var unparsed);

        Assert.NotNull(filter);
        Assert.Single(filter!);
        Assert.Contains(Movies, filter);
        Assert.Single(unparsed);
        Assert.Contains("not-an-id", unparsed);
    }

    [Fact]
    public void AllZeroesIsNotAnId()
    {
        // Guid.TryParse accepts it happily; it is never a library.
        var filter = LibrariesFilter.Parse(Guid.Empty.ToString("N"), out var unparsed);

        Assert.NotNull(filter);
        Assert.Empty(filter!);
        Assert.Single(unparsed);
    }

    [Fact]
    public void OnlyUnreadableTokensYieldEmptyFilterNotAll()
    {
        // Same stance as TypesFilter: an unparseable filter must not quietly
        // widen back to everything.
        var filter = LibrariesFilter.Parse("garbage", out _);

        Assert.NotNull(filter);
        Assert.Empty(filter!);
        Assert.False(LibrariesFilter.Matches(new[] { Movies }, filter));
    }

    [Fact]
    public void NoFilterServesEverything()
    {
        Assert.True(LibrariesFilter.Matches(new[] { Bench }, null));
        Assert.True(LibrariesFilter.Matches(null, null));
    }

    [Fact]
    public void UnknownLibrariesAreAlwaysServed()
    {
        // Absent means "we do not know", never "belongs to nothing" — a
        // pre-v1.1 record, a boxset, a folder-less artist. Dropping those
        // here would lose real items; the client decides instead.
        var filter = LibrariesFilter.Parse(Movies.ToString("N"), out _);

        Assert.True(LibrariesFilter.Matches(null, filter));
        Assert.True(LibrariesFilter.Matches(Array.Empty<Guid>(), filter));
    }

    [Fact]
    public void KnownLibrariesMustIntersect()
    {
        var filter = LibrariesFilter.Parse($"{Movies:N},{Shows:N}", out _);

        Assert.True(LibrariesFilter.Matches(new[] { Shows }, filter));
        Assert.False(LibrariesFilter.Matches(new[] { Bench }, filter));
    }

    [Fact]
    public void OneMatchingLibraryIsEnough()
    {
        // A path under two libraries belongs to both; syncing either is
        // reason enough to send the record.
        var filter = LibrariesFilter.Parse(Movies.ToString("N"), out _);

        Assert.True(LibrariesFilter.Matches(new List<Guid> { Bench, Movies }, filter));
    }
}
