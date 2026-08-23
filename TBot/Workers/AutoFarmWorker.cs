using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Common.Logging;
using Tbot.Includes;
using TBot.Ogame.Infrastructure.Enums;
using Tbot.Services;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tbot.Helpers;
using TBot.Model;
using TBot.Ogame.Infrastructure.Models;
using TBot.Ogame.Infrastructure;
using Tbot.Common.Settings;

namespace Tbot.Workers {
	public class AutoFarmWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		private readonly AutoFarmBlacklist _blacklist;
		private readonly AutoFarmSuccessfulTargets _successfulTargets;
		private readonly AutoFarmStateStore _stateStore;
		private bool _stateLoaded;

		// FarmTargetCache (SQLite) complements the upstream AutoFarmBlacklist
		// (JSON file) / AutoFarmSuccessfulTargets (JSON file) - FarmTargetCache's own doc comment
		// documents this was already the intent ("coordinate blacklist ... backed by SQLite instead
		// of the single JSON file ... used before"), and its attack_history table covers what
		// AutoFarmSuccessfulTargets did plus per-player tracking. Loaded/disposed once per Execute()
		// cycle (see Execute()), not held open across the worker's whole lifetime.
		private FarmTargetCache _farmTargetCache;

		// Lazily created once and reused across Execute() cycles (unlike _farmTargetCache) since it
		// only holds an in-memory, self-refreshing snapshot of /api/highscore.xml - no per-cycle
		// open/dispose needed.
		private HighscorePlayerRankCache _highscoreRankCache;

		// Tracks the single expendable probe-as-attack sent per target to detect defenses when a
		// normal espionage report couldn't reveal them (see SendDefenseProbeAttacks). Static so it
		// survives worker re-instantiation, matching the cross-cycle nature of "wait for this fleet
		// to arrive, then wait for it to either return or not."
		private record DefenseProbeInfo(int FleetId, DateTime SentAt, DateTime? ArrivalTime, bool SeenReturning);
		private static readonly Dictionary<string, DefenseProbeInfo> _defenseProbeFleets = new();

