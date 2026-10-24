using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

public class BrainSnapshotTests {
    [Theory]
    [InlineData("mine")]
    [InlineData("lf-mine")]
    [InlineData("lf-research")]
    public async Task Child_workers_receive_accumulated_replacement_snapshots(string worker) {
        var f = await BrainDataFixture.Create();
        f.ReplaceSnapshots = true;
        f.Calculate = (m, a) => m.Name switch {
            "GetNextMineToBuild" => Buildables.Null,
            "CalcNextDaysOfInvestmentReturn" => 1f,
            "GetNextLFBuildingToBuild" => LFBuildables.ResidentialSector,
            _ => null
        };

        await f.Run(worker);

        var child = Assert.Single(f.Children);
        Assert.NotSame(f.Data.celestials[0], child);
        if (worker == "lf-research") {
            Assert.Equal(9, child.LFBuildings.ResidentialSector);
            Assert.Equal(1, child.LFTechs.IntergalacticEnvoys);
        } else {
            Assert.Equal(27, child.Buildings.CrystalMine);
            Assert.Equal(9, child.LFBuildings.ResidentialSector);
        }
        if (worker == "mine") {
            Assert.Equal(1f, child.LFBonuses.GetShipCargoBonus(Buildables.SmallCargo));
            Assert.Equal(UpdateTypes.Fast, f.Updates[3].Type);
            Assert.Equal(27, f.Updates[3].Input.Buildings.CrystalMine);
            Assert.Equal(9, f.Updates[3].Input.LFBuildings.ResidentialSector);
        } else {
            Assert.Equal(1701, child.Resources.Metal);
            if (worker == "lf-mine") Assert.Equal(1234, child.ResourcesProduction.Metal.CurrentProduction);
        }
    }

    [Fact]
    public async Task Research_priority_calculations_keep_all_planet_refreshes() {
        var f = await BrainDataFixture.Create();
        f.ReplaceSnapshots = true;
        f.Settings.Brain.AutoResearch.PrioritizePlasmaTechnology = true;
        List<Planet> planned = null;
        // Capture the actual calculation argument, including the last refresh's returned object.
        f.Calculate = (m, a) => {
            if (m.Name == "CalcNextPlasmaTechDOIR") { planned = (List<Planet>)a[0]; return 1f; }
            if (m.Name == "CalcNextAstroDOIR") return 1f;
            if (m.Name == "GetNextResearchToBuild") return Buildables.Null;
            return null;
        };

        await f.Run("research");

        var planet = Assert.Single(planned);
        Assert.Equal(17, planet.Facilities.ResearchLab);
        Assert.Equal(27, planet.Buildings.CrystalMine);
        Assert.Equal(1f, planet.LFBonuses.GetShipCargoBonus(Buildables.SmallCargo));
    }

    [Fact]
    public async Task Lifeform_limits_are_specific_to_each_planets_lifeform() {
        var f = await BrainDataFixture.Create();
        f.Data.celestials.Add(BrainDataFixture.Planet(102, 6));
        f.Data.celestials[1].LFtype = LFTypes.Mechas;
        f.Settings.Brain.LifeformAutoMine.MaxBasePopulationBuilding = 42;
        var limits = new List<LFBuildings>();
        f.Calculate = (m, a) => {
            if (m.Name != "GetNextLFBuildingToBuild") return null;
            limits.Add((LFBuildings)a[1]);
            return LFBuildables.None;
        };

        await f.Run("lf-mine");

        Assert.Equal(2, limits.Count);
        Assert.Equal(42, limits[0].ResidentialSector);
        Assert.Equal(0, limits[0].AssemblyLine);
        Assert.Equal(0, limits[1].ResidentialSector);
        Assert.Equal(42, limits[1].AssemblyLine);
        Assert.NotSame(limits[0], limits[1]);
    }

    [Fact]
    public async Task Lifeform_research_refresh_passes_prior_replacement_to_the_next_bridge_update() {
        var f = await BrainDataFixture.Create();
        f.ReplaceSnapshots = true;
        f.Calculate = (m, a) => m.Name == "GetNextLFTechToBuild" ? LFTechno.None : null;

        await f.Run("lf-research-celestial");

        Assert.Equal(UpdateTypes.Facilities, f.Updates[0].Type);
        Assert.Equal(UpdateTypes.Buildings, f.Updates[1].Type);
        Assert.Equal(17, f.Updates[1].Input.Facilities.ResearchLab);
        Assert.Equal(UpdateTypes.LFBonuses, f.Updates[2].Type);
        Assert.Equal(17, f.Updates[2].Input.Facilities.ResearchLab);
        Assert.Equal(27, f.Updates[2].Input.Buildings.CrystalMine);
    }
}
