using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Tbot.Includes;
using Tbot.Workers;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Xunit;

namespace TBot.Tests {
	public partial class ExpeditionsWorkerTests {
		[Theory]
		[InlineData(12, 4, 4, 4)]
		[InlineData(10, 4, 3, 3)]
		[InlineData(2, 1, 1, 0)]
		[InlineData(0, 0, 0, 0)]
		public async Task PrimaryShipsAreSharedAcrossPendingExpeditions(long available, long first, long second, long third) {
			var scenario = new PrimarySharingScenario(new[] { available }, 3);
			await Execute(scenario.Worker);
			Assert.Equal(new[] { first, second, third }, scenario.Sent.Select(f => f.Ships.LargeCargo));
			Assert.Equal(3, scenario.Reads[1]);
		}

		[Fact]
		public async Task PrimaryReserveIsKeptWithoutMutatingTheSharedShipSnapshot() {
			var scenario = new PrimarySharingScenario(new long[] { 15 }, 3);
			scenario.Settings.Expeditions.PrimaryToKeep = 3;
			await Execute(scenario.Worker);
			Assert.Equal(new long[] { 4, 4, 4 }, scenario.Sent.Select(f => f.Ships.LargeCargo));
			Assert.Equal(3, scenario.Stock[1].LargeCargo);
			Assert.Equal(new long[] { 15, 11, 7 }, scenario.ReadSnapshots.Select(s => s.LargeCargo));
		}

		[Fact]
		public async Task SharingUsesEachOriginsOwnPendingCountAndKeepsFairRounds() {
			var scenario = new PrimarySharingScenario(new long[] { 12, 6 }, 3);
			await Execute(scenario.Worker);
			Assert.Equal(new[] { 1, 2, 1 }, scenario.Sent.Select(f => f.Origin.Position));
			Assert.Equal(new long[] { 6, 6, 6 }, scenario.Sent.Select(f => f.Ships.LargeCargo));
			Assert.Equal(2, scenario.Reads[1]);
			Assert.Equal(1, scenario.Reads[2]);
		}

		[Fact]
		public async Task SharingRespectsOriginCapsWhenTheyCannotFillEverySlot() {
			var scenario = new PrimarySharingScenario(new long[] { 12, 12 }, 5, maxPerOrigin: 2);
			await Execute(scenario.Worker);
			Assert.Equal(new[] { 1, 2, 1, 2 }, scenario.Sent.Select(f => f.Origin.Position));
			Assert.All(scenario.Sent, fleet => Assert.Equal(6, fleet.Ships.LargeCargo));
		}

		[Fact]
		public async Task ConfirmedFailureRebalancesOnlyTheUnsentShipsAndDoesNotRetryThatOrigin() {
			var scenario = new PrimarySharingScenario(new long[] { 12, 12 }, 3);
			scenario.RejectedOrigin = 2;
			await Execute(scenario.Worker);
			Assert.Equal(new[] { 1, 1, 1 }, scenario.Sent.Select(f => f.Origin.Position));
			Assert.Equal(new long[] { 6, 3, 3 }, scenario.Sent.Select(f => f.Ships.LargeCargo));
			Assert.Equal(1, scenario.Reads[2]);
		}

		[Fact]
		public async Task CurrentSlotAvailabilityShortensTheRemainingSharingPlan() {
			var scenario = new PrimarySharingScenario(new long[] { 12 }, 3);
			scenario.SlotsAfterFirstSend = 2;
			await Execute(scenario.Worker);
			Assert.Equal(new long[] { 4, 8 }, scenario.Sent.Select(f => f.Ships.LargeCargo));
		}

		[Fact]
		public async Task SharingDoesNotRequireEnoughRareEscortsForEveryPendingFleet() {
			var scenario = new PrimarySharingScenario(new long[] { 12 }, 3);
			scenario.Stock[1].Reaper = 1;
			await Execute(scenario.Worker);
			Assert.Equal(new long[] { 4, 4, 4 }, scenario.Sent.Select(f => f.Ships.LargeCargo));
			Assert.Equal(new long[] { 1, 0, 0 }, scenario.Sent.Select(f => f.Ships.Reaper));
			Assert.Equal(new long[] { 0, 1, 1 }, scenario.Sent.Select(f => f.Ships.HeavyFighter));
		}

		[Fact]
		public async Task SharingNeverExceedsTheExistingIdealExpeditionComposition() {
			var scenario = new PrimarySharingScenario(new long[] { long.MaxValue }, 3);
			var ideal = new CalculationService(null, null).CalcIdealExpeditionShips(
				Buildables.LargeCargo, 0, 0, null, scenario.Server).LargeCargo;
			await Execute(scenario.Worker);
			Assert.Equal(3, scenario.Sent.Count);
			Assert.All(scenario.Sent, fleet => Assert.Equal(ideal, fleet.Ships.LargeCargo));
		}

