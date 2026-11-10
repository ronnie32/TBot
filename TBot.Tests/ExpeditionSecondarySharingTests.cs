using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace TBot.Tests {
	public partial class ExpeditionsWorkerTests {
		[Theory]
		[InlineData(24, 8, 8, 8)]
		[InlineData(30, 8, 8, 8)]
		[InlineData(10, 4, 3, 3)]
		[InlineData(8, 3, 3, 2)]
		[InlineData(2, 1, 1, 0)]
		[InlineData(0, 0, 0, 0)]
		public async Task SecondaryShipsAreSharedAndCappedByActualPrimaryCount(long available, long first, long second, long third) {
			var scenario = SecondaryScenario(available);
			await Execute(scenario.Worker);
			Assert.Equal(new long[] { 4, 4, 4 }, scenario.Sent.Select(f => f.Ships.LargeCargo));
			Assert.Equal(new[] { first, second, third }, scenario.Sent.Select(f => f.Ships.SmallCargo));
		}

		[Theory]
		[InlineData(0, 0)]
		[InlineData(0.5, 2)]
		[InlineData(0.3, 1)]
		public async Task SecondaryRatioUsesFloorOfTheActualPrimaryCount(double ratio, long expected) {
			var scenario = SecondaryScenario(24);
			scenario.Settings.Expeditions.SecondaryToPrimaryRatio = ratio;
			await Execute(scenario.Worker);
			Assert.Equal(3, scenario.Sent.Count);
			Assert.All(scenario.Sent, fleet => Assert.Equal(expected, fleet.Ships.SmallCargo));
		}

		[Fact]
		public async Task SecondaryMinimumPreventsAnyDispatchWhenTheSharedAmountIsTooSmall() {
			var scenario = SecondaryScenario(3);
			scenario.Settings.Expeditions.MinSecondaryToSend = 2;
			await Execute(scenario.Worker);
			Assert.Empty(scenario.Sent);
			Assert.Equal(12, scenario.Stock[1].LargeCargo);
			Assert.Equal(3, scenario.Stock[1].SmallCargo);
		}

		[Fact]
		public async Task SecondaryMinimumDoesNotOverrideTheRatioLimit() {
			var scenario = SecondaryScenario(24);
			scenario.Settings.Expeditions.SecondaryToPrimaryRatio = 0;
			scenario.Settings.Expeditions.MinSecondaryToSend = 1;
			await Execute(scenario.Worker);
			Assert.Empty(scenario.Sent);
		}

		[Fact]
		public async Task OriginBelowSecondaryMinimumLeavesItsOpportunityForAnotherOrigin() {
			var scenario = new PrimarySharingScenario(new long[] { 12, 12 }, 3);
			ConfigureSecondary(scenario);
			scenario.Settings.Expeditions.MinSecondaryToSend = 2;
			scenario.Stock[2].SmallCargo = 20;
			await Execute(scenario.Worker);
			Assert.Equal(new[] { 2, 2, 2 }, scenario.Sent.Select(f => f.Origin.Position));
			Assert.Equal(new long[] { 4, 4, 4 }, scenario.Sent.Select(f => f.Ships.LargeCargo));
			Assert.Equal(new long[] { 7, 7, 6 }, scenario.Sent.Select(f => f.Ships.SmallCargo));
			Assert.Equal(1, scenario.Reads[1]);
		}

		[Fact]
		public async Task SecondarySharingKeepsOriginFairnessAndUsesLocalInventory() {
			var scenario = new PrimarySharingScenario(new long[] { 12, 6 }, 3);
			ConfigureSecondary(scenario);
			scenario.Stock[1].SmallCargo = 12;
			scenario.Stock[2].SmallCargo = 6;
			await Execute(scenario.Worker);
			Assert.Equal(new[] { 1, 2, 1 }, scenario.Sent.Select(f => f.Origin.Position));
			Assert.Equal(new long[] { 6, 6, 6 }, scenario.Sent.Select(f => f.Ships.SmallCargo));
		}

		[Fact]
		public async Task SecondarySharingUsesPrimaryAfterReserveWithoutMutatingSnapshots() {
			var scenario = new PrimarySharingScenario(new long[] { 15 }, 3);
			ConfigureSecondary(scenario);
			scenario.Settings.Expeditions.PrimaryToKeep = 3;
			scenario.Stock[1].SmallCargo = 30;
			await Execute(scenario.Worker);
			Assert.All(scenario.Sent, fleet => { Assert.Equal(4, fleet.Ships.LargeCargo); Assert.Equal(8, fleet.Ships.SmallCargo); });
			Assert.Equal(new long[] { 30, 22, 14 }, scenario.ReadSnapshots.Select(s => s.SmallCargo));
			Assert.Equal(3, scenario.Stock[1].LargeCargo);
		}

		[Fact]
		public async Task SecondarySharingRecalculatesWhenAvailableSlotsShrink() {
			var scenario = SecondaryScenario(12);
			scenario.SlotsAfterFirstSend = 2;
			await Execute(scenario.Worker);
			Assert.Equal(new long[] { 4, 8 }, scenario.Sent.Select(f => f.Ships.SmallCargo));
		}

		[Theory]
		[InlineData(0, 3)]
		[InlineData(1, 0)]
		public async Task ZeroPrimaryOnlyAllowsEscortFleetsWhenSecondaryMinimumIsZero(long minimum, int sends) {
			var scenario = new PrimarySharingScenario(new long[] { 0 }, 3);
			ConfigureSecondary(scenario);
			scenario.Settings.Expeditions.MinSecondaryToSend = minimum;
			scenario.Stock[1].SmallCargo = 12;
			await Execute(scenario.Worker);
			Assert.Equal(sends, scenario.Sent.Count);
			Assert.All(scenario.Sent, fleet => { Assert.Equal(0, fleet.Ships.SmallCargo); Assert.Equal(1, fleet.Ships.EspionageProbe); });
		}

		[Fact]
		public async Task SecondaryTypeAlreadySelectedAsEscortCannotOverdrawThatType() {
			var scenario = SecondaryScenario(0);
			scenario.Settings.Expeditions.SecondaryShip = "HeavyFighter";
			scenario.Stock[1].HeavyFighter = 1;
			await Execute(scenario.Worker);
			Assert.Equal(3, scenario.Sent.Count);
			Assert.Equal(new long[] { 1, 0, 0 }, scenario.Sent.Select(f => f.Ships.HeavyFighter));
		}

		[Theory]
		[InlineData("Null")]
		[InlineData(null)]
		public async Task DisabledSecondarySettingsKeepPrimarySharing(string type) {
			var scenario = SecondaryScenario(24);
			scenario.Settings.Expeditions.SecondaryShip = type;
			await Execute(scenario.Worker);
			Assert.Equal(new long[] { 4, 4, 4 }, scenario.Sent.Select(f => f.Ships.LargeCargo));
			Assert.All(scenario.Sent, fleet => Assert.Equal(0, fleet.Ships.SmallCargo));
		}

		[Theory]
		[InlineData("LargeCargo")]
		[InlineData("SolarSatellite")]
		[InlineData("Crawler")]
		[InlineData("MetalMine")]
		[InlineData("999")]
		[InlineData("not-a-ship")]
		public async Task InvalidSecondaryTypeCannotDispatchOrConsumePrimaryReserve(string type) {
			var scenario = SecondaryScenario(24);
			scenario.Settings.Expeditions.SecondaryShip = type;
			scenario.Settings.Expeditions.PrimaryToKeep = 3;
			await Execute(scenario.Worker);
			Assert.Empty(scenario.Sent);
			Assert.Equal(12, scenario.Stock[1].LargeCargo);
		}

		[Theory]
		[InlineData(-1, 0)]
		[InlineData(2, -1)]
		public async Task NegativeSecondaryRatioOrMinimumCannotDispatch(double ratio, long minimum) {
			var scenario = SecondaryScenario(24);
			scenario.Settings.Expeditions.SecondaryToPrimaryRatio = ratio;
			scenario.Settings.Expeditions.MinSecondaryToSend = minimum;
			await Execute(scenario.Worker);
			Assert.Empty(scenario.Sent);
		}

		[Fact]
		public async Task LargeSecondaryRatioAndInventoryDoNotOverflowOrOverdraw() {
			var scenario = SecondaryScenario(long.MaxValue);
			scenario.Settings.Expeditions.SecondaryToPrimaryRatio = decimal.MaxValue;
			await Execute(scenario.Worker);
			Assert.Equal(3, scenario.Sent.Count);
			Assert.Equal(long.MaxValue, scenario.Sent.Sum(f => f.Ships.SmallCargo));
			Assert.Equal(0, scenario.Stock[1].SmallCargo);
		}

		[Fact]
		public async Task ManualCompositionBypassesAutomaticSecondarySettings() {
			var scenario = SecondaryScenario(24);
			scenario.Settings.Expeditions.ManualShips.Active = true;
			scenario.Settings.Expeditions.ManualShips.Ships.LargeCargo = 2;
			scenario.Settings.Expeditions.ManualShips.Ships.SmallCargo = 1;
			scenario.Settings.Expeditions.MinSecondaryToSend = 100;
			await Execute(scenario.Worker);
			Assert.Equal(3, scenario.Sent.Count);
			Assert.All(scenario.Sent, fleet => { Assert.Equal(2, fleet.Ships.LargeCargo); Assert.Equal(1, fleet.Ships.SmallCargo); });
		}

		[Fact]
		public async Task SecondaryTypeAloneUsesThePublicRatioAndMinimumDefaults() {
			var scenario = new PrimarySharingScenario(new long[] { 12 }, 3);
			scenario.Settings.Expeditions.SecondaryShip = "smallcargo";
			scenario.Stock[1].SmallCargo = 24;
			await Execute(scenario.Worker);
			Assert.Equal(3, scenario.Sent.Count);
			Assert.All(scenario.Sent, fleet => Assert.Equal(8, fleet.Ships.SmallCargo));
		}

		[Fact]
		public async Task SecondarySharingRespectsOriginCaps() {
			var scenario = new PrimarySharingScenario(new long[] { 12, 12 }, 5, maxPerOrigin: 2);
			ConfigureSecondary(scenario);
			scenario.Stock[1].SmallCargo = scenario.Stock[2].SmallCargo = 12;
			await Execute(scenario.Worker);
			Assert.Equal(new[] { 1, 2, 1, 2 }, scenario.Sent.Select(f => f.Origin.Position));
			Assert.All(scenario.Sent, fleet => Assert.Equal(6, fleet.Ships.SmallCargo));
		}

		[Fact]
		public async Task ConfirmedFailureRebalancesOnlyRemainingSecondaryShips() {
			var scenario = new PrimarySharingScenario(new long[] { 12, 12 }, 3);
			ConfigureSecondary(scenario);
			scenario.Stock[1].SmallCargo = scenario.Stock[2].SmallCargo = 12;
			scenario.RejectedOrigin = 2;
			await Execute(scenario.Worker);
			Assert.Equal(new[] { 1, 1, 1 }, scenario.Sent.Select(f => f.Origin.Position));
			Assert.Equal(new long[] { 6, 3, 3 }, scenario.Sent.Select(f => f.Ships.SmallCargo));
			Assert.Equal(1, scenario.Reads[2]);
		}

		private static PrimarySharingScenario SecondaryScenario(long secondary) {
			var scenario = new PrimarySharingScenario(new long[] { 12 }, 3);
			ConfigureSecondary(scenario);
			scenario.Stock[1].SmallCargo = secondary;
			return scenario;
		}

		private static void ConfigureSecondary(PrimarySharingScenario scenario) {
			scenario.Settings.Expeditions.SecondaryShip = "SmallCargo";
			scenario.Settings.Expeditions.SecondaryToPrimaryRatio = 2;
			scenario.Settings.Expeditions.MinSecondaryToSend = 0;
		}
	}
}
