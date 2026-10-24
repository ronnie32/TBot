using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tbot.Common.Settings;
using Tbot.Includes;
using Tbot.Services;
using Tbot.Workers;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Xunit;

namespace TBot.Tests {
	public class AutoDiscoveryWorkerTests {
		[Fact]
		public async Task UsesTheNearestCachedSystemBeforeScanningAnotherSystem() {
			var alias = $"autodiscovery-nearest-{Guid.NewGuid():N}";
			var firstOrigin = new Planet {
				ID = 901,
				Name = "Near origin",
				Coordinate = new Coordinate(1, 100, 8, Celestials.Planet),
				Ships = new Ships(pathfinder: 100)
			};
			var secondOrigin = new Planet {
				ID = 902,
				Name = "Far origin",
				Coordinate = new Coordinate(1, 400, 8, Celestials.Planet),
				Ships = new Ships(pathfinder: 100)
			};
			var userData = new UserData {
				celestials = new List<Celestial> { firstOrigin, secondOrigin },
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 20, InUse = 0 },
				serverData = new ServerData { Galaxies = 6, Systems = 499 },
				researches = new Researches(),
				userInfo = new UserInfo()
			};
			var settings = SettingsService.GetMergedSettingsFromContents(
				$$"""
{
  "General": { "SlotsToLeaveFree": 0, "SlotPriorityLevel": { "Brain": 1, "Expeditions": 1, "AutoFarm": 1, "Colonize": 1, "AutoDiscovery": 1, "AutoHarvest": 1 } },
  "SleepMode": { "Active": false, "GoToSleep": "23:00", "WakeUp": "07:00" },
  "Brain": { "Active": false, "Transports": { "Active": false, "MaxSlots": 0 }, "AutoMine": { "Active": false }, "AutoResearch": { "Active": false }, "LifeformAutoMine": { "Active": false }, "LifeformAutoResearch": { "Active": false } },
  "Expeditions": { "Active": false },
  "AutoFarm": { "Active": false, "MaxSlots": 0 },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "AutoDiscovery": { "Active": true, "MaxSlots": 1, "CheckIntervalMin": 1, "CheckIntervalMax": 1, "Origin": [
    { "Galaxy": 1, "System": 100, "Position": 8, "Type": "Planet" },
    { "Galaxy": 1, "System": 400, "Position": 8, "Type": "Planet" }
  ] }
}
""",
				new List<string> { "{}" });
			var parent = Proxy<ITBotMain>.Create((method, _) => method.Name switch {
				"get_InstanceAlias" => alias,
				"get_InstanceSettings" => settings,
				"get_UserData" => userData,
				_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});