		[Fact]
		public async Task ManualFleetCompositionRemainsFixed() {
			var scenario = new PrimarySharingScenario(new long[] { 12 }, 3);
			scenario.Settings.Expeditions.ManualShips.Active = true;
			scenario.Settings.Expeditions.ManualShips.Ships.LargeCargo = 2;
			await Execute(scenario.Worker);
			Assert.Equal(new long[] { 2, 2, 2 }, scenario.Sent.Select(f => f.Ships.LargeCargo));
		}

		private sealed class PrimarySharingScenario {
			public readonly ExpeditionsWorker Worker;
			public readonly dynamic Settings;
			public readonly ServerData Server = new ServerData { Galaxies = 6, Systems = 499, TopScore = 1000000 };
			public readonly Dictionary<int, Ships> Stock = new();
			public readonly Dictionary<int, int> Reads = new();
			public readonly List<Ships> ReadSnapshots = new();
			public readonly List<Fleet> Sent = new();
			public int? RejectedOrigin;
			public int? SlotsAfterFirstSend;

			public PrimarySharingScenario(long[] primary, int slots, int maxPerOrigin = 20) {
				var origins = primary.Select((count, index) => CreateOrigin(1, 100, index + 1, index + 1)).ToArray();
				foreach (var origin in origins)
					Stock[origin.ID] = new Ships(largeCargo: primary[origin.ID - 1], heavyFighter: slots, espionageProbe: slots);
				Settings = CreateSettings(origins, maxPerOrigin, slots, false);
				var userData = CreateUserData(origins);
				userData.serverData = Server;
				var bonuses = new LFBonuses { LfResourceBonuses = new LFBonusesResources() };
				var ogame = Proxy<IOgameService>.Create((method, args) => {
					if (method.Name == nameof(IOgameService.GetServerData)) return Task.FromResult(Server);
					if (method.Name == nameof(IOgameService.GetLFBonuses)) return Task.FromResult(bonuses);
					if (method.Name == nameof(IOgameService.GetShips)) {
						var id = ((Celestial)args[0]).ID;
						Reads[id] = Reads.GetValueOrDefault(id) + 1;
						var snapshot = CopyShips(Stock[id]);
						ReadSnapshots.Add(snapshot);
						return Task.FromResult(snapshot);
					}
					return Proxy<IOgameService>.Default(method.ReturnType);
				});
				var scheduler = Proxy<IFleetScheduler>.Create((method, args) => {
					if (method.Name == nameof(IFleetScheduler.UpdateFleets)) {
						userData.fleets = Sent.ToList();
						return Task.FromResult(userData.fleets);
					}
					if (method.Name == "get_LastFleetSnapshotWasFresh") return true;
					if (method.Name is "SendExpedition" or "SendFleet") {
						var origin = (Celestial)args[0];
						if (origin.ID == RejectedOrigin) return Task.FromResult((int)SendFleetCode.GenericError);
						var fleet = CopyShips((Ships)args[1]);
						foreach (var property in typeof(Ships).GetProperties()) {
							var left = (long)property.GetValue(Stock[origin.ID]) - (long)property.GetValue(fleet);
							Assert.True(left >= 0, "A dispatch must not use unavailable ships.");
							property.SetValue(Stock[origin.ID], left);
						}
						Sent.Add(new Fleet { ID = 7000 + Sent.Count, Mission = Missions.Expedition,
							Origin = origin.Coordinate, Destination = (Coordinate)args[2], Ships = fleet, BackIn = 100 });
						return Task.FromResult(7000 + Sent.Count);
					}
					return Proxy<IFleetScheduler>.Default(method.ReturnType);
				});
				var realCalculation = new CalculationService(null, null);
				var calculation = Proxy<ICalculationService>.Create((method, args) =>
					method.Name == nameof(ICalculationService.CalcSlotsPriority) ? 100 : method.Invoke(realCalculation, args));
				var normalBridge = CreateBridge(userData, slots);
				var bridge = Proxy<ITBotOgamedBridge>.Create((method, args) => {
					if (method.Name == nameof(ITBotOgamedBridge.UpdateSlots) && Sent.Count > 0 && SlotsAfterFirstSend.HasValue)
						return Task.FromResult(new Slots { Total = 100, InUse = Sent.Count, ExpTotal = SlotsAfterFirstSend.Value, ExpInUse = Sent.Count });
					return method.Invoke(normalBridge, args);
				});
				Worker = new ExpeditionsWorker(CreateParent(Settings, userData), ogame, scheduler, calculation, bridge);
			}

			private static Ships CopyShips(Ships source) {
				var copy = new Ships();
				foreach (var property in typeof(Ships).GetProperties()) property.SetValue(copy, property.GetValue(source));
				return copy;
			}
		}
	}
}
