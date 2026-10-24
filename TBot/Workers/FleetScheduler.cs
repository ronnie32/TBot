using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tbot.Helpers;
using Tbot.Includes;
using TBot.Common.Logging;
using Tbot.Services;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using System.ComponentModel.Design;
using TBot.Model;
using System.Threading;
using System.Globalization;
using TBot.Ogame.Infrastructure;
using Tbot.Common.Settings;
using System.Numerics;
using TBot.Ogame.Infrastructure.Exceptions;
using System.Net;

namespace Tbot.Workers {
	public class FleetScheduler : IFleetScheduler, IFleetSnapshotProvider {
		private readonly object _fleetLock = new();
		private readonly SemaphoreSlim _dispatchSemaphore = new(1, 1);
		private volatile bool _lastFleetSnapshotWasFresh;
		private int _priorityDispatchWaiters;
		// No server idempotency key exists. Keep uncertain actions blocked for this
		// process rather than allowing the next worker cycle to replay them.
		private readonly Dictionary<string, (HashSet<int> KnownFleetIds, Ships Ships)> _ambiguousDispatches = new();
		private readonly HashSet<string> _uncertainDiscoveryPositions = new();
		private static string DiscoveryKey(Coordinate coordinate) => $"{coordinate.Galaxy}:{coordinate.System}:{coordinate.Position}";

		private async Task RefreshUncertainDiscoverySnapshot() {
			// The ordinary Fleet DTO cannot prove the strict discovery movement
			// fields used by Go. Refresh accounting, but keep the guard until a
			// process restart rather than weakening that evidence boundary.
			try {
				var fleets = await _ogameService.GetFleets();
				if (fleets != null) {
					lock (_fleetLock) _tbotInstance.UserData.fleets = fleets.Where(fleet => fleet != null).ToList();
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Uncertain discovery movement refresh failed: {e.Message}");
			}
		}


		private async Task EnterDispatchGate(bool priority = false) {
			if (priority) Interlocked.Increment(ref _priorityDispatchWaiters);
			try {
				while (true) {
					await _dispatchSemaphore.WaitAsync();
					if (priority || Volatile.Read(ref _priorityDispatchWaiters) == 0) return;
					_dispatchSemaphore.Release();
					await Task.Yield();
				}
			} finally {
				if (priority) Interlocked.Decrement(ref _priorityDispatchWaiters);
			}
		}
		private ITBotMain _tbotInstance = null;
		private ITBotOgamedBridge _tbotOgameBridge;
		private readonly IOgameService _ogameService;
		private readonly ICalculationService _calcService;
		

		private readonly Dictionary<string, Timer> timers = new();
		private readonly object _timersLock = new();

		private bool TryGetTimer(string key, out Timer timer) {
			lock (_timersLock) {
				return timers.TryGetValue(key, out timer);
			}
		}
		private bool ContainsTimer(string key) {
			lock (_timersLock) {
				return timers.ContainsKey(key);
			}
		}
		private void AddTimer(string key, Timer timer) {
			lock (_timersLock) {
				if (!timers.ContainsKey(key)) timers.Add(key, timer);
			}
		}
		private void ReplaceTimer(string key, Timer timer) {
			lock (_timersLock) {
				if (timers.TryGetValue(key, out var oldTimer)) {
					try { oldTimer.Dispose(); } catch { }
					timers.Remove(key);
				}
				timers[key] = timer;
			}
		}
		private void DisposeAndRemoveTimer(string key) {
			lock (_timersLock) {
				if (timers.TryGetValue(key, out var t)) {
					try { t.Dispose(); } catch { }
					timers.Remove(key);
				}
			}
		}


		public FleetScheduler(
			ICalculationService helpService,
			IOgameService ogameService) {
			_calcService = helpService;
			_ogameService = ogameService;
		}

		public void SetTBotInstance(ITBotMain tbotInstance) {
			_tbotInstance = tbotInstance;
		}

		public void SetTBotOgameBridge(ITBotOgamedBridge tbotOgameBridge) {
			_tbotOgameBridge = tbotOgameBridge;
		}

		public async Task SpyCrash(Celestial fromCelestial, Coordinate target = null) {
			decimal speed = Speeds.HundredPercent;
			fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.Ships);
			fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.Resources);
			var payload = fromCelestial.Resources;
			Random random = new Random();

			if (fromCelestial.Ships.EspionageProbe == 0 || payload.Deuterium < 1) {
				_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"No probes or no Fuel on {fromCelestial.Coordinate.ToString()}!");
				await _tbotInstance.SendTelegramMessage($"No probes or no Fuel on {fromCelestial.Coordinate.ToString()}!");
				return;
			}
                         List<Coordinate> spycrash = new();

                           if (target == null) {
                                int playerid = _tbotInstance.UserData.userInfo.PlayerID;

                               for (int sys = fromCelestial.Coordinate.System - 2; sys <= fromCelestial.Coordinate.System + 2; sys++) {
                              int clampedSys = GeneralHelper.ClampSystem(sys);

                                  GalaxyInfo galaxyInfo = await _ogameService.GetGalaxyInfo(fromCelestial.Coordinate.Galaxy, clampedSys);
                              if (galaxyInfo?.Planets == null) continue;

                               foreach (var planet in galaxyInfo.Planets) {
                                   try {
                                           if (planet != null
                                         && !planet.Administrator
                                         && !planet.Inactive
                                         && !planet.StrongPlayer
                                         && !planet.Newbie
                                         && !planet.Banned
                                         && !planet.Vacation
                                         && planet.Player != null
                                         && planet.Player.ID != playerid) {

                    spycrash.Add(new Coordinate(
                        planet.Coordinate.Galaxy,
                        planet.Coordinate.System,
                        planet.Coordinate.Position,
                        Celestials.Planet));
                }
            } catch (NullReferenceException) {
                continue;
            }
        }
    }

                               if (spycrash.Count == 0) {
                                  await _tbotInstance.SendTelegramMessage("No planet to spycrash on could be found over system -2 -> +2");
                                   return;
                          }

                                         target = spycrash[random.Next(spycrash.Count)];
                              }
	              		var attackingShips = new Ships().Add(Buildables.EspionageProbe, 1);

                         int fleetId = await SendFleet(fromCelestial, attackingShips, target, Missions.Attack, speed);

                   if (fleetId != (int)SendFleetCode.GenericError &&
                              fleetId != (int)SendFleetCode.AfterSleepTime &&
                              fleetId != (int)SendFleetCode.NotEnoughSlots)
                 {
                  _tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler,
                                $"EspionageProbe sent to crash on {target}");