			var cache = new DiscoverySystemCacheStore(alias);
			cache.Observe(1, 95, new[] { 1, 2 }, DateTime.UtcNow);
			var dispatches = new List<(int originId, int system, int position)>();
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetCelestials) => Task.FromResult(new List<Celestial> { firstOrigin, secondOrigin }),
				nameof(IOgameService.GetAvailableDiscoveries) => Task.FromResult(1),
				nameof(IOgameService.GetPositionsAvailableForDiscoveryFleet) => Task.FromResult(
					new List<Coordinate> { new Coordinate(1, ((Coordinate)args[1]).System, 1), new Coordinate(1, ((Coordinate)args[1]).System, 2) }),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				nameof(ITBotOgamedBridge.UpdateSlots) => Task.FromResult(new Slots { Total = 20, InUse = 0 }),
				nameof(ITBotOgamedBridge.CheckCelestials) => Task.FromResult(true),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, args) => method.Name switch {
				nameof(IFleetScheduler.UpdateFleets) => Task.FromResult(new List<Fleet>()),
				nameof(IFleetScheduler.SendDiscovery) => RecordDiscovery(args, dispatches),
				nameof(IFleetScheduler.SendSystemDiscovery) => SendUnexpectedSystemDiscovery(args),
				_ => Proxy<IFleetScheduler>.Default(method.ReturnType)
			});
			var calculation = Proxy<ICalculationService>.Create((method, _) => method.Name switch {
				nameof(ICalculationService.CalcSlotsPriority) => 1,
				_ => Proxy<ICalculationService>.Default(method.ReturnType)
			});

			try {
				var worker = new AutoDiscoveryWorker(parent, ogame, scheduler, calculation, bridge);
				await Execute(worker);

				var dispatch = Assert.Single(dispatches);
				Assert.Equal(901, dispatch.originId);
				Assert.Equal(95, dispatch.system);
				Assert.Equal(1, dispatch.position);
			} finally {
				DeleteDataFile($"autodiscovery_cursors_{alias}.json");
				DeleteDataFile($"autodiscovery_systems_{alias}.json");
				DeleteDataFile($"autodiscovery_artifacts_{alias}.json");
			}
		}

		[Fact]
		public async Task UsesSystemDispatchForCachedSystemEvenWhenCursorIsPastItsFirstPosition() {
			var alias = $"autodiscovery-cached-system-dispatch-{Guid.NewGuid():N}";
			var origin = new Planet {
				ID = 903,
				Name = "Cached system origin",
				Coordinate = new Coordinate(1, 100, 8, Celestials.Planet),
				Ships = new Ships(pathfinder: 100)
			};
			var userData = new UserData {
				celestials = new List<Celestial> { origin },
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 20, InUse = 0 },
				serverData = new ServerData { Galaxies = 6, Systems = 499 },
				researches = new Researches(),
				userInfo = new UserInfo()
			};
			var settings = SettingsService.GetMergedSettingsFromContents(
				$$"""
{
  "General": { "SlotsToLeaveFree": 0, "SlotPriorityLevel": { "Brain": 1, "Expeditions": 1, "AutoFarm": 1, "Colonize": 1, "AutoDiscovery": 1, "AutoHarvest": 1 } },
  "SleepMode": { "Active": false, "GoToSleep": "23:00", "WakeUp": "07:00" },
  "Brain": { "Active": false, "Transports": { "Active": false, "MaxSlots": 0 }, "AutoMine": { "Active": false }, "AutoResearch": { "Active": false }, "LifeformAutoMine": { "Active": false }, "LifeformAutoResearch": { "Active": false } },
  "Expeditions": { "Active": false },
  "AutoFarm": { "Active": false, "MaxSlots": 0 },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "AutoDiscovery": { "Active": true, "MaxSlots": 2, "CheckIntervalMin": 1, "CheckIntervalMax": 1, "Origin": [{ "Galaxy": 1, "System": 100, "Position": 8, "Type": "Planet" }] }
}
""",
				new List<string> { "{}" });
			var parent = Proxy<ITBotMain>.Create((method, _) => method.Name switch {
				"get_InstanceAlias" => alias,
				"get_InstanceSettings" => settings,
				"get_UserData" => userData,
				_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});

			var cache = new DiscoverySystemCacheStore(alias);
			cache.Observe(1, 95, new[] { 1, 2 }, DateTime.UtcNow);
			new AutoDiscoveryCursorStore($"autodiscovery_cursors_{alias}.json")
				.SaveShared(1, new DiscoveryCursorState(100, 5));
			var systemDispatches = new List<(int galaxy, int system, int expected)>();
			var individualDispatches = new List<int>();
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetCelestials) => Task.FromResult(new List<Celestial> { origin }),
				nameof(IOgameService.GetAvailableDiscoveries) => Task.FromResult(2),
				nameof(IOgameService.GetPositionsAvailableForDiscoveryFleet) => Task.FromResult(
					new List<Coordinate> { new Coordinate(1, ((Coordinate)args[1]).System, 1), new Coordinate(1, ((Coordinate)args[1]).System, 2) }),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				nameof(ITBotOgamedBridge.UpdateSlots) => Task.FromResult(new Slots { Total = 20, InUse = 0 }),
				nameof(ITBotOgamedBridge.CheckCelestials) => Task.FromResult(true),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, args) => method.Name switch {
				nameof(IFleetScheduler.UpdateFleets) => Task.FromResult(new List<Fleet>()),
				nameof(IFleetScheduler.SendSystemDiscovery) => SendSystemDiscovery(args, systemDispatches,
					new List<Coordinate> { new Coordinate(1, 95, 1), new Coordinate(1, 95, 2) }),
				nameof(IFleetScheduler.SendDiscovery) => SendUnexpectedIndividualDiscovery(args, individualDispatches),
				_ => Proxy<IFleetScheduler>.Default(method.ReturnType)
			});
			var calculation = Proxy<ICalculationService>.Create((method, _) => method.Name switch {
				nameof(ICalculationService.CalcSlotsPriority) => 2,
				_ => Proxy<ICalculationService>.Default(method.ReturnType)
			});

			try {
				var worker = new AutoDiscoveryWorker(parent, ogame, scheduler, calculation, bridge);
				await Execute(worker);

				Assert.Equal(new[] { (1, 95, 2) }, systemDispatches);
				Assert.Empty(individualDispatches);
			} finally {
				DeleteDataFile($"autodiscovery_cursors_{alias}.json");
				DeleteDataFile($"autodiscovery_systems_{alias}.json");
				DeleteDataFile($"autodiscovery_artifacts_{alias}.json");
			}
		}
		[Theory]
        [InlineData(false)]
        [InlineData(true)]
		public async Task UsesOneSystemDispatchForAllAvailablePositionsBeforeMovingOn(bool partial) {
			var alias = $"autodiscovery-{Guid.NewGuid():N}";
			var origin = new Planet {
				ID = 110,
				Name = "System-first origin",
				Coordinate = new Coordinate(1, 30, 8, Celestials.Planet),
				Ships = new Ships(pathfinder: 100)
			};
			var userData = new UserData {
				celestials = new List<Celestial> { origin },
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 20, InUse = 0 },
				serverData = new ServerData { Galaxies = 6, Systems = 499 },
				researches = new Researches(),
				userInfo = new UserInfo()
			};
			var settings = SettingsService.GetMergedSettingsFromContents(
				$$"""
{
  "General": { "SlotsToLeaveFree": 0, "SlotPriorityLevel": { "Brain": 1, "Expeditions": 1, "AutoFarm": 1, "Colonize": 1, "AutoDiscovery": 1, "AutoHarvest": 1 } },
  "SleepMode": { "Active": false, "GoToSleep": "23:00", "WakeUp": "07:00" },
  "Brain": { "Active": false, "Transports": { "Active": false, "MaxSlots": 0 }, "AutoMine": { "Active": false }, "AutoResearch": { "Active": false }, "LifeformAutoMine": { "Active": false }, "LifeformAutoResearch": { "Active": false } },
  "Expeditions": { "Active": false },
  "AutoFarm": { "Active": false, "MaxSlots": 0 },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "AutoDiscovery": { "Active": true, "MaxSlots": 3, "CheckIntervalMin": 1, "CheckIntervalMax": 1, "Origin": [{ "Galaxy": 1, "System": 30, "Position": 8, "Type": "Planet" }] }
}
""",
				new List<string> { "{}" });
			var parent = Proxy<ITBotMain>.Create((method, _) => method.Name switch {
				"get_InstanceAlias" => alias,
				"get_InstanceSettings" => settings,
				"get_UserData" => userData,
				_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});

			var systemDispatches = new List<(int galaxy, int system, int expected)>();
			var individualDispatches = new List<int>();
			var available = new List<Coordinate> {
				new Coordinate(1, 30, 1), new Coordinate(1, 30, 2), new Coordinate(1, 30, 3)
			};
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetCelestials) => Task.FromResult(new List<Celestial> { origin }),
				nameof(IOgameService.GetAvailableDiscoveries) => Task.FromResult(3),
				nameof(IOgameService.GetPositionsAvailableForDiscoveryFleet) => Task.FromResult(available),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				nameof(ITBotOgamedBridge.UpdateSlots) => Task.FromResult(new Slots { Total = 20, InUse = 0 }),
				nameof(ITBotOgamedBridge.CheckCelestials) => Task.FromResult(true),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, args) => method.Name switch {
				nameof(IFleetScheduler.UpdateFleets) => Task.FromResult(new List<Fleet>()),
				nameof(IFleetScheduler.SendSystemDiscovery) => SendSystemDiscovery(args, systemDispatches, partial ? available.Take(1).ToList() : available, partial),
				nameof(IFleetScheduler.SendDiscovery) => SendUnexpectedIndividualDiscovery(args, individualDispatches),
				_ => Proxy<IFleetScheduler>.Default(method.ReturnType)
			});
			var calculation = Proxy<ICalculationService>.Create((method, _) => method.Name switch {
				nameof(ICalculationService.CalcSlotsPriority) => 3,
				_ => Proxy<ICalculationService>.Default(method.ReturnType)
			});

			try {
				var worker = new AutoDiscoveryWorker(parent, ogame, scheduler, calculation, bridge);
				await Execute(worker);

				Assert.Single(systemDispatches);
				Assert.Equal((1, 30, 3), systemDispatches[0]);
				Assert.Empty(individualDispatches);
                if (partial) {
                    var check = typeof(AutoDiscoveryWorker).GetMethod("IsBlacklistedAndActive", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.True((bool)check.Invoke(worker, new object[] { available[0], DateTime.UtcNow }));
                    Assert.False((bool)check.Invoke(worker, new object[] { available[1], DateTime.UtcNow }));
                }
			} finally {
				var cursorPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", $"autodiscovery_cursors_{alias}.json");
				if (System.IO.File.Exists(cursorPath))
					System.IO.File.Delete(cursorPath);
			}
		}

		[Fact]
		public async Task RejectedPositionIsNotQuarantinedAndLaterPositionsAreStillTried() {
			var alias = $"autodiscovery-{Guid.NewGuid():N}";
			var origin = new Planet {
				ID = 100,
				Name = "Discovery origin",
				Coordinate = new Coordinate(1, 30, 8, Celestials.Planet),
				Ships = new Ships(pathfinder: 100),
				Resources = new Resources(0, 0, 100000)
			};
			var userData = new UserData {
				celestials = new List<Celestial> { origin },
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 20, InUse = 0 },
				serverData = new ServerData { Galaxies = 6, Systems = 499 },
				researches = new Researches(),
				userInfo = new UserInfo()
			};
			var settings = SettingsService.GetMergedSettingsFromContents(
				$$"""
{
  "General": {
    "SlotsToLeaveFree": 0,
    "SlotPriorityLevel": { "Brain": 1, "Expeditions": 1, "AutoFarm": 1, "Colonize": 1, "AutoDiscovery": 1, "AutoHarvest": 1 }
  },
  "SleepMode": { "Active": false, "GoToSleep": "23:00", "WakeUp": "07:00" },
  "Brain": {
    "Active": false,
    "Transports": { "Active": false, "MaxSlots": 0 },
    "AutoMine": { "Active": false }, "AutoResearch": { "Active": false },
    "LifeformAutoMine": { "Active": false }, "LifeformAutoResearch": { "Active": false }
  },
  "Expeditions": { "Active": false },
  "AutoFarm": { "Active": false, "MaxSlots": 0 },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "AutoDiscovery": {
    "Active": true,
    "MaxSlots": 3,
    "CheckIntervalMin": 1,
    "CheckIntervalMax": 1,
    "Origin": [{"Galaxy":1,"System":30,"Position":8,"Type":"Planet"}]
  }
}
""",
				new List<string> { "{}" });
			var parent = Proxy<ITBotMain>.Create((method, _) => method.Name switch {
				"get_InstanceAlias" => alias,
				"get_InstanceSettings" => settings,
				"get_UserData" => userData,
				_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});

			var sentPositions = new List<int>();
			var responses = new Queue<DiscoveryDispatchStatus>(new[] {
				DiscoveryDispatchStatus.Rejected,
				DiscoveryDispatchStatus.Sent,
				DiscoveryDispatchStatus.Sent,
				DiscoveryDispatchStatus.Sent
			});
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetCelestials) => Task.FromResult(new List<Celestial> { origin }),
				nameof(IOgameService.GetAvailableDiscoveries) => Task.FromResult(3),
				nameof(IOgameService.GetPositionsAvailableForDiscoveryFleet) => Task.FromResult(
					new List<Coordinate> {
						new Coordinate(1, 30, 1), new Coordinate(1, 30, 2), new Coordinate(1, 30, 3), new Coordinate(1, 30, 4)
					}),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				nameof(ITBotOgamedBridge.UpdateSlots) => Task.FromResult(new Slots { Total = 20, InUse = 0 }),
				nameof(ITBotOgamedBridge.CheckCelestials) => Task.FromResult(true),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, args) => method.Name switch {
				nameof(IFleetScheduler.UpdateFleets) => Task.FromResult(new List<Fleet>()),
				nameof(IFleetScheduler.SendDiscovery) => SendDiscovery(args, sentPositions, responses),
				nameof(IFleetScheduler.SendSystemDiscovery) => SendUnexpectedSystemDiscovery(args),
				_ => Proxy<IFleetScheduler>.Default(method.ReturnType)
			});
			var calculation = Proxy<ICalculationService>.Create((method, _) => method.Name switch {
				nameof(ICalculationService.CalcSlotsPriority) => 3,
				_ => Proxy<ICalculationService>.Default(method.ReturnType)
			});

			try {
				var worker = new AutoDiscoveryWorker(parent, ogame, scheduler, calculation, bridge);
				await Execute(worker);

				// Position 1 is rejected and therefore does not consume a fleet slot.
				// The worker must continue past it and may then confirm three fleets.
				Assert.Equal(new[] { 1, 2, 3, 4 }, sentPositions);
				Assert.DoesNotContain(userData.discoveryBlackList.Keys, coordinate => coordinate.Position == 1);
				Assert.Contains(userData.discoveryBlackList.Keys, coordinate => coordinate.Position == 2);
				Assert.Contains(userData.discoveryBlackList.Keys, coordinate => coordinate.Position == 3);
				Assert.Contains(userData.discoveryBlackList.Keys, coordinate => coordinate.Position == 4);
			} finally {
				var cursorPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", $"autodiscovery_cursors_{alias}.json");
				if (System.IO.File.Exists(cursorPath))
					System.IO.File.Delete(cursorPath);
			}
		}

		[Fact]
		public async Task PathfinderShortageSkipsOnlyTheCurrentOrigin() {
			var alias = $"autodiscovery-{Guid.NewGuid():N}";
			var firstOrigin = new Planet {
				ID = 101,
				Name = "First discovery origin",
				Coordinate = new Coordinate(1, 30, 8, Celestials.Planet)
			};
			var secondOrigin = new Planet {
				ID = 102,
				Name = "Second discovery origin",
				Coordinate = new Coordinate(1, 31, 8, Celestials.Planet)
			};
			var userData = new UserData {
				celestials = new List<Celestial> { firstOrigin, secondOrigin },
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 20, InUse = 0 },
				serverData = new ServerData { Galaxies = 6, Systems = 499 },
				researches = new Researches(),
				userInfo = new UserInfo()
			};
			var settings = SettingsService.GetMergedSettingsFromContents(
				$$"""
{
  "General": {
    "SlotsToLeaveFree": 0,
    "SlotPriorityLevel": { "Brain": 1, "Expeditions": 1, "AutoFarm": 1, "Colonize": 1, "AutoDiscovery": 1, "AutoHarvest": 1 }
  },
  "SleepMode": { "Active": false, "GoToSleep": "23:00", "WakeUp": "07:00" },
  "Brain": {
    "Active": false,
    "Transports": { "Active": false, "MaxSlots": 0 },
    "AutoMine": { "Active": false }, "AutoResearch": { "Active": false },
    "LifeformAutoMine": { "Active": false }, "LifeformAutoResearch": { "Active": false }
  },
  "Expeditions": { "Active": false },
  "AutoFarm": { "Active": false, "MaxSlots": 0 },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "AutoDiscovery": {
					"Active": true,
					"MaxSlots": 2,
    "CheckIntervalMin": 1,
    "CheckIntervalMax": 1,
    "Origin": [
      {"Galaxy":1,"System":30,"Position":8,"Type":"Planet"},
      {"Galaxy":1,"System":31,"Position":8,"Type":"Planet"}
    ]
  }
}
""",
				new List<string> { "{}" });
			var parent = Proxy<ITBotMain>.Create((method, _) => method.Name switch {
				"get_InstanceAlias" => alias,
				"get_InstanceSettings" => settings,
				"get_UserData" => userData,
				_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});

			var attemptedOrigins = new List<int>();
			var responses = new Queue<DiscoveryDispatchStatus>(new[] {
				DiscoveryDispatchStatus.NoShips,
				DiscoveryDispatchStatus.Sent,
				DiscoveryDispatchStatus.Sent
			});
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				 nameof(IOgameService.GetCelestials) => Task.FromResult(new List<Celestial> { firstOrigin, secondOrigin }),
				 nameof(IOgameService.GetAvailableDiscoveries) => Task.FromResult(2),
					 nameof(IOgameService.GetPositionsAvailableForDiscoveryFleet) => Task.FromResult(
					new List<Coordinate> {
						new Coordinate(1, ((Coordinate)args[1]).System, 1),
						new Coordinate(1, ((Coordinate)args[1]).System, 2),
						new Coordinate(1, ((Coordinate)args[1]).System, 3)
					}),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				nameof(ITBotOgamedBridge.UpdateSlots) => Task.FromResult(new Slots { Total = 20, InUse = 0 }),
				nameof(ITBotOgamedBridge.CheckCelestials) => Task.FromResult(true),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, args) => method.Name switch {
				nameof(IFleetScheduler.UpdateFleets) => Task.FromResult(new List<Fleet>()),
				nameof(IFleetScheduler.SendDiscovery) => SendDiscoveryWithOrigins(args, attemptedOrigins, responses),
				nameof(IFleetScheduler.SendSystemDiscovery) => SendUnexpectedSystemDiscovery(args),
				_ => Proxy<IFleetScheduler>.Default(method.ReturnType)
			});
			var calculation = Proxy<ICalculationService>.Create((method, _) => method.Name switch {
				 nameof(ICalculationService.CalcSlotsPriority) => 2,
				_ => Proxy<ICalculationService>.Default(method.ReturnType)
			});

			try {
				var worker = new AutoDiscoveryWorker(parent, ogame, scheduler, calculation, bridge);
				await Execute(worker);

				Assert.Equal(new[] { 101, 102, 102 }, attemptedOrigins);
			} finally {
				var cursorPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", $"autodiscovery_cursors_{alias}.json");
				if (System.IO.File.Exists(cursorPath))
					System.IO.File.Delete(cursorPath);
			}
		}

		[Fact]
		public async Task MultipleOriginsAreUsedInRoundRobinOrderBeforeReusingOne() {
			var alias = $"autodiscovery-{Guid.NewGuid():N}";
			var firstOrigin = new Planet {
				ID = 201,
				Name = "First discovery origin",
				Coordinate = new Coordinate(1, 30, 8, Celestials.Planet),
				Ships = new Ships(pathfinder: 100)
			};
			var secondOrigin = new Planet {
				ID = 202,
				Name = "Second discovery origin",
				Coordinate = new Coordinate(1, 31, 8, Celestials.Planet),
				Ships = new Ships(pathfinder: 100)
			};
			var userData = new UserData {
				celestials = new List<Celestial> { firstOrigin, secondOrigin },
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 20, InUse = 0 },
				serverData = new ServerData { Galaxies = 6, Systems = 499 },
				researches = new Researches(),
				userInfo = new UserInfo()
			};
			var settings = SettingsService.GetMergedSettingsFromContents(
				$$"""
{
  "General": {
    "SlotsToLeaveFree": 0,
    "SlotPriorityLevel": { "Brain": 1, "Expeditions": 1, "AutoFarm": 1, "Colonize": 1, "AutoDiscovery": 1, "AutoHarvest": 1 }
  },
  "SleepMode": { "Active": false, "GoToSleep": "23:00", "WakeUp": "07:00" },
  "Brain": {
    "Active": false,
    "Transports": { "Active": false, "MaxSlots": 0 },
    "AutoMine": { "Active": false }, "AutoResearch": { "Active": false },
    "LifeformAutoMine": { "Active": false }, "LifeformAutoResearch": { "Active": false }
  },
  "Expeditions": { "Active": false },
  "AutoFarm": { "Active": false, "MaxSlots": 0 },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "AutoDiscovery": {
    "Active": true,
    "MaxSlots": 3,
    "CheckIntervalMin": 1,
    "CheckIntervalMax": 1,
    "Origin": [
      {"Galaxy":1,"System":30,"Position":8,"Type":"Planet"},
      {"Galaxy":1,"System":31,"Position":8,"Type":"Planet"}
    ]
  }
}
""",
				new List<string> { "{}" });
			var parent = Proxy<ITBotMain>.Create((method, _) => method.Name switch {
				"get_InstanceAlias" => alias,
				"get_InstanceSettings" => settings,
				"get_UserData" => userData,
				_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});

			var attemptedOrigins = new List<int>();
			var responses = new Queue<DiscoveryDispatchStatus>(new[] {
				DiscoveryDispatchStatus.Sent,
				DiscoveryDispatchStatus.Sent,
				DiscoveryDispatchStatus.Sent
			});
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetCelestials) => Task.FromResult(new List<Celestial> { firstOrigin, secondOrigin }),
				nameof(IOgameService.GetAvailableDiscoveries) => Task.FromResult(3),
				nameof(IOgameService.GetPositionsAvailableForDiscoveryFleet) => Task.FromResult(
					new List<Coordinate> {
						new Coordinate(1, ((Celestial)args[0]).Coordinate.System, 1),
						new Coordinate(1, ((Celestial)args[0]).Coordinate.System, 2)
					}),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				nameof(ITBotOgamedBridge.UpdateSlots) => Task.FromResult(new Slots { Total = 20, InUse = 0 }),
				nameof(ITBotOgamedBridge.CheckCelestials) => Task.FromResult(true),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, args) => method.Name switch {
				nameof(IFleetScheduler.UpdateFleets) => Task.FromResult(new List<Fleet>()),
				nameof(IFleetScheduler.SendDiscovery) => SendDiscoveryWithOrigins(args, attemptedOrigins, responses),
					nameof(IFleetScheduler.SendSystemDiscovery) => SendSystemDiscoveryForOrigins(args, attemptedOrigins),
				_ => Proxy<IFleetScheduler>.Default(method.ReturnType)
			});
			var calculation = Proxy<ICalculationService>.Create((method, _) => method.Name switch {
				nameof(ICalculationService.CalcSlotsPriority) => 3,
				_ => Proxy<ICalculationService>.Default(method.ReturnType)
			});

			try {
				var worker = new AutoDiscoveryWorker(parent, ogame, scheduler, calculation, bridge);
				await Execute(worker);

				Assert.Equal(new[] { 201, 202 }, attemptedOrigins);
			} finally {
				var cursorPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", $"autodiscovery_cursors_{alias}.json");
				if (System.IO.File.Exists(cursorPath))
					System.IO.File.Delete(cursorPath);
			}
		}

		[Fact]
		public async Task SystemFirstKeepsTheSameOriginUntilTheCurrentSystemBudgetIsConsumed() {
			var alias = $"autodiscovery-{Guid.NewGuid():N}";
			var firstOrigin = new Planet {
				ID = 301,
				Name = "Nearest origin",
				Coordinate = new Coordinate(1, 30, 8, Celestials.Planet),
				Ships = new Ships(pathfinder: 100)
			};
			var secondOrigin = new Planet {
				ID = 302,
				Name = "Second origin",
				Coordinate = new Coordinate(1, 31, 8, Celestials.Planet),
				Ships = new Ships(pathfinder: 100)
			};
			var userData = new UserData {
				celestials = new List<Celestial> { firstOrigin, secondOrigin },
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 20, InUse = 0 },
				serverData = new ServerData { Galaxies = 6, Systems = 499 },
				researches = new Researches(),
				userInfo = new UserInfo()
			};
			var settings = SettingsService.GetMergedSettingsFromContents(
				$$"""
{
  "General": {
    "SlotsToLeaveFree": 0,
    "SlotPriorityLevel": { "Brain": 1, "Expeditions": 1, "AutoFarm": 1, "Colonize": 1, "AutoDiscovery": 1, "AutoHarvest": 1 }
  },
  "SleepMode": { "Active": false, "GoToSleep": "23:00", "WakeUp": "07:00" },
  "Brain": {
    "Active": false,
    "Transports": { "Active": false, "MaxSlots": 0 },
    "AutoMine": { "Active": false }, "AutoResearch": { "Active": false },
    "LifeformAutoMine": { "Active": false }, "LifeformAutoResearch": { "Active": false }
  },
  "Expeditions": { "Active": false },
  "AutoFarm": { "Active": false, "MaxSlots": 0 },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "AutoDiscovery": {
    "Active": true,
    "MaxSlots": 4,
    "CheckIntervalMin": 1,
    "CheckIntervalMax": 1,
    "Origin": [
      {"Galaxy":1,"System":30,"Position":8,"Type":"Planet"},
      {"Galaxy":1,"System":31,"Position":8,"Type":"Planet"}
    ]
  }
}
""",
				new List<string> { "{}" });
			var parent = Proxy<ITBotMain>.Create((method, _) => method.Name switch {
				"get_InstanceAlias" => alias,
				"get_InstanceSettings" => settings,
				"get_UserData" => userData,
				_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});

			var dispatches = new List<(int originId, int system, int position)>();
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetCelestials) => Task.FromResult(new List<Celestial> { firstOrigin, secondOrigin }),
				nameof(IOgameService.GetAvailableDiscoveries) => Task.FromResult(4),
				nameof(IOgameService.GetPositionsAvailableForDiscoveryFleet) => Task.FromResult(
					Enumerable.Range(1, 15)
						.Select(position => new Coordinate(1, ((Coordinate)args[1]).System, position))
						.ToList()),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				nameof(ITBotOgamedBridge.UpdateSlots) => Task.FromResult(new Slots { Total = 20, InUse = 0 }),
				nameof(ITBotOgamedBridge.CheckCelestials) => Task.FromResult(true),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, args) => method.Name switch {
				nameof(IFleetScheduler.UpdateFleets) => Task.FromResult(new List<Fleet>()),
				nameof(IFleetScheduler.SendDiscovery) => SendDiscoveryAndRecord(args, dispatches),
				nameof(IFleetScheduler.SendSystemDiscovery) => SendUnexpectedSystemDiscovery(args),
				_ => Proxy<IFleetScheduler>.Default(method.ReturnType)
			});
			var calculation = Proxy<ICalculationService>.Create((method, _) => method.Name switch {
				nameof(ICalculationService.CalcSlotsPriority) => 4,
				_ => Proxy<ICalculationService>.Default(method.ReturnType)
			});

			try {
				var worker = new AutoDiscoveryWorker(parent, ogame, scheduler, calculation, bridge);
				await Execute(worker);

				Assert.Equal(4, dispatches.Count);
				Assert.All(dispatches, dispatch => Assert.Equal(301, dispatch.originId));
				Assert.Equal(new[] { 1, 2, 3, 4 }, dispatches.Select(dispatch => dispatch.position));
			} finally {
				var cursorPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", $"autodiscovery_cursors_{alias}.json");
				if (System.IO.File.Exists(cursorPath))
					System.IO.File.Delete(cursorPath);
			}
		}

		private static Task<DiscoveryDispatchResult> SendDiscovery(object[] args, List<int> sentPositions, Queue<DiscoveryDispatchStatus> responses) {
			sentPositions.Add(((Coordinate)args[1]).Position);
			var status = responses.Dequeue();
			return Task.FromResult(new DiscoveryDispatchResult(status, status.ToString()));
		}

		private static Task<DiscoveryDispatchResult> SendDiscoveryWithOrigins(object[] args, List<int> attemptedOrigins, Queue<DiscoveryDispatchStatus> responses) {
			attemptedOrigins.Add(((Celestial)args[0]).ID);
			var status = responses.Dequeue();
			return Task.FromResult(new DiscoveryDispatchResult(status, status.ToString()));
		}

		private static Task<DiscoveryDispatchResult> SendDiscoveryAndRecord(object[] args, List<(int originId, int system, int position)> dispatches) {
			var origin = (Celestial)args[0];
			var destination = (Coordinate)args[1];
			dispatches.Add((origin.ID, destination.System, destination.Position));
			return Task.FromResult(new DiscoveryDispatchResult(DiscoveryDispatchStatus.Sent, "sent"));
		}

		private static Task<DiscoveryDispatchResult> RecordDiscovery(object[] args, List<(int originId, int system, int position)> dispatches) {
			var origin = (Celestial)args[0];
			var destination = (Coordinate)args[1];
			dispatches.Add((origin.ID, destination.System, destination.Position));
			return Task.FromResult(new DiscoveryDispatchResult(DiscoveryDispatchStatus.Sent, "sent"));
		}

		private static Task<DiscoverySystemDispatchResult> SendSystemDiscovery(object[] args, List<(int galaxy, int system, int expected)> dispatches, List<Coordinate> coordinates, bool partial = false) {
            Assert.Equal(5, args.Length);
            Assert.Equal((int)args[3], ((IReadOnlyList<int>)args[4]).Count);
			dispatches.Add(((int)args[1], (int)args[2], (int)args[3]));
			return Task.FromResult(new DiscoverySystemDispatchResult(partial ? DiscoveryDispatchStatus.Failed : DiscoveryDispatchStatus.Sent, coordinates, "sent"));
		}

		private static Task<DiscoverySystemDispatchResult> SendSystemDiscoveryForOrigins(object[] args, List<int> attemptedOrigins) {
			attemptedOrigins.Add(((Celestial)args[0]).ID);
			var galaxy = (int)args[1];
			var system = (int)args[2];
			var expected = (int)args[3];
			var coordinates = Enumerable.Range(1, expected)
				.Select(position => new Coordinate(galaxy, system, position))
				.ToList();
			return Task.FromResult(new DiscoverySystemDispatchResult(
				DiscoveryDispatchStatus.Sent,
				coordinates,
				"sent"));
		}

		private static Task<DiscoveryDispatchResult> SendUnexpectedIndividualDiscovery(object[] args, List<int> dispatches) {
			dispatches.Add(((Coordinate)args[1]).Position);
			return Task.FromResult(new DiscoveryDispatchResult(DiscoveryDispatchStatus.Sent, "unexpected"));
		}

		private static Task<DiscoverySystemDispatchResult> SendUnexpectedSystemDiscovery(object[] args) {
			return Task.FromResult(new DiscoverySystemDispatchResult(
				DiscoveryDispatchStatus.Failed,
				message: "unexpected system dispatch in individual-position regression"));
		}

		private static async Task Execute(AutoDiscoveryWorker worker) {
			var execute = typeof(AutoDiscoveryWorker).GetMethod(
				"Execute", BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(execute);
			await (Task)execute.Invoke(worker, null);
		}

		private static void DeleteDataFile(string fileName) {
			var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", fileName);
			if (System.IO.File.Exists(path))
				System.IO.File.Delete(path);
		}

		private class Proxy<T> : DispatchProxy where T : class {
			private Func<MethodInfo, object[], object> _handler;

			public static T Create(Func<MethodInfo, object[], object> handler) {
				var proxy = DispatchProxy.Create<T, Proxy<T>>();
				((Proxy<T>)(object)proxy)._handler = handler;
				return proxy;
			}

			public static object Default(Type returnType) {
				if (returnType == typeof(void)) return null;
				if (returnType == typeof(Task)) return Task.CompletedTask;
				if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)) {
					var valueType = returnType.GetGenericArguments()[0];
					var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
					return typeof(Task).GetMethod(nameof(Task.FromResult))
						.MakeGenericMethod(valueType).Invoke(null, new[] { value });
				}
				if (returnType.IsValueType) return Activator.CreateInstance(returnType);
				return null;
			}

			protected override object Invoke(MethodInfo targetMethod, object[] args) {
				return _handler?.Invoke(targetMethod, args ?? Array.Empty<object>()) ?? Default(targetMethod.ReturnType);
			}
		}
	}
}
