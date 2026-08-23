using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TBot.Common.Logging;
using Tbot.Helpers;
using Tbot.Includes;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Tbot.Services;

namespace Tbot.Workers.Brain {
	public class AutoFleetJumpGateWorker : WorkerBase {
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		private readonly IOgameService _ogameService;

		public AutoFleetJumpGateWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			ITBotOgamedBridge tbotOgameBridge) :
			base(parentInstance) {
			_ogameService = ogameService;
			_tbotOgameBridge = tbotOgameBridge;
		}

		private dynamic GetJumpGateSettings() {
			if (_tbotInstance.InstanceSettings.Brain is IDictionary<string, object> brain) {
				if (brain.TryGetValue("AutoFleetJumpGate", out var currentSettings))
					return currentSettings;

				// Keep existing user settings working after the spelling correction.
				if (brain.TryGetValue("AutoFleepJumpGate", out var legacySettings)) {
					DoLog(LogLevel.Warning,
						"Brain.AutoFleepJumpGate is deprecated; rename it to Brain.AutoFleetJumpGate.");
					return legacySettings;
				}
			}

			throw new InvalidOperationException("Brain.AutoFleetJumpGate settings are missing.");
		}

		private JumpGateTarget GetTargetCoordinates(dynamic jumpGateSettings) {
			if (!(jumpGateSettings is IDictionary<string, object> settings))
				throw new InvalidOperationException("Brain.AutoFleetJumpGate settings are invalid.");

			var target = JumpGateTargetResolver.Resolve(settings, out var usedLegacyArray);
			if (usedLegacyArray)
				DoLog(LogLevel.Warning,
					"Brain.AutoFleetJumpGate.Target should be an object; using the first entry from the legacy array format.");

			return target;
		}

