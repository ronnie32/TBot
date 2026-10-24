using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

public class BrainShipmentTests {
    public static IEnumerable<object[]> CargoCases() {
        foreach (string worker in new[] { "research", "mine-celestial", "lf-mine-celestial", "lf-research-celestial" }) {
            // Small cargos hold 4,950 with no LF bonus, and 9,950 with a 100% bonus.
            yield return new object[] { worker, Buildables.SmallCargo, 0, 1f, 0f, 2L };
            yield return new object[] { worker, Buildables.SmallCargo, 0, 0f, 1f, 3L };
            // Configured probes hold 990 / 1,990 and must use the server's cargo setting.
            yield return new object[] { worker, Buildables.EspionageProbe, 1000, 1f, 0f, 6L };
            yield return new object[] { worker, Buildables.EspionageProbe, 1000, 0f, 1f, 12L };
            // Preserve the planner's rejection when probes cannot carry resources.
            yield return new object[] { worker, Buildables.EspionageProbe, 0, 1f, 0f, 0L };
        }
    }

    [Theory]
    [MemberData(nameof(CargoCases))]
    public async Task Multiple_origin_shipments_use_the_sending_planets_cargo_bonus(
        string worker, Buildables ship, int probeCargo, float senderBonus, float destinationBonus, long expectedShips) {
        var f = await BrainDataFixture.Create();
        var destination = f.Data.celestials[0];
        destination.LFBonuses = BrainDataFixture.CargoBonus(ship, destinationBonus);
        var origin = BrainDataFixture.Planet(102, 6);
        origin.Resources = new Resources { Metal = 11000 };
        origin.Ships.Add(ship, 30);
        origin.LFBonuses = BrainDataFixture.CargoBonus(ship, senderBonus);
        f.Data.celestials.Add(origin);
        f.Data.serverData.ProbeCargo = probeCargo;
        f.Settings.Brain.Transports.Active = true;
        f.Settings.Brain.Transports.CargoType = ship.ToString();
        f.Settings.Brain.Transports.MaxSlots = 10;
        f.Settings.Brain.Transports.RoundResources = false;
        f.Settings.Brain.Transports.SendToTheMoonIfPossible = false;
        f.Settings.Brain.Transports.CheckMoonOrPlanetFirst = false;
        f.Settings.Brain.Transports.DeutToLeaveOnMoons = 0;
        f.Settings.Brain.Transports.MultipleOrigins.Active = true;
        f.Settings.Brain.Transports.MultipleOrigins.OnlyFromMoons = false;
        f.Settings.Brain.Transports.MultipleOrigins.MinimumResourcesToSend = 0;
        f.Settings.Brain.AutoMine.Transports.Active = true;
        f.Settings.Brain.AutoResearch.Transports.Active = true;
        f.Settings.Brain.LifeformAutoMine.Transports.Active = true;
        f.Settings.Brain.LifeformAutoResearch.Transports.Active = true;
        f.Settings.General.SlotsToLeaveFree = 0;
        f.Settings.Expeditions.Active = false;
        f.Settings.AutoFarm.Active = false;
        f.Settings.AutoColonize.Active = false;
        f.Settings.AutoHarvest.Active = false;
        // Hold construction selection/cost fixed; use the real shipment planner and cargo math.
        f.Calculate = (m, a) => m.Name switch {
            "GetNextResearchToBuild" => Buildables.EnergyTechnology,
            "GetNextBuildingToBuild" => Buildables.MetalMine,
            "GetNextLFBuildingToBuild" => LFBuildables.ResidentialSector,
            "GetNextLFTechToBuild" => LFTechno.IntergalacticEnvoys,
            "GetLessExpensiveLFTechToBuild" => LFTechno.None,
            "CalcPrice" => new Resources { Metal = 11000 },
            _ => null
        };

        await f.Run(worker);

        if (expectedShips == 0) {
            Assert.Empty(f.Sends);
            return;
        }
        var send = Assert.Single(f.Sends);
        Assert.Same(origin, send.Origin);
        Assert.Equal(11000, send.Resources.Metal);
        Assert.Equal(expectedShips, send.Ships.GetAmount(ship));
        Assert.True(destination.Coordinate.IsSame(send.Destination));
    }
}
