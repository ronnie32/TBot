using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Tbot.Common.Settings;
using Tbot.Includes;
using Tbot.Services;
using Tbot.Workers;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Xunit;

namespace TBot.Tests {
	public partial class ExpeditionsWorkerTests {
		[Fact]
		public async Task FixedOrderSkipsAnEmptyOriginAndFillsTheHealthyOriginsFairly() {
			var origins = new[] {
				CreateOrigin(1, 100, 8, 1),
				CreateOrigin(1, 101, 8, 2),
				CreateOrigin(1, 102, 8, 3)
			};
			var sentOrigins = new List<int>();
			var fleetSnapshots = new List<Fleet>();
			var shipRefreshes = new Dictionary<int, int>();
			var settings = CreateSettings(origins, maxPerOrigin: 2, expeditionSlots: 4, randomizeOrder: false);
			var userData = CreateUserData(origins);
			var parent = CreateParent(settings, userData);

			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetServerData) => Task.FromResult(new ServerData {
					Galaxies = 6,
					Systems = 499,
					ProbeCargo = 0
				}),
				nameof(IOgameService.GetLFBonuses) => Task.FromResult(new LFBonuses()),
				nameof(IOgameService.GetShips) => GetShips(args[0] as Celestial, shipRefreshes),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});

			var scheduler = CreateScheduler(
				userData,
				sentOrigins,
				fleetSnapshots,
				() => true);
			var calculation = CreateCalculation();
			var bridge = CreateBridge(userData, expeditionSlots: 4);
			var worker = new ExpeditionsWorker(parent, ogame, scheduler, calculation, bridge);

			await Execute(worker);

			Assert.Equal(new[] { 2, 3, 2, 3 }, sentOrigins);
			Assert.Equal(1, shipRefreshes[origins[0].ID]);
			Assert.Equal(2, shipRefreshes[origins[1].ID]);
			Assert.Equal(2, shipRefreshes[origins[2].ID]);
			Assert.Equal(4, fleetSnapshots.Count);
		}

		[Fact]
		public async Task FixedOrderStopsAfterHealthyCapacityIsExhaustedInsteadOfRetryingFailedOriginsForever() {
			var origins = new[] {
				CreateOrigin(2, 200, 8, 1),
				CreateOrigin(2, 201, 8, 2),
				CreateOrigin(2, 202, 8, 3)
			};
			var sentOrigins = new List<int>();
			var fleetSnapshots = new List<Fleet>();
			var shipRefreshes = new Dictionary<int, int>();
			var settings = CreateSettings(origins, maxPerOrigin: 1, expeditionSlots: 5, randomizeOrder: false);
			var userData = CreateUserData(origins);
			var parent = CreateParent(settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetServerData) => Task.FromResult(new ServerData { Galaxies = 6, Systems = 499 }),
				nameof(IOgameService.GetLFBonuses) => Task.FromResult(new LFBonuses()),
				nameof(IOgameService.GetShips) => GetShips(args[0] as Celestial, shipRefreshes),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var scheduler = CreateScheduler(userData, sentOrigins, fleetSnapshots, () => true);
			var worker = new ExpeditionsWorker(parent, ogame, scheduler, CreateCalculation(), CreateBridge(userData, 5));

			await Execute(worker);

			Assert.Equal(new[] { 2, 3 }, sentOrigins);
			Assert.Equal(2, fleetSnapshots.Count);
			Assert.Equal(1, shipRefreshes[origins[0].ID]);
		}

		[Fact]
		public async Task FixedOrderContinuesAfterAConfirmedDispatchFailureWithoutRetryingTheSameOrigin() {
			var origins = new[] {
				CreateOrigin(2, 210, 8, 1),
				CreateOrigin(2, 211, 8, 2),
				CreateOrigin(2, 212, 8, 3)
			};
			var sentOrigins = new List<int>();
			var fleetSnapshots = new List<Fleet>();
			var shipRefreshes = new Dictionary<int, int>();
			var settings = CreateSettings(origins, maxPerOrigin: 1, expeditionSlots: 3, randomizeOrder: false);
			var userData = CreateUserData(origins);
			var parent = CreateParent(settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetServerData) => Task.FromResult(new ServerData { Galaxies = 6, Systems = 499 }),
				nameof(IOgameService.GetLFBonuses) => Task.FromResult(new LFBonuses()),
				nameof(IOgameService.GetShips) => GetHealthyShips(args[0] as Celestial, shipRefreshes),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var scheduler = CreateScheduler(
				userData,
				sentOrigins,
				fleetSnapshots,
				() => true,
				originId => originId == origins[0].ID ? (int)SendFleetCode.GenericError : (int?)null);
			var worker = new ExpeditionsWorker(parent, ogame, scheduler, CreateCalculation(), CreateBridge(userData, 3));

			await Execute(worker);

			Assert.Equal(new[] { origins[1].ID, origins[2].ID }, sentOrigins);
			Assert.Equal(1, shipRefreshes[origins[0].ID]);
			Assert.Equal(2, fleetSnapshots.Count);
		}

		[Fact]
		public async Task FixedOrderUsesAllThirteenOriginsBeforeReusingAnyOrigin() {
			var origins = Enumerable.Range(1, 13)
				.Select(index => CreateOrigin(1, 100 + index, 8, index))
				.ToArray();
			var sentOrigins = new List<int>();
			var fleetSnapshots = new List<Fleet>();
			var shipRefreshes = new Dictionary<int, int>();
			var settings = CreateSettings(origins, maxPerOrigin: 2, expeditionSlots: 13, randomizeOrder: false);
			var userData = CreateUserData(origins);
			var parent = CreateParent(settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetServerData) => Task.FromResult(new ServerData { Galaxies = 6, Systems = 499 }),
				nameof(IOgameService.GetLFBonuses) => Task.FromResult(new LFBonuses()),
				nameof(IOgameService.GetShips) => GetHealthyShips(args[0] as Celestial, shipRefreshes),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var scheduler = CreateScheduler(userData, sentOrigins, fleetSnapshots, () => true);
			var worker = new ExpeditionsWorker(
				parent,
				ogame,
				scheduler,
				CreateCalculation(),
				CreateBridge(userData, 13));

			await Execute(worker);

			Assert.Equal(13, sentOrigins.Count);
			Assert.All(origins, origin => Assert.Equal(1, sentOrigins.Count(id => id == origin.ID)));
			Assert.Equal(13, shipRefreshes.Count);
		}

		[Fact]
		public async Task RandomizedOrderStillUsesEveryHealthyOriginAndKeepsTheLoadBalanced() {
			var origins = new[] {
				CreateOrigin(3, 300, 8, 1),
				CreateOrigin(3, 301, 8, 2),
				CreateOrigin(3, 302, 8, 3),
				CreateOrigin(3, 303, 8, 4)
			};
			var sentOrigins = new List<int>();
			var fleetSnapshots = new List<Fleet>();
			var shipRefreshes = new Dictionary<int, int>();
			var settings = CreateSettings(origins, maxPerOrigin: 2, expeditionSlots: 6, randomizeOrder: true);
			var userData = CreateUserData(origins);
			var parent = CreateParent(settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetServerData) => Task.FromResult(new ServerData { Galaxies = 6, Systems = 499 }),
				nameof(IOgameService.GetLFBonuses) => Task.FromResult(new LFBonuses()),
				nameof(IOgameService.GetShips) => GetShips(args[0] as Celestial, shipRefreshes),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var scheduler = CreateScheduler(userData, sentOrigins, fleetSnapshots, () => true);
			var worker = new ExpeditionsWorker(parent, ogame, scheduler, CreateCalculation(), CreateBridge(userData, 6));

			await Execute(worker);

			Assert.Equal(6, sentOrigins.Count);
			Assert.Equal(new[] { 0, 2, 2, 2 }, origins.Select(origin => sentOrigins.Count(id => id == origin.ID)));
			Assert.DoesNotContain(origins[0].ID, sentOrigins);
			Assert.Equal(6, fleetSnapshots.Count);
		}

		[Fact]
		public async Task ActiveExpeditionsAreCountedBeforeSelectingTheNextOrigin() {
			var origins = new[] {
				CreateOrigin(4, 400, 8, 2),
				CreateOrigin(4, 401, 8, 3)
			};
			var sentOrigins = new List<int>();
			var fleetSnapshots = new List<Fleet> {
				new Fleet {
					ID = 4001,
					Mission = Missions.Expedition,
					Origin = origins[0].Coordinate,
					Destination = new Coordinate(4, 400, 16, Celestials.DeepSpace),
					Ships = new Ships(largeCargo: 1),
					BackIn = 500
				}
			};
			var shipRefreshes = new Dictionary<int, int>();
			var settings = CreateSettings(origins, maxPerOrigin: 2, expeditionSlots: 4, randomizeOrder: false);
			var userData = CreateUserData(origins);
			var parent = CreateParent(settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetServerData) => Task.FromResult(new ServerData { Galaxies = 6, Systems = 499 }),
				nameof(IOgameService.GetLFBonuses) => Task.FromResult(new LFBonuses()),
				nameof(IOgameService.GetShips) => GetShips(args[0] as Celestial, shipRefreshes),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var scheduler = CreateScheduler(userData, sentOrigins, fleetSnapshots, () => true);
			var worker = new ExpeditionsWorker(parent, ogame, scheduler, CreateCalculation(), CreateBridge(userData, 4));

			await Execute(worker);

			Assert.Equal(new[] { 3, 2, 3 }, sentOrigins);
			Assert.Equal(4, fleetSnapshots.Count);
		}

		[Fact]
		public async Task StaleFleetSnapshotPreventsDispatchInsteadOfUsingCachedOriginState() {
			var origins = new[] {
				CreateOrigin(4, 410, 8, 1),
				CreateOrigin(4, 411, 8, 2)
			};
			var sentOrigins = new List<int>();
			var fleetSnapshots = new List<Fleet>();
			var shipRefreshes = new Dictionary<int, int>();
			var settings = CreateSettings(origins, maxPerOrigin: 1, expeditionSlots: 2, randomizeOrder: false);
			var userData = CreateUserData(origins);
			var parent = CreateParent(settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetServerData) => Task.FromResult(new ServerData { Galaxies = 6, Systems = 499 }),
				nameof(IOgameService.GetLFBonuses) => Task.FromResult(new LFBonuses()),
				nameof(IOgameService.GetShips) => GetHealthyShips(args[0] as Celestial, shipRefreshes),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var scheduler = CreateScheduler(userData, sentOrigins, fleetSnapshots, () => false);
			var worker = new ExpeditionsWorker(
				parent,
				ogame,
				scheduler,
				CreateCalculation(),
				CreateBridge(userData, 2));

			await Execute(worker);

			Assert.Empty(sentOrigins);
			Assert.Empty(shipRefreshes);
			Assert.Empty(fleetSnapshots);
		}

		private static async Task Execute(ExpeditionsWorker worker) {
			var execute = typeof(ExpeditionsWorker).GetMethod(
				"Execute",
				BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(execute);
			await (Task)execute.Invoke(worker, null);
		}

		private static Celestial CreateOrigin(int galaxy, int system, int position, int id) {
			return new Moon {
				ID = id,
				Name = $"Origin {id}",
				Coordinate = new Coordinate(galaxy, system, position, Celestials.Moon),
				Ships = new Ships(largeCargo: 100)
			};
		}

		private static dynamic CreateSettings(
			IReadOnlyList<Celestial> origins,
			int maxPerOrigin,
			int expeditionSlots,
			bool randomizeOrder) {
			var originJson = string.Join(",", origins.Select(origin =>
				$"{{\"Galaxy\":{origin.Coordinate.Galaxy},\"System\":{origin.Coordinate.System},\"Position\":{origin.Coordinate.Position},\"Type\":\"Moon\"}}"));
			var json = $$"""
{
  "General": {
    "SlotsToLeaveFree": 0,
    "SlotPriorityLevel": {
      "Brain": 1,
      "Expeditions": 1,
      "AutoFarm": 1,
      "Colonize": 1,
      "AutoDiscovery": 1,
      "AutoHarvest": 1
    }
  },
  "SleepMode": { "Active": false, "GoToSleep": "23:00", "WakeUp": "07:00" },
  "Brain": {
    "Active": false,
    "Transports": { "Active": false, "MaxSlots": 0 },
    "AutoMine": { "Active": false },
    "AutoResearch": { "Active": false },
    "LifeformAutoMine": { "Active": false },
    "LifeformAutoResearch": { "Active": false }
  },
  "AutoFarm": { "Active": false, "MaxSlots": 0 },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoDiscovery": { "Active": false, "MaxSlots": 0 },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "Expeditions": {
    "Active": true,
    "PrimaryShip": "LargeCargo",
    "PrimaryToKeep": 0,
    "FuelToCarry": 0,
    "MinWaitNextFleet": 0,
    "MaxWaitNextFleet": 0,
    "MinWaitNextRound": 0,
    "MaxWaitNextRound": 0,
    "RandomizeOrder": {{randomizeOrder.ToString().ToLowerInvariant()}},
    "WaitForAllExpeditions": false,
    "WaitForMajorityOfExpeditions": false,
    "MaxExpeditionsPerOrigin": {{maxPerOrigin}},
    "ManualShips": { "Active": false, "Ships": {} },
    "SplitExpeditionsBetweenSystems": { "Active": false, "Range": 1 },
    "Origin": [{{originJson}}]
  }
}
""";
			return SettingsService.GetMergedSettingsFromContents(json, new List<string> { "{}" });
		}

		private static UserData CreateUserData(IReadOnlyList<Celestial> origins) {
			return new UserData {
				celestials = origins.ToList(),
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 100, ExpTotal = 4 },
				serverData = new ServerData { Galaxies = 6, Systems = 499 },
				researches = new Researches { Astrophysics = 1 },
				userInfo = new UserInfo()
			};
		}

		private static ITBotMain CreateParent(dynamic settings, UserData userData) {
			return Proxy<ITBotMain>.Create((method, _) => method.Name switch {
				"get_InstanceAlias" => "ExpeditionWorkerTests",
				"get_InstanceSettings" => settings,
				"get_UserData" => userData,
				_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});
		}

		private static IFleetScheduler CreateScheduler(
			UserData userData,
			List<int> sentOrigins,
			List<Fleet> fleetSnapshots,
			Func<bool> fresh,
			Func<int, int?> sendResult = null) {
			var nextFleetId = 5000;
			return Proxy<IFleetScheduler>.Create((method, args) => {
				if (method.Name == nameof(IFleetScheduler.UpdateFleets)) {
					userData.fleets = fleetSnapshots.ToList();
					return Task.FromResult(userData.fleets);
				}
				if (method.Name == nameof(IFleetScheduler.SendExpedition)) {
					var origin = (Celestial)args[0];
					var forcedResult = sendResult?.Invoke(origin.ID);
					if (forcedResult.HasValue)
						return Task.FromResult(forcedResult.Value);

					var destination = (Coordinate)args[2];
					var ships = (Ships)args[1];
					sentOrigins.Add(origin.ID);
					fleetSnapshots.Add(new Fleet {
						ID = ++nextFleetId,
						Mission = Missions.Expedition,
						Origin = origin.Coordinate,
						Destination = destination,
						Ships = ships,
						BackIn = 100
					});
					return Task.FromResult(nextFleetId);
				}
				if (method.Name == "get_LastFleetSnapshotWasFresh")
					return fresh();
				return Proxy<IFleetScheduler>.Default(method.ReturnType);
			});
		}

		private static ICalculationService CreateCalculation() {
			return Proxy<ICalculationService>.Create((method, _) => {
				if (method.Name == nameof(ICalculationService.CalcSlotsPriority))
					return 100;
				if (method.Name == nameof(ICalculationService.CalcFullExpeditionShips))
					return new Ships(largeCargo: 1);
				return Proxy<ICalculationService>.Default(method.ReturnType);
			});
		}

		private static ITBotOgamedBridge CreateBridge(UserData userData, int expeditionSlots) {
			return Proxy<ITBotOgamedBridge>.Create((method, _) => {
				if (method.Name == nameof(ITBotOgamedBridge.UpdateResearches))
					return Task.FromResult(new Researches { Astrophysics = 1 });
				if (method.Name == nameof(ITBotOgamedBridge.UpdateSlots)) {
					userData.slots = new Slots {
						Total = Math.Max(100, expeditionSlots),
						InUse = userData.fleets.Count,
						ExpTotal = expeditionSlots,
						ExpInUse = userData.fleets.Count(fleet => fleet.Mission == Missions.Expedition)
					};
					return Task.FromResult(userData.slots);
				}
				if (method.Name == nameof(ITBotOgamedBridge.CheckCelestials))
					return Task.FromResult(true);
				return Proxy<ITBotOgamedBridge>.Default(method.ReturnType);
			});
		}

		private static Task<Ships> GetShips(Celestial origin, Dictionary<int, int> refreshes) {
			if (origin == null)
				return Task.FromResult(new Ships());

			refreshes[origin.ID] = refreshes.TryGetValue(origin.ID, out var count) ? count + 1 : 1;
			return Task.FromResult(origin.ID == 1
				? new Ships()
				: new Ships(largeCargo: 100));
		}

		private static Task<Ships> GetHealthyShips(Celestial origin, Dictionary<int, int> refreshes) {
			if (origin == null)
				return Task.FromResult(new Ships());

			refreshes[origin.ID] = refreshes.TryGetValue(origin.ID, out var count) ? count + 1 : 1;
			return Task.FromResult(new Ships(largeCargo: 100));
		}

		private class Proxy<T> : DispatchProxy where T : class {
			private Func<MethodInfo, object[], object> _handler;

			public static T Create(Func<MethodInfo, object[], object> handler) {
				var proxy = DispatchProxy.Create<T, Proxy<T>>();
				((Proxy<T>)(object)proxy)._handler = handler;
				return proxy;
			}

			public static object Default(Type returnType) {
				if (returnType == typeof(void))
					return null;
				if (returnType == typeof(Task))
					return Task.CompletedTask;
				if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)) {
					var valueType = returnType.GetGenericArguments()[0];
					var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
					return typeof(Task)
						.GetMethod(nameof(Task.FromResult))
						.MakeGenericMethod(valueType)
						.Invoke(null, new[] { value });
				}
				if (returnType.IsValueType)
					return Activator.CreateInstance(returnType);
				return null;
			}

			protected override object Invoke(MethodInfo targetMethod, object[] args) {
				var result = _handler?.Invoke(targetMethod, args ?? Array.Empty<object>());
				return result ?? Default(targetMethod.ReturnType);
			}
		}
	}
}
