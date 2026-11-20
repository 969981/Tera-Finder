using PKHeX.Core;
using TeraFinder.Core;
using Xunit;

namespace TeraFinder.Tests;

public class EventRaidRecoveryTests
{
    [Fact]
    public void MissingEncounterDataNeverProducesAnEventGroup()
    {
        Assert.Equal((byte)0, EventUtil.GetUniqueMatchingEventGroupID(
            [], 0xDB72E373u, GameVersion.VL, EventProgress.Stage3));
    }

    [Fact]
    public void HistoricalGimmighoulEventResolvesReportedSeedToGroupOne()
    {
        // Only the public event identifier and raid seed are used here; no
        // trainer information or save-file content is committed to the repo.
        LegalityUtil.SetDefaultlegalitySettings();
        var (allDistribution, _) = ResourcesUtil.GetAllEventEncounters();
        var gimmighoulEvent = allDistribution
            .Where(encounter => encounter.Identifier / 100u == 20230622u)
            .ToArray();

        Assert.NotEmpty(gimmighoulEvent);
        Assert.Equal((byte)1, EventUtil.GetUniqueMatchingEventGroupID(
            gimmighoulEvent, 0xDB72E373u, GameVersion.VL, EventProgress.Stage3));
    }
}
