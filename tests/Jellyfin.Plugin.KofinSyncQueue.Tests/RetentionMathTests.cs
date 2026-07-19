using Jellyfin.Plugin.KofinSyncQueue.Data;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public class RetentionMathTests
{
    [Fact]
    public void DisabledRetentionReportsNoCutoff()
    {
        Assert.Equal(0, RetentionMath.CutoffFor(0, 1_800_000_000));
        Assert.Equal(0, RetentionMath.CutoffFor(-1, 1_800_000_000));
    }

    [Fact]
    public void CutoffIsDaysBack()
    {
        Assert.Equal(
            1_800_000_000 - (90L * 86400),
            RetentionMath.CutoffFor(90, 1_800_000_000));
    }
}
