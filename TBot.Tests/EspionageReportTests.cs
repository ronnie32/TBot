using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

public class EspionageReportTests {
    public static IEnumerable<object[]> InvalidThreatFields() => Threats
        .Concat(new[] { "SolarSatellite", "ColonyShip", "EspionageProbe", "Crawler", "AntiBallisticMissiles", "InterplanetaryMissiles" })
        .Select(field => new object[] { field });

    [Theory]
    [MemberData(nameof(InvalidThreatFields))]
    public void AutoFarm_policy_never_converts_a_negative_count_into_an_eligible_attack(string field) {
        var report = new Newtonsoft.Json.Linq.JObject { [field] = -1 }.ToObject<EspionageReport>();
        report.HasFleetInformation = true;
        report.HasDefensesInformation = true;
        report.Metal = 1000000;
        var decision = TBot.Model.AutoFarmReportPolicy.Evaluate(report, 0, "", default, true, true, true, 0, 0);
        Assert.Equal(TBot.Model.AutoFarmReportDecisionKind.NotSuitable, decision.Kind);
    }

    private static readonly string[] Threats = {
        "LightFighter", "HeavyFighter", "Cruiser", "Battleship", "Battlecruiser",
        "Bomber", "Destroyer", "Deathstar", "SmallCargo", "LargeCargo", "Recycler",
        "Reaper", "Pathfinder", "RocketLauncher", "LightLaser", "HeavyLaser",
        "GaussCannon", "IonCannon", "PlasmaTurret", "SmallShieldDome", "LargeShieldDome"
    };

    public static IEnumerable<object[]> Counts() {
        foreach (string field in Threats)
            foreach (bool probes in new[] { false, true }) {
                yield return new object[] { field, null, probes, true };
                yield return new object[] { field, 0L, probes, true };
                yield return new object[] { field, 1L, probes, false };
                yield return new object[] { field, -1L, probes, false };
            }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void Complete_report_accepts_only_absent_or_zero_threats(string field, long? count, bool probes, bool expected) {
        var report = new EspionageReport { HasFleetInformation = true, HasDefensesInformation = true };
        typeof(EspionageReport).GetProperty(field).SetValue(report, count);

        Assert.Equal(expected, report.IsDefenceless(probes));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Missing_information_remains_unsafe_even_with_explicit_zero_counts(bool fleet, bool defence) {
        var report = new EspionageReport { HasFleetInformation = fleet, HasDefensesInformation = defence };
        foreach (string field in Threats)
            typeof(EspionageReport).GetProperty(field).SetValue(report, 0L);

        Assert.False(report.IsDefenceless());
        Assert.False(report.IsDefenceless(true));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void Probes_require_absent_or_zero_satellites(int? satellites, bool safeForProbes) {
        var report = new EspionageReport {
            HasFleetInformation = true, HasDefensesInformation = true, SolarSatellite = satellites
        };

        Assert.Equal(safeForProbes, report.IsDefenceless(true));
        Assert.True(report.IsDefenceless());
    }
}