		protected override async Task Execute() {
			bool jumpGateAttempted = false;
			bool jumpGateSucceeded = false;
			long highestRechargeCountdown = 0;
			dynamic jumpGateSettings = null;

			if (!_tbotInstance.UserData.isSleeping) {
				DoLog(LogLevel.Information, $"Starting jumpgate");
				await SetDefaultWorkerPeriod();
				try {
					jumpGateSettings = GetJumpGateSettings();
					var targetCoordinates = GetTargetCoordinates(jumpGateSettings);
					// Get target moon coordinates
					var targetGalaxy = targetCoordinates.Galaxy;
					var targetSystem = targetCoordinates.System;
					var targetPosition = targetCoordinates.Position;

					Celestial moondest = _tbotInstance.UserData.celestials.Unique()
						.Where(c => c.Coordinate.Galaxy == targetGalaxy)
						.Where(c => c.Coordinate.System == targetSystem)
						.Where(c => c.Coordinate.Position == targetPosition)
						.Where(c => c.Coordinate.Type == Celestials.Moon)
						.SingleOrDefault() ?? new() { ID = 0 };

					if (moondest.ID == 0) {
						DoLog(LogLevel.Warning,
							$"Target moon not found: {targetGalaxy}:{targetSystem}:{targetPosition}");
						return;
					}


					// Update target moon facilities to check JumpGate
					try {
						moondest = await _tbotOgameBridge.UpdatePlanet(moondest, UpdateTypes.Facilities) as Moon;
						if (moondest == null) {
							DoLog(LogLevel.Error,
								$"Failed to update target moon facilities - UpdatePlanet returned null");
							return;
						}

						if (moondest.Facilities.JumpGate <= 0) {
							DoLog(LogLevel.Error,
								$"Target moon {moondest.Coordinate} does not have JumpGate facility (Level: {moondest.Facilities.JumpGate})");
							return;
						}
					} catch (Exception ex) {
						DoLog(LogLevel.Error, $"Exception while validating target moon facilities: {ex.Message}");
						return;
					}


					var minPointsToTeleport = (long) jumpGateSettings
						.MinimumAmountOfShipPointsToTeleport;

					// Define jumpable ships and map 'leave on moon' config only for jumpable
					var jumpable = new HashSet<Buildables> {
						Buildables.LightFighter,
						Buildables.HeavyFighter,
						Buildables.Cruiser,
						Buildables.Battleship,
						Buildables.Battlecruiser,
						Buildables.Bomber,
						Buildables.Destroyer,
						Buildables.Deathstar,
						Buildables.SmallCargo,
						Buildables.LargeCargo,
						Buildables.Recycler,
						Buildables.ColonyShip,
						Buildables.EspionageProbe,
						Buildables.Reaper,
						Buildables.Pathfinder
					};

					var leaveCfg = jumpGateSettings
						.MinimumAmountOfShipsToBeLeftOnMoon;
					var shipsToLeave = new Ships(
						lightFighter: (long) leaveCfg.LightFighter,
						heavyFighter: (long) leaveCfg.HeavyFighter,
						cruiser: (long) leaveCfg.Cruiser,
						battleship: (long) leaveCfg.Battleship,
						battlecruiser: (long) leaveCfg.Battlecruiser,
						bomber: (long) leaveCfg.Bomber,
						destroyer: (long) leaveCfg.Destroyer,
						deathstar: (long) leaveCfg.Deathstar,
						smallCargo: (long) leaveCfg.SmallCargo,
						largeCargo: (long) leaveCfg.LargeCargo,
						colonyShip: (long) leaveCfg.ColonyShip,
						recycler: (long) leaveCfg.Recycler,
						espionageProbe: (long) leaveCfg.EspionageProbe,
						solarSatellite: 0,
						crawler: 0,
						reaper: (long) leaveCfg.Reaper,
						pathfinder: (long) leaveCfg.Pathfinder
					);

					// Get all moons except target, calculate potential teleportable fleet points
					var moonsWithTeleportablePoints = new List<(Moon moon, long teleportablePoints)>();

					foreach (var celestial in _tbotInstance.UserData.celestials.Where(c =>
						         c is Moon && c.ID != moondest.ID)) {
						var moon = celestial as Moon;
						// Update ships on moon
						moon = await _tbotOgameBridge.UpdatePlanet(moon, UpdateTypes.Ships) as Moon;
						// Update facilities to check jump gate
						moon = await _tbotOgameBridge.UpdatePlanet(moon, UpdateTypes.Facilities) as Moon;

						// Check if moon has jump gate
						if (moon.Facilities.JumpGate <= 0) {
							continue;
						}

						// Calculate ships that would be left (only for jumpable types)
						var shipsToLeaveAdjusted = new Ships();
						foreach (var shipType in jumpable) {
							var currentAmount = moon.Ships.GetAmount(shipType);
							var leftAmount = shipsToLeave.GetAmount(shipType);
							var amountToLeave = Math.Min(currentAmount, leftAmount);
							shipsToLeaveAdjusted.SetAmount(shipType, amountToLeave);
						}

						// Calculate teleportable points (total points - points of ships to leave)
						var totalPoints = moon.Ships.GetFleetPoints();
						var leftPoints = shipsToLeaveAdjusted.GetFleetPoints();
						var teleportablePoints = totalPoints - leftPoints;

						// Keep qualified list for actual jump attempts
						if (teleportablePoints >= minPointsToTeleport) {
							DoLog(LogLevel.Debug, $"Moon {moon.Coordinate} qualifies for JumpGate with teleportablePoints={teleportablePoints}");
							moonsWithTeleportablePoints.Add((moon, teleportablePoints));
						} else {
							DoLog(LogLevel.Debug, $"Moon {moon.Coordinate} does not qualify for JumpGate with teleportablePoints={teleportablePoints}, min required={minPointsToTeleport}");

						}
					}

					// Order by teleportable points descending
					var orderedMoons = moonsWithTeleportablePoints
						.OrderByDescending(x => x.teleportablePoints)
						.Select(x => x.moon)
						.ToList();

					if (orderedMoons.Count <= 0) {
						DoLog(LogLevel.Debug, $"No moons with ships");
						return;
					}

					// Build actions: best -> target, then pairwise i=1 -> i+1, consolidating the rest
					// towards the target over subsequent cycles. When the source-moon count is even,
					// one moon would otherwise be left out of every pair - send it straight to the
					// target too instead of stranding it (2026-08-03 fix).
					var teleportsPairs = new List<(Moon origin, Celestial destination)>();
					teleportsPairs.Add((orderedMoons[0], moondest));

					for (var i = 1; i + 1 < orderedMoons.Count; i += 2) {
						teleportsPairs.Add((orderedMoons[i], orderedMoons[i + 1]));
					}

					if (orderedMoons.Count % 2 == 0 && orderedMoons.Count > 1) {
						teleportsPairs.Add((orderedMoons[orderedMoons.Count - 1], moondest));
					}

					for (var i = 0; i < teleportsPairs.Count; i++) {
						var origin = teleportsPairs[i].origin;
						var destination = teleportsPairs[i].destination;
						jumpGateAttempted = true;

						// Calculate ships to send (only for jumpable types)
						var shipsToSend = new Ships();
						foreach (var shipType in jumpable) {
							var currentAmount = origin.Ships.GetAmount(shipType);
							var leftAmount = shipsToLeave.GetAmount(shipType);
							var amountToSend = Math.Max(0, currentAmount - leftAmount);
							shipsToSend.SetAmount(shipType, amountToSend);
						}

						if (shipsToSend.IsEmpty()) {
							continue; // No ships to send
						}

						// Perform JumpGate
						try {
							var result = await _ogameService.JumpGate(origin, destination, shipsToSend);
							if (result.Success) {
								DoLog(LogLevel.Information,
									$"JumpGated {shipsToSend} from {origin.Coordinate} to {destination.Coordinate} (recharge: {result.RechargeCountdown}s)");
								if (result.RechargeCountdown > highestRechargeCountdown) {
									highestRechargeCountdown = result.RechargeCountdown;
								}

								jumpGateSucceeded = true;
							} else {
								if (!string.IsNullOrWhiteSpace(result.Error)) {
									DoLog(LogLevel.Error,
										$"JumpGate failed from {origin.Coordinate}: {result.Error}");
								} else {
									DoLog(LogLevel.Information,
										$"JumpGate not available from {origin.Coordinate} (cooldown: {result.RechargeCountdown}s)");
								}
							}
						} catch (Exception ex) {
							DoLog(LogLevel.Error, $"JumpGate failed from {origin.Coordinate}: {ex.Message}");
						}

						// Small delay between jumpgates in the same loop if we have any pending jumps.
						if (i < teleportsPairs.Count - 1) {
							try {
								var delayMs = RandomizeHelper.CalcRandomIntervalSecToMs(5, 15);
								await Task.Delay(delayMs);
							} catch { }
						}
					}
				} catch (Exception ex) {
					DoLog(LogLevel.Error, $"Error in JumpGate execution: {ex.Message}");
				}
			}

			// Set next interval based on whether JumpGate was attempted/succeeded
			if (jumpGateAttempted && jumpGateSucceeded) {
				// Compare recharge (ms) vs configured random interval (min -> ms), then add 2-5 min jitter if recharge dominates
				int minIntervalMin = (int) jumpGateSettings.CheckIntervalMin;
				int maxIntervalMin = (int) jumpGateSettings.CheckIntervalMax;
				long cfgRandom = RandomizeHelper.CalcRandomInterval(minIntervalMin, maxIntervalMin);
				long rechargeMs = highestRechargeCountdown > 0 ? highestRechargeCountdown * 1000 : 0;
				long interval = rechargeMs > cfgRandom
					? rechargeMs + RandomizeHelper.CalcRandomInterval(2, 5) // jitter in minutes
					: cfgRandom;
				if (interval <= 0)
					interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
				var time = await _tbotOgameBridge.GetDateTime();
				var newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(interval);
				DoLog(LogLevel.Information, $"Next JumpGate check at {newTime.ToString()}");
			} else {
				// Set next interval using minutes (helper converts to ms)
				var newTime = await SetDefaultWorkerPeriod();
				DoLog(LogLevel.Information, $"Next JumpGate check at {newTime.ToString()}");
			}
		}