                         await _tbotInstance.SendTelegramMessage(
                                     $"EspionageProbe sent to crash on {target}");
                                     }

                               return;
                            }
		public Task AutoFleetSave(Celestial celestial, bool isSleepTimeFleetSave = false, long minDuration = 0, bool WaitFleetsReturn = false, Missions TelegramMission = Missions.None, bool fromTelegram = false, bool saveall = false) =>
			AutoFleetSaveCore(celestial, isSleepTimeFleetSave, minDuration, WaitFleetsReturn, TelegramMission, fromTelegram, saveall);

		// Reports only a dispatch confirmed during this save. Shared movement
		// snapshots and a prior caller's reconciled request cannot confer ownership.
		public async Task<int?> AutoFleetSaveWithResult(Celestial celestial, long minDuration = 0) {
			int? confirmedFleetId = null;
			try {
				await AutoFleetSaveCore(celestial, minDuration: minDuration,
					onConfirmedFleet: id => confirmedFleetId = id);
			} catch (Exception e) when (confirmedFleetId.HasValue) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
					$"Fleet {confirmedFleetId} was confirmed, but fleet-save follow-up failed: {e.Message}");
			}
			return confirmedFleetId;
		}

		private async Task AutoFleetSaveCore(Celestial celestial, bool isSleepTimeFleetSave = false, long minDuration = 0, bool WaitFleetsReturn = false, Missions TelegramMission = Missions.None, bool fromTelegram = false, bool saveall = false, Action<int> onConfirmedFleet = null) {
			DateTime departureTime = await _tbotOgameBridge.GetDateTime();
			_tbotInstance.SleepDuration = minDuration;

			if (WaitFleetsReturn) {

				_tbotInstance.UserData.fleets = await UpdateFleets();
				long interval;
				try {
					interval = (_tbotInstance.UserData.fleets
						.Where(f => f.Mission != Missions.Discovery)
						.OrderBy(f => f.BackIn)
						.Last()
						.BackIn ?? 0
					) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
				} catch {
					interval = 0;
				}

				if (interval > 0 && (!TryGetTimer("GhostSleepTimer", out Timer value))) {
					List<Feature> features = new List<Feature> {
						Feature.Colonize,
						Feature.BrainAutoRepatriate,
						Feature.BrainAutoMine,
						Feature.BrainAutoResearch,
						Feature.BrainAutoMine,
						Feature.BrainLifeformAutoMine,
						Feature.BrainLifeformAutoResearch,
						Feature.AutoFarm,
						Feature.Harvest,
						Feature.Expeditions,
						Feature.AutoDiscovery
					};
					foreach(var feat in features) {
						await _tbotInstance.StopFeature(feat);
					}
					

					interval += RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
					DateTime TimeToGhost = departureTime.AddMilliseconds(interval);
					_tbotInstance.NextWakeUpTime = TimeToGhost.AddMilliseconds(minDuration * 1000);

					if (saveall)
						AddTimer("GhostSleepTimer", new Timer(GhostandSleepAfterFleetsReturnAll, null, interval, Timeout.Infinite));
					else
						AddTimer("GhostSleepTimer", new Timer(GhostandSleepAfterFleetsReturn, null, interval, Timeout.Infinite));

					_tbotInstance.log(LogLevel.Information, LogSender.SleepMode, $"Fleets active, Next check at {TimeToGhost.ToString()}");
					await _tbotInstance.SendTelegramMessage($"Waiting for fleets return, delaying ghosting at {TimeToGhost.ToString()}");

					return;
				} else if (interval == 0 && (!TryGetTimer("GhostSleepTimer", out Timer value2))) {

					_tbotInstance.log(LogLevel.Information, LogSender.SleepMode, $"No fleets active, Ghosting now.");
					_tbotInstance.NextWakeUpTime = departureTime.AddMilliseconds(minDuration * 1000);
					if (saveall)
						GhostandSleepAfterFleetsReturnAll(null);
					else
						GhostandSleepAfterFleetsReturn(null);

					return;
				} else if (TryGetTimer("GhostSleepTimer", out Timer value3)) {
					await _tbotInstance.SendTelegramMessage($"GhostSleep already planned, try /cancelghostsleep");
					return;
				}
			}

			celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Ships);
			if (celestial.Ships.GetMovableShips().IsEmpty()) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Skipping fleetsave from {celestial.ToString()}: there is no fleet to save!");
				if (fromTelegram)
					await _tbotInstance.SendTelegramMessage($"{celestial.ToString()}: there is no fleet!");
				return;
			}

			celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Resources);
			Celestial destination = new() { ID = 0 };

			if (celestial.Resources.Deuterium == 0) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Skipping fleetsave from {celestial.ToString()}: there is no fuel!");
				if (fromTelegram)
					await _tbotInstance.SendTelegramMessage($"{celestial.ToString()}: there is no fuel!");
				return;
			}

			long maxDeuterium = celestial.Resources.Deuterium;

			if (isSleepTimeFleetSave) {
				if (DateTime.TryParse((string) _tbotInstance.InstanceSettings.SleepMode.WakeUp, out DateTime wakeUp)) {
					if (departureTime >= wakeUp)
						wakeUp = wakeUp.AddDays(1);
					minDuration = (long) wakeUp.Subtract(departureTime).TotalSeconds;
				} else {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Could not plan fleetsave from {celestial.ToString()}: unable to parse comeback time");
					return;
				}
			}

			var payload = celestial.Resources;
			if ((long) _tbotInstance.InstanceSettings.SleepMode.AutoFleetSave.DeutToLeave > 0)
				payload.Deuterium -= (long) _tbotInstance.InstanceSettings.SleepMode.AutoFleetSave.DeutToLeave;
			if (payload.Deuterium < 0)
				payload.Deuterium = 0;

			FleetHypotesis possibleFleet = new();
			int fleetId = (int) SendFleetCode.GenericError;
			bool AlreadySent = false;

			Missions mission;
			if (!Missions.TryParse(_tbotInstance.InstanceSettings.SleepMode.AutoFleetSave.DefaultMission, out mission)) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Error: Could not parse 'DefaultMission' from settings, value set to Harvest.");
				mission = Missions.Harvest;
			}

			if (TelegramMission != Missions.None)
				mission = TelegramMission;

			List<FleetHypotesis> fleetHypotesis = await GetFleetSaveDestination(_tbotInstance.UserData.celestials, celestial, departureTime, minDuration, mission, maxDeuterium);
			if (fleetHypotesis.Count() > 0) {
				foreach (FleetHypotesis fleet in fleetHypotesis.OrderBy(pf => pf.Fuel).ThenBy(pf => pf.Duration <= minDuration)) {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"checking {mission} fleet to: {fleet.Destination}");
					if (CheckFuel(fleet, celestial)) {
						fleetId = await DispatchFleet(fleet.Origin, fleet.Ships, fleet.Destination, fleet.Mission, fleet.Speed, payload, _tbotInstance.UserData.userInfo.Class, force: true, validateShips: false, reduceExpeditionPayload: false, onConfirmedFleet: onConfirmedFleet);
						if (fleetId == (int)SendFleetCode.AmbiguousDispatch) return;

						if (fleetId != (int)SendFleetCode.GenericError &&
    fleetId != (int)SendFleetCode.AfterSleepTime &&
    fleetId != (int)SendFleetCode.NotEnoughSlots) {
    possibleFleet = fleet;
    AlreadySent = true;
    break;
}
					}
				}
			}

			if (fromTelegram && !AlreadySent && mission == Missions.Harvest && fleetHypotesis.Count() == 0) {
				await _tbotInstance.SendTelegramMessage($"No debris field found for {mission}, try to /spycrash.");
				return;
			} else if (fromTelegram && !AlreadySent && fleetHypotesis.Count() >= 0) {
				await _tbotInstance.SendTelegramMessage($"Available fuel: {celestial.Resources.Deuterium}\nNo destination found for {mission}, try to reduce ghost time.");
				return;
			}

			if (!AlreadySent) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Fleetsave from {celestial.ToString()} no {mission} possible, checking next mission..");
				if (mission == Missions.Harvest) { mission = Missions.Deploy; } else { mission = Missions.Harvest; };
				mission = Missions.Deploy;
				fleetHypotesis = await GetFleetSaveDestination(_tbotInstance.UserData.celestials, celestial, departureTime, minDuration, mission, maxDeuterium);
				if (fleetHypotesis.Count > 0) {
					foreach (FleetHypotesis fleet in fleetHypotesis.OrderBy(pf => pf.Fuel).ThenBy(pf => pf.Duration <= minDuration)) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"checking {mission} fleet to: {fleet.Destination}");
						if (CheckFuel(fleet, celestial)) {
							fleetId = await DispatchFleet(fleet.Origin, fleet.Ships, fleet.Destination, fleet.Mission, fleet.Speed, payload, _tbotInstance.UserData.userInfo.Class, force: true, validateShips: false, reduceExpeditionPayload: false, onConfirmedFleet: onConfirmedFleet);
						if (fleetId == (int)SendFleetCode.AmbiguousDispatch) return;

							if (fleetId != (int)SendFleetCode.GenericError &&
    fleetId != (int)SendFleetCode.AfterSleepTime &&
    fleetId != (int)SendFleetCode.NotEnoughSlots)
{
    possibleFleet = fleet;
    AlreadySent = true;
    break;
}
						}
					}
				}
			}
			if (!AlreadySent && celestial.Ships.ColonyShip > 0) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Fleetsave from {celestial.ToString()} no {mission} found, checking Colonize destination...");
				mission = Missions.Colonize;
				fleetHypotesis = await GetFleetSaveDestination(_tbotInstance.UserData.celestials, celestial, departureTime, minDuration, mission, maxDeuterium);
				if (fleetHypotesis.Count > 0) {
					foreach (FleetHypotesis fleet in fleetHypotesis.OrderBy(pf => pf.Fuel).ThenBy(pf => pf.Duration <= minDuration)) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"checking {mission} fleet to: {fleet.Destination}");
						if (CheckFuel(fleet, celestial)) {
							fleetId = await DispatchFleet(fleet.Origin, fleet.Ships, fleet.Destination, fleet.Mission, fleet.Speed, payload, _tbotInstance.UserData.userInfo.Class, force: true, validateShips: false, reduceExpeditionPayload: false, onConfirmedFleet: onConfirmedFleet);
						if (fleetId == (int)SendFleetCode.AmbiguousDispatch) return;

							if (fleetId != (int)SendFleetCode.GenericError &&
    fleetId != (int)SendFleetCode.AfterSleepTime &&
    fleetId != (int)SendFleetCode.NotEnoughSlots)
{
    possibleFleet = fleet;
    AlreadySent = true;
    break;
}

						}
					}
				}
			}

			if (!AlreadySent && celestial.Ships.EspionageProbe > 0) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Fleetsave from {celestial.ToString()} no {mission} found, checking Spy destination...");
				mission = Missions.Spy;
				fleetHypotesis = await GetFleetSaveDestination(_tbotInstance.UserData.celestials, celestial, departureTime, minDuration, mission, maxDeuterium);
				if (fleetHypotesis.Count > 0) {
					foreach (FleetHypotesis fleet in fleetHypotesis.OrderBy(pf => pf.Fuel).ThenBy(pf => pf.Duration <= minDuration)) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"checking {mission} fleet to: {fleet.Destination}");
						if (CheckFuel(fleet, celestial)) {
							fleetId = await DispatchFleet(fleet.Origin, fleet.Ships, fleet.Destination, fleet.Mission, fleet.Speed, payload, _tbotInstance.UserData.userInfo.Class, force: true, validateShips: false, reduceExpeditionPayload: false, onConfirmedFleet: onConfirmedFleet);
						if (fleetId == (int)SendFleetCode.AmbiguousDispatch) return;

							if (fleetId != (int)SendFleetCode.GenericError &&
    fleetId != (int)SendFleetCode.AfterSleepTime &&
    fleetId != (int)SendFleetCode.NotEnoughSlots)
{
    possibleFleet = fleet;
    AlreadySent = true;
    break;
}

						}
					}
				}
			}

			if (!AlreadySent) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Fleetsave from {celestial.ToString()} no {mission} found, checking Transport destination...");
				mission = Missions.Transport;
				fleetHypotesis = await GetFleetSaveDestination(_tbotInstance.UserData.celestials, celestial, departureTime, minDuration, mission, maxDeuterium);
				if (fleetHypotesis.Count > 0) {
					foreach (FleetHypotesis fleet in fleetHypotesis.OrderBy(pf => pf.Fuel).ThenBy(pf => pf.Duration <= minDuration)) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"checking {mission} fleet to: {fleet.Destination}");
						if (CheckFuel(fleet, celestial)) {
							fleetId = await DispatchFleet(fleet.Origin, fleet.Ships, fleet.Destination, fleet.Mission, fleet.Speed, payload, _tbotInstance.UserData.userInfo.Class, force: true, validateShips: false, reduceExpeditionPayload: false, onConfirmedFleet: onConfirmedFleet);
						if (fleetId == (int)SendFleetCode.AmbiguousDispatch) return;

							if (fleetId != (int)SendFleetCode.GenericError &&
    fleetId != (int)SendFleetCode.AfterSleepTime &&
    fleetId != (int)SendFleetCode.NotEnoughSlots)
{
    possibleFleet = fleet;
    AlreadySent = true;
    break;
}

						}
					}
				}
			}

			bool hasMoon = _tbotInstance.UserData.celestials.Count(c => c.HasCoords(new Coordinate(celestial.Coordinate.Galaxy, celestial.Coordinate.System, celestial.Coordinate.Position, Celestials.Moon))) == 1;
			if (!AlreadySent && hasMoon && !TryGetTimer("GhostSleepTimer", out Timer val)) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Fleetsave from {celestial.ToString()} no {mission} possible (missing fuel?), checking for switch if has Moon");
				decimal speed = 10;
				AlreadySent = await _tbotInstance.TelegramSwitch(speed, celestial);
			}

			if (!AlreadySent) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Fleetsave from {celestial.Coordinate.ToString()} no suitable destination found, you gonna get hit!");
				await _tbotInstance.SendTelegramMessage($"Fleetsave from {celestial.Coordinate.ToString()} No destination found!, you gonna get hit!");
				return;
			}



			if ((bool)_tbotInstance.InstanceSettings.SleepMode.AutoFleetSave.Recall && AlreadySent)
{
    if (fleetId > 0)
    {
        Fleet fleet = _tbotInstance.UserData.fleets.SingleOrDefault(f => f.ID == fleetId);
if (fleet == null) return;
        DateTime time = await _tbotOgameBridge.GetDateTime();
        var interval = ((minDuration / 2) * 1000) + RandomizeHelper.CalcRandomInterval(IntervalType.AMinuteOrTwo);

        if (interval <= 0)
            interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);

        DateTime newTime = time.AddMilliseconds(interval);
        AddTimer($"RecallTimer-{fleetId}", new Timer(RetireFleet, fleet, interval, Timeout.Infinite));

        _tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler,
            $"The fleet will be recalled at {newTime}");
    }
}

	else
{
    if (fleetId > 0)
    {
        Fleet fleet = _tbotInstance.UserData.fleets.Single(f => f.ID == fleetId);
        DateTime returntime = (DateTime)fleet.BackTime;

        _tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler,
            $"Fleet {fleetId} send to {possibleFleet.Mission} on {possibleFleet.Destination}, arrive at {possibleFleet.Duration} fuel consumed: {possibleFleet.Fuel.ToString("#,#", CultureInfo.InvariantCulture)}");

        if (fromTelegram)
            await _tbotInstance.SendTelegramMessage(
                $"Fleet {fleetId} send to {possibleFleet.Mission} on {possibleFleet.Destination}, arrive at {possibleFleet.Duration}, returned at {returntime} fuel consumed: {possibleFleet.Fuel.ToString("#,#", CultureInfo.InvariantCulture)}");
    }
}
		}
		public Task<int> SendFleet(Celestial origin, Ships ships, Coordinate destination, Missions mission, decimal speed, Resources payload = null, CharacterClass playerClass = CharacterClass.NoClass, bool force = false) {
			return DispatchFleet(origin, ships, destination, mission, speed, payload, playerClass, force, validateShips: false, reduceExpeditionPayload: false);
		}

		public Task<int> SendValidatedFleet(Celestial origin, Ships ships, Coordinate destination, Missions mission, decimal speed, Resources payload = null, CharacterClass playerClass = CharacterClass.NoClass, bool force = false) {
			return DispatchFleet(origin, ships, destination, mission, speed, payload, playerClass, force, validateShips: true, reduceExpeditionPayload: false);
		}

		public Task<int> SendExpedition(Celestial origin, Ships ships, Coordinate destination, decimal speed, Resources payload = null, CharacterClass playerClass = CharacterClass.NoClass) {
			return DispatchFleet(origin, ships, destination, Missions.Expedition, speed, payload, playerClass, force: false, validateShips: true, reduceExpeditionPayload: true);
		}

		/// <summary>
		/// Sends a lifeform discovery through the same per-account dispatch gate as
		/// every other state-changing fleet action. The discovery endpoint builds the
		/// fleet itself, so it cannot use DispatchFleet's ship validation pipeline.
		/// </summary>
		public async Task<DiscoveryDispatchResult> SendDiscovery(Celestial origin, Coordinate destination) {
			await EnterDispatchGate();
			try {
				if (origin?.Coordinate == null || destination == null)
					return new DiscoveryDispatchResult(DiscoveryDispatchStatus.Failed, "origin or destination is invalid");

				bool requested = false;
				try {
					// The slot snapshot is part of the dispatch decision. It must be
					// refreshed while the account-wide dispatch gate is held so another
					// worker cannot consume the observed slot between the check and POST.
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					int slotsToLeaveFree = (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree;
					if (_tbotInstance.UserData.slots == null ||
						_tbotInstance.UserData.slots.Free <= slotsToLeaveFree) {
						var result = new DiscoveryDispatchResult(
							DiscoveryDispatchStatus.NoSlots,
							$"only {_tbotInstance.UserData.slots?.Free ?? 0} fleet slot(s) available; {slotsToLeaveFree} reserved");
						_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler,
							$"Discovery not sent from {origin.Coordinate}: {result.Message}.");
						return result;
					}

					if (_uncertainDiscoveryPositions.Contains(DiscoveryKey(destination))) {
						await RefreshUncertainDiscoverySnapshot();
						return new(DiscoveryDispatchStatus.Failed, "an earlier discovery at this position is still uncertain; no action was repeated");
					}
					requested = true;
					var sent = await _ogameService.SendDiscovery(origin, destination);
					if (!sent) {
						_uncertainDiscoveryPositions.Add(DiscoveryKey(destination));
						var result = new DiscoveryDispatchResult(
							DiscoveryDispatchStatus.Rejected,
							"OGame returned an unsuccessful discovery response");
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
							$"Discovery rejected from {origin.Coordinate} to {destination}: {result.Message}.");
						return result;
					}

					// A successful action is confirmed by the wrapper. Refreshing the
					// local snapshot is useful for the next worker, but a refresh failure
					// must not turn an already accepted game action into a retryable error.
					try {
						_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					} catch (Exception refreshException) {
						_tbotInstance.log(LogLevel.Debug, LogSender.FleetScheduler,
							$"Discovery was accepted, but slot refresh failed: {refreshException.Message}");
					}

					return new DiscoveryDispatchResult(DiscoveryDispatchStatus.Sent, "discovery fleet accepted by OGame");
				} catch (Exception e) {
					if (requested) _uncertainDiscoveryPositions.Add(DiscoveryKey(destination));
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
						$"Unable to send discovery fleet from {origin.Coordinate} to {destination}: {e.Message}");
					return ClassifyDiscoveryDispatchException(e);
				}
			} finally {
				_dispatchSemaphore.Release();
			}
		}

		// The historical count-only API cannot reserve exact coordinates. Keep its
		// source contract, but require an explicit reservation before any action.
		public Task<DiscoverySystemDispatchResult> SendSystemDiscovery(Celestial origin, int galaxy, int system, int expectedFleetCount) =>
			SendSystemDiscovery(origin, galaxy, system, expectedFleetCount, null);

		public async Task<DiscoverySystemDispatchResult> SendSystemDiscovery(Celestial origin, int galaxy, int system, int expectedFleetCount, IReadOnlyList<int> positions) {
			if (origin?.Coordinate == null || galaxy <= 0 || system <= 0 || expectedFleetCount <= 0 || expectedFleetCount > 15 ||
				positions == null || positions.Count != expectedFleetCount || positions.Distinct().Count() != positions.Count ||
				positions.Any(position => position < 1 || position > 15))
				return new(DiscoveryDispatchStatus.Failed, message: "an exact valid discovery reservation is required");

			var confirmed = new List<Coordinate>();
			// The worker still plans a system as one batch. Each bridge request is
			// bounded to one launch, so a queued Defender may run between targets.
			foreach (var position in positions) {
				await EnterDispatchGate();
				var destination = new Coordinate(galaxy, system, position, Celestials.Planet);
				bool requested = false;
				try {
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					int reserved = (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree;
					int usable = Math.Max(0, (_tbotInstance.UserData.slots?.Free ?? 0) - reserved);
					int needed = confirmed.Count == 0 ? expectedFleetCount : 1;
					if (usable < needed)
						return new(DiscoveryDispatchStatus.NoSlots, confirmed, $"only {usable} fleet slot(s) available for the reserved discoveries");
					if (_uncertainDiscoveryPositions.Contains(DiscoveryKey(destination))) {
						await RefreshUncertainDiscoverySnapshot();
						return new(DiscoveryDispatchStatus.Failed, confirmed, "an earlier discovery at this position is still uncertain; no action was repeated");
					}
					requested = true;
					var result = await _ogameService.SendBoundedSystemDiscovery(origin, galaxy, system, 1, new[] { position });
					var coordinates = result?.Coordinates;
					if (coordinates == null || coordinates.Count > 1 || coordinates.Any(c =>
						c == null || c.Type != Celestials.Planet || c.Galaxy != galaxy || c.System != system || c.Position != position)) {
						_uncertainDiscoveryPositions.Add(DiscoveryKey(destination));
						return new(DiscoveryDispatchStatus.Failed, confirmed, "invalid bounded discovery movement evidence");
					}
					confirmed.AddRange(coordinates);
					if (coordinates.Count == 0) _uncertainDiscoveryPositions.Add(DiscoveryKey(destination));
					if (!result.Complete || coordinates.Count != 1)
						return new(DiscoveryDispatchStatus.Failed, confirmed, result.Error ?? "bounded discovery stopped before its reserved position was confirmed");
				} catch (Exception e) {
					if (requested) _uncertainDiscoveryPositions.Add(DiscoveryKey(destination));
					return new(ClassifyDiscoveryDispatchException(e).Status, confirmed, e.GetBaseException().Message);
			} finally {
				_dispatchSemaphore.Release();
			}
			}
			return new(DiscoveryDispatchStatus.Sent, confirmed, $"{confirmed.Count} discovery fleets confirmed");
		}

		private static DiscoveryDispatchResult ClassifyDiscoveryDispatchException(Exception exception) {
			var message = exception?.GetBaseException()?.Message?.Trim();
			if (string.IsNullOrWhiteSpace(message))
				message = "unknown discovery dispatch error";

			if (message.Contains("code 100", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("still processing", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("previous request", StringComparison.OrdinalIgnoreCase))
				return new DiscoveryDispatchResult(DiscoveryDispatchStatus.RequestPending, message);

			if (message.Contains("not logged", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("logged out", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("login", StringComparison.OrdinalIgnoreCase))
				return new DiscoveryDispatchResult(DiscoveryDispatchStatus.SessionExpired, message);

			if (message.Contains("no ships", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("not enough ships", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("no pathfinder", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("not enough pathfinder", StringComparison.OrdinalIgnoreCase))
				return new DiscoveryDispatchResult(DiscoveryDispatchStatus.NoShips, message);

			if (message.Contains("slot", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("maximum number of fleets", StringComparison.OrdinalIgnoreCase))
				return new DiscoveryDispatchResult(DiscoveryDispatchStatus.NoSlots, message);

			if (message.Contains("position", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("target is not present", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("cooldown", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("can't send discovery mission", StringComparison.OrdinalIgnoreCase))
				return new DiscoveryDispatchResult(DiscoveryDispatchStatus.PositionUnavailable, message);

			return new DiscoveryDispatchResult(DiscoveryDispatchStatus.Failed, message);
		}

		public bool LastFleetSnapshotWasFresh => _lastFleetSnapshotWasFresh;

		private async Task<int> DispatchFleet(Celestial origin, Ships ships, Coordinate destination, Missions mission, decimal speed, Resources payload, CharacterClass playerClass, bool force, bool validateShips, bool reduceExpeditionPayload, Action<int> onConfirmedFleet = null) {
			await EnterDispatchGate(force);
			try {
				return await SendFleetCore(origin, ships, destination, mission, speed, payload, playerClass, force, validateShips, reduceExpeditionPayload, onConfirmedFleet);
			} finally {
				_dispatchSemaphore.Release();
			}
		}

		private async Task<int> SendFleetCore(Celestial origin, Ships ships, Coordinate destination, Missions mission, decimal speed, Resources payload, CharacterClass playerClass, bool force, bool validateShips, bool reduceExpeditionPayload, Action<int> onConfirmedFleet) {
			_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Sending fleet from {origin.Coordinate.ToString()} to {destination.ToString()}. Mission: {mission.ToString()}. Speed: {(speed * 10).ToString()}% Ships: {ships?.ToString() ?? "none"}");

			if (playerClass == CharacterClass.NoClass)
				playerClass = _tbotInstance.UserData.userInfo.Class;

			if (ships == null || !ships.HasMovableFleet()) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to send fleet: there are no ships to send");
				return (int) SendFleetCode.GenericError;
			}
			if (mission == Missions.Expedition && ships.IsOnlyProbes()) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to send fleet: cannot send an expedition with no ships");
				return (int) SendFleetCode.GenericError;
			}
			if (origin.Coordinate.IsSame(destination)) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to send fleet: origin and destination are the same");
				return (int) SendFleetCode.GenericError;
			}
			if (destination.Galaxy <= 0 || destination.Galaxy > _tbotInstance.UserData.serverData.Galaxies || destination.Position <= 0 || destination.Position > 17) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to send fleet: invalid destination");
				return (int) SendFleetCode.GenericError;
			}
			if (destination.System <= 0 || destination.System > _tbotInstance.UserData.serverData.Systems) {
				if (_tbotInstance.UserData.serverData.DonutGalaxy) {
					if (destination.System <= 0) {
						destination.System += _tbotInstance.UserData.serverData.Systems;
					} else if (destination.System > _tbotInstance.UserData.serverData.Systems) {
						destination.System -= _tbotInstance.UserData.serverData.Systems;
					}
				} else {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to send fleet: invalid destination");
					return (int) SendFleetCode.GenericError;
				}
			}

			if (!_calcService.GetValidSpeedsForClass(playerClass).Any(s => s == speed)) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
					"Unable to send fleet: speed not available for your class");
				return (int)SendFleetCode.GenericError;
			}

			origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.LFBonuses);

			if (validateShips) {
				var latestShips = await _ogameService.GetShips(origin);
				if (latestShips == null || !latestShips.HasAtLeast(ships, 1)) {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
						$"Unable to send fleet from {origin.Coordinate}: the requested ships are no longer available.");
					return (int)SendFleetCode.GenericError;
				}
				origin.Ships = latestShips;
			}

			FleetPrediction fleetPrediction = _calcService.CalcFleetPrediction(
				origin.Coordinate,
				destination,
				ships,
				mission,
				speed,
				_tbotInstance.UserData.researches,
				_tbotInstance.UserData.serverData,
				origin.LFBonuses,
				_tbotInstance.UserData.userInfo.Class,
				_tbotInstance.UserData.allianceClass
			);

			_tbotInstance.log(LogLevel.Debug, LogSender.FleetScheduler,
				$"Calculated flight time (one-way): {TimeSpan.FromSeconds(fleetPrediction.Time)}");

			var flightTime = mission switch
			{
				Missions.Deploy => fleetPrediction.Time,
				Missions.Expedition => (long)Math.Round((double)(2 * fleetPrediction.Time) + 3600, 0, MidpointRounding.ToPositiveInfinity),
				_ => (long)Math.Round((double)(2 * fleetPrediction.Time), 0, MidpointRounding.ToPositiveInfinity)
			};
			_tbotInstance.log(LogLevel.Debug, LogSender.FleetScheduler,
				$"Calculated flight time (full trip): {TimeSpan.FromSeconds(flightTime)}");
			_tbotInstance.log(LogLevel.Debug, LogSender.FleetScheduler,
				$"Calculated flight fuel: {fleetPrediction.Fuel}");

			origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Resources);

			if (origin.Resources.Deuterium < fleetPrediction.Fuel) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
					"Unable to send fleet: not enough deuterium!");
				return (int)SendFleetCode.GenericError;
			}

			long fuelCapacity = _calcService.CalcFleetFuelCapacity(
				ships,
				_tbotInstance.UserData.serverData,
				_tbotInstance.UserData.researches.HyperspaceTechnology,
				_tbotInstance.UserData.userInfo.Class,
				_tbotInstance.UserData.serverData.ProbeCargo
			);

			if (fuelCapacity != 0 && fuelCapacity < fleetPrediction.Fuel) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
					"Unable to send fleet: ships don't have enough fuel capacity!");
				return (int)SendFleetCode.GenericError;
			}

			bool ignoreSleepForExpeditions =
				mission == Missions.Expedition &&
				(bool)_tbotInstance.InstanceSettings.Expeditions.IgnoreSleep;

			if (
				(bool)_tbotInstance.InstanceSettings.SleepMode.Active &&
				DateTime.TryParse((string)_tbotInstance.InstanceSettings.SleepMode.GoToSleep, out DateTime goToSleep) &&
				DateTime.TryParse((string)_tbotInstance.InstanceSettings.SleepMode.WakeUp, out DateTime wakeUp) &&
				!force
			)
			{
				DateTime time = await _tbotOgameBridge.GetDateTime();

				if (GeneralHelper.ShouldSleep(time, goToSleep, wakeUp)) {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
						"Unable to send fleet: bed time has passed");
					return (int)SendFleetCode.AfterSleepTime;
				}

				if (goToSleep >= wakeUp) wakeUp = wakeUp.AddDays(1);
				if (goToSleep < time) goToSleep = goToSleep.AddDays(1);
				if (wakeUp < time) wakeUp = wakeUp.AddDays(1);

				DateTime returnTime = time.AddSeconds(flightTime);

				if (returnTime >= goToSleep && returnTime <= wakeUp && !ignoreSleepForExpeditions) {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
						"Unable to send fleet: it would come back during sleep time");
					return (int)SendFleetCode.AfterSleepTime;
				}
			}
			_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
			int slotsToLeaveFree = (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree;
			if (_tbotInstance.UserData.slots.Free == 0) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to send fleet, no slots available");
				return (int) SendFleetCode.NotEnoughSlots;
			} else if (_tbotInstance.UserData.slots.Free > slotsToLeaveFree || force) {
				if (payload == null)
					payload = new();
				var dispatchKey = $"{origin.ID}|{destination.Galaxy}:{destination.System}:{destination.Position}:{destination.Type}|{mission}";
				if (_ambiguousDispatches.TryGetValue(dispatchKey, out var uncertainKnownIds)) {
					var reconciled = await TryReconcileAmbiguousDispatch(uncertainKnownIds.KnownFleetIds, origin, uncertainKnownIds.Ships, destination, mission);
					if (reconciled == null) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
							$"An earlier dispatch from {origin.Coordinate} to {destination} is still uncertain; no new fleet request was sent.");
						return (int)SendFleetCode.AmbiguousDispatch;
					}
					_ambiguousDispatches.Remove(dispatchKey);
					lock (_fleetLock) _tbotInstance.UserData.fleets = FleetSnapshotPolicy.AddDispatchedFleet(_tbotInstance.UserData.fleets, reconciled);
					return reconciled.ID;
				}
				HashSet<int> knownFleetIds = null;
				try {
					var beforeSend = await _ogameService.GetFleets();
					if (beforeSend != null) knownFleetIds = beforeSend.Where(fleet => fleet != null).Select(fleet => fleet.ID).ToHashSet();
				} catch (Exception snapshotException) {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Pre-dispatch movement snapshot failed: {snapshotException.Message}");
				}
				var requestedShips = Newtonsoft.Json.JsonConvert.DeserializeObject<Ships>(Newtonsoft.Json.JsonConvert.SerializeObject(ships));
				try {
					if (payload.Metal < 0)
						payload.Metal = 0;
					if (payload.Crystal < 0)
						payload.Crystal = 0;
					if (payload.Deuterium < 0)
						payload.Deuterium = 0;
					if (payload.Food < 0)
						payload.Food = 0;

					if (reduceExpeditionPayload && mission == Missions.Expedition) {
						var cargoCapacity = _calcService.CalcFleetCapacity(
							ships,
							_tbotInstance.UserData.serverData,
							_tbotInstance.UserData.researches.HyperspaceTechnology,
							origin.LFBonuses,
							_tbotInstance.UserData.userInfo.Class,
							_tbotInstance.UserData.serverData.ProbeCargo);
						var requestedPayload = payload.Deuterium;
						payload.Deuterium = ExpeditionPayloadPolicy.GetDeuteriumPayload(
							requestedPayload,
							origin.Resources.Deuterium,
							fleetPrediction.Fuel,
							cargoCapacity);
						if (payload.Deuterium != requestedPayload) {
							_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler,
								$"Reduced expedition deuterium payload from {requestedPayload} to {payload.Deuterium}; flight fuel was reserved first.");
						}
					}
					Fleet newFleet = await _ogameService.SendFleet(origin, ships, destination, mission, speed, payload);
					if (newFleet == null) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Unable to send fleet from {origin.Coordinate.ToString()}: no ships to send.");
						return (int) SendFleetCode.GenericError;
					}
					if (newFleet.ID <= 0) throw new InvalidOperationException("Fleet response contains no valid movement ID.");
					lock (_fleetLock) {
						_tbotInstance.UserData.fleets = FleetSnapshotPolicy.AddDispatchedFleet(
							_tbotInstance.UserData.fleets,
							newFleet);
					}
					onConfirmedFleet?.Invoke(newFleet.ID);
					_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, "Fleet sent successfully!");
					try { _tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots(); }
					catch (Exception e) { _tbotInstance.log(LogLevel.Debug, LogSender.FleetScheduler, $"Fleet {newFleet.ID} was accepted, but slot refresh failed: {e.Message}"); }
					return newFleet.ID;
				} catch (Exception e) {
					if (e is OgamedException rejection && rejection.StatusCode == HttpStatusCode.BadRequest) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Fleet rejected: {e.Message}");
						return (int)SendFleetCode.GenericError;
					}
					var reconciledFleet = await TryReconcileAmbiguousDispatch(
						knownFleetIds,
						origin,
						requestedShips,
						destination,
						mission);
					if (reconciledFleet != null) {
						lock (_fleetLock) {
							_tbotInstance.UserData.fleets = FleetSnapshotPolicy.AddDispatchedFleet(
								_tbotInstance.UserData.fleets,
								reconciledFleet);
						}
						onConfirmedFleet?.Invoke(reconciledFleet.ID);
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler,
							$"Fleet dispatch response was ambiguous, but fleet {reconciledFleet.ID} was found during reconciliation. No retry was performed.");
						return reconciledFleet.ID;
					}

					_tbotInstance.log(LogLevel.Error, LogSender.FleetScheduler, $"Unable to send fleet: an exception has occurred: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Stacktrace: {e.StackTrace}");
					_ambiguousDispatches[dispatchKey] = (knownFleetIds, requestedShips);
					return (int)SendFleetCode.AmbiguousDispatch;
				}
			} else {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to send fleet, no slots available");
				return (int) SendFleetCode.NotEnoughSlots;
			}
		}

		private async Task<Fleet> TryReconcileAmbiguousDispatch(
			HashSet<int> knownFleetIds,
			Celestial origin,
			Ships ships,
			Coordinate destination,
			Missions mission) {
			if (knownFleetIds == null) return null;
			try {
				var currentFleets = await _ogameService.GetFleets();
				var matches = currentFleets?.Where(fleet =>
					fleet != null && fleet.ID > 0 && !fleet.ReturnFlight &&
					!knownFleetIds.Contains(fleet.ID) &&
					fleet.Mission == mission &&
					fleet.Origin != null &&
					fleet.Origin.IsSame(origin.Coordinate) &&
					fleet.Destination != null &&
					fleet.Destination.IsSame(destination) &&
					fleet.Ships != null &&
					fleet.Ships.HasAtLeast(ships, 1) &&
					ships.HasAtLeast(fleet.Ships, 1)).ToList();
				return matches?.Count == 1 ? matches[0] : null;
			} catch (Exception reconciliationException) {
				_tbotInstance.log(LogLevel.Debug, LogSender.FleetScheduler,
					$"Unable to reconcile ambiguous fleet dispatch: {reconciliationException.Message}");
				return null;
			}
		}

		public async Task CancelFleet(Fleet fleet) {
			try {
				bool isUnderAttack = await _ogameService.IsUnderAttack();
				if (isUnderAttack) {
					var attacks = await _ogameService.GetAttacks();
					var attacksOnOrigin = attacks
						.Where(a => a.Destination.IsSame(fleet.Origin))
						.OrderByDescending(a => a.ArriveIn)
						.ToList();

					if (attacksOnOrigin.Any()) {
						var lastAttack = attacksOnOrigin.First();
						var currentTime = await _tbotOgameBridge.GetDateTime();
						var currentFleet = _tbotInstance.UserData.fleets.SingleOrDefault(f => f.ID == fleet.ID);
						if (currentFleet != null && currentFleet.ArriveIn > 0) {
							long estimatedReturnSeconds = currentFleet.ArriveIn * 2;

							_tbotInstance.log(LogLevel.Debug, LogSender.FleetScheduler, $"[RECALL-CHECK] Fleet {fleet.ID} would return in ~{estimatedReturnSeconds}s if recalled now");
							_tbotInstance.log(LogLevel.Debug, LogSender.FleetScheduler, $"[RECALL-CHECK] Last attack on {fleet.Origin} arrives in {lastAttack.ArriveIn}s");
							if (estimatedReturnSeconds < lastAttack.ArriveIn) {
								_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"[RECALL-BLOCKED] Fleet {fleet.ID} would return in {estimatedReturnSeconds}s but attack arrives in {lastAttack.ArriveIn}s - DELAYING RECALL!");

								var newRecallDelay = (lastAttack.ArriveIn - estimatedReturnSeconds + (lastAttack.ArriveIn / 100 * 30)) * 1000;
								if (newRecallDelay < 60000) newRecallDelay = 60000;

								_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"[RECALL-RESCHEDULED] New recall in {newRecallDelay/1000}s");

								DisposeAndRemoveTimer($"RecallTimer-{fleet.ID.ToString()}");
                                AddTimer($"RecallTimer-{fleet.ID.ToString()}", new Timer(RetireFleet, fleet, (int)newRecallDelay, Timeout.Infinite));

								await _tbotInstance.SendFeatureNotification(_tbotInstance.InstanceSettings.Defender,
									$"⚠️ Recall delayed! Attack still incoming on {fleet.Origin}. New recall in {newRecallDelay/1000}s",
									NotificationKind.Warning);
								return;
							} else {
								_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"[RECALL-OK] Fleet {fleet.ID} return time ({estimatedReturnSeconds}s) is AFTER last attack ({lastAttack.ArriveIn}s) - proceeding with recall");
							}
						}
					}
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"[RECALL-CHECK] Error checking attacks before recall: {e.Message}");
			}

			try {
				await Task.Delay((int) RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
				_tbotInstance.log(LogLevel.Debug, LogSender.FleetScheduler, $"Recall Fleet with ID: {fleet.ID}");
				await _ogameService.CancelFleet(fleet);
				await Task.Delay((int) RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
				var updatedFleets = await UpdateFleets();
				lock (_fleetLock) {
					_tbotInstance.UserData.fleets = updatedFleets;
				}
				Fleet recalledFleet = _tbotInstance.UserData.fleets.SingleOrDefault(f => f.ID == fleet.ID) ?? new() { ID = (int) SendFleetCode.GenericError };
				if (recalledFleet.ID == (int) SendFleetCode.GenericError) {
					_tbotInstance.log(LogLevel.Error, LogSender.FleetScheduler, "Unable to recall fleet: an unknon error has occurred, already recalled ?.");
				} else {
					_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Fleet recalled. Arrival time: {recalledFleet.BackTime.ToString()}");
					await _tbotInstance.SendFeatureNotification(_tbotInstance.InstanceSettings.Defender,
						$"Fleet recalled. Arrival time: {recalledFleet.BackTime.ToString()}",
						NotificationKind.Success);
					return;
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Error, LogSender.FleetScheduler, $"Unable to recall fleet: an exception has occurred: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Stacktrace: {e.StackTrace}");
				return;
			} finally {
				DisposeAndRemoveTimer($"RecallTimer-{fleet.ID.ToString()}");
}
		}
		public async Task<List<Fleet>> UpdateFleets() {
			var result = await UpdateFleetsWithFreshness();
			return (result.Fleets ?? Array.Empty<Fleet>()).ToList();
		}

		public async Task<FleetSnapshotResult> UpdateFleetsWithFreshness() {
			await EnterDispatchGate();
			try {
				_lastFleetSnapshotWasFresh = false;
				try {
					var fleets = await _ogameService.GetFleets();
					if (fleets == null)
						throw new InvalidOperationException("OGame returned no fleet snapshot.");
					var snapshot = fleets
						.Where(fleet => fleet != null)
						.ToList();
					_lastFleetSnapshotWasFresh = true;

					lock (_fleetLock) {
						_tbotInstance.UserData.fleets = snapshot;
						return new FleetSnapshotResult(snapshot.ToList(), true);
					}
				} catch (Exception e) {
					_tbotInstance.log(LogLevel.Debug, LogSender.Tbot, $"UpdateFleets() Exception: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.Tbot, $"Stacktrace: {e.StackTrace}");
					lock (_fleetLock) {
						var cached = (_tbotInstance?.UserData?.fleets ?? new List<Fleet>())
							.Where(fleet => fleet != null)
							.ToList();
						return new FleetSnapshotResult(cached, false);
					}
				}
			} finally {
				_dispatchSemaphore.Release();
			}
		}

		public async void RetireFleet(object fleet) {
			try {
				await CancelFleet((Fleet)fleet);
			} catch (Exception ex) {
				_tbotInstance?.log(LogLevel.Error, LogSender.FleetScheduler, $"RetireFleet callback failed: {ex.Message}");
			}
		}


		private async void GhostandSleepAfterFleetsReturnAll(object state) {
			try {
				DisposeAndRemoveTimer("GhostSleepTimer");

				var celestialsToFleetsave = await _tbotOgameBridge.UpdateCelestials();
				var moons = celestialsToFleetsave.Where(c => c.Coordinate.Type == Celestials.Moon).ToList();
				var targets = moons.Count > 0 ? moons : celestialsToFleetsave.Where(c => c.Coordinate.Type == Celestials.Planet).ToList();

				foreach (Celestial celestial in targets)
					await AutoFleetSave(celestial, false, _tbotInstance.SleepDuration, false, _tbotInstance.TelegramUserData.Mission, true);

				await _tbotInstance.SleepNow(_tbotInstance.NextWakeUpTime);
			} catch (Exception ex) {
				_tbotInstance?.log(LogLevel.Error, LogSender.SleepMode, $"GhostSleep(all) timer callback failed: {ex.Message}");
			}
		}

		private async void GhostandSleepAfterFleetsReturn(object state) {
			try {
				DisposeAndRemoveTimer("GhostSleepTimer");

				await AutoFleetSave(_tbotInstance.TelegramUserData.CurrentCelestialToSave, false, _tbotInstance.SleepDuration, false, _tbotInstance.TelegramUserData.Mission, true);
				await _tbotInstance.SleepNow(_tbotInstance.NextWakeUpTime);
			} catch (Exception ex) {
				_tbotInstance?.log(LogLevel.Error, LogSender.SleepMode, $"GhostSleep timer callback failed: {ex.Message}");
			}
		}

		private bool CheckFuel(FleetHypotesis fleetHypotesis, Celestial celestial) {
			if (celestial.Resources.Deuterium < fleetHypotesis.Fuel) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Skipping fleetsave from {celestial.ToString()}: not enough fuel!");
				return false;
			}
			if (_calcService.CalcFleetFuelCapacity(fleetHypotesis.Ships, _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo) < fleetHypotesis.Fuel) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Skipping fleetsave from {celestial.ToString()}: ships don't have enough fuel capacity!");
				return false;
			}
			return true;
		}

		private async Task<List<FleetHypotesis>> GetFleetSaveDestination(List<Celestial> source, Celestial origin, DateTime departureDate, long minFlightTime, Missions mission, long maxFuel) {
			var validSpeeds = _tbotInstance.UserData.userInfo.Class == CharacterClass.General ? Speeds.GetGeneralSpeedsList() : Speeds.GetNonGeneralSpeedsList();
			List<FleetHypotesis> possibleFleets = new();
			List<Coordinate> possibleDestinations = new();
			GalaxyInfo galaxyInfo = new();
			origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Resources);
			origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Ships);
			origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.LFBonuses);
			int sys = 0;

			int playerid = _tbotInstance.UserData.userInfo.PlayerID;

			switch (mission) {
				case Missions.Transport:
					for (sys = origin.Coordinate.System - 5; sys <= origin.Coordinate.System + 5; sys++) {
						if (possibleDestinations.Count() == 10) {
							break;
						}
						sys = GeneralHelper.ClampSystem(sys);

                                     var gi = await _ogameService.GetGalaxyInfo(origin.Coordinate.Galaxy, sys);
                                      if (gi?.Planets == null) continue;

                                      foreach (var planet in gi.Planets) {
                                    if (planet != null && !planet.Vacation && !planet.Administrator && !planet.Banned && !planet.StrongPlayer) {
                             possibleDestinations.Add(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet));
                                          }
                            }
					}

					if (possibleDestinations.Count() > 0) {
						foreach (var possibleDestination in possibleDestinations) {
							foreach (var currentSpeed in validSpeeds) {
								FleetPrediction fleetPrediction = _calcService.CalcFleetPrediction(origin.Coordinate, possibleDestination, origin.Ships.GetMovableShips(), mission, currentSpeed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, origin.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

								FleetHypotesis fleetHypotesis = new() {
									Origin = origin,
									Destination = possibleDestination,
									Ships = origin.Ships.GetMovableShips(),
									Mission = mission,
									Speed = currentSpeed,
									Duration = fleetPrediction.Time,
									Fuel = fleetPrediction.Fuel
								};
								if (fleetHypotesis.Duration >= minFlightTime / 2 && fleetHypotesis.Fuel <= maxFuel) {
									possibleFleets.Add(fleetHypotesis);
									break;
								}
							}
						}
					}
					break;

				case Missions.Spy:
					if (origin.Ships.EspionageProbe == 0) {
						_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"No espionageprobe available, skipping to next mission...");
						break;
					}
					for (sys = origin.Coordinate.System - 5; sys <= origin.Coordinate.System + 5; sys++) {
						sys = GeneralHelper.ClampSystem(sys);
						Coordinate destination = new(origin.Coordinate.Galaxy, sys, 16, Celestials.Planet);
						foreach (var currentSpeed in validSpeeds) {
							FleetPrediction fleetPrediction = _calcService.CalcFleetPrediction(origin.Coordinate, destination, origin.Ships.GetMovableShips(), mission, currentSpeed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, origin.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

							FleetHypotesis fleetHypotesis = new() {
								Origin = origin,
								Destination = destination,
								Ships = origin.Ships.GetMovableShips(),
								Mission = mission,
								Speed = currentSpeed,
								Duration = fleetPrediction.Time,
								Fuel = fleetPrediction.Fuel
							};
							if (fleetHypotesis.Duration >= minFlightTime / 2 && fleetHypotesis.Fuel <= maxFuel) {
								possibleFleets.Add(fleetHypotesis);
								break;
							}
						}
					}
					break;

				case Missions.Colonize:
					if (origin.Ships.ColonyShip == 0) {
						_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"No colony ship available, skipping to next mission...");
						break;
					}
					galaxyInfo = await _ogameService.GetGalaxyInfo(origin.Coordinate);

					for (sys = origin.Coordinate.System - 5; sys <= origin.Coordinate.System + 5; sys++) {
						int pos = 0;
						sys = GeneralHelper.ClampSystem(sys);
						galaxyInfo = await _ogameService.GetGalaxyInfo(origin.Coordinate.Galaxy, sys);
						foreach (var planet in galaxyInfo.Planets) {
					pos++;
					if (pos >= 1 && pos <= 15 && planet == null) {
						possibleDestinations.Add(new(origin.Coordinate.Galaxy, sys, pos));
					}
				}
					}

					if (possibleDestinations.Count() > 0) {
						foreach (var possibleDestination in possibleDestinations) {
							foreach (var currentSpeed in validSpeeds) {
								FleetPrediction fleetPrediction = _calcService.CalcFleetPrediction(origin.Coordinate, possibleDestination, origin.Ships.GetMovableShips(), mission, currentSpeed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, origin.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

								FleetHypotesis fleetHypotesis = new() {
									Origin = origin,
									Destination = possibleDestination,
									Ships = origin.Ships.GetMovableShips(),
									Mission = mission,
									Speed = currentSpeed,
									Duration = fleetPrediction.Time,
									Fuel = fleetPrediction.Fuel
								};
								if (fleetHypotesis.Duration >= minFlightTime / 2 && fleetHypotesis.Fuel <= maxFuel) {
									possibleFleets.Add(fleetHypotesis);
									break;
								}
							}
						}
					}
					break;

				case Missions.Harvest:
					if (origin.Ships.Recycler == 0) {
						_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"No recycler available, skipping to next mission...");
						break;
					}
					for (sys = origin.Coordinate.System - 5; sys <= origin.Coordinate.System + 5; sys++) {
						sys = GeneralHelper.ClampSystem(sys);
						galaxyInfo = await _ogameService.GetGalaxyInfo(origin.Coordinate.Galaxy, sys);
						foreach (var planet in galaxyInfo.Planets) {
							if (planet != null && planet.Debris != null && planet.Debris.Resources.TotalResources > 0) {
								possibleDestinations.Add(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Debris));
							}
						}
					}


					if (possibleDestinations.Count() > 0) {
						foreach (var possibleDestination in possibleDestinations) {
							foreach (var currentSpeed in validSpeeds) {
								FleetPrediction fleetPrediction = _calcService.CalcFleetPrediction(origin.Coordinate, possibleDestination, origin.Ships.GetMovableShips(), mission, currentSpeed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, origin.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

								FleetHypotesis fleetHypotesis = new() {
									Origin = origin,
									Destination = possibleDestination,
									Ships = origin.Ships.GetMovableShips(),
									Mission = mission,
									Speed = currentSpeed,
									Duration = fleetPrediction.Time,
									Fuel = fleetPrediction.Fuel
								};
								if (fleetHypotesis.Duration >= minFlightTime / 2 && fleetHypotesis.Fuel <= maxFuel) {
									possibleFleets.Add(fleetHypotesis);
									break;
								}
							}
						}
					}
					break;

				case Missions.Deploy:
					possibleDestinations = _tbotInstance.UserData.celestials
						.Where(planet => planet.ID != origin.ID)
						.Where(planet => (planet.Coordinate.Type == Celestials.Moon))
						.Select(planet => planet.Coordinate)
						.ToList();

					if (possibleDestinations.Count == 0) {
						possibleDestinations = _tbotInstance.UserData.celestials
							.Where(planet => planet.ID != origin.ID)
							.Select(planet => planet.Coordinate)
							.ToList();
					}

					foreach (var possibleDestination in possibleDestinations) {
						foreach (var currentSpeed in validSpeeds) {
							FleetPrediction fleetPrediction = _calcService.CalcFleetPrediction(origin.Coordinate, possibleDestination, origin.Ships.GetMovableShips(), mission, currentSpeed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, origin.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

							FleetHypotesis fleetHypotesis = new() {
								Origin = origin,
								Destination = possibleDestination,
								Ships = origin.Ships.GetMovableShips(),
								Mission = mission,
								Speed = currentSpeed,
								Duration = fleetPrediction.Time,
								Fuel = fleetPrediction.Fuel
							};
							if (fleetHypotesis.Duration >= minFlightTime && fleetHypotesis.Fuel <= maxFuel) {
								possibleFleets.Add(fleetHypotesis);
								break;
							}
						}
					}
					break;

				default:
					break;
			}

			if (possibleFleets.Count() > 0) {
				return possibleFleets;

			} else {
				return new List<FleetHypotesis>();
			}
		}

		public async Task<int> HandleMinerTransport(Celestial origin, Celestial destination, Celestial differentDestination, Resources resources, Buildables buildable = Buildables.Null, Buildings maxBuildings = null, Facilities maxFacilities = null, Facilities maxLunarFacilities = null, AutoMinerSettings autoMinerSettings = null) {
			try {
				if (origin.ID == destination.ID) {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Skipping transport: origin and destination are the same.");
					return 0;
				} else if (origin.ID == 0) {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Skipping transport: unable to parse transport origin.");
					return 0;
				} else {
					var missingResources = resources.Difference(destination.Resources);
					Resources resToLeave = new(0, 0, 0);
					if ((long) _tbotInstance.InstanceSettings.Brain.Transports.DeutToLeaveOnMoons > 0)
						resToLeave.Deuterium = (long) _tbotInstance.InstanceSettings.Brain.Transports.DeutToLeaveOnMoons;

					origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Resources);
					if (origin.Resources.IsEnoughFor(missingResources, resToLeave)) {
						origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Ships);
						origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.LFBonuses);
						Buildables preferredShip = Buildables.SmallCargo;
						if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.Brain.Transports.CargoType, true, out preferredShip)) {
							_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to parse CargoType. Falling back to default SmallCargo");
							preferredShip = Buildables.SmallCargo;
						}
						
						origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.LFBonuses);
						float cargoBonus = origin.LFBonuses.GetShipCargoBonus(preferredShip);
						long idealShips = _calcService.CalcShipNumberForPayload(missingResources, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
						Ships ships = new();
						Ships tempShips = new();
						tempShips.Add(preferredShip, 1);
						var flightPrediction = _calcService.CalcFleetPrediction(origin.Coordinate, destination.Coordinate, tempShips, Missions.Transport, Speeds.HundredPercent, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, origin.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
						long flightTime = flightPrediction.Time;
						var availableShips = origin.Ships.GetAmount(preferredShip);
						if (buildable != Buildables.Null) {
							int level = _calcService.GetNextLevel(destination, buildable);
							long buildTime = _calcService.CalcProductionTime(buildable, level, _tbotInstance.UserData.serverData, destination.Facilities);
							if (maxBuildings != null && maxFacilities != null && maxLunarFacilities != null && autoMinerSettings != null) {
								var tempCelestial = destination;
								while (flightTime * 2 >= buildTime && idealShips <= availableShips) {
									tempCelestial.SetLevel(buildable, level);
									if (buildable != Buildables.SolarSatellite && buildable != Buildables.Crawler && buildable != Buildables.SpaceDock) {
										tempCelestial.Fields.Built += 1;
									}
									var nextBuildable = Buildables.Null;
									if (tempCelestial.Coordinate.Type == Celestials.Planet) {
										tempCelestial.Resources.Energy += _calcService.GetProductionEnergyDelta(buildable, level, _tbotInstance.UserData.researches.EnergyTechnology, 1, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff.Engineer, _tbotInstance.UserData.staff.IsFull);
										tempCelestial.ResourcesProduction.Energy.Available += _calcService.GetProductionEnergyDelta(buildable, level, _tbotInstance.UserData.researches.EnergyTechnology, 1, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff.Engineer, _tbotInstance.UserData.staff.IsFull);
										tempCelestial.Resources.Energy -= _calcService.GetRequiredEnergyDelta(buildable, level);
										tempCelestial.ResourcesProduction.Energy.Available -= _calcService.GetRequiredEnergyDelta(buildable, level);
										nextBuildable = _calcService.GetNextBuildingToBuild(tempCelestial as Planet, _tbotInstance.UserData.researches, maxBuildings, maxFacilities, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff, _tbotInstance.UserData.serverData, autoMinerSettings, 1);
									} else {
										nextBuildable = _calcService.GetNextLunarFacilityToBuild(tempCelestial as Moon, _tbotInstance.UserData.researches, maxLunarFacilities);
									}
									if ((nextBuildable != Buildables.Null) && (buildable != Buildables.SolarSatellite)) {
										var nextLevel = _calcService.GetNextLevel(tempCelestial, nextBuildable);
										var newMissingRes = missingResources.Sum(_calcService.CalcPrice(nextBuildable, nextLevel));

										if (origin.Resources.IsEnoughFor(newMissingRes, resToLeave)) {
											var newIdealShips = _calcService.CalcShipNumberForPayload(newMissingRes, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
											if (newIdealShips <= origin.Ships.GetAmount(preferredShip)) {
												idealShips = newIdealShips;
												missingResources = newMissingRes;
												buildTime += _calcService.CalcProductionTime(nextBuildable, nextLevel, _tbotInstance.UserData.serverData, tempCelestial.Facilities);
												_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Sending resources for {nextBuildable.ToString()} level {nextLevel} too");
												level = nextLevel;
												buildable = nextBuildable;
											} else {
												break;
											}
										} else {
											break;
										}
									} else {
										break;
									}
								}
							}
						}

						if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.Transports, "RoundResources") && (bool) _tbotInstance.InstanceSettings.Brain.Transports.RoundResources) {
							missingResources = missingResources.Round();
							idealShips = _calcService.CalcShipNumberForPayload(missingResources, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
						}

						bool doMultipleTransports = (idealShips > origin.Ships.GetAmount(preferredShip) && (bool) _tbotInstance.InstanceSettings.Brain.Transports.DoMultipleTransportIsNotEnoughShipButSamePosition) &&
							(origin.Coordinate.Galaxy == destination.Coordinate.Galaxy &&
							origin.Coordinate.System == destination.Coordinate.System &&
							origin.Coordinate.Position == destination.Coordinate.Position) ?
								true:
								false;

						if (idealShips <= origin.Ships.GetAmount(preferredShip) || (doMultipleTransports && !origin.Ships.IsEmpty())) {
							if (doMultipleTransports) {
								ships.Add(preferredShip, origin.Ships.GetAmount(preferredShip));
							} else {
								ships.Add(preferredShip, idealShips);
							}

							if (destination.Coordinate.Type == Celestials.Planet) {
								destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.ResourceSettings);
								destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.Buildings);
								destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.ResourcesProduction);

								float metProdInASecond = destination.ResourcesProduction.Metal.CurrentProduction / (float) 3600;
								float cryProdInASecond = destination.ResourcesProduction.Crystal.CurrentProduction / (float) 3600;
								float deutProdInASecond = destination.ResourcesProduction.Deuterium.CurrentProduction / (float) 3600;
								var metProdInFlightTime = metProdInASecond * flightTime;
								var cryProdInFlightTime = cryProdInASecond * flightTime;
								var deutProdInFlightTime = deutProdInASecond * flightTime;

								if (
									(metProdInASecond == 0 && missingResources.Metal > 0) ||
									(cryProdInFlightTime == 0 && missingResources.Crystal > 0) ||
									(deutProdInFlightTime == 0 && missingResources.Deuterium > 0) ||
									missingResources.Metal >= metProdInFlightTime ||
									missingResources.Crystal >= cryProdInFlightTime ||
									missingResources.Deuterium >= deutProdInFlightTime ||
									resources.Metal > destination.ResourcesProduction.Metal.StorageCapacity ||
									resources.Crystal > destination.ResourcesProduction.Crystal.StorageCapacity ||
									resources.Deuterium > destination.ResourcesProduction.Deuterium.StorageCapacity
								) {
									if (differentDestination != null && differentDestination.ID != 0)
										destination = differentDestination;
									_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Sending {ships.ToString()} with {missingResources.TransportableResources} from {origin.ToString()} to {destination.ToString()}");
									return await SendFleet(origin, ships, destination.Coordinate, Missions.Transport, Speeds.HundredPercent, missingResources, _tbotInstance.UserData.userInfo.Class);
								} else {
									_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, "Skipping transport: it is quicker to wait for production.");
									return (int) SendFleetCode.QuickerToWaitForProduction;
								}
							} else {
								if (differentDestination != null && differentDestination.ID != 0)
									destination = differentDestination;
								_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Sending {ships.ToString()} with {missingResources.TransportableResources} from {origin.ToString()} to {destination.ToString()}");
								return await SendFleet(origin, ships, destination.Coordinate, Missions.Transport, Speeds.HundredPercent, missingResources, _tbotInstance.UserData.userInfo.Class);
							}
						} else {
							_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, "Skipping transport: not enough ships to transport required resources.");
							return 0;
						}
					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Skipping transport: not enough resources in {origin.ToString()}. Needed: {missingResources.TransportableResources} - Available: {origin.Resources.TransportableResources}");
						return (int) SendFleetCode.NotEnoughRessources;
					}
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Error, LogSender.FleetScheduler, $"HandleMinerTransport Exception: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Stacktrace: {e.StackTrace}");
				return 0;
			}
		}

		public async Task<int> HandleMinerTransport(Celestial origin, Celestial destination, Celestial differentDestination, Resources resources, LFBuildables buildable = LFBuildables.None, LFBuildings maxLFBuildings = null, bool preventIfMoreExpensiveThanNextMine = false) {
			try {
				if (origin.ID == destination.ID) {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Skipping transport: origin and destination are the same.");
					return 0;
				} else if (origin.ID == 0) {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Skipping transport: unable to parse transport origin.");
					return 0;
				} else {
					var missingResources = resources.Difference(destination.Resources);
					Resources resToLeave = new(0, 0, 0);
					if ((long) _tbotInstance.InstanceSettings.Brain.Transports.DeutToLeaveOnMoons > 0)
						resToLeave.Deuterium = (long) _tbotInstance.InstanceSettings.Brain.Transports.DeutToLeaveOnMoons;
					
					origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Resources);
					if (origin.Resources.IsEnoughFor(missingResources, resToLeave)) {
						origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Ships);
						destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.Facilities);
						destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.Constructions);
						Buildables preferredShip = Buildables.SmallCargo;
						if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.Brain.Transports.CargoType, true, out preferredShip)) {
							_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to parse CargoType. Falling back to default SmallCargo");
							preferredShip = Buildables.SmallCargo;
						}

						origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.LFBuildings);
						origin = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.LFBonuses);
						float cargoBonus = origin.LFBonuses.GetShipCargoBonus(preferredShip);
						long idealShips = _calcService.CalcShipNumberForPayload(missingResources, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo) + 1;
						Ships ships = new();
						Ships tempShips = new();
						if (maxLFBuildings == null)
							maxLFBuildings = new();
						tempShips.Add(preferredShip, 1);
						var flightPrediction = _calcService.CalcFleetPrediction(origin.Coordinate, destination.Coordinate, tempShips, Missions.Transport, Speeds.HundredPercent, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, origin.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
						long flightTime = flightPrediction.Time;
						idealShips = _calcService.CalcShipNumberForPayload(missingResources, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
						var availableShips = origin.Ships.GetAmount(preferredShip);
						if (buildable != LFBuildables.None) {
							destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.LFBuildings);
							destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.LFTechs);
							destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.LFBonuses);
							int level = _calcService.GetNextLevel(destination, buildable);
							long buildTime = _calcService.CalcProductionTime(buildable, level, _tbotInstance.UserData.serverData, destination);
							var tempCelestial = destination;
							while (flightTime * 2 >= buildTime && idealShips <= availableShips) {
								tempCelestial.SetLevel(buildable, level);
									
								tempCelestial.ResourcesProduction.Population.LivingSpace = _calcService.CalcLivingSpace(tempCelestial as Planet);
								tempCelestial.ResourcesProduction.Population.Satisfied = _calcService.CalcSatisfied(tempCelestial as Planet);

								bool preventTechBuilding = false;
								if (tempCelestial.Constructions.LFResearchCountdown > 0) {
									preventTechBuilding = true;
								}

								var nextBuildable = LFBuildables.None;									
								nextBuildable = _calcService.GetNextLFBuildingToBuild(tempCelestial as Planet, maxLFBuildings, preventIfMoreExpensiveThanNextMine, preventTechBuilding);
								if (nextBuildable != LFBuildables.None) {
									var nextLevel = _calcService.GetNextLevel(tempCelestial, nextBuildable);
									float costReduction = _calcService.CalcLFBuildingsResourcesCostBonus(tempCelestial);
									float popReduction = _calcService.CalcLFBuildingsPopulationCostBonus(tempCelestial);
									var newMissingRes = missingResources.Sum(_calcService.CalcPrice(nextBuildable, nextLevel, costReduction, 0, popReduction));

									if (origin.Resources.IsEnoughFor(newMissingRes, resToLeave)) {
										var newIdealShips = _calcService.CalcShipNumberForPayload(newMissingRes, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
										if (newIdealShips <= origin.Ships.GetAmount(preferredShip)) {
											idealShips = newIdealShips;
											missingResources = newMissingRes;
											buildTime += _calcService.CalcProductionTime(nextBuildable, nextLevel, _tbotInstance.UserData.serverData, tempCelestial);
											_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Sending resources for {nextBuildable.ToString()} level {nextLevel} too");
											level = nextLevel;
											buildable = nextBuildable;
										} else {
											break;
										}
									} else {
										break;
									}
								} else {
									break;
								}
							}
						}

						if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.Transports, "RoundResources") && (bool) _tbotInstance.InstanceSettings.Brain.Transports.RoundResources) {
							missingResources = missingResources.Round();
							idealShips = _calcService.CalcShipNumberForPayload(missingResources, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
						}

						bool doMultipleTransports = (idealShips > origin.Ships.GetAmount(preferredShip) && (bool) _tbotInstance.InstanceSettings.Brain.Transports.DoMultipleTransportIsNotEnoughShipButSamePosition) &&
							(origin.Coordinate.Galaxy == destination.Coordinate.Galaxy &&
							origin.Coordinate.System == destination.Coordinate.System &&
							origin.Coordinate.Position == destination.Coordinate.Position) ?
								true:
								false;
						
						if (idealShips <= origin.Ships.GetAmount(preferredShip) || (doMultipleTransports && !origin.Ships.IsEmpty())) {
							if (doMultipleTransports) {
								ships.Add(preferredShip, origin.Ships.GetAmount(preferredShip));
							} else {
								ships.Add(preferredShip, idealShips);
							}

							if (destination.Coordinate.Type == Celestials.Planet) {
								destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.ResourceSettings);
								destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.Buildings);
								destination = await _tbotOgameBridge.UpdatePlanet(destination, UpdateTypes.ResourcesProduction);

								float metProdInASecond = destination.ResourcesProduction.Metal.CurrentProduction / (float) 3600;
								float cryProdInASecond = destination.ResourcesProduction.Crystal.CurrentProduction / (float) 3600;
								float deutProdInASecond = destination.ResourcesProduction.Deuterium.CurrentProduction / (float) 3600;
								var metProdInFlightTime = metProdInASecond * flightTime;
								var cryProdInFlightTime = cryProdInASecond * flightTime;
								var deutProdInFlightTime = deutProdInASecond * flightTime;

								if (
									(metProdInASecond == 0 && missingResources.Metal > 0) ||
									(cryProdInFlightTime == 0 && missingResources.Crystal > 0) ||
									(deutProdInFlightTime == 0 && missingResources.Deuterium > 0) ||
									missingResources.Metal >= metProdInFlightTime ||
									missingResources.Crystal >= cryProdInFlightTime ||
									missingResources.Deuterium >= deutProdInFlightTime ||
									resources.Metal > destination.ResourcesProduction.Metal.StorageCapacity ||
									resources.Crystal > destination.ResourcesProduction.Crystal.StorageCapacity ||
									resources.Deuterium > destination.ResourcesProduction.Deuterium.StorageCapacity
								) {
									if (differentDestination != null && differentDestination.ID != 0)
										destination = differentDestination;
									_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Sending {ships.ToString()} with {missingResources.TransportableResources} from {origin.ToString()} to {destination.ToString()}");
									return await SendFleet(origin, ships, destination.Coordinate, Missions.Transport, Speeds.HundredPercent, missingResources, _tbotInstance.UserData.userInfo.Class);
								} else {
									_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, "Skipping transport: it is quicker to wait for production.");
									return (int) SendFleetCode.QuickerToWaitForProduction;
								}
							} else {
								if (differentDestination != null && differentDestination.ID != 0)
									destination = differentDestination;
								_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Sending {ships.ToString()} with {missingResources.TransportableResources} from {origin.ToString()} to {destination.ToString()}");
								return await SendFleet(origin, ships, destination.Coordinate, Missions.Transport, Speeds.HundredPercent, missingResources, _tbotInstance.UserData.userInfo.Class);
							}
						} else {
							_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, "Skipping transport: not enough ships to transport required resources.");
							return 0;
						}
					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Skipping transport: not enough resources in {origin.ToString()}. Needed: {missingResources.TransportableResources} - Available: {origin.Resources.TransportableResources}");
						return (int) SendFleetCode.NotEnoughRessources;
					}
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Error, LogSender.FleetScheduler, $"HandleMinerTransport Exception: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Stacktrace: {e.StackTrace}");
				return 0;
			}
		}

		public async Task Collect(bool noLimit = false) {
			await CollectImpl(true, noLimit);
		}

		public async Task CollectDeut(long MinAmount = 0) {
			_tbotInstance.UserData.fleets = await UpdateFleets();
			long TotalDeut = 0;
			Coordinate destinationCoordinate;

			Celestial cel = _tbotInstance.UserData.celestials
					.Unique()
					.Where(c => c.Coordinate.Galaxy == (int) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.Target.Galaxy)
					.Where(c => c.Coordinate.System == (int) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.Target.System)
					.Where(c => c.Coordinate.Position == (int) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.Target.Position)
					.Where(c => c.Coordinate.Type == Enum.Parse<Celestials>((string) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.Target.Type))
					.SingleOrDefault() ?? new() { ID = 0 };

			if (cel.ID == 0) {
				await _tbotInstance.SendTelegramMessage("Error! Could not parse auto repatriate Celestial from JSON InstanceSettings. Need <code>/editsettings</code>");
				return;
			} else {
				destinationCoordinate = cel.Coordinate;
			}

			foreach (Celestial celestial in _tbotInstance.UserData.celestials.ToList()) {
				if (celestial.Coordinate.IsSame(destinationCoordinate)) {
					continue;
				}
				if (celestial is Moon) {
					continue;
				}

				var tempCelestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Fast);
				_tbotInstance.UserData.fleets = await UpdateFleets();

				tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
				tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);

				Buildables preferredShip = Buildables.LargeCargo;
				if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.CargoType, true, out preferredShip)) {
					preferredShip = Buildables.LargeCargo;
				}
				Resources payload = tempCelestial.Resources;
				payload.Metal = 0;
				payload.Crystal = 0;
				payload.Food = 0;

				if ((long) tempCelestial.Resources.Deuterium < (long) MinAmount || payload.IsEmpty()) {
					continue;
				}

				tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
				float cargoBonus = tempCelestial.LFBonuses.GetShipCargoBonus(preferredShip);
				long idealShips = _calcService.CalcShipNumberForPayload(payload, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);

				Ships ships = new();
				if (tempCelestial.Ships.GetAmount(preferredShip) != 0) {
					if (idealShips <= tempCelestial.Ships.GetAmount(preferredShip)) {
						ships.Add(preferredShip, idealShips);
					} else {
						ships.Add(preferredShip, tempCelestial.Ships.GetAmount(preferredShip));
					}
					payload = _calcService.CalcMaxTransportableResources(ships, payload, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);

					if ((long) payload.TotalResources >= (long) MinAmount) {
						var fleetId = await SendFleet(tempCelestial, ships, destinationCoordinate, Missions.Transport, Speeds.HundredPercent, payload);
						if (fleetId == (int) SendFleetCode.AfterSleepTime) {
							continue;
						}
						if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
							continue;
						}

						TotalDeut += payload.Deuterium;
					}
				} else {
					continue;
				}
			}

			if (TotalDeut > 0) {
				await _tbotInstance.SendTelegramMessage($"{TotalDeut} Deuterium sent.");
			} else {
				await _tbotInstance.SendTelegramMessage("No resources sent");
			}
		}

		public async Task<RepatriateCode> CollectImpl(bool fromTelegram, bool noLimit = false) {
			try {
				_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, "Repatriating resources...");

				if (fromTelegram) {
					_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Telegram collect initated..");
				}
				
				List<Celestial> tempCelestials = new();
				if (_tbotInstance.InstanceSettings.Brain.AutoRepatriate.Target.Length > 0) {
					tempCelestials = _calcService.ParseCelestialsList(_tbotInstance.InstanceSettings.Brain.AutoRepatriate.Target, _tbotInstance.UserData.celestials);
				} else {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Skipping autorepatriate: no custom destination");
					return RepatriateCode.Failure;
				}
				if (tempCelestials.Count() > 0) {
					_tbotInstance.UserData.fleets = await UpdateFleets();
					long TotalMet = 0;
					long TotalCri = 0;
					long TotalDeut = 0;
					bool samePosition = (bool) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.TargetAssociateMoon;
					List<Celestial> newCelestials = _tbotInstance.UserData.celestials.ToList();
					List<Celestial> celestialsToExclude = _calcService.ParseCelestialsList(_tbotInstance.InstanceSettings.Brain.AutoRepatriate.Exclude, _tbotInstance.UserData.celestials);
					List<Celestial> celestialList = _tbotInstance.UserData.celestials.ToList();
					
					celestialList = (bool) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.RandomOrder ? celestialList.Shuffle().ToList() : celestialList.ToList();
					
					foreach (Celestial celestial in celestialList) {
						List<Celestial> closestCelestials = tempCelestials
							.OrderBy(c => _calcService.CalcDistance(c.Coordinate, celestial.Coordinate, _tbotInstance.UserData.serverData)).ToList();
						Coordinate destinationCoordinate = new();
						if (samePosition) {
							if (celestial.Coordinate.Type == Celestials.Planet && !_calcService.IsThereMoonHere(_tbotInstance.UserData.celestials, celestial)) {
								_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Skipping {celestial.ToString()}: There is no moon.");
								continue;
							}
							destinationCoordinate = new(
								(int) celestial.Coordinate.Galaxy,
								(int) celestial.Coordinate.System,
								(int) celestial.Coordinate.Position,
								celestial.Coordinate.Type == Celestials.Planet ? Celestials.Moon : Celestials.Planet
							);
						} else {
							destinationCoordinate = closestCelestials.First().Coordinate;
						}
						if (celestialsToExclude.Has(celestial)) {
							_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Skipping {celestial.ToString()}: celestial in exclude list.");
							continue;
						}
						if (celestial.Coordinate.IsSame(destinationCoordinate)) {
							_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Skipping {celestial.ToString()}: celestial is the target.");
							continue;
						}

						var tempCelestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Fast);

						_tbotInstance.UserData.fleets = await UpdateFleets();
						if ((bool) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.SkipIfIncomingTransport && _calcService.IsThereTransportTowardsCelestial(celestial, _tbotInstance.UserData.fleets) && (!fromTelegram)) {
							_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Skipping {tempCelestial.ToString()}: there is a transport incoming.");
							continue;
						}
						if (celestial.Coordinate.Type == Celestials.Moon && (bool) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.ExcludeMoons) {
							_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Skipping {tempCelestial.ToString()}: celestial is a moon.");
							continue;
						}

						tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
						tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);

						Buildables preferredShip = Buildables.SmallCargo;
						if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.CargoType, true, out preferredShip)) {
							_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to parse CargoType. Falling back to default SmallCargo");
							preferredShip = Buildables.SmallCargo;
						}
						Resources payload = tempCelestial.Resources;

						if ((long) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.LeaveDeut.DeutToLeave > 0) {
							if ((bool) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.LeaveDeut.OnlyOnMoons) {
								if (tempCelestial.Coordinate.Type == Celestials.Moon) {
									payload = payload.Difference(new(0, 0, (long) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.LeaveDeut.DeutToLeave));
								}
							} else {
								payload = payload.Difference(new(0, 0, (long) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.LeaveDeut.DeutToLeave));
							}
						}

						if ((!noLimit && payload.TotalResources < (long) _tbotInstance.InstanceSettings.Brain.AutoRepatriate.MinimumResources) || payload.IsEmpty()) {
							_tbotInstance.log(LogLevel.Information, LogSender.FleetScheduler, $"Skipping {tempCelestial.ToString()}: resources under set limit");
							continue;
						}

						tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
						float cargoBonus = tempCelestial.LFBonuses.GetShipCargoBonus(preferredShip);
						long idealShips = _calcService.CalcShipNumberForPayload(payload, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);

						Ships ships = new();
						if (tempCelestial.Ships.GetAmount(preferredShip) != 0) {
							if (idealShips <= tempCelestial.Ships.GetAmount(preferredShip)) {
								ships.Add(preferredShip, idealShips);
							} else {
								ships.Add(preferredShip, tempCelestial.Ships.GetAmount(preferredShip));
							}
							payload = _calcService.CalcMaxTransportableResources(ships, payload, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);

							if (payload.TotalResources > 0) {
								var fleetId = await SendFleet(tempCelestial, ships, destinationCoordinate, Missions.Transport, Speeds.HundredPercent, payload);
								if (fleetId == (int) SendFleetCode.AfterSleepTime) {
									return RepatriateCode.Stop;
								}
								if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
									return RepatriateCode.Delay;
								}
								TotalMet += payload.Metal;
								TotalCri += payload.Crystal;
								TotalDeut += payload.Deuterium;
							}
						} else {
							_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Skipping {tempCelestial.ToString()}: there are no {preferredShip.ToString()}");
						}

						newCelestials.Remove(celestial);
						newCelestials.Add(tempCelestial);
					}
					_tbotInstance.UserData.celestials = newCelestials;
					if (fromTelegram) {
						if ((TotalMet > 0) || (TotalCri > 0) || (TotalDeut > 0)) {
							await _tbotInstance.SendTelegramMessage($"Resources sent!:\n{TotalMet} Metal\n{TotalCri} Crystal\n{TotalDeut} Deuterium");
						} else {
							await _tbotInstance.SendTelegramMessage("No resources sent");
						}
					}
					return RepatriateCode.Success;
				} else {
					_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Skipping autorepatriate: unable to parse custom destination");
					return RepatriateCode.Failure;
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Unable to complete repatriate: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, $"Stacktrace: {e.StackTrace}");
				return RepatriateCode.Failure;
			}
		}
	


}
}