		public AutoFarmWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) :
			base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
			string blacklistPath = $"autofarm_blacklist_{_tbotInstance.InstanceAlias}.json";
			_blacklist = new AutoFarmBlacklist(blacklistPath);
			string successfulPath = $"autofarm_successful_{_tbotInstance.InstanceAlias}.json";
			_successfulTargets = new AutoFarmSuccessfulTargets(successfulPath);
			_stateStore = new AutoFarmStateStore($"autofarm_{_tbotInstance.InstanceAlias}.db");
		}
		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool) _tbotInstance.InstanceSettings.AutoFarm.Active;
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "AutoFarm";
		}
		public override Feature GetFeature() {
			return Feature.AutoFarm;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoFarm;
		}

		private void LoadPersistedTargets() {
			if (_stateLoaded)
				return;

			_tbotInstance.UserData.farmTargets ??= new List<FarmTarget>();
			foreach (var persisted in _stateStore.LoadTargets()) {
				if (!persisted.ConsumedReportId.HasValue &&
					persisted.State == FarmState.AttackSent &&
					persisted.Report?.ID > 0) {
					// Databases created before report consumption was persisted still
					// contain the report that was used for this completed dispatch.
					persisted.ConsumedReportId = persisted.Report.ID;
				}

				var current = _tbotInstance.UserData.farmTargets.FirstOrDefault(target =>
					target?.Celestial?.Coordinate?.IsSame(persisted.Celestial.Coordinate) == true);
				if (current == null) {
					_tbotInstance.UserData.farmTargets.Add(persisted);
					continue;
				}

				current.Celestial = persisted.Celestial;
				current.State = persisted.State;
				current.Report = persisted.Report;
				current.ConsumedReportId = persisted.ConsumedReportId;
			}

			var cursor = _stateStore.LoadScanCursor();
			if (cursor != null) {
				_tbotInstance.UserData.autoFarmLastRangeIndex = cursor.RangeIndex;
				_tbotInstance.UserData.autoFarmLastGalaxy = cursor.Galaxy;
				_tbotInstance.UserData.autoFarmLastSystem = cursor.System;
			}
			_stateLoaded = true;
		}

		private void PersistTargets() {
			_stateStore.ReplaceTargets(_tbotInstance.UserData.farmTargets, DateTime.UtcNow);
		}

		private void SaveScanCursor(int rangeIndex, int galaxy, int system) {
			_tbotInstance.UserData.autoFarmLastRangeIndex = rangeIndex;
			_tbotInstance.UserData.autoFarmLastGalaxy = galaxy;
			_tbotInstance.UserData.autoFarmLastSystem = system;
			_stateStore.SaveScanCursor(rangeIndex, galaxy, system, DateTime.UtcNow);
		}

		private TimeSpan GetSystemDataTtl() {
			int days = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm,
				"DaysToKeepOldSystemData", 7);
			return TimeSpan.FromDays(Math.Max(1, days));
		}

		private TimeSpan GetEmptySystemCooldown() {
			int days = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm,
				"EmptySystemCooldownDays", 30);
			return TimeSpan.FromDays(Math.Max(1, days));
		}

		private async Task PruneOldReports(List<Fleet> fleets) {
			var newTime = await _tbotOgameBridge.GetDateTime();
			var removeReports = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackSent || (t.Report != null && DateTime.Compare(t.Report.Date.AddMinutes((double) _tbotInstance.InstanceSettings.AutoFarm.KeepReportFor), newTime) < 0)).ToList();
			foreach (var remove in removeReports) {
				if (remove.State == FarmState.AttackSent && AutoFarmAttackPolicy.IsAttackInProgress(remove, fleets)) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Keeping {remove} in AttackSent state while an attack fleet is still in progress.");
					continue;
				}

				var updateReport = remove;
				updateReport.State = FarmState.ProbesPending;
				updateReport.Report = null;
				_tbotInstance.UserData.farmTargets.Remove(remove);
				_tbotInstance.UserData.farmTargets.Add(updateReport);
			}
		}

		private Dictionary<int, long> GetCachedCelestialProbes(List<Celestial> origins) {
			return (origins ?? new List<Celestial>())
				.Where(celestial => celestial != null)
				.GroupBy(celestial => celestial.ID)
				.ToDictionary(
					group => group.Key,
					group => group.First().Ships?.EspionageProbe ?? 0);
		}

		private List<Celestial> GetFarmOrigins() {
			List<Celestial> origins;
			if (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0)
				origins = _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials);
			else
				origins = _tbotInstance.UserData.celestials;

			return (origins ?? new List<Celestial>())
				.Where(celestial => celestial?.Coordinate != null)
				.GroupBy(celestial => $"{celestial.Coordinate.Galaxy}:{celestial.Coordinate.System}:{celestial.Coordinate.Position}:{celestial.Coordinate.Type}")
				.Select(group => group.First())
				.ToList();
		}

		// Live variant kept from the previous tree: refreshes every celestial before reading its
		// probe count, for the code paths that cannot rely on an already-updated origin list.
		private async Task<Dictionary<int, long>> GetCelestialProbes() {
			var localCelestials = await _tbotOgameBridge.UpdateCelestials();
			Dictionary<int, long> celestialProbes = new Dictionary<int, long>();
			foreach (var celestial in localCelestials) {
				Celestial tempCelestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Fast);
				tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);
				celestialProbes.Add(tempCelestial.ID, tempCelestial.Ships.EspionageProbe);
			}
			return celestialProbes;
		}

		private bool ShouldExcludeSystem(int galaxy, int system) {
			bool excludeSystem = false;
			foreach (var exclude in _tbotInstance.InstanceSettings.AutoFarm.Exclude) {
				bool hasPosition = false;
				foreach (var value in exclude.Keys)
					if (value == "Position")
						hasPosition = true;
				if ((int) exclude.Galaxy == galaxy && (int) exclude.System == system && !hasPosition) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping system {system.ToString()}: system in exclude list.");
					excludeSystem = true;
					break;
				}
			}
			return excludeSystem;
		}

		// Persists what a live scan found for this celestial into the FastFarm cache - basic identity
		// only (coordinate, player, inactive flag), so a later FastFarmMode cycle can rebuild the
		// system's target list without a live galaxy scan. Report-derived detail (resources,
		// buildings, defenses) is filled in separately by CacheUpsertFromReport once actually probed.
		private async Task CacheUpsertFromScan(Celestial celestial) {
			var planet = celestial as Planet;
			if (planet == null)
				return;
			var entry = _farmTargetCache.Get(planet.Coordinate) ?? new FarmTargetCacheEntry { Coordinate = planet.Coordinate };
			entry.PlayerName = planet.Player?.Name;
			entry.PlayerRank = planet.Player?.Rank ?? entry.PlayerRank;
			entry.IsInactive = planet.Inactive;
			entry.LastSeenDate = DateTime.UtcNow;
			await _farmTargetCache.Upsert(entry);
		}

		private async Task<List<Celestial>> GetScannedTargetsFromGalaxy(int galaxy, int system) {
			DateTime nowUtc = DateTime.UtcNow;

			// FastFarmMode: when explicitly enabled, a recent enough cached scan of this system
			// replaces the live galaxy scan entirely.
			bool fastFarmMode = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FastFarmMode") && (bool) _tbotInstance.InstanceSettings.AutoFarm.FastFarmMode;
			int fastFarmMaxCacheAgeMinutes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FastFarmMaxCacheAge") ? (int) _tbotInstance.InstanceSettings.AutoFarm.FastFarmMaxCacheAge : 1440;

			if (fastFarmMode && _farmTargetCache != null) {
				var scanAge = _farmTargetCache.GetSystemScanAge(galaxy, system);
				if (scanAge != null && scanAge.Value.TotalMinutes < fastFarmMaxCacheAgeMinutes) {
					var cachedEntries = _farmTargetCache.GetInRange(galaxy, system, system).Where(e => e.IsInactive).ToList();
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"FastFarm: reusing cached scan of {galaxy}:{system} ({cachedEntries.Count} target(s), scanned {scanAge.Value.TotalMinutes:F0}min ago) instead of a live galaxy scan.");
					List<Celestial> cachedTargets = cachedEntries.Select(e => (Celestial) new Planet {
						Coordinate = e.Coordinate,
						Inactive = e.IsInactive,
						Player = new Player { Name = e.PlayerName, Rank = e.PlayerRank }
					}).ToList();
					await _fleetScheduler.UpdateFleets();
					cachedTargets.RemoveAll(t => _tbotInstance.UserData.fleets.Any(f => f.Destination.IsSame(t.Coordinate) && f.Mission == Missions.Attack));
					return cachedTargets;
				}
			}

			var cachedSnapshot = _stateStore.GetSystemSnapshot(galaxy, system);
			var cacheDecision = AutoFarmSystemCachePolicy.GetDecision(
				cachedSnapshot,
				nowUtc,
				GetSystemDataTtl(),
				GetEmptySystemCooldown());
			if (cacheDecision == AutoFarmSystemCacheDecision.Skip) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Skipping cached empty system {galaxy}:{system} until {cachedSnapshot.ObservedAtUtc.Add(GetEmptySystemCooldown()):u}.");
				return null;
			}

			if (cacheDecision == AutoFarmSystemCacheDecision.UseSnapshot) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Using cached galaxy data for {galaxy}:{system} ({(nowUtc - cachedSnapshot.ObservedAtUtc).TotalDays:F1} days old).");
				var snapshotTargets = AutoFarmSystemPolicy.GetEligibleTargets(cachedSnapshot.Planets);
				// Same guard as the live-scan path: never hand back a coordinate that already has an
				// attack fleet on its way.
				await _fleetScheduler.UpdateFleets();
				snapshotTargets.RemoveAll(t => _tbotInstance.UserData.fleets.Any(f => f.Destination.IsSame(t.Coordinate) && f.Mission == Missions.Attack));
				return snapshotTargets;
			}

			GalaxyInfo galaxyInfo = null;
			int retryCount = 0;
			int maxRetries = 5;

			while (retryCount < maxRetries) {
				try {
					galaxyInfo = await _ogameService.GetGalaxyInfo(galaxy, system);
					break;
				} catch (Exception e) when (e.Message.Contains("system must be within") || e.Message.Contains("503") || e.Message.Contains("Service Unavailable")) {
					retryCount++;
					int waitSeconds = retryCount * 3;

					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Exception details: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Galaxy scan failed. Retry {retryCount}/{maxRetries} in {waitSeconds}s...");

					if (retryCount < maxRetries) {
						await Task.Delay(waitSeconds * 1000);

						try {
							_tbotInstance.UserData.serverData = await _ogameService.GetServerData();
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"ServerData after refresh: Systems={_tbotInstance.UserData.serverData.Systems}");
						} catch (Exception serverDataEx) {
							_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Failed to refresh ServerData: {serverDataEx.Message}");
						}

						if (retryCount == 4 && _tbotInstance.UserData.serverData.Systems == 0) {
							_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"ServerData broken. Root cause: {e.Message}. Restarting ogamed...");

							try {
								_ogameService.KillOgamedExecutable();
								await Task.Delay(5000);
								_ogameService.RerunOgamed();
								await Task.Delay(10000);

								try {
									_tbotInstance.UserData.serverData = await _ogameService.GetServerData();
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"ServerData after restart: Systems={_tbotInstance.UserData.serverData.Systems}");
								} catch (Exception restartEx) {
									_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Failed to get ServerData after ogamed restart: {restartEx.Message}");
								}
							} catch (Exception ogamedEx) {
								_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Failed to restart ogamed: {ogamedEx.Message}");
								_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {ogamedEx.StackTrace}");
							}
						}
					} else {
						_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Galaxy scan failed after {maxRetries} retries.");
						throw;
					}
				}
			}

			if (galaxyInfo?.Planets == null) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Galaxy scan returned no planet data for {galaxy}:{system}; this system will not be cached as empty.");
				return null;
			}

			var planets = galaxyInfo.Planets.Where(p => p != null).ToList();
			bool isEmptySystem = AutoFarmSystemPolicy.IsEmptySystem(planets);
			_stateStore.SaveSystemSnapshot(galaxy, system, nowUtc, isEmptySystem, planets);

			if (isEmptySystem)
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"System {galaxy}:{system} is empty for AutoFarm; applying the configured long cooldown.");

			List<Celestial> scannedTargets = AutoFarmSystemPolicy.GetEligibleTargets(planets);
			await _fleetScheduler.UpdateFleets();
			scannedTargets.RemoveAll(t => _tbotInstance.UserData.fleets.Any(f => f.Destination.IsSame(t.Coordinate) && f.Mission == Missions.Attack));

			// Feed the FastFarm cache with what this live scan found, regardless of whether
			// FastFarmMode is on right now - a later cycle with it enabled reuses this.
			if (_farmTargetCache != null) {
				foreach (var target in scannedTargets) {
					await CacheUpsertFromScan(target);
				}
				_farmTargetCache.MarkSystemScanned(galaxy, system);
			}

			return scannedTargets;
		}

		private async Task<bool> IsTargetInMinimumRank(Celestial planet, List<Celestial> scannedTargets) {
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinimumPlayerRank") && _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank != 0) {
				int rank = 1;
				if (planet.Coordinate.Type == Celestials.Planet) {
					rank = (planet as Planet).Player.Rank;
				} else if (scannedTargets.Any(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet)))) {
					rank = (scannedTargets.Single(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet))) as Planet).Player.Rank;
				} else {
					// Sibling planet wasn't in this scan (e.g. a moon whose planet is out of range or
					// already filtered out). Fall back to the persistent target cache's last known
					// player name, then resolve a fresh rank for it via highscore.xml instead of
					// silently defaulting to rank 1 (which always passes the filter).
					var cachedEntry = _farmTargetCache?.Get(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet));
					if (!string.IsNullOrEmpty(cachedEntry?.PlayerName)) {
						_highscoreRankCache ??= new HighscorePlayerRankCache(_ogameService);
						rank = await _highscoreRankCache.GetRank(cachedEntry.PlayerName) ?? cachedEntry.PlayerRank;
					}
				}
				if ((int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank < rank) {

					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Skipping {planet.ToString()}: player has rank {rank} that is less than minimum configured {(int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank}.");
					return false;
				}
			}
			return true;
		}

		private bool ShouldExcludePlanet(Celestial planet) {
			bool excludePlanet = false;
			foreach (var exclude in _tbotInstance.InstanceSettings.AutoFarm.Exclude) {
				bool hasPosition = false;
				foreach (var value in exclude.Keys)
					if (value == "Position")
						hasPosition = true;
				if ((int) exclude.Galaxy == planet.Coordinate.Galaxy && (int) exclude.System == planet.Coordinate.System && hasPosition && (int) exclude.Position == planet.Coordinate.Position) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {planet.ToString()}: celestial in exclude list.");
					excludePlanet = true;
					break;
				}
			}
			return excludePlanet;
		}

		private bool IsDefenseProbeEnabled() =>
			SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ProbeAttackForDefenseCheck")
			&& (bool) _tbotInstance.InstanceSettings.AutoFarm.ProbeAttackForDefenseCheck;

		// Some targets never reveal defenses through espionage (insufficient Espionage Technology on
		// the report, or it's simply never come back with HasDefensesInformation). Instead of leaving
		// them stuck as ProbesRequired forever, sends a single expendable Espionage Probe as an ATTACK
		// mission - if it survives and comes home, the target has no defenses (queue the real attack);
		// if it never returns, the target has defenses (blacklist it). Cheaper than repeatedly trying
		// to out-tech-scan a defended target.
		private async Task<int> SendDefenseProbeAttacks(int freeSlots, int slotsToLeaveFree) {
			if (!IsDefenseProbeEnabled()) return freeSlots;

			var targets = _tbotInstance.UserData.farmTargets
				.Where(t => t.State == FarmState.ProbesRequired || t.State == FarmState.FailedProbesRequired)
				.ToList();
			if (!targets.Any()) return freeSlots;

			_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
				$"ProbeAttack: sending 1 EP attack to {targets.Count} target(s) to check for defenses.");

			List<Celestial> availCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0)
				? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials)
				: _tbotInstance.UserData.celestials.ToList();

			foreach (var target in targets) {
				if (freeSlots <= slotsToLeaveFree) {
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					freeSlots = _tbotInstance.UserData.slots.Free;
					if (freeSlots <= slotsToLeaveFree) break;
				}

				var ordered = availCelestials
					.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData))
					.ToList();

				Celestial origin = null;
				foreach (var cel in ordered) {
					var updated = await _tbotOgameBridge.UpdatePlanet(cel, UpdateTypes.Ships);
					if (updated.Ships.EspionageProbe >= 1) {
						origin = updated;
						break;
					}
				}
				if (origin == null) {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
						$"ProbeAttack: no origin with probes for {target.Celestial.Coordinate}, skipping.");
					continue;
				}

				Ships probeShip = new();
				probeShip.Add(Buildables.EspionageProbe, 1);

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
					$"ProbeAttack: attacking {target.Celestial.Coordinate} with 1 EP from {origin.Coordinate}.");

				var fleetId = await _fleetScheduler.SendFleet(origin, probeShip, target.Celestial.Coordinate, Missions.Attack, Speeds.HundredPercent);

				if (fleetId > (int) SendFleetCode.GenericError) {
					freeSlots--;
					_defenseProbeFleets[target.Celestial.Coordinate.ToString()] = new DefenseProbeInfo(fleetId, DateTime.UtcNow, null, false);
					target.State = FarmState.DefenseProbing;
				} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
					break;
				}

				await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);
			}
			return freeSlots;
		}

		// Resolves outstanding defense-probe-attacks from a previous cycle: if the fleet is seen
		// coming back, the probe survived (no defenses) - queue the real attack. If it's gone from the
		// fleet list without ever being seen returning, and enough time has passed since arrival that
		// it can't just be lagging behind, it was destroyed (defenses present) - blacklist the target.
		private async Task ProcessDefenseProbingResults() {
			if (!IsDefenseProbeEnabled() || _farmTargetCache == null) return;

			var probingTargets = _tbotInstance.UserData.farmTargets
				.Where(t => t.State == FarmState.DefenseProbing)
				.ToList();
			if (!probingTargets.Any()) return;

			var now = await _tbotOgameBridge.GetDateTime();
			_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

			foreach (var target in probingTargets) {
				string coordKey = target.Celestial.Coordinate.ToString();
				if (!_defenseProbeFleets.TryGetValue(coordKey, out var info)) {
					target.State = FarmState.ProbesPending;
					target.Report = null;
					continue;
				}

				var fleet = _tbotInstance.UserData.fleets.FirstOrDefault(f => f.ID == info.FleetId);

				if (fleet != null) {
					if (fleet.ReturnFlight) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"ProbeAttack: EP returning from {coordKey} - no defenses, queuing attack.");
						var cacheEntry = _farmTargetCache.Get(target.Celestial.Coordinate);
						if (cacheEntry != null) {
							cacheEntry.HasDefenses = false;
							await _farmTargetCache.Upsert(cacheEntry);
						}
						target.State = FarmState.AttackPending;
						_defenseProbeFleets.Remove(coordKey);
					} else if (info.ArrivalTime == null) {
						_defenseProbeFleets[coordKey] = info with { ArrivalTime = fleet.ArrivalTime };
					}
				} else {
					if (info.SeenReturning) {
						_defenseProbeFleets.Remove(coordKey);
					} else if (info.ArrivalTime.HasValue && info.ArrivalTime.Value.AddMinutes(5) < now) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"ProbeAttack: EP not seen returning after arrival at {coordKey} - defenses present, blacklisting.");
						var cacheEntry = _farmTargetCache.Get(target.Celestial.Coordinate);
						if (cacheEntry != null) {
							cacheEntry.HasDefenses = true;
							await _farmTargetCache.Upsert(cacheEntry);
						}
						bool blacklistActiveProbe = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
							SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
							(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
						if (blacklistActiveProbe) {
							int resetHours = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 168);
							_farmTargetCache.Blacklist(target.Celestial.Coordinate, DateTime.UtcNow.AddHours(resetHours));
						}
						target.State = FarmState.NotSuitable;
						_defenseProbeFleets.Remove(coordKey);
					}
				}
			}
		}

		private double GetAcceptableFleetLossPercentage() {
			return SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "AcceptableFleetLossPercentage")
				? (double) _tbotInstance.InstanceSettings.AutoFarm.AcceptableFleetLossPercentage
				: 0;
		}

		// Warship types the user allows AutoFarm to draw on to fight through a defended target, read from
		// AutoFarm.Ships (e.g. { "Cruiser": true, "Battleship": true, ... }). Cargo/utility ships (probes,
		// cargos, recyclers, colony ships) are never included even if set to true in that same object -
		// AutoFarm.Ships lists every ship type for config discoverability, but only these fight.
		private List<Buildables> GetAllowedFarmCombatShips() {
			List<Buildables> allowed = new();
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Ships"))
				return allowed;
			var shipsSetting = _tbotInstance.InstanceSettings.AutoFarm.Ships;
			Buildables[] combatTypes = {
				Buildables.LightFighter, Buildables.HeavyFighter, Buildables.Cruiser, Buildables.Battleship,
				Buildables.Battlecruiser, Buildables.Bomber, Buildables.Destroyer, Buildables.Deathstar,
				Buildables.Reaper, Buildables.Pathfinder,
			};
			foreach (var type in combatTypes) {
				if (SettingsService.IsSettingSet(shipsSetting, type.ToString()) && (bool) shipsSetting[type.ToString()])
					allowed.Add(type);
			}
			return allowed;
		}

		// Checks whether attacking a defended target is worthwhile. Builds a combat fleet from the allowed
		// ship types available at the origin, simulates the battle against the target's reported fleet and
		// defences at increasing fleet sizes (10% steps) until one destroys the defender within the
		// acceptable loss percentage, then checks that the loot is worth the resource value of the ships
		// expected to be lost. Returns the (minimal sufficient) ships to send only if both checks pass.
		private bool TryGetAcceptableCombatFleet(EspionageReport report, Ships availableShips, out Ships combatShips, out double predictedLossPercentage, out Resources predictedDebris) {
			combatShips = new Ships();
			predictedLossPercentage = 0;
			predictedDebris = new Resources();
			double acceptableLoss = GetAcceptableFleetLossPercentage();
			if (acceptableLoss <= 0)
				return false;
			if (!report.HasFleetInformation || !report.HasDefensesInformation)
				return false;

			var allowedTypes = GetAllowedFarmCombatShips();
			Ships maxCombatShips = new Ships();
			foreach (var type in allowedTypes) {
				long available = availableShips.GetAmount(type);
				if (available > 0)
					maxCombatShips.Add(type, available);
			}
			if (maxCombatShips.IsEmpty())
				return false;

			Defences defenderDefences = new() {
				RocketLauncher = report.RocketLauncher ?? 0,
				LightLaser = report.LightLaser ?? 0,
				HeavyLaser = report.HeavyLaser ?? 0,
				GaussCannon = report.GaussCannon ?? 0,
				IonCannon = report.IonCannon ?? 0,
				PlasmaTurret = report.PlasmaTurret ?? 0,
				SmallShieldDome = report.SmallShieldDome ?? 0,
				LargeShieldDome = report.LargeShieldDome ?? 0,
			};
			Ships defenderShips = new Ships()
				.Add(Buildables.LightFighter, report.LightFighter ?? 0)
				.Add(Buildables.HeavyFighter, report.HeavyFighter ?? 0)
				.Add(Buildables.Cruiser, report.Cruiser ?? 0)
				.Add(Buildables.Battleship, report.Battleship ?? 0)
				.Add(Buildables.Battlecruiser, report.Battlecruiser ?? 0)
				.Add(Buildables.Bomber, report.Bomber ?? 0)
				.Add(Buildables.Destroyer, report.Destroyer ?? 0)
				.Add(Buildables.Deathstar, report.Deathstar ?? 0)
				.Add(Buildables.SmallCargo, report.SmallCargo ?? 0)
				.Add(Buildables.LargeCargo, report.LargeCargo ?? 0)
				.Add(Buildables.Recycler, report.Recycler ?? 0)
				.Add(Buildables.Reaper, report.Reaper ?? 0)
				.Add(Buildables.Pathfinder, report.Pathfinder ?? 0);
			Researches defenderResearches = new() {
				WeaponsTechnology = report.WeaponsTechnology ?? 0,
				ShieldingTechnology = report.ShieldingTechnology ?? 0,
				ArmourTechnology = report.ArmourTechnology ?? 0,
			};

			double minLootToRiskRatio = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootToRiskRatio")
				? (double) _tbotInstance.InstanceSettings.AutoFarm.MinLootToRiskRatio : 1.0;
			long lootValue = report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources;

			// Scan fleet sizes from 10% up to 100% of what's available at the origin (rounding each ship
			// type up so small counts aren't rounded down to 0 at low fractions) and stop at the first size
			// that both destroys the defender within the acceptable loss and is worth it economically.
			Ships lastTriedShips = null;
			for (int step = 1; step <= 10; step++) {
				double fraction = step / 10.0;
				Ships candidateShips = new Ships();
				foreach (var type in allowedTypes) {
					long max = maxCombatShips.GetAmount(type);
					if (max <= 0)
						continue;
					long scaled = (long) Math.Ceiling(max * fraction);
					if (scaled > max)
						scaled = max;
					if (scaled > 0)
						candidateShips.Add(type, scaled);
				}
				if (candidateShips.IsEmpty())
					continue;
				if (lastTriedShips != null && candidateShips.ToString() == lastTriedShips.ToString())
					continue;
				lastTriedShips = candidateShips;

				var result = CombatSimulator.SimulateBattle(candidateShips, _tbotInstance.UserData.researches, defenderShips, defenderDefences, defenderResearches);

				if (!result.DefenderDestroyed)
					continue;

				if (result.AttackerLossPercentage > acceptableLoss) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Battle simulation on {report.Coordinate}: predicted fleet loss {result.AttackerLossPercentage:F1}% exceeds acceptable {acceptableLoss:F1}% even with {fraction:P0} of available combat ships, skipping.");
					return false;
				}

				double riskedValue = candidateShips.GetFleetPoints() * 1000.0 * (result.AttackerLossPercentage / 100.0);
				if (riskedValue > 0 && lootValue < riskedValue * minLootToRiskRatio) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Battle simulation on {report.Coordinate}: loot ({lootValue}) doesn't justify the resource value expected to be lost (~{riskedValue:F0}, ratio required {minLootToRiskRatio:F1}x) with {fraction:P0} of available combat ships, skipping.");
					return false;
				}

				combatShips = candidateShips;
				predictedLossPercentage = result.AttackerLossPercentage;
				predictedDebris = EstimateDebrisField(result);
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Battle simulation on {report.Coordinate}: defender destroyed in {result.Rounds} round(s) using {fraction:P0} of available combat ships, predicted fleet loss {predictedLossPercentage:F1}% (acceptable {acceptableLoss:F1}%), risked value ~{riskedValue:F0} vs loot {lootValue}, predicted debris ~{predictedDebris.TotalResources}.");
				return true;
			}

			_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Battle simulation on {report.Coordinate}: defender NOT destroyed even with 100% of available combat ships, skipping.");
			return false;
		}

		// Estimates the debris field a simulated battle would leave, from the ships (both sides) and
		// defences the simulator predicted destroyed, using real per-unit build cost (CalcPrice) and the
		// universe's actual DebrisFactor/DebrisFactorDef (ServerData). Only ever computed for defended
		// targets going through TryGetAcceptableCombatFleet - undefended targets never fight.
		private Resources EstimateDebrisField(CombatSimulationResult result) {
			Resources shipDebris = new();
			foreach (var kv in new[] { result.AttackerShipsLost, result.DefenderShipsLost }) {
				foreach (Buildables type in Enum.GetValues(typeof(Buildables))) {
					long lost = kv.GetAmount(type);
					if (lost <= 0)
						continue;
					var price = _calculationService.CalcPrice(type, 1);
					shipDebris.Metal += price.Metal * lost;
					shipDebris.Crystal += price.Crystal * lost;
				}
			}

			Resources defenceDebris = new();
			if (_tbotInstance.UserData.serverData.DebrisFactorDef > 0) {
				foreach (Buildables type in Enum.GetValues(typeof(Buildables))) {
					long lost = result.DefencesLost.GetAmount(type);
					if (lost <= 0)
						continue;
					var price = _calculationService.CalcPrice(type, 1);
					defenceDebris.Metal += price.Metal * lost;
					defenceDebris.Crystal += price.Crystal * lost;
				}
			}

			float debrisFactor = _tbotInstance.UserData.serverData.DebrisFactor;
			float debrisFactorDef = _tbotInstance.UserData.serverData.DebrisFactorDef;
			return new Resources(
				metal: (long) (shipDebris.Metal * debrisFactor + defenceDebris.Metal * debrisFactorDef),
				crystal: (long) (shipDebris.Crystal * debrisFactor + defenceDebris.Crystal * debrisFactorDef)
			);
		}

		// Persists what this report revealed into the FastFarm cache, so a future cycle has this
		// target's last-known state even before a fresh probe comes back. Resource-extrapolation-based
		// scan skipping (using this data to avoid re-probing recently-seen targets) is not wired up yet -
		// this only builds up the persistent history for now.
		private async Task CacheUpsertFromReport(EspionageReport report) {
			if (_farmTargetCache == null)
				return;
			var entry = _farmTargetCache.Get(report.Coordinate) ?? new FarmTargetCacheEntry { Coordinate = report.Coordinate };
			entry.IsInactive = report.IsInactive;
			entry.LastReportDate = report.Date;
			entry.LastSeenDate = DateTime.UtcNow;
			entry.LastKnownResources = new Resources(report.Metal, report.Crystal, report.Deuterium);
			entry.PlayerClass = report.CharacterClass;
			if (report.HasBuildingsInformation) {
				entry.Buildings = new Buildings {
					MetalMine = report.MetalMine ?? 0,
					CrystalMine = report.CrystalMine ?? 0,
					DeuteriumSynthesizer = report.DeuteriumSynthesizer ?? 0
				};
			}
			if (report.HasDefensesInformation && report.HasFleetInformation) {
				entry.HasDefenses = !report.IsDefenceless();
				entry.HasFleet = !report.IsDefenceless();
			}
			await _farmTargetCache.Upsert(entry);
		}

		private void AddMoons(List<Celestial> scannedTargets) {
			foreach (var t in scannedTargets.ToList()) {
				var planet = t as Planet;
				if (planet == null)
					continue;
				if (planet.Moon != null) {
					Celestial tempCelestial = planet.Moon;
					// Build a fresh Coordinate instead of mutating the planet's own instance
					// (assigning then setting Type turned the planet's coordinate into a moon).
					tempCelestial.Coordinate = new Coordinate(
						t.Coordinate.Galaxy,
						t.Coordinate.System,
						t.Coordinate.Position,
						Celestials.Moon);
					scannedTargets.Add(tempCelestial);
				}
			}
		}

		private FarmTarget CheckDuplicatesAndGetExisting(Celestial planet) {
			var exists = _tbotInstance.UserData.farmTargets.Where(t => t != null && t.Celestial.HasCoords(planet.Coordinate)).ToList();
			if (exists.Count() > 1) {
				var firstExisting = exists.First();
				_tbotInstance.UserData.farmTargets.RemoveAll(c => c.Celestial.HasCoords(planet.Coordinate));
				_tbotInstance.UserData.farmTargets.Add(firstExisting);
				return firstExisting;
			}
			else if (exists.Count() == 1) {
				return exists.First();
			}
			return null;
		}

		private FarmTarget GetFarmTarget(Celestial planet) {
		bool blacklistActive = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
			SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
			(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;

		if (blacklistActive && _blacklist.IsBlacklisted(planet.Coordinate)) {
			var blacklistedTarget = _blacklist.GetBlacklistedTarget(planet.Coordinate);
			_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} is blacklisted (Reason: {blacklistedTarget.Reason}). Skipping...");
			return null;
		}

		// The SQLite target cache keeps its own, time-limited blacklist (used by the defense-probe
		// flow); honour it too so a target blacklisted there isn't picked up again.
		if (blacklistActive && _farmTargetCache != null && _farmTargetCache.IsBlacklisted(planet.Coordinate, out DateTime blacklistedUntil)) {
			_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} is blacklisted until {blacklistedUntil}. Skipping...");
			return null;
		}

			var target = CheckDuplicatesAndGetExisting(planet);

			if (target == null) {
				target = new(planet, FarmState.ProbesPending);
				_tbotInstance.UserData.farmTargets.Add(target);
			} else {
				target.Celestial = planet;

				if (target.State == FarmState.Idle)
					target.State = FarmState.ProbesPending;

				if (target.State == FarmState.NotSuitable && target.Report != null) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} marked as Not Suitable. Skipping...");
					return null;
				}

				if (target.State == FarmState.ProbesSent || target.State == FarmState.AttackPending || target.State == FarmState.AttackSent) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} marked as {target.State.ToString()}. Skipping...");
					return null;
				}
			}
			return target;
		}

		private int GetNeededProbes(FarmTarget target) {
			int neededProbes = (int) _tbotInstance.InstanceSettings.AutoFarm.NumProbes;
			if (target.State == FarmState.ProbesRequired)
				neededProbes *= 3;
			if (target.State == FarmState.FailedProbesRequired)
				neededProbes *= 9;
			return neededProbes;
		}

		private Buildables GetConfiguredCargoType() {
			if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out var cargoType))
				return Buildables.LargeCargo;

			return cargoType;
		}

		private AutoFarmSlotBudget GetCurrentSlotBudget(Buildables cargoType) {
			return AutoFarmSlotPlanner.Calculate(
				(int) _tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
				(int) (_tbotInstance.UserData.slots?.Free ?? 0),
				(int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree,
				_tbotInstance.UserData.fleets,
				cargoType);
		}

		private class SpyOriginResult {
			public SpyOriginResult(Celestial origin, int freeSlots) {
				Origin = origin;
				BackIn = 0;
				FreeSlots = freeSlots;
			}

			public SpyOriginResult(Celestial origin, int backin, int freeSlots) {
				Origin = origin;
				BackIn = backin;
				FreeSlots = freeSlots;
			}
			public Celestial Origin { get; set; }
			public int BackIn { get; set; }
			public int FreeSlots { get; set; }
		}

		private async Task<SpyOriginResult> GetBestOrigin(List<Celestial> closestCelestials,
			Dictionary<int, long> celestialProbes,
			FarmTarget target,
			int neededProbes,
			int slotsToLeaveFree,
			int freeSlots) {
			SpyOriginResult bestOrigin = new SpyOriginResult(closestCelestials.First(), int.MaxValue, freeSlots);
			foreach (var closest in closestCelestials) {
				var tempCelestial = await _tbotOgameBridge.UpdatePlanet(closest, UpdateTypes.Ships);
				celestialProbes.Remove(closest.ID);
				celestialProbes.Add(closest.ID, tempCelestial.Ships.EspionageProbe);

				if (celestialProbes[closest.ID] >= neededProbes) {
					bestOrigin = new SpyOriginResult(closest, freeSlots);
					break;
				}

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

				if (freeSlots <= slotsToLeaveFree) {
					var espionageMissions = _calculationService.GetMissionsInProgress(closest.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets);
					if (espionageMissions.Any()) {
						var returningProbes = espionageMissions.Sum(f => f.Ships.EspionageProbe);
						if (celestialProbes[closest.ID] + returningProbes >= neededProbes) {
							var returningFleets = espionageMissions.OrderBy(f => f.BackIn).ToArray();
							long probesCount = 0;
							for (int i = 0; i < returningFleets.Length; i++) {
								probesCount += returningFleets[i].Ships.EspionageProbe;
								if (probesCount >= neededProbes) {
									if (bestOrigin.BackIn > returningFleets[i].BackIn)
										bestOrigin = new SpyOriginResult(closest, returningFleets[i].BackIn ?? int.MaxValue, freeSlots);
									break;
								}
							}
						}
					}
				} else {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Cannot spy {target.Celestial.Coordinate.ToString()} from {closest.Coordinate.ToString()}, insufficient probes ({celestialProbes[closest.ID]}/{neededProbes}).");
					if (bestOrigin.BackIn < int.MaxValue)
						continue;

					tempCelestial = await _tbotOgameBridge.UpdatePlanet(closest, UpdateTypes.Constructions);
					if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
						Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
						continue;
					}
					await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Productions);
					if (tempCelestial.Productions.Any(p => p.ID == (int) Buildables.EspionageProbe)) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: Probes already building.");
						continue;
					}

					await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);
					var buildProbes = neededProbes - celestialProbes[closest.ID];
					var cost = _calculationService.CalcPrice(Buildables.EspionageProbe, (int) buildProbes);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
					if (tempCelestial.Resources.IsEnoughFor(cost)) {
						bestOrigin = new SpyOriginResult(closest, int.MaxValue, freeSlots);
					}
				}
			}

			if (bestOrigin.BackIn != 0) {
				if (bestOrigin.BackIn != int.MaxValue) {
					int interval = (int) ((1000 * bestOrigin.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds));
					if (interval < 0)
						interval = 1000;
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Not enough free slots {freeSlots}/{slotsToLeaveFree}. Waiting {TimeSpan.FromMilliseconds(interval)} for probes to return...");
					await Task.Delay(interval, _ct);
					bestOrigin.Origin = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
					bestOrigin.FreeSlots++;
				}
			}

			return bestOrigin;
		}

		private async Task<int> WaitForFreeSlots(int freeSlots, int slotsToLeaveFree) {
			if (freeSlots <= slotsToLeaveFree) {
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				freeSlots = _tbotInstance.UserData.slots.Free;
			}

			_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
			while (freeSlots <= slotsToLeaveFree) {
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				if (_tbotInstance.UserData.fleets.Any()) {
					// Floor of MinPollIntervalMs: short farm flights mean the "next fleet back" is
					// often already due any moment, which without a floor produced a sub-second
					// retry loop (dozens of log lines and ogamed API calls per minute) instead of
					// actually waiting for a slot to free up.
					const int MinPollIntervalMs = 5000;
					int interval = Math.Max(MinPollIntervalMs, (int) ((1000 * _tbotInstance.UserData.fleets.OrderBy(fleet => fleet.BackIn).First().BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanASecond)));
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Out of fleet slots. Waiting {TimeSpan.FromMilliseconds(interval)} for fleet to return...");
					await Task.Delay(interval, _ct);
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					freeSlots = _tbotInstance.UserData.slots.Free;
				} else {
					_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, "Error: No fleet slots available and no fleets returning!");
					throw new Exception("No fleet slots available and no fleets returning!");
				}
			}
			return freeSlots;
		}

		protected override async Task Execute() {
			bool stop = false;
			bool stopAfterFullScan = false;
			bool finishedFullScan = false;
			int skippedEmptySystems = 0;
			try {
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Running autofarm...");
				LoadPersistedTargets();
				// Dispose the previous cycle's SQLite connection before opening a new one - a fresh
				// connection per Execute() cycle avoids holding one open indefinitely across the
				// worker's whole lifetime.
				_farmTargetCache?.Dispose();
				_farmTargetCache = await FarmTargetCache.Load(_tbotInstance.InstanceSettingsPath, _tbotInstance.InstanceAlias);
				stopAfterFullScan = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "StopAfterFullScan")
					&& (bool)_tbotInstance.InstanceSettings.AutoFarm.StopAfterFullScan;
				if ((bool) _tbotInstance.InstanceSettings.AutoFarm.Active) {				
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					
					int freeSlots = _tbotInstance.UserData.slots.Free;
					int slotsToLeaveFree = (int) _tbotInstance.InstanceSettings.AutoFarm.SlotsToLeaveFree;
					if (freeSlots <= slotsToLeaveFree) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to start auto farm, no slots available");
						return;
					}

					try {
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						await PruneOldReports(_tbotInstance.UserData.fleets);
						PersistTargets();

						await ProcessDefenseProbingResults();
						freeSlots = await SendDefenseProbeAttacks(freeSlots, slotsToLeaveFree);

						var farmOrigins = GetFarmOrigins();
						var celestialProbes = GetCachedCelestialProbes(farmOrigins);
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

						int numProbed = 0;

						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Detecting farm targets...");
						bool stopAutoFarm = false;

						var scanRanges = ((IEnumerable<dynamic>)_tbotInstance.InstanceSettings.AutoFarm.ScanRange).ToList();

						var allScanRanges = ((IEnumerable<dynamic>)_tbotInstance.InstanceSettings.AutoFarm.ScanRange).ToList();
						int totalSystemsAcrossAllGalaxies = allScanRanges.Sum(r => (int)r.EndSystem - (int)r.StartSystem + 1);
						int instanceHash = Math.Abs(_tbotInstance.InstanceAlias.GetHashCode());
						int minSpacing = 499;
						int numSlotsGlobal = Math.Max(1, totalSystemsAcrossAllGalaxies / minSpacing);
						int globalSlotIndex = instanceHash % numSlotsGlobal;
						int globalOffset = globalSlotIndex * minSpacing;

						int targetGalaxy = 1;
						int targetSystem = 1;
						int remainingOffset = globalOffset;
						foreach (var r in allScanRanges) {
							int systemsInRange = (int)r.EndSystem - (int)r.StartSystem + 1;
							if (remainingOffset < systemsInRange) {
								targetGalaxy = (int)r.Galaxy;
								targetSystem = (int)r.StartSystem + remainingOffset;
								break;
							}
							remainingOffset -= systemsInRange;
						}

						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Instance: {(LogPrivacy.HideAccountInfo ? "Instance Alias" : _tbotInstance.InstanceAlias)}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Total systems across all galaxies: {totalSystemsAcrossAllGalaxies}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Num slots globally: {numSlotsGlobal}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Global slot index: {globalSlotIndex}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Global offset: {globalOffset} systems");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Target Galaxy: {targetGalaxy}, Target System: {targetSystem}");

						var orderedRanges = scanRanges
							.OrderBy(r => (int)r.Galaxy)
							.ThenBy(r => (int)r.StartSystem)
							.ToList();

						if (!orderedRanges.Any()) {
							_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "No scan ranges match galaxies with planets. Skipping AutoFarm.");
							stopAutoFarm = true;
						}

						int startRangeIndex = _tbotInstance.UserData.autoFarmLastRangeIndex;
						if (startRangeIndex < 0 || startRangeIndex >= orderedRanges.Count) {
							startRangeIndex = 0;
						}
						if (_tbotInstance.UserData.autoFarmLastGalaxy == 0 && _tbotInstance.UserData.autoFarmLastSystem == 0) {
							int idx = orderedRanges.FindIndex(r => (int)r.Galaxy == targetGalaxy);
							if (idx >= 0) startRangeIndex = idx;
							_tbotInstance.UserData.autoFarmLastRangeIndex = startRangeIndex;
						}

						for (int rangeOffset = 0; rangeOffset < orderedRanges.Count; rangeOffset++) {
							int rangeIndex = (startRangeIndex + rangeOffset) % orderedRanges.Count;
							if (stopAutoFarm)
								break;
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") && ((int)_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0) && numProbed >= (int)_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
								break;
							}

							var range = orderedRanges[rangeIndex];

							int galaxy = (int) range.Galaxy;
							int originalStartSystem = (int) range.StartSystem;
							int endSystem = (int) range.EndSystem;
							int startSystem = originalStartSystem;
                                 if (_tbotInstance.UserData.autoFarmLastGalaxy == galaxy &&
                                       _tbotInstance.UserData.autoFarmLastSystem >= originalStartSystem &&
                                    _tbotInstance.UserData.autoFarmLastSystem <= endSystem) {

                                startSystem = _tbotInstance.UserData.autoFarmLastSystem;
                                       _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
                                       $"Resuming scan from Galaxy {galaxy} System {startSystem}");
                           }
                            else {
                                         startSystem = originalStartSystem;
                                  _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
                               $"[START] Galaxy {galaxy} - Ordered start at system {startSystem}");
                              }

							foreach (int system in AutoFarmScanPlanner.EnumerateSystems(originalStartSystem, endSystem, startSystem)) {

								if (stopAutoFarm)
									break;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") && ((int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0) && numProbed >= (int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Maximum number of targets to probe reached, proceeding to attack.");
									SaveScanCursor(rangeIndex, galaxy, system);
									stopAutoFarm = true;
									break;
								}

								bool excludeSystem = ShouldExcludeSystem(galaxy, system);
								if (excludeSystem)
									continue;

								List<Celestial> scannedTargets;
								try {
									scannedTargets = await GetScannedTargetsFromGalaxy(galaxy, system);
								} catch (Exception e) {
									_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Skipping system {galaxy}:{system} after scan failure: {e.Message}");
									continue;
								}

								if (scannedTargets == null) {
									skippedEmptySystems++;
									continue;
								}

								_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Found {scannedTargets.Count} targets on System {galaxy}:{system}");

								if (!scannedTargets.Any()) {
									continue;
								}

								if ((bool) _tbotInstance.InstanceSettings.AutoFarm.ExcludeMoons == false) {
									AddMoons(scannedTargets);
								}

								foreach (Celestial planet in scannedTargets) {
									if (stopAutoFarm)
										break;
									if (!await IsTargetInMinimumRank(planet, scannedTargets)) {
										continue;
									}

									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") &&
										_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0 && numProbed >= (int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Maximum number of targets to probe reached, proceeding to attack.");
										SaveScanCursor(rangeIndex, galaxy, system);
										stopAutoFarm = true;
										break;
									}

									if (ShouldExcludePlanet(planet))
										continue;

									var target = GetFarmTarget(planet);
									if (target == null)
										continue;

									List<Celestial> tempCelestials = farmOrigins;

									List<Celestial> closestCelestials = tempCelestials
										.OrderByDescending(planet => planet.Coordinate.Type == Celestials.Moon)
										.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData)).ToList();

									if (!closestCelestials.Any()) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"No origin celestials available. Skipping target {target.Celestial.ToString()}");
										continue;
									}


									int neededProbes = GetNeededProbes(target);

									await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);

									_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
									_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
									var slotBudget = GetCurrentSlotBudget(GetConfiguredCargoType());
									if (slotBudget.AvailableSlots <= 0) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
											$"AutoFarm slot budget exhausted ({slotBudget.OwnedSlots}/{slotBudget.MaxSlots}); pausing further probes.");
										SaveScanCursor(rangeIndex, galaxy, system);
										stopAutoFarm = true;
										break;
									}

									var bestOrigin = await GetBestOrigin(closestCelestials,
										celestialProbes,
										target,
										neededProbes,
										slotsToLeaveFree,
										freeSlots);

									freeSlots = bestOrigin.FreeSlots;

									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Best origin found: {bestOrigin.Origin.Name} ({bestOrigin.Origin.Coordinate.ToString()})");

									if (_calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets).Any(f => f.Destination.IsSame(target.Celestial.Coordinate))) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Probes already on route towards {target.ToString()}.");
										continue;
									}
									if (_calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Attack, _tbotInstance.UserData.fleets).Any(f => f.Destination.IsSame(target.Celestial.Coordinate) && f.ReturnFlight == false)) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Attack already on route towards {target.ToString()}.");
										continue;
									}

									if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
										var tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
										celestialProbes.Remove(bestOrigin.Origin.ID);
										celestialProbes.Add(bestOrigin.Origin.ID, tempCelestial.Ships.EspionageProbe);
									}

									if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Insufficient probes ({celestialProbes[bestOrigin.Origin.ID]}/{neededProbes}).");
										if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "BuildProbes") && _tbotInstance.InstanceSettings.AutoFarm.BuildProbes == true) {
											if (_tbotInstance.UserData.isSleeping) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {bestOrigin.Origin.ToString()}: sleep mode active, not building probes.");
												break;
											}

											var tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Constructions);
											if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
												Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
												SaveScanCursor(rangeIndex, galaxy, system);
												break;
											}

											tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Productions);
											if (tempCelestial.Productions.Any(p => p.ID == (int) Buildables.EspionageProbe)) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: Probes already building.");
												SaveScanCursor(rangeIndex, galaxy, system);
												break;
											}

											var buildProbes = neededProbes - celestialProbes[bestOrigin.Origin.ID];
											var cost = _calculationService.CalcPrice(Buildables.EspionageProbe, (int) buildProbes);
											tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Resources);
											if (tempCelestial.Resources.IsEnoughFor(cost)) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Building {buildProbes}x{Buildables.EspionageProbe.ToString()}");

												try {
													await _ogameService.BuildShips(tempCelestial, Buildables.EspionageProbe, buildProbes);
													tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Facilities);
													int interval = (int) (_calculationService.CalcProductionTime(Buildables.EspionageProbe, (int) buildProbes, _tbotInstance.UserData.serverData, tempCelestial.Facilities) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Production succesfully started. Waiting {TimeSpan.FromMilliseconds(interval)} for build order to finish...");
													await Task.Delay(interval, _ct);
													// Refresh the cached probe count after the build finished, otherwise the
													// dispatch below still sees the pre-build number and skips the target.
													var rebuiltCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
													celestialProbes[bestOrigin.Origin.ID] = rebuiltCelestial.Ships.EspionageProbe;
												} catch {
													_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to start ship production.");
												}
											} else {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Not enough resources to build probes.");
												_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
												var spyMissions = _calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets);
												if (spyMissions.Any()) {
													var spyMissionToWait = spyMissions.OrderBy(c => c.BackIn).First();
													int interval = (int) ((1000 * spyMissionToWait.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanASecond));
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Waiting {TimeSpan.FromMilliseconds(interval)} for spy mission to return...");
													await Task.Delay(interval);
												} else {
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"There are not enough probes or resources to build them. Skipping this AutoFarm Execution.");
													SaveScanCursor(rangeIndex, galaxy, system);
													stopAutoFarm = true;
													break;
												}
											}
										}
									}

									if (celestialProbes[bestOrigin.Origin.ID] >= neededProbes) {

										Ships ships = new();
										ships.Add(Buildables.EspionageProbe, neededProbes);

										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Spying {target.ToString()} from {bestOrigin.Origin.ToString()} with {neededProbes} probes.");

										_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
										_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
										slotBudget = GetCurrentSlotBudget(GetConfiguredCargoType());
									if (slotBudget.AvailableSlots <= 0) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
											$"AutoFarm slot budget exhausted ({slotBudget.OwnedSlots}/{slotBudget.MaxSlots}); no probe dispatched.");
										SaveScanCursor(rangeIndex, galaxy, system);
										stopAutoFarm = true;
										break;
									}

										bestOrigin.Origin = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
										var availableProbes = bestOrigin.Origin.Ships.EspionageProbe;

										if (availableProbes < neededProbes) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
												$"Insufficient probes on {bestOrigin.Origin.ToString()} ({availableProbes}/{neededProbes}). Skipping {target.ToString()}.");
											continue;
										}

										var fleetId = await _fleetScheduler.SendFleet(bestOrigin.Origin, ships, target.Celestial.Coordinate, Missions.Spy, Speeds.HundredPercent);

										if (fleetId > (int) SendFleetCode.GenericError) {
											freeSlots--;
											numProbed++;
											celestialProbes[bestOrigin.Origin.ID] -= neededProbes;

											if (target.State == FarmState.ProbesRequired || target.State == FarmState.FailedProbesRequired)
												continue;

											_tbotInstance.UserData.farmTargets.Remove(target);
											target.State = FarmState.ProbesSent;
											_tbotInstance.UserData.farmTargets.Add(target);

											continue;
										} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
											stop = true;
											return;
										} else if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Another worker took the last available slot; pausing AutoFarm instead of retrying repeatedly.");
											SaveScanCursor(rangeIndex, galaxy, system);
											stopAutoFarm = true;
											break;
										} else {
											continue;
										}
									}
								}
							}
						}
							if (!stopAutoFarm && orderedRanges.Any()) {
								SaveScanCursor(0, 0, 0);
								 finishedFullScan = true;

								 _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
										"Full scan cycle completed, resetting scan position for next cycle");

                              if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "StopAfterFullScan") &&
                               (bool)_tbotInstance.InstanceSettings.AutoFarm.StopAfterFullScan) {

									 _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
									"StopAfterFullScan=true -> Full scan completed. Attacks will be processed before AutoFarm stops.");
								 }
							 }

					} catch (Exception e) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Exception: {e.Message}");
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to parse scan range");
					}

					if (skippedEmptySystems > 0)
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Skipped {skippedEmptySystems} systems still in the empty-system cooldown.");
					PersistTargets();

					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					Fleet firstReturning = _calculationService.GetLastReturningEspionage(_tbotInstance.UserData.fleets);
					if (firstReturning != null) {
						int interval = (int) ((1000 * firstReturning.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Waiting {TimeSpan.FromMilliseconds(interval)} for all probes to return...");
						await Task.Delay(interval, _ct);
					}

					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Processing espionage reports of found inactives...");

					await AutoFarmProcessReports();
					PersistTargets();

					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					Buildables configuredCargoType = GetConfiguredCargoType();
					List<RankSlotsPriority> rankSlotsPriority = new() {
						new RankSlotsPriority(Feature.BrainAutoMine,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
							((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.Transports.Active && ((bool) _tbotInstance.InstanceSettings.Brain.AutoMine.Active || (bool) _tbotInstance.InstanceSettings.Brain.AutoResearch.Active || (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active || (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
							(int) _tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)),
						new RankSlotsPriority(Feature.Expeditions,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
							(bool) _tbotInstance.InstanceSettings.Expeditions.Active,
							(int) _tbotInstance.UserData.slots.ExpTotal,
							(int)_tbotInstance.UserData.slots.ExpInUse),
						new RankSlotsPriority(Feature.AutoFarm,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
							(bool) _tbotInstance.InstanceSettings.AutoFarm.Active,
							(int) _tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => AutoFarmSlotPlanner.IsOwnedFleet(f, configuredCargoType))),
						new RankSlotsPriority(Feature.Colonize,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoColonize,
							(bool) _tbotInstance.InstanceSettings.AutoColonize.Active,
							(bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ?
								(int) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots :
								1,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)),
						new RankSlotsPriority(Feature.AutoDiscovery,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
							(bool) _tbotInstance.InstanceSettings.AutoDiscovery.Active,
							(int) _tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)),
						new RankSlotsPriority(Feature.Harvest,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
							(bool) _tbotInstance.InstanceSettings.AutoHarvest.Active,
							(int) _tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest))
					};
					int MaxSlots = _calculationService.CalcSlotsPriority(Feature.AutoFarm, rankSlotsPriority, _tbotInstance.UserData.slots, _tbotInstance.UserData.fleets, (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree);

					List<FarmTarget> attackTargets;
					if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Metal")
						attackTargets = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Metal).ToList();
					else if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Crystal")
						attackTargets = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Crystal).ToList();
					else if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Deuterium")
						attackTargets = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Deuterium).ToList();
					else
						attackTargets = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources).ToList();

					if (attackTargets.Count() > 0) {
						var resourceAmount = new Resources();
						attackTargets.ForEach(target => resourceAmount = resourceAmount.Sum(target.Report.Loot(_tbotInstance.UserData.userInfo.Class)));
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking suitable farm targets... (Estimated total profit: {resourceAmount.TransportableResources})");
					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "No suitable targets found.");
						return;
					}

					Buildables cargoShip = configuredCargoType;
					if (cargoShip == Buildables.Null) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to send attack: cargoShip is Null");
						return;
					}
					if (cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 0) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to send attack: cargoShip set to EspionageProbe, but this universe does not have probe cargo.");
						return;
					}

					_tbotInstance.UserData.researches = await _tbotOgameBridge.UpdateResearches();
					_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdateCelestials();
					int attackTargetsCount = 0;
					decimal lootFuelRatio = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") ? (decimal) _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio : (decimal) 0.0001;
					decimal speed = 0;
					foreach (FarmTarget target in attackTargets) {
						attackTargetsCount++;
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking target {attackTargetsCount}/{attackTargets.Count()} at {target.Celestial.Coordinate.ToString()} for {target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TransportableResources}.");
						var loot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class);
						Celestial tempCelestial = _tbotInstance.UserData.celestials.Where(c => c.Coordinate.Type == Celestials.Planet).First();
						tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
						float cargoBonus = tempCelestial.LFBonuses.GetShipCargoBonus(cargoShip);
						var numCargo = _calculationService.CalcShipNumberForPayload(loot, cargoShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
						if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "CargoSurplusPercentage") && (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage > 0) {
							numCargo = (long) Math.Round(numCargo + (numCargo / 100 * (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage), 0);
						}
						var attackingShips = new Ships().Add(cargoShip, numCargo);

						List<Celestial> tempCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0) ? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials) : _tbotInstance.UserData.celestials;
						List<Celestial> closestCelestials = tempCelestials
							.OrderByDescending(planet => planet.Coordinate.Type == Celestials.Moon)
							.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData))
							.ToList();

						Celestial fromCelestial = null;
						foreach (var c in closestCelestials) {
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(c, UpdateTypes.Ships);
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
							if (tempCelestial.Ships != null && tempCelestial.Ships.GetAmount(cargoShip) >= (numCargo + _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep)) {
								speed = 0;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
									long maxFlightTime = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ? (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime : 86400;
									var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
									if (optimalSpeed == 0) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to calculate a valid optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");

									} else {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Calculated optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");
										speed = optimalSpeed;
									}
								}
								if (speed == 0) {
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
										speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
										if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
											speed = Speeds.HundredPercent;
										}
									} else {
										speed = Speeds.HundredPercent;
									}
								}
								FleetPrediction prediction = _calculationService.CalcFleetPrediction(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, Missions.Attack, speed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

								if (
									(
										!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ||
										(long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime == 0 ||
										prediction.Time <= (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime
									) &&
									prediction.Fuel <= tempCelestial.Resources.Deuterium
								) {
									fromCelestial = tempCelestial;
									break;
								}
							}
						}

						if (fromCelestial == null) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"No origin celestial available near destination {target.Celestial.ToString()} with enough cargo ships.");
							foreach (var closest in closestCelestials) {
								tempCelestial = closest;
								tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);
								tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
								tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
								speed = 0;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
									speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
									if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
										speed = Speeds.HundredPercent;
									}
								} else {
									speed = 0;
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
										long maxFlightTime = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ? (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime : 86400;
										var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
										if (optimalSpeed == 0) {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to calculate a valid optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");

										} else {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Calculated optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");
											speed = optimalSpeed;
										}
									}
									if (speed == 0) {
										if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
											speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
											if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
												_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
												speed = Speeds.HundredPercent;
											}
										} else {
											speed = Speeds.HundredPercent;
										}
									}
								}
								FleetPrediction prediction = _calculationService.CalcFleetPrediction(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, Missions.Attack, speed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

								if (
									tempCelestial.Ships.GetAmount(cargoShip) < numCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep &&
									tempCelestial.Resources.Deuterium >= prediction.Fuel &&
									(
										!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ||
										(long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime == 0 ||
										prediction.Time <= (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime
									)
								) {
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "BuildCargos") && _tbotInstance.InstanceSettings.AutoFarm.BuildCargos == true) {
										if (_tbotInstance.UserData.isSleeping) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: sleep mode active, not building {cargoShip.ToString()}.");
											continue;
										}

										tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Constructions);
										if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
											Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
											continue;
										}

										tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Productions);
										if (tempCelestial.Productions.Any()) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: a production is already in progress.");
											continue;
										}

										var neededCargos = numCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep - tempCelestial.Ships.GetAmount(cargoShip);
										var cost = _calculationService.CalcPrice(cargoShip, (int) neededCargos);
										if (tempCelestial.Resources.IsEnoughFor(cost)) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Building {neededCargos}x{cargoShip.ToString()}");
										} else {
											var buildableCargos = _calculationService.CalcMaxBuildableNumber(cargoShip, tempCelestial.Resources);
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Not enough resources to build {neededCargos}x{cargoShip.ToString()}. {buildableCargos.ToString()} will be built instead.");
											neededCargos = buildableCargos;
										}

										try {
											await _ogameService.BuildShips(tempCelestial, cargoShip, neededCargos);
											tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Facilities);
											int interval = (int) (_calculationService.CalcProductionTime(cargoShip, (int) neededCargos, _tbotInstance.UserData.serverData, tempCelestial.Facilities) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Production succesfully started. Waiting {TimeSpan.FromMilliseconds(interval)} for build order to finish...");
											await Task.Delay(interval, _ct);
										} catch {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to start ship production.");
										}
									}

									if (tempCelestial.Ships.GetAmount(cargoShip) - (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep < (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToSend) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Insufficient {cargoShip.ToString()} on {tempCelestial.Coordinate}, require {numCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep} {cargoShip.ToString()}.");
										continue;
									}

									numCargo = tempCelestial.Ships.GetAmount(cargoShip) - (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep;
									fromCelestial = tempCelestial;
									break;
								}
							}
						}

						if (fromCelestial == null) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Unable to attack {target.Celestial.Coordinate}. No suitable origin celestial available near the destination.");
							continue;
						}

						bool isUsingProbesForDefenceCheck = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1;
						if (!target.Report.IsDefenceless(isUsingProbesForDefenceCheck)) {
							if (!TryGetAcceptableCombatFleet(target.Report, fromCelestial.Ships, out Ships combatShips, out _, out _)) {
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping defended target {target.Celestial.Coordinate}: no acceptable combat fleet available at {fromCelestial}.");
								continue;
							}
							foreach (var type in GetAllowedFarmCombatShips()) {
								long qty = combatShips.GetAmount(type);
								if (qty > 0)
									attackingShips = attackingShips.Add(type, qty);
							}
						}

						_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						freeSlots = _tbotInstance.UserData.slots.Free;
						var slotBudget = GetCurrentSlotBudget(cargoShip);
						if (MaxSlots > 0 && slotBudget.AvailableSlots > 0) {
							fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.Ships);
							var availableShips = fromCelestial.Ships.GetAmount(cargoShip) - (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep;
							if (availableShips <= 0) {
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"No {cargoShip.ToString()} available on {fromCelestial.ToString()} (all ships already in use). Skipping target.");
								continue;
							}
							if (availableShips < numCargo) {
								_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Only {availableShips} {cargoShip.ToString()} available (needed {numCargo}). Adjusting fleet size.");
								numCargo = availableShips;
								attackingShips = new Ships();
								attackingShips.Add(cargoShip, numCargo);

								var cargoCapacity = _calculationService.CalcFleetCapacity(
									attackingShips, _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology,
									fromCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class);
								var totalLoot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources;

								if (cargoCapacity < totalLoot) {
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
										$"Insufficient cargo space after adjustment. {numCargo} {cargoShip.ToString()} can carry {cargoCapacity:N0} but need {totalLoot:N0}. Skipping target.");
									continue;
								}
							}

							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking {target.ToString()} from {fromCelestial} with {numCargo} {cargoShip.ToString()}.");
							Ships ships = new();
							fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.LFBonuses);

							speed = 0;
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
								long maxFlightTime = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ? (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime : 86400;
								var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(fromCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, fromCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
								if (optimalSpeed == 0) {
									_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to calculate a valid optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");

								} else {
									_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Calculated optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");
									speed = optimalSpeed;
								}
							}
							if (speed == 0) {
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
									speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
									if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
										speed = Speeds.HundredPercent;
									}
								} else {
									speed = Speeds.HundredPercent;
								}
							}

							var fleetId = await _fleetScheduler.SendFleet(fromCelestial, attackingShips, target.Celestial.Coordinate, Missions.Attack, speed);

							if (fleetId > (int) SendFleetCode.GenericError) {
								freeSlots--;

								_successfulTargets.RecordAttack(target.Celestial.Coordinate, loot);
								string attackedPlayerName = (target.Celestial as Planet)?.Player?.Name;
								if (_farmTargetCache != null)
									await _farmTargetCache.RecordAttack(target.Celestial.Coordinate, attackedPlayerName, loot.Metal, loot.Crystal, loot.Deuterium, DateTime.UtcNow);
								_tbotInstance.UserData.farmTargets.Remove(target);
								target.State = FarmState.AttackSent;
								target.ConsumedReportId = target.Report?.ID > 0 ? target.Report.ID : target.ConsumedReportId;
								_tbotInstance.UserData.farmTargets.Add(target);
								if (!_stateStore.RecordAttack(target, loot, DateTime.UtcNow, fromCelestial.Coordinate, Missions.Attack.ToString()))
									_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
										$"Attack sent to {target.Celestial.Coordinate}, but it could not be recorded in the AutoFarm database.");
								PersistTargets();
							} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
								stop = true;
								return;
							} else if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
								_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
									"Another worker took the last available slot; keeping the attack pending for the next cycle.");
								break;
							} else {
								_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
									$"Attack dispatch failed for {target.Celestial.Coordinate}; keeping the target pending.");
								continue;
							}
						} else {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
								$"AutoFarm attack budget exhausted: {_tbotInstance.UserData.slots.Free} slots free, {slotBudget.OwnedSlots}/{slotBudget.MaxSlots} AutoFarm slots used.");
							break;
						}
					}
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarm Exception: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
			} finally {
				PersistTargets();
				if (stopAfterFullScan && finishedFullScan) {
					stop = true;
				}

				if (_farmTargetCache != null) {
					await _farmTargetCache.Save();
				}

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacked targets: {_tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackSent).Count()}");
				_tbotInstance.UserData.farmTargets.RemoveAll(t => t.State == FarmState.ProbesSent);
				PersistTargets();
				if (!_tbotInstance.UserData.isSleeping) {
					if (stop) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Stopping feature.");
						await EndExecution();
					} else {
						var time = await _tbotOgameBridge.GetDateTime();
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						long interval = RandomizeHelper.CalcRandomInterval(
							(int) _tbotInstance.InstanceSettings.AutoFarm.CheckIntervalMin,
							(int) _tbotInstance.InstanceSettings.AutoFarm.CheckIntervalMax);
						if (interval <= 0)
							interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						var newTime = time.AddMilliseconds(interval);
						ChangeWorkerPeriod(interval);
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							$"Next autofarm check at {newTime.ToString()} (rechecking with any remaining fleet slots).");
						await _tbotOgameBridge.CheckCelestials();
					}
				}
			}
		}

		private async Task AutoFarmProcessReports() {
			List<EspionageReportSummary> summaryReports;
			try {
				summaryReports = await _ogameService.GetEspionageReports() ?? new List<EspionageReportSummary>();
			} catch (Exception e) {
				// Report cleanup is best effort. A temporary OGame/API failure must not
				// prevent the attack phase from processing targets already marked pending.
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"Unable to retrieve espionage reports; keeping existing attack-pending targets: {e.Message}");
				return;
			}

			foreach (var summary in summaryReports) {
				if (summary == null || summary.Type == EspionageReportType.Action)
					continue;

				try {
					var report = await _ogameService.GetEspionageReport(summary.ID);
					if (report?.Coordinate == null) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
							$"Ignoring malformed espionage report {summary.ID}: no coordinate was returned.");
						continue;
					}
					if (_stateStore.HasConsumedReport(report.ID)) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"Ignoring consumed espionage report {report.ID} for {report.Coordinate}.");
						await _ogameService.DeleteReport(report.ID);
						continue;
					}
					if (DateTime.Compare(report.Date.AddMinutes((double) _tbotInstance.InstanceSettings.AutoFarm.KeepReportFor), await _tbotOgameBridge.GetDateTime()) < 0) {
						await _ogameService.DeleteReport(report.ID);
						continue;
					}

					if (_tbotInstance.UserData.farmTargets.Any(t => t.HasCoords(report.Coordinate))) {
						FarmTarget target;
						var matchingTarget = _tbotInstance.UserData.farmTargets.Where(t => t.HasCoords(report.Coordinate));
						if (matchingTarget.Count() == 0) {
							if (!report.IsInactive)
								continue;
							var galaxyInfo = await _ogameService.GetGalaxyInfo(report.Coordinate.Galaxy, report.Coordinate.System);
							var planet = galaxyInfo.Planets.FirstOrDefault(p => p != null && p.Inactive && !p.Administrator && !p.Banned && !p.Vacation && p.HasCoords(report.Coordinate));
							if (planet != null) {
								target = GetFarmTarget(planet);
								if (target == null)
									continue;
							} else {
								continue;
							}
						} else {
							target = matchingTarget.First();
						}

						if (!AutoFarmAttackPolicy.CanProcessReport(target, report)) {
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
								$"Ignoring already consumed or still active espionage report {report.ID} for {report.Coordinate}.");
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						if (AutoFarmAttackPolicy.IsRepeatedNonActionableReport(target, report)) {
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
								$"Ignoring previously handled non-actionable espionage report {report.ID} for {report.Coordinate}; deletion may have failed.");
							continue;
						}

						var newFarmTarget = target;

						if (target.Report != null && DateTime.Compare(report.Date, target.Report.Date) < 0) {
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						Buildables cargoShip;
						Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip);
						bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1 ? true : false;
						newFarmTarget.Report = report;
						await CacheUpsertFromReport(report);
						if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Metal" && report.Loot(_tbotInstance.UserData.userInfo.Class).Metal > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources
							|| _tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Crystal" && report.Loot(_tbotInstance.UserData.userInfo.Class).Crystal > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources
							|| _tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Deuterium" && report.Loot(_tbotInstance.UserData.userInfo.Class).Deuterium > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources
							|| (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "" && report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources)) {
							if (!report.HasFleetInformation || !report.HasDefensesInformation) {
								if (target.State == FarmState.ProbesRequired)
									newFarmTarget.State = FarmState.FailedProbesRequired;
								else if (target.State == FarmState.FailedProbesRequired)
									newFarmTarget.State = FarmState.NotSuitable;
								else
									newFarmTarget.State = FarmState.ProbesRequired;

								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Need more probes on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else if (report.IsDefenceless(isUsingProbes)) {
								newFarmTarget.State = FarmState.AttackPending;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else if (GetAcceptableFleetLossPercentage() > 0) {
								// Defended, but the user allows fighting through a garrison within an acceptable
								// fleet-loss threshold. Feasibility (does the combat fleet at the chosen origin
								// actually beat this target within the threshold?) can only be checked once an
								// origin is picked, so it's re-evaluated in Execute() right before sending.
								newFarmTarget.State = FarmState.AttackPending;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate} (defended - will simulate battle before sending). Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else {
								newFarmTarget.State = FarmState.NotSuitable;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - defences present.");
							bool blacklistActive = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
								SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
							if (blacklistActive) {
								int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
								_blacklist.AddTarget(report.Coordinate, BlacklistReason.HasDefense, hoursUntilReset);
								_farmTargetCache?.Blacklist(report.Coordinate, DateTime.UtcNow.AddHours(hoursUntilReset));
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (defenses present).");
							}
							}
						} else {
							newFarmTarget.State = FarmState.NotSuitable;
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - insufficient loot ({report.Loot(_tbotInstance.UserData.userInfo.Class)})");
							bool blacklistActiveLowRes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
								SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
							if (blacklistActiveLowRes) {
								long minResources = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist", (long)500000);
								if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < minResources) {
									int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
									_blacklist.AddTarget(report.Coordinate, BlacklistReason.LowResources, hoursUntilReset);
									_farmTargetCache?.Blacklist(report.Coordinate, DateTime.UtcNow.AddHours(hoursUntilReset));
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (low resources: {report.Loot(_tbotInstance.UserData.userInfo.Class)}).");
								}
							}
						}

						_tbotInstance.UserData.farmTargets.Remove(target);
						_tbotInstance.UserData.farmTargets.Add(newFarmTarget);
					} else {
					bool processAllReports = (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ProcessAllReports") &&
						(bool) _tbotInstance.InstanceSettings.AutoFarm.ProcessAllReports) ||
						(SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
						SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ProcessAllReports") &&
						(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.ProcessAllReports);
					if (processAllReports && report.IsInactive) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Processing report for {report.Coordinate} not scanned by TBot (ProcessAllReports enabled)...");
						var galaxyInfo = await _ogameService.GetGalaxyInfo(report.Coordinate.Galaxy, report.Coordinate.System);
						var planet = galaxyInfo.Planets.FirstOrDefault(p => p != null && p.Inactive && !p.Administrator && !p.Banned && !p.Vacation && p.HasCoords(report.Coordinate));
						if (planet != null) {
							var target = GetFarmTarget(planet);
							if (target != null) {
								if (!AutoFarmAttackPolicy.CanProcessReport(target, report)) {
									_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
										$"Ignoring already consumed or still active espionage report {report.ID} for {report.Coordinate}.");
									await _ogameService.DeleteReport(report.ID);
									continue;
								}

								if (AutoFarmAttackPolicy.IsRepeatedNonActionableReport(target, report)) {
									_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
										$"Ignoring previously handled non-actionable espionage report {report.ID} for {report.Coordinate}; deletion may have failed.");
									continue;
								}

								var newFarmTarget = target;
								Buildables cargoShip;
								Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip);
								bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1 ? true : false;
								newFarmTarget.Report = report;
								await CacheUpsertFromReport(report);
								if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources) {
									if (report.HasFleetInformation && report.HasDefensesInformation) {
										if (report.IsDefenceless(isUsingProbes)) {
											newFarmTarget.State = FarmState.AttackPending;
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
										} else if (GetAcceptableFleetLossPercentage() > 0) {
											newFarmTarget.State = FarmState.AttackPending;
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate} (defended - will simulate battle before sending). Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
										} else {
											newFarmTarget.State = FarmState.NotSuitable;
											bool blacklistActiveDefense2 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
												(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
											if (blacklistActiveDefense2) {
												int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
												_blacklist.AddTarget(report.Coordinate, BlacklistReason.HasDefense, hoursUntilReset);
												_farmTargetCache?.Blacklist(report.Coordinate, DateTime.UtcNow.AddHours(hoursUntilReset));
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (defenses present).");
											}
										}
									}
								} else {
									newFarmTarget.State = FarmState.NotSuitable;
									bool blacklistActiveLowRes2 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
										(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
									if (blacklistActiveLowRes2) {
										long minResources = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist", (long)500000);
										if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < minResources) {
											int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
											_blacklist.AddTarget(report.Coordinate, BlacklistReason.LowResources, hoursUntilReset);
											_farmTargetCache?.Blacklist(report.Coordinate, DateTime.UtcNow.AddHours(hoursUntilReset));
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (low resources: {report.Loot(_tbotInstance.UserData.userInfo.Class)}).");
										}
									}
								}
								_tbotInstance.UserData.farmTargets.Remove(target);
								_tbotInstance.UserData.farmTargets.Add(newFarmTarget);
							}
						}
					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not scanned by TBot, ignoring...");
					}
					}
				} catch (Exception e) {
					_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarmProcessReports Exception: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
					continue;
				}
			}

			int deleteRetries = 3;
			for (int i = 0; i < deleteRetries; i++) {
				try {
					await _ogameService.DeleteAllEspionageReports();
					break;
				} catch (Exception e) {
					var message = e.Message ?? string.Empty;
					var retryable = message.Contains("503", StringComparison.OrdinalIgnoreCase)
						|| message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
						|| message.Contains("Unable to delete", StringComparison.OrdinalIgnoreCase);

					if (retryable && i < deleteRetries - 1) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
							$"Failed to delete espionage reports ({message}), retry {i + 1}/{deleteRetries}...");
						await Task.Delay(3000, _ct);
						continue;
					}

					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
						$"Could not delete espionage reports after {i + 1} attempt(s): {message}. Attack decisions are retained for the next cycle.");
					break;
				}
			}

		}
	}
}