		private async Task<DateTime> SetDefaultWorkerPeriod()
		{
			dynamic jumpGateSettings = GetJumpGateSettings();
			int minIntervalMin = (int) jumpGateSettings.CheckIntervalMin;
			int maxIntervalMin = (int) jumpGateSettings.CheckIntervalMax;
			long interval = RandomizeHelper.CalcRandomInterval(minIntervalMin, maxIntervalMin);
			if (interval <= 0)
				interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
			var time = await _tbotOgameBridge.GetDateTime();
			var newTime = time.AddMilliseconds(interval);
			ChangeWorkerPeriod(interval);
			return newTime;
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				dynamic jumpGateSettings = GetJumpGateSettings();
				return (
					(bool) _tbotInstance.InstanceSettings.Brain.Active &&
					(bool) jumpGateSettings.Active
				);
			} catch (Exception) {
				return false;
			}
		}

		public override string GetWorkerName() {
			return "AutoFleetJumpGate";
		}

		public override Feature GetFeature() {
			return Feature.BrainAutoFleetJumpGate;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoFleetJumpGate;
		}

		public async Task RunFromTelegramAsync()
		{
			try
			{
				await Execute();
			}
			catch (Exception ex)
			{
				DoLog(LogLevel.Error, $"Manual /fleetjumpgate execution failed: {ex.Message}");
				throw;
			}
		}
	}
}
