using TBot.Model;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Xunit;

namespace TBot.Tests {
	public class AutoFarmAttackCompositionTests {
		[Fact]
		public void DefaultCargoOnlyModeDoesNotAddEscortShips() {
			var report = new EspionageReport {
				HasDefensesInformation = true,
				HasFleetInformation = true,
				RocketLauncher = 1000,
				Reaper = 100
			};

			var ships = AutoFarmAttackComposition.Build(
				25, Buildables.LargeCargo, report,
				attackDefendedTargets: false,
				attackTargetsWithFleet: false,
				ignoreMissiles: true,
				bombersPer100kDefense: 4,
				reapersPer100kFleet: 2);

			Assert.Equal(25, ships.LargeCargo);
			Assert.Equal(0, ships.Bomber);
			Assert.Equal(0, ships.Reaper);
		}

		[Fact]
		public void EscortCountsRoundUpPerOneHundredThousandPoints() {
			var report = new EspionageReport {
				HasDefensesInformation = true,
				HasFleetInformation = true,
				RocketLauncher = 50,
				Reaper = 1
			};

			var oneStep = AutoFarmAttackComposition.Build(
				25, Buildables.LargeCargo, report, true, true, true, 4, 2);
			Assert.Equal(4, oneStep.Bomber);
			Assert.Equal(4, oneStep.Reaper);

				report.RocketLauncher = 100;
			var twoSteps = AutoFarmAttackComposition.Build(
				25, Buildables.LargeCargo, report, true, false, true, 4, 2);
			Assert.Equal(8, twoSteps.Bomber);
			Assert.Equal(0, twoSteps.Reaper);
		}

		[Fact]
		public void IgnoredMissilesDoNotCreateACombativeEscortRequirement() {
			var report = new EspionageReport {
				HasDefensesInformation = true,
				HasFleetInformation = true,
				InterplanetaryMissiles = 100000
			};

			var ships = AutoFarmAttackComposition.Build(
				10, Buildables.SmallCargo, report, true, false, true, 4, 2);

			Assert.Equal(0, ships.Bomber);
			Assert.Equal(10, ships.SmallCargo);
		}

		[Fact]
		public void TenMillionDefensePointsRequestFourHundredBombers() {
			var report = new EspionageReport {
				HasDefensesInformation = true,
				RocketLauncher = 5_000
			};

			Assert.Equal(10_000_000, report.GetDefenseResourceValue(includeMissiles: true));

			var ships = AutoFarmAttackComposition.Build(
				25, Buildables.LargeCargo, report,
				attackDefendedTargets: true,
				attackTargetsWithFleet: false,
				ignoreMissiles: true,
				bombersPer100kDefense: 4,
				reapersPer100kFleet: 2);

			Assert.Equal(400, ships.Bomber);
		}

		[Fact]
		public void ThirtySevenMillionFleetValueRequestsSevenHundredFortyReapers() {
			var report = new EspionageReport {
				HasFleetInformation = true,
				HasDefensesInformation = true,
				LargeCargo = 3000,
				LightFighter = 250
			};

			Assert.Equal(37_000_000, report.GetFleetResourceValue());
			var ships = AutoFarmAttackComposition.Build(
				25, Buildables.SmallCargo, report, true, true, true, 4, 2);

			Assert.Equal(740, ships.Reaper);
			Assert.Equal(0, ships.Bomber);
			Assert.Equal(25, ships.SmallCargo);
		}
	}
}
