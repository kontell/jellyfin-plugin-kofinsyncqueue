using Jellyfin.Plugin.KofinSyncQueue.Data;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public class TypesFilterTests
{
    [Fact]
    public void AbsentMeansAll()
    {
        Assert.Null(TypesFilter.Parse(null, out _));
        Assert.Null(TypesFilter.Parse("", out _));
        Assert.Null(TypesFilter.Parse("   ", out _));
    }

    [Fact]
    public void KnownTokensParse()
    {
        var include = TypesFilter.Parse("movies, tvshows,BOXSETS", out var unknown);

        Assert.NotNull(include);
        Assert.Equal(3, include!.Count);
        Assert.Contains("boxsets", include);
        Assert.Empty(unknown);
    }

    [Fact]
    public void UnknownTokensAreReportedNeverGuessed()
    {
        var include = TypesFilter.Parse("movies,playlist,", out var unknown);

        Assert.NotNull(include);
        Assert.Single(include!);
        Assert.Contains("movies", include);
        Assert.Single(unknown);
        Assert.Contains("playlist", unknown);
    }

    [Fact]
    public void OnlyUnknownTokensYieldEmptyIncludeNotAll()
    {
        // The legacy foot-gun: an unparseable filter must not become
        // "movies" — an empty include set filters everything out instead.
        var include = TypesFilter.Parse("garbage", out var unknown);

        Assert.NotNull(include);
        Assert.Empty(include!);
        Assert.Single(unknown);
    }
}
