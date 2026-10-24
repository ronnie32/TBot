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
		private readonly HashSet<int> _autoFarmEspionageFleetIds = new();
		private bool _stateLoaded;
		private bool _lastSystemScanUsedEmptyCooldown;
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
				current.ProbeFleetId = persisted.ProbeFleetId;
				current.ProbeLevel = persisted.ProbeLevel;
				current.ProbeSentAtUtc = persisted.ProbeSentAtUtc;
			}

			var cursor = _stateStore.LoadScanCursor();
			if (cursor != null) {
				_tbotInstance.UserData.autoFarmLastRangeIndex = cursor.RangeIndex;
				_tbotInstance.UserData.autoFarmLastGalaxy = cursor.Galaxy;
				_tbotInstance.UserData.autoFarmLastSystem = cursor.System;
				_tbotInstance.UserData.autoFarmReportPhasePending = cursor.ReportPhasePending;
			}
			_stateLoaded = true;
		}

		private void PersistTargets() {
			_stateStore.ReplaceTargets(_tbotInstance.UserData.farmTargets, DateTime.UtcNow);
		}

		private void SaveScanCursor(int rangeIndex, int galaxy, int system, bool reportPhasePending = false) {
			_tbotInstance.UserData.autoFarmLastRangeIndex = rangeIndex;
			_tbotInstance.UserData.autoFarmLastGalaxy = galaxy;
			_tbotInstance.UserData.autoFarmLastSystem = system;
			_tbotInstance.UserData.autoFarmReportPhasePending = reportPhasePending;
			_stateStore.SaveScanCursor(rangeIndex, galaxy, system, DateTime.UtcNow, reportPhasePending);
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

		private TimeSpan GetReportCooldown() {
			int minutes = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm,
				"KeepReportFor", 1440);
			return TimeSpan.FromMinutes(Math.Max(0, minutes));
		}

		private bool GetAutoFarmBoolean(string name, bool defaultValue) {
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, name))
				return defaultValue;
			try {
				return Convert.ToBoolean(_tbotInstance.InstanceSettings.AutoFarm[name]);
			} catch {
				return defaultValue;
			}
		}

		private long GetAutoFarmLong(string name, long defaultValue) {
			if (!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, name))
				return defaultValue;
			try {
				return Convert.ToInt64(_tbotInstance.InstanceSettings.AutoFarm[name]);
			} catch {
				return defaultValue;
			}
		}

		private bool AttackDefendedTargetsEnabled() => GetAutoFarmBoolean("AttackDefendedTargets", false);
		private bool AttackTargetsWithFleetEnabled() => GetAutoFarmBoolean("AttackTargetsWithFleet", false);
		private bool IgnoreMissiles() => GetAutoFarmBoolean("IgnoreMissiles", true);

		private AutoFarmReportDecision EvaluateReport(EspionageReport report) {
			return AutoFarmReportPolicy.Evaluate(
				report,
				GetAutoFarmLong("MinimumResources", 1000000),
				Convert.ToString(_tbotInstance.InstanceSettings.AutoFarm.PreferedResource) ?? string.Empty,
				_tbotInstance.UserData.userInfo.Class,
				AttackDefendedTargetsEnabled(),
				AttackTargetsWithFleetEnabled(),
				IgnoreMissiles(),
				GetAutoFarmLong("MaxDefensePoints", 0),
				GetAutoFarmLong("MaxFleetPoints", 0));
		}

		private void ApplyReportDecision(FarmTarget target, EspionageReport report) {
			if (target == null || report == null)
				return;

			var decision = EvaluateReport(report);
			if (decision.Kind == AutoFarmReportDecisionKind.NeedMoreInformation) {
				var probeLevel = target.ProbeLevel;
				if (probeLevel == 0 && target.State == FarmState.ProbesRequired)
					probeLevel = 1;
				else if (probeLevel == 0 && target.State == FarmState.FailedProbesRequired)
					probeLevel = 2;

				if (probeLevel <= 0) {
					target.State = FarmState.ProbesRequired;
					target.ProbeLevel = 1;
				} else if (probeLevel == 1) {
					target.State = FarmState.FailedProbesRequired;
					target.ProbeLevel = 2;
				} else {
					target.State = FarmState.NotSuitable;
					target.ProbeLevel = 0;
				}

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
					$"Need more probes on {report.Coordinate}. Reason: {decision.Reason}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
				return;
			}

			target.ProbeLevel = 0;
			if (decision.Kind == AutoFarmReportDecisionKind.AttackPending) {
				target.State = FarmState.AttackPending;
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
					$"Attack pending on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
				return;
			}

			target.State = FarmState.NotSuitable;
			_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
				$"Target {report.Coordinate} not suitable - {decision.Reason}.");

			bool shouldBlacklist = decision.BlacklistReason.HasValue &&
				(decision.BlacklistReason != BlacklistReason.LowResources ||
					 report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < GetAutoFarmLong(
						"MinimumResourcesToNotBlacklist", 500000));
			if (shouldBlacklist &&
				SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
				SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
				(bool)_tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active) {
				var hoursUntilReset = SettingsService.GetSetting(
					_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
				_blacklist.AddTarget(report.Coordinate, decision.BlacklistReason.Value, hoursUntilReset);
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
					$"Target {report.Coordinate} blacklisted for {hoursUntilReset}h ({decision.Reason}).");
			}
		}

		private void ApplyReportToTarget(FarmTarget target, EspionageReport report) {
			if (target == null || report == null)
				return;

			// A returned report is the only fresh source of resources, fleet and
			// defence information. Clear the completed probe association before
			// applying the single report policy used by every processing path.
			target.Report = report;
			target.ProbeFleetId = null;
			target.ProbeSentAtUtc = null;
			ApplyReportDecision(target, report);
		}

		private async Task<DateTime> GetServerTimeUtc() {
			return (await _tbotOgameBridge.GetDateTime()).ToUniversalTime();
		}

		private bool IsProcessAllReportsEnabled() {
			// Processing reports outside ScanRange is an AutoFarm decision. It must
			// never be enabled implicitly by the blacklist settings, otherwise a
			// normal G1 scan can turn a manual G2/G3 report into an attack target.
			return SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ProcessAllReports") &&
				(bool) _tbotInstance.InstanceSettings.AutoFarm.ProcessAllReports;
		}

		private bool KeepReportsWithDefenseEnabled() {
			// Default to enabled so existing instance files receive the safer
			// behaviour without requiring a migration. The setting can still be
			// explicitly disabled for users who want the previous cleanup policy.
			return !SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "KeepReportsWithDefense") ||
				(bool) _tbotInstance.InstanceSettings.AutoFarm.KeepReportsWithDefense;
		}

		private bool IsManualReviewDefenseReport(int reportId) {
			if (reportId <= 0)
				return false;

			var target = (_tbotInstance.UserData.farmTargets ?? new List<FarmTarget>())
				.FirstOrDefault(candidate => candidate?.Report?.ID == reportId);
			if (target?.Report != null)
				return ShouldPreserveDefenseReport(target.Report);

			return KeepReportsWithDefenseEnabled() &&
				AutoFarmReportPolicy.IsManualReviewDefense(_stateStore.GetProcessedReportOutcome(reportId));
		}

		private bool ShouldPreserveDefenseReport(EspionageReport report) {
			if (!KeepReportsWithDefenseEnabled() || report == null ||
				!report.HasDefensiveStructures(!IgnoreMissiles()))
				return false;

			// Explicit automated-defense mode takes precedence over manual retention,
			// except when the configured safety ceiling is exceeded.
			if (!AttackDefendedTargetsEnabled())
				return true;

			var maxDefensePoints = GetAutoFarmLong("MaxDefensePoints", 0);
			return maxDefensePoints > 0 && report.GetDefensePoints(!IgnoreMissiles()) > maxDefensePoints;
		}

		private void PreserveReportForManualReview(EspionageReport report, bool processAllReports) {
			var target = (_tbotInstance.UserData.farmTargets ?? new List<FarmTarget>())
				.FirstOrDefault(candidate => candidate?.HasCoords(report.Coordinate) == true);
			if (target != null && AutoFarmReportPolicy.IsOwnedByAutoFarm(target, report, processAllReports) &&
				target.State != FarmState.AttackSent) {
				target.Report = report;
				target.State = FarmState.NotSuitable;
				target.ProbeFleetId = null;
				target.ProbeSentAtUtc = null;
				target.ProbeLevel = 0;
				PersistTargets();
			}

			RecordProcessedReport(report, AutoFarmReportPolicy.ManualReviewDefenseOutcome);
			_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
				$"Keeping espionage report {report.ID} for {report.Coordinate}: planetary defenses detected; manual review required.");
		}

		private async Task PruneOldReports(List<Fleet> fleets) {
			var newTimeUtc = await GetServerTimeUtc();
			var reportCooldown = GetReportCooldown();
			var removeReports = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackSent ||
				(t.State != FarmState.ProbesSent &&
					t.State != FarmState.ProbesRequired &&
					t.State != FarmState.FailedProbesRequired &&
					t.Report != null &&
					AutoFarmReportPolicy.IsExpired(t.Report.Date, newTimeUtc, reportCooldown))).ToList();
			foreach (var remove in removeReports) {
				if (remove.State == FarmState.AttackSent && AutoFarmAttackPolicy.IsAttackInProgress(remove, fleets)) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Keeping {remove} in AttackSent state while an attack fleet is still in progress.");
					continue;
				}

				var updateReport = remove;
				var previousState = updateReport.State;
				updateReport.State = FarmState.ProbesPending;
				updateReport.ProbeFleetId = null;
				updateReport.ProbeLevel = 0;
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Rechecking {updateReport.Celestial.Coordinate}: {previousState} report expired; the next probe will get a fresh resource snapshot.");
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

		private async Task<List<Celestial>> GetScannedTargetsFromGalaxy(int galaxy, int system) {
			_lastSystemScanUsedEmptyCooldown = false;
			DateTime nowUtc = DateTime.UtcNow;
			var cachedSnapshot = _stateStore.GetSystemSnapshot(galaxy, system);
			var systemDataTtl = GetSystemDataTtl();
			var emptySystemCooldown = GetEmptySystemCooldown();
			var cacheDecision = AutoFarmSystemCachePolicy.GetDecision(
				cachedSnapshot,
				nowUtc,
				systemDataTtl,
				emptySystemCooldown);
			if (cacheDecision == AutoFarmSystemCacheDecision.Skip) {
				_lastSystemScanUsedEmptyCooldown = true;
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Skipping cached empty system {galaxy}:{system} until {cachedSnapshot.ObservedAtUtc.Add(emptySystemCooldown):u}.");
				return null;
			}

			if (cacheDecision == AutoFarmSystemCacheDecision.UseSnapshot) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Using cached galaxy data for {galaxy}:{system} ({(nowUtc - cachedSnapshot.ObservedAtUtc).TotalDays:F1} days old).");
				return AutoFarmSystemPolicy.GetEligibleTargets(cachedSnapshot.Planets);
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

						// A transient galaxy failure must not restart the wrapper from inside
						// a worker. The process supervisor owns wrapper lifetime; restarting
						// here can create a second process and invalidate the current session.
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
			ResetStaleTargetsAfterSystemRefresh(galaxy, system);

			if (isEmptySystem)
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"System {galaxy}:{system} is empty for AutoFarm; applying the configured long cooldown.");

			return AutoFarmSystemPolicy.GetEligibleTargets(planets);
		}

		private void ResetStaleTargetsAfterSystemRefresh(int galaxy, int system) {
			var nowUtc = DateTime.UtcNow;
			var reportCooldown = GetReportCooldown();
			var reopenedTargets = _tbotInstance.UserData.farmTargets
				.Where(target => target?.Celestial?.Coordinate != null
					&& target.Celestial.Coordinate.Galaxy == galaxy
					&& target.Celestial.Coordinate.System == system)
				.ToList();

			foreach (var target in reopenedTargets) {
				if (!AutoFarmTargetPolicy.ShouldReopenAfterSystemRefresh(target, nowUtc, reportCooldown))
					continue;

				if (!AutoFarmTargetPolicy.ResetForFreshSystemData(target))
					continue;

				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Fresh system data reopened {target.Celestial.Coordinate} for a new espionage report.");
			}
		}

		private bool IsTargetInMinimumRank(Celestial planet, List<Celestial> scannedTargets) {
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinimumPlayerRank") && _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank != 0) {
				int rank = 1;
				if (planet.Coordinate.Type == Celestials.Planet) {
					rank = (planet as Planet).Player.Rank;
				} else {
					if (scannedTargets.Any(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet)))) {
						rank = (scannedTargets.Single(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet))) as Planet).Player.Rank;
					}
				}
				if ((int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank < rank) {

					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Skipping {planet}: player rank {rank} exceeds the configured rank limit {(int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank}.");
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

		private void AddMoons(List<Celestial> scannedTargets) {
			foreach (var t in scannedTargets.ToList()) {
				var planet = t as Planet;
				if (planet == null)
					continue;
				if (planet.Moon != null) {
					Celestial tempCelestial = planet.Moon;
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
			} else if (exists.Count() == 1) {
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

			var target = CheckDuplicatesAndGetExisting(planet);

			if (target == null) {
				target = new(planet, FarmState.ProbesPending);
				_tbotInstance.UserData.farmTargets.Add(target);
			} else {
				target.Celestial = planet;

				if (target.State == FarmState.Idle)
					target.State = FarmState.ProbesPending;

				if (target.State == FarmState.NotSuitable && target.Report != null) {
					var nowUtc = DateTime.UtcNow;
					var reportCooldown = GetReportCooldown();
					if (AutoFarmTargetPolicy.IsWaitingForReportCooldown(target, nowUtc, reportCooldown, out var nextProbeAtUtc)) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Skipping {planet}: NotSuitable from report {target.Report.ID} dated {target.Report.Date.ToUniversalTime():u} " +
							$"(loot: {target.Report.Loot(_tbotInstance.UserData.userInfo.Class)}, fleet info: {target.Report.HasFleetInformation}, " +
							$"defense info: {target.Report.HasDefensesInformation}) is still current until {nextProbeAtUtc:u}; " +
							"cached resources are not used as a new attack decision.");
						return null;
					}

					target.State = FarmState.ProbesPending;
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Rechecking {planet}: NotSuitable report {target.Report.ID} is stale; scheduling a fresh espionage report.");
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
			int probeLevel = target?.ProbeLevel ?? 0;
			// Infer the level for databases created before ProbeLevel was persisted.
			if (probeLevel == 0 && target?.State == FarmState.ProbesRequired)
				probeLevel = 1;
			else if (probeLevel == 0 && target?.State == FarmState.FailedProbesRequired)
				probeLevel = 2;

			if (probeLevel == 1)
				neededProbes *= 3;
			else if (probeLevel >= 2)
				neededProbes *= 9;
			return neededProbes;
		}

		private int GetProbeLevelForDispatch(FarmTarget target) {
			if (target == null)
				return 0;

			if (target.ProbeLevel > 0)
				return target.ProbeLevel;

			return target.State switch {
				FarmState.ProbesRequired => 1,
				FarmState.FailedProbesRequired => 2,
				_ => 0
			};
		}

		private Buildables GetConfiguredCargoType() {
			if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out var cargoType))
				return Buildables.LargeCargo;

			return cargoType;
		}

		private long CalculateFarmCargoCount(Resources loot, Buildables cargoShip, LFBonuses lfBonuses) {
			var cargoCount = _calculationService.CalcShipNumberForPayload(
				loot,
				cargoShip,
				_tbotInstance.UserData.researches.HyperspaceTechnology,
				_tbotInstance.UserData.serverData,
				(lfBonuses ?? new LFBonuses()).GetShipCargoBonus(cargoShip),
				_tbotInstance.UserData.userInfo.Class,
				_tbotInstance.UserData.serverData.ProbeCargo);

			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "CargoSurplusPercentage") &&
				(double)_tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage > 0) {
				cargoCount = (long)Math.Round(
					cargoCount + cargoCount / 100D * (double)_tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage,
					0,
					MidpointRounding.AwayFromZero);
			}

			return Math.Max(0, cargoCount);
		}

		private Ships BuildAttackFleet(long cargoCount, Buildables cargoShip, EspionageReport report) {
			return AutoFarmAttackComposition.Build(
				cargoCount,
				cargoShip,
				report,
				AttackDefendedTargetsEnabled(),
				AttackTargetsWithFleetEnabled(),
				IgnoreMissiles(),
				GetAutoFarmLong("BombersPer100kDefense", 4),
				GetAutoFarmLong("ReapersPer100kFleet", 2));
		}

		private AutoFarmSlotBudget GetCurrentSlotBudget(Buildables cargoType) {
			return AutoFarmSlotPlanner.Calculate(
				(int) _tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
				(int) (_tbotInstance.UserData.slots?.Free ?? 0),
				(int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree,
				_tbotInstance.UserData.fleets,
				cargoType,
				_autoFarmEspionageFleetIds,
				AttackDefendedTargetsEnabled(),
				AttackTargetsWithFleetEnabled());
		}

		private async Task<List<Fleet>> RefreshFleetSnapshotOrThrow(string phase) {
			FleetSnapshotResult snapshot;
			if (_fleetScheduler is IFleetSnapshotProvider atomicProvider)
				snapshot = await atomicProvider.UpdateFleetsWithFreshness();
			else {
				var fleets = await _fleetScheduler.UpdateFleets();
				snapshot = new FleetSnapshotResult(fleets ?? new List<Fleet>(), _fleetScheduler.LastFleetSnapshotWasFresh);
			}

			if (!snapshot.IsFresh)
				throw new InvalidOperationException($"Fleet snapshot was not fresh during AutoFarm {phase}; pausing decisions to avoid stale duplicate dispatches.");

			return (snapshot.Fleets ?? Array.Empty<Fleet>()).ToList();
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

		private IReadOnlyList<Fleet> GetOwnedEspionageMissions(Celestial origin) {
			if (origin?.Coordinate == null)
				return Array.Empty<Fleet>();

			return AutoFarmEspionagePolicy.GetMissionsToAwait(
				_calculationService.GetMissionsInProgress(origin.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets),
				_autoFarmEspionageFleetIds);
		}

		private bool ReconcileProbeStates(IEnumerable<Fleet> fleets) {
			_autoFarmEspionageFleetIds.Clear();
			var currentFleets = (fleets ?? Enumerable.Empty<Fleet>())
				.Where(fleet => fleet?.Mission == Missions.Spy && fleet.ID > 0)
				.ToList();
			bool changed = false;

			foreach (var target in _tbotInstance.UserData.farmTargets ?? new List<FarmTarget>()) {
				if (target?.State != FarmState.ProbesSent)
					continue;

				var probe = target.ProbeFleetId.HasValue
					? currentFleets.FirstOrDefault(fleet =>
						fleet.ID == target.ProbeFleetId.Value &&
						fleet.Destination?.IsSame(target.Celestial?.Coordinate) == true)
					: currentFleets.FirstOrDefault(fleet =>
						fleet.Destination?.IsSame(target.Celestial?.Coordinate) == true);
				if (probe != null)
					_autoFarmEspionageFleetIds.Add(probe.ID);
				else {
					// OGame can briefly omit a returning fleet from its snapshot. Keep
					// the durable association during that window so a reload cannot
					// dispatch a duplicate probe. Reopen only after the recovery grace
					// period has elapsed.
					if (!AutoFarmEspionagePolicy.ShouldReopenMissingProbe(
						target.ProbeSentAtUtc,
						DateTime.UtcNow,
						TimeSpan.FromMinutes(10))) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"Keeping {target.Celestial.Coordinate} in ProbesSent state while its persisted probe is temporarily absent from the fleet snapshot.");
						continue;
					}

					target.State = FarmState.ProbesPending;
					target.ProbeFleetId = null;
					target.ProbeLevel = 0;
					target.ProbeSentAtUtc = null;
					changed = true;
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Reopening {target.Celestial.Coordinate}: its persisted espionage fleet is no longer in flight.");
				}
			}

			return changed;
		}

		private void RestoreOwnedEspionageMissions(IEnumerable<Fleet> fleets) {
			ReconcileProbeStates(fleets);
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

				_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("origin probe selection");

				if (freeSlots <= slotsToLeaveFree) {
					var espionageMissions = GetOwnedEspionageMissions(closest);
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

			_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("slot waiting");
			while (freeSlots <= slotsToLeaveFree) {
				_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("slot waiting");
				if (_tbotInstance.UserData.fleets.Any()) {
					int interval = (int) ((1000 * _tbotInstance.UserData.fleets.OrderBy(fleet => fleet.BackIn).First().BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanASecond));
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
			bool reportPhaseCompleted = false;
			int skippedEmptySystems = 0;
			try {
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Running autofarm...");
				LoadPersistedTargets();
				finishedFullScan = _tbotInstance.UserData.autoFarmReportPhasePending;
				stopAfterFullScan = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "StopAfterFullScan")
					&& (bool) _tbotInstance.InstanceSettings.AutoFarm.StopAfterFullScan;
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"AutoFarm cache policy: system data TTL={GetSystemDataTtl().TotalDays:0.#}d, report cooldown={GetReportCooldown().TotalHours:0.#}h, " +
					$"empty-system cooldown={GetEmptySystemCooldown().TotalDays:0.#}d, StopAfterFullScan={stopAfterFullScan}.");
				if ((bool) _tbotInstance.InstanceSettings.AutoFarm.Active) {
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();

					int freeSlots = _tbotInstance.UserData.slots.Free;
					int slotsToLeaveFree = (int) _tbotInstance.InstanceSettings.AutoFarm.SlotsToLeaveFree;
					if (freeSlots <= slotsToLeaveFree) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to start auto farm, no slots available");
						return;
					}

					try {
						_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("startup");
						RestoreOwnedEspionageMissions(_tbotInstance.UserData.fleets);
						await PruneOldReports(_tbotInstance.UserData.fleets);
						PersistTargets();

						var farmOrigins = GetFarmOrigins();
						var celestialProbes = GetCachedCelestialProbes(farmOrigins);
						_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("scan preparation");

						int numProbed = 0;

						if (_tbotInstance.UserData.autoFarmReportPhasePending) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
								"A previous full scan is awaiting report processing; skipping the galaxy scan until the report phase succeeds.");
						} else {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Detecting farm targets...");
							bool stopAutoFarm = false;

						var scanRanges = ((IEnumerable<dynamic>) _tbotInstance.InstanceSettings.AutoFarm.ScanRange).ToList();

						var orderedRanges = scanRanges
							.OrderBy(r => (int) r.Galaxy)
							.ThenBy(r => (int) r.StartSystem)
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
							// A fresh instance always starts at the first configured range.
							// The cursor is the only durable source of scan position; a
							// process-local hash offset would make restarts non-deterministic.
							_tbotInstance.UserData.autoFarmLastRangeIndex = startRangeIndex;
						}

						for (int rangeOffset = 0; rangeOffset < orderedRanges.Count; rangeOffset++) {
							int rangeIndex = (startRangeIndex + rangeOffset) % orderedRanges.Count;
							if (stopAutoFarm)
								break;
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") && ((int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0) && numProbed >= (int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
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
							} else {
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
									SaveScanCursor(rangeIndex, galaxy, system);
									stopAutoFarm = true;
									break;
								}

								if (scannedTargets == null) {
									if (_lastSystemScanUsedEmptyCooldown) {
										skippedEmptySystems++;
									} else {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
											$"System {galaxy}:{system} did not return complete galaxy data; preserving the scan cursor for a retry.");
										SaveScanCursor(rangeIndex, galaxy, system);
										stopAutoFarm = true;
									}
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
									if (!IsTargetInMinimumRank(planet, scannedTargets)) {
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

									_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("probe slot check");
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

							if (AutoFarmEspionagePolicy.HasOwnedMissionTo(
								_tbotInstance.UserData.fleets,
								target.Celestial.Coordinate,
								_autoFarmEspionageFleetIds)) {
								_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
									$"An AutoFarm spy mission is already on route towards {target}; no duplicate probe will be dispatched.");
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
												} catch {
													_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to start ship production.");
												}
											} else {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Not enough resources to build probes.");
														_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("probe wait");
								var spyMissions = GetOwnedEspionageMissions(bestOrigin.Origin);
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

										_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("probe dispatch");
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

										var fleetId = await _fleetScheduler.SendValidatedFleet(bestOrigin.Origin, ships, target.Celestial.Coordinate, Missions.Spy, Speeds.HundredPercent);

										if (fleetId > (int) SendFleetCode.GenericError) {
											freeSlots--;
											numProbed++;
											_autoFarmEspionageFleetIds.Add(fleetId);
											celestialProbes[bestOrigin.Origin.ID] -= neededProbes;
											// Persist the association immediately. A settings reload or process
											// restart must not turn an in-flight probe into a second probe.
											_tbotInstance.UserData.farmTargets.Remove(target);
							target.ProbeFleetId = fleetId;
							target.ProbeLevel = GetProbeLevelForDispatch(target);
							target.ProbeSentAtUtc = DateTime.UtcNow;
							target.State = FarmState.ProbesSent;
											_tbotInstance.UserData.farmTargets.Add(target);
											PersistTargets();

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
							finishedFullScan = true;
							SaveScanCursor(0, 0, 0, true);

							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							   "Full scan cycle completed; report processing is now the durable checkpoint");

							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "StopAfterFullScan") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.StopAfterFullScan) {

								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
									"StopAfterFullScan=true -> Full scan completed. Attacks will be processed before AutoFarm stops.");
							}
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

					_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("report preparation");
					var espionageMissionsToAwait = AutoFarmEspionagePolicy.GetMissionsToAwait(
						_tbotInstance.UserData.fleets,
						_autoFarmEspionageFleetIds);
					Fleet firstReturning = espionageMissionsToAwait
						.OrderBy(fleet => fleet.BackIn)
						.LastOrDefault();
					if (firstReturning != null) {
						int interval = (int) ((1000 * firstReturning.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Waiting {TimeSpan.FromMilliseconds(interval)} for all probes to return...");
						await Task.Delay(interval, _ct);
					}

					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Processing espionage reports of found inactives...");

					reportPhaseCompleted = await AutoFarmProcessReports();
					if (AutoFarmCyclePolicy.CanFinalizeFullScan(finishedFullScan, reportPhaseCompleted)) {
						SaveScanCursor(0, 0, 0);
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
							"Report processing completed; scan position reset for next cycle");
					}
					PersistTargets();

					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("attack preparation");
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
										(int) _tbotInstance.UserData.fleets.Count(f => AutoFarmSlotPlanner.IsOwnedFleet(
											f,
											configuredCargoType,
											_autoFarmEspionageFleetIds,
											AttackDefendedTargetsEnabled(),
											AttackTargetsWithFleetEnabled()))),
						new RankSlotsPriority(Feature.Colonize,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Colonize,
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
						_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("attack decision");
						if (AutoFarmAttackPolicy.IsAttackInProgress(target, _tbotInstance.UserData.fleets)) {
							target.State = FarmState.AttackSent;
							_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
								$"An attack is already on route towards {target}; no duplicate attack will be dispatched.");
							PersistTargets();
							continue;
						}
						if (target.Report?.ID > 0 && _stateStore.HasConsumedReport(target.Report.ID)) {
							target.State = FarmState.NotSuitable;
							target.ConsumedReportId = target.Report.ID;
							_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
								$"Report {target.Report.ID} for {target} is already recorded as consumed; refusing to attack it again.");
							PersistTargets();
							continue;
						}
						var loot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class);
						var numCargo = 0L;
						var attackingShips = new Ships();

						List<Celestial> tempCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0) ? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials) : _tbotInstance.UserData.celestials;
						List<Celestial> closestCelestials = tempCelestials
							.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData))
							.ThenByDescending(c => c.Coordinate.Type == Celestials.Moon)
							.ToList();

						Celestial tempCelestial;
						Celestial fromCelestial = null;
						foreach (var c in closestCelestials) {
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(c, UpdateTypes.Ships);
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
							var candidateCargo = CalculateFarmCargoCount(loot, cargoShip, tempCelestial.LFBonuses);
							var candidateShips = BuildAttackFleet(candidateCargo, cargoShip, target.Report);
							if (tempCelestial.Ships != null &&
								tempCelestial.Ships.GetAmount(cargoShip) >= candidateCargo + (long)_tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep &&
								tempCelestial.Ships.HasAtLeast(candidateShips)) {
								numCargo = candidateCargo;
								attackingShips = candidateShips;
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
								var candidateCargo = CalculateFarmCargoCount(loot, cargoShip, tempCelestial.LFBonuses);
								var candidateShips = BuildAttackFleet(candidateCargo, cargoShip, target.Report);
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
										var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(tempCelestial.Coordinate, target.Celestial.Coordinate, candidateShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
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
								if ((tempCelestial.Ships?.GetAmount(cargoShip) ?? 0) < candidateCargo + (long)_tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep) {
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "BuildCargos") && _tbotInstance.InstanceSettings.AutoFarm.BuildCargos == true) {
										var neededCargos = candidateCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep - tempCelestial.Ships.GetAmount(cargoShip);
										var cost = _calculationService.CalcPrice(cargoShip, (int) neededCargos);
										if (tempCelestial.Resources.IsEnoughFor(cost)) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Building {neededCargos}x{cargoShip.ToString()}");
										} else {
											var buildableCargos = _calculationService.CalcMaxBuildableNumber(cargoShip, tempCelestial.Resources);
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Not enough resources to build {neededCargos}x{cargoShip.ToString()}. {buildableCargos.ToString()} will be built instead.");
											neededCargos = buildableCargos;
										}
										if (neededCargos <= 0)
											continue;

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

									tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);
									var availableCargo = (tempCelestial.Ships?.GetAmount(cargoShip) ?? 0) - (long)_tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep;
									var actualCargo = Math.Min(Math.Max(0, availableCargo), candidateCargo);
									if (actualCargo < (long)_tbotInstance.InstanceSettings.AutoFarm.MinCargosToSend) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Insufficient {cargoShip.ToString()} on {tempCelestial.Coordinate}, require {candidateCargo + (long)_tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep} {cargoShip.ToString()}.");
										continue;
									}

									var actualShips = BuildAttackFleet(actualCargo, cargoShip, target.Report);
									if (!tempCelestial.Ships.HasAtLeast(actualShips)) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
											$"Insufficient escort ships on {tempCelestial}: required {actualShips}, available {tempCelestial.Ships}.");
										continue;
									}
									var actualCargoCapacity = _calculationService.CalcFleetCapacity(
										actualShips,
										_tbotInstance.UserData.serverData,
										_tbotInstance.UserData.researches.HyperspaceTechnology,
										tempCelestial.LFBonuses,
										_tbotInstance.UserData.userInfo.Class,
										_tbotInstance.UserData.serverData.ProbeCargo);
									if (actualCargoCapacity < loot.TotalResources) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
											$"Insufficient cargo space after the available-fleet adjustment. {actualCargo} {cargoShip} can carry {actualCargoCapacity:N0} but need {loot.TotalResources:N0}.");
										continue;
									}

									var actualPrediction = _calculationService.CalcFleetPrediction(
										tempCelestial.Coordinate,
										target.Celestial.Coordinate,
										actualShips,
										Missions.Attack,
										speed,
										_tbotInstance.UserData.researches,
										_tbotInstance.UserData.serverData,
										tempCelestial.LFBonuses,
										_tbotInstance.UserData.userInfo.Class,
										_tbotInstance.UserData.allianceClass);
									if (tempCelestial.Resources.Deuterium < actualPrediction.Fuel ||
										(SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") &&
											(long)_tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime > 0 &&
											actualPrediction.Time > (long)_tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime))
										continue;

									numCargo = actualCargo;
									attackingShips = actualShips;
									fromCelestial = tempCelestial;
									break;
								}
							}
						}

						if (fromCelestial == null) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Unable to attack {target.Celestial.Coordinate}. No suitable origin celestial available near the destination.");
							continue;
						}

						_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
						_tbotInstance.UserData.fleets = await RefreshFleetSnapshotOrThrow("attack slot check");
						var slotBudget = GetCurrentSlotBudget(cargoShip);
						if (MaxSlots > 0 && slotBudget.AvailableSlots > 0) {
							fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.Ships);
							fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.LFBonuses);
							var availableShips = (fromCelestial.Ships?.GetAmount(cargoShip) ?? 0) - (long)_tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep;
							var finalCargo = Math.Min(Math.Max(0, availableShips), numCargo);
							if (finalCargo < (long)_tbotInstance.InstanceSettings.AutoFarm.MinCargosToSend) {
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"No {cargoShip.ToString()} available on {fromCelestial.ToString()} (all ships already in use). Skipping target.");
								continue;
							}
							if (finalCargo < numCargo)
								_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Only {finalCargo} {cargoShip.ToString()} available (needed {numCargo}). Adjusting fleet size.");

							numCargo = finalCargo;
							attackingShips = BuildAttackFleet(numCargo, cargoShip, target.Report);
							if (!fromCelestial.Ships.HasAtLeast(attackingShips)) {
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
									$"No complete attack fleet available on {fromCelestial}; required {attackingShips}, available {fromCelestial.Ships}.");
								continue;
							}
							var cargoCapacity = _calculationService.CalcFleetCapacity(
								attackingShips, _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology,
								fromCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class,
								_tbotInstance.UserData.serverData.ProbeCargo);
							var totalLoot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources;

							if (cargoCapacity < totalLoot) {
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
									$"Insufficient cargo space after adjustment. {numCargo} {cargoShip.ToString()} can carry {cargoCapacity:N0} but need {totalLoot:N0}. Skipping target.");
								continue;
							}

							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking {target.ToString()} from {fromCelestial} with {numCargo} {cargoShip.ToString()}.");
							Ships ships = new();

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

							var fleetId = await _fleetScheduler.SendValidatedFleet(fromCelestial, attackingShips, target.Celestial.Coordinate, Missions.Attack, speed);

							if (fleetId > (int) SendFleetCode.GenericError) {
								freeSlots--;

								_successfulTargets.RecordAttack(target.Celestial.Coordinate, loot);
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
			} catch (OperationCanceledException) when (_ct.IsCancellationRequested) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, "AutoFarm execution canceled.");
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarm Exception: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
			} finally {
				PersistTargets();
				if (stopAfterFullScan && AutoFarmCyclePolicy.CanFinalizeFullScan(finishedFullScan, reportPhaseCompleted)) {
					stop = true;
				}

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacked targets: {_tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackSent).Count()}");
				PersistTargets();
				if (!_tbotInstance.UserData.isSleeping && !_ct.IsCancellationRequested) {
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

		private async Task<bool> TryDeleteEspionageReport(int reportId, string reason) {
			if (reportId <= 0)
				return false;

			const int maxAttempts = 3;
			for (int attempt = 1; attempt <= maxAttempts; attempt++) {
				try {
					await _ogameService.DeleteReport(reportId);
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Deleted espionage report {reportId} ({reason}).");
					return true;
				} catch (Exception e) {
					var message = e.Message ?? string.Empty;
					bool transient = AutoFarmReportPolicy.IsTransientDeletionFailure(e);

					if (transient && attempt < maxAttempts) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
							$"Failed to delete espionage report {reportId} ({reason}), retry {attempt}/{maxAttempts}...");
						await Task.Delay(TimeSpan.FromSeconds(attempt), _ct);
						continue;
					}

					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
						$"Could not delete espionage report {reportId} ({reason}): {message}. The report ID remains protected in AutoFarm state.");
					return false;
				}
			}

			return false;
		}

		private bool IsKnownAutoFarmReport(int reportId) {
			if (reportId <= 0)
				return false;

			if (_stateStore.HasConsumedReport(reportId) || _stateStore.HasProcessedReport(reportId))
				return true;

			return (_tbotInstance.UserData.farmTargets ?? new List<FarmTarget>())
				.Any(target => target?.ConsumedReportId == reportId || target.Report?.ID == reportId);
		}

		private void RecordProcessedReport(EspionageReport report, string outcome) {
			if (report?.ID <= 0)
				return;

			_stateStore.RecordProcessedReport(report.ID, report.Coordinate, outcome, DateTime.UtcNow);
		}

		private async Task<bool> ConsumeUnresolvableReport(EspionageReport report, string outcome, string reason) {
			RecordProcessedReport(report, outcome);
			_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
				$"Consuming espionage report {report.ID} for {report.Coordinate}: {outcome}.");
			return await TryDeleteEspionageReport(report.ID, reason);
		}

		private async Task<(Planet EligiblePlanet, bool TargetExists)> ResolveInactivePlanet(EspionageReport report) {
			var galaxyInfo = await _ogameService.GetGalaxyInfo(report.Coordinate.Galaxy, report.Coordinate.System);
			if (galaxyInfo?.Planets == null)
				throw new InvalidOperationException($"Galaxy lookup returned no planet list for {report.Coordinate}.");

			var targetPlanet = galaxyInfo.Planets.FirstOrDefault(planet =>
				planet != null && planet.HasCoords(report.Coordinate));
			var eligiblePlanet = targetPlanet != null
				&& targetPlanet.Inactive
				&& !targetPlanet.Administrator
				&& !targetPlanet.Banned
				&& !targetPlanet.Vacation
				? targetPlanet
				: null;

			return (eligiblePlanet, targetPlanet != null);
		}

		private bool IsPreservedOutsideConfiguredRange(int reportId, bool processAllReports) {
			return AutoFarmReportPolicy.IsPreservedOutsideConfiguredRange(
				_stateStore.GetProcessedReportOutcome(reportId),
				processAllReports);
		}

		private bool IsMarkedAsNotScanned(int reportId) {
			return string.Equals(
				_stateStore.GetProcessedReportOutcome(reportId),
				"NotScanned",
				StringComparison.OrdinalIgnoreCase);
		}

		private async Task<(List<EspionageReportSummary> Summaries, bool Complete)> GetEspionageReportSummariesForProcessing() {
			const int maxPages = 100;
			var summaries = new List<EspionageReportSummary>();
			var seenReportIds = new HashSet<int>();
			bool processAllReports = IsProcessAllReportsEnabled();
			bool reachedSafetyLimit = true;
			bool complete = true;

			for (int page = 1; page <= maxPages; page++) {
				var pageReports = await _ogameService.GetEspionageReportsPage(page)
					?? new List<EspionageReportSummary>();
				if (pageReports.Count == 0) {
					reachedSafetyLimit = false;
					break;
				}

				bool addedNewReport = false;
				bool reachedKnownBoundary = false;
				foreach (var summary in pageReports) {
					if (summary == null || summary.ID <= 0 || !seenReportIds.Add(summary.ID))
						continue;

					addedNewReport = true;
					summaries.Add(summary);
					if (IsKnownAutoFarmReport(summary.ID)
						&& !IsManualReviewDefenseReport(summary.ID)
						&& !IsPreservedOutsideConfiguredRange(summary.ID, processAllReports)
						&& !(processAllReports && IsMarkedAsNotScanned(summary.ID))) {
						reachedKnownBoundary = true;
					}
				}

				if (reachedKnownBoundary) {
					reachedSafetyLimit = false;
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Report page {page} contains the first known AutoFarm report; no older pages will be requested.");
					break;
				}

				// An older OGamed binary may ignore the pagination field and return the
				// same complete list for every page. Do not loop forever in that case.
				if (!addedNewReport) {
					reachedSafetyLimit = false;
					complete = false;
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Report page {page} contained no new report IDs; stopping pagination.");
					break;
				}
			}

			if (reachedSafetyLimit)
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"AutoFarm report pagination reached the safety limit of {maxPages} pages.");

			return (summaries, complete && !reachedSafetyLimit);
		}

		private async Task<(bool Continue, bool CleanupSucceeded)> ProcessEspionageReportSummary(
			EspionageReportSummary summary,
			bool processAllReports,
			DateTime serverNowUtc) {
			if (summary == null || summary.ID <= 0 || summary.Type == EspionageReportType.Action)
				return (true, true);

			if (IsManualReviewDefenseReport(summary.ID)) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Skipping preserved espionage report {summary.ID}: planetary defenses are reserved for manual review.");
				return (true, true);
			}

			if (IsPreservedOutsideConfiguredRange(summary.ID, processAllReports)) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Preserving espionage report {summary.ID}: it is outside the configured AutoFarm range.");
				return (true, true);
			}

			if (IsKnownAutoFarmReport(summary.ID) &&
				!(processAllReports && IsMarkedAsNotScanned(summary.ID))) {
				_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
					$"Encountered already handled espionage report {summary.ID}; cleaning it up and stopping at this boundary.");
				var deleted = await TryDeleteEspionageReport(summary.ID, "already handled");
				return (false, deleted);
			}


			var report = await _ogameService.GetEspionageReport(summary.ID);
			// Scope must be established before age/malformed cleanup. Manual reports
			// may be expired too, but owned-only AutoFarm must leave them alone.
			if (!processAllReports) {
				var summaryTarget = summary.Target == null ? null :
					(_tbotInstance.UserData.farmTargets ?? new List<FarmTarget>())
						.FirstOrDefault(target => target?.HasCoords(summary.Target) == true);
				if (!AutoFarmReportPolicy.IsOwnedByAutoFarm(summaryTarget,
					new EspionageReport { ID = summary.ID }, false))
					return (true, true);
			}
			if (report == null || report.ID <= 0 || report.Coordinate == null) {
				var malformedReport = new EspionageReport { ID = summary.ID, Coordinate = summary.Target };
				RecordProcessedReport(malformedReport, "Malformed");
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"Ignoring malformed espionage report {summary.ID}: no coordinate was returned.");
				var malformedDeleted = await TryDeleteEspionageReport(summary.ID, "malformed report");
				return (malformedDeleted, malformedDeleted);
			}

			if (ShouldPreserveDefenseReport(report)) {
				PreserveReportForManualReview(report, processAllReports);
				return (true, true);
			}

			if (IsManualReviewDefenseReport(report.ID))
				return (true, true);

			if (IsKnownAutoFarmReport(report.ID) &&
				!(processAllReports && IsMarkedAsNotScanned(report.ID))) {
				var knownDeleted = await TryDeleteEspionageReport(report.ID, "already consumed");
				return (false, knownDeleted);
			}

			if (!processAllReports) {
				var ownedTarget = (_tbotInstance.UserData.farmTargets ?? new List<FarmTarget>())
					.FirstOrDefault(target => target?.HasCoords(report.Coordinate) == true);
				if (!AutoFarmReportPolicy.IsOwnedByAutoFarm(ownedTarget, report, false)) {
					RecordProcessedReport(report, "NotScanned");
					return (true, true);
				}
			}

			if (AutoFarmReportPolicy.IsExpired(report.Date, serverNowUtc, GetReportCooldown())) {
				RecordProcessedReport(report, "Expired");
				var expiredDeleted = await TryDeleteEspionageReport(report.ID, "expired report");
				return (expiredDeleted, expiredDeleted);
			}

			var target = (_tbotInstance.UserData.farmTargets ?? new List<FarmTarget>())
				.FirstOrDefault(candidate => candidate?.HasCoords(report.Coordinate) == true);
			if (target == null && processAllReports && report.IsInactive) {
				var resolved = await ResolveInactivePlanet(report);
				if (resolved.EligiblePlanet == null) {
					var outcome = resolved.TargetExists ? "TargetNoLongerEligible" : "TargetGone";
					var unresolvedDeleted = await ConsumeUnresolvableReport(report, outcome, outcome);
					return (unresolvedDeleted, unresolvedDeleted);
				}

				target = GetFarmTarget(resolved.EligiblePlanet);
				if (target == null) {
					var blacklistedDeleted = await ConsumeUnresolvableReport(report, "Blacklisted", "blacklisted target");
					return (blacklistedDeleted, blacklistedDeleted);
				}
			}

			if (target == null) {
				if (!processAllReports) {
					RecordProcessedReport(report, "NotScanned");
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
						$"Target {report.Coordinate} was not scanned by AutoFarm; preserving the report.");
				} else {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
						$"Preserving report {report.ID}: it is not an eligible inactive target.");
				}
				return (true, true);
			}

			if (!AutoFarmReportPolicy.IsOwnedByAutoFarm(target, report, processAllReports)) {
				RecordProcessedReport(report, "NotScanned");
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
					$"Target {report.Coordinate} has a matching entry, but report {report.ID} was not produced by AutoFarm; preserving it.");
				return (true, true);
			}

			if (!AutoFarmAttackPolicy.CanProcessReport(target, report)) {
				RecordProcessedReport(report, "AlreadyConsumedOrActive");
				var activeDeleted = await TryDeleteEspionageReport(report.ID, "already consumed or active");
				return (activeDeleted, activeDeleted);
			}

			if (AutoFarmAttackPolicy.IsPreviouslyStoredReport(target, report)) {
				RecordProcessedReport(report, "PreviouslyHandled");
				var duplicateDeleted = await TryDeleteEspionageReport(report.ID, "previously handled report");
				return (false, duplicateDeleted);
			}

			if (target.Report != null && report.Date.ToUniversalTime() < target.Report.Date.ToUniversalTime()) {
				RecordProcessedReport(report, "OlderThanStoredReport");
				var olderDeleted = await TryDeleteEspionageReport(report.ID, "older than stored report");
				return (false, olderDeleted);
			}

			ApplyReportToTarget(target, report);
			PersistTargets();
			RecordProcessedReport(report, target.State.ToString());
			var processedDeleted = await TryDeleteEspionageReport(report.ID, "processed");
			return (processedDeleted, processedDeleted);
		}

		private async Task<bool> AutoFarmProcessReports() {
			// Snapshot every required summary page before deleting any report: deleting
			// page one changes the offsets of all later mailbox pages.
			(List<EspionageReportSummary> Summaries, bool Complete) traversal;
			DateTime serverNowUtc;
			try {
				serverNowUtc = await GetServerTimeUtc();
				traversal = await GetEspionageReportSummariesForProcessing();
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"Unable to snapshot espionage reports; keeping the report phase pending: {e.Message}");
				return false;
			}
			var complete = traversal.Complete;
			var processAllReports = IsProcessAllReportsEnabled();
			foreach (var summary in traversal.Summaries) {
					try {
						var result = await ProcessEspionageReportSummary(summary, processAllReports, serverNowUtc);
					complete &= result.CleanupSucceeded;
					if (!result.Continue) return complete;
					} catch (OperationCanceledException) when (_ct.IsCancellationRequested) {
						throw;
					} catch (Exception e) {
					complete = false;
						_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm,
							$"AutoFarm report {summary.ID} could not be processed: {e.Message}");
					}
				}
			return complete;
		}

		private async Task<bool> AutoFarmProcessReportsLegacy() {
			List<EspionageReportSummary> summaryReports;
			try {
				summaryReports = (await GetEspionageReportSummariesForProcessing()).Summaries;
			} catch (Exception e) {
				// Report cleanup is best effort. A temporary OGame/API failure must not
				// prevent the attack phase from processing targets already marked pending.
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"Unable to retrieve espionage reports; keeping existing attack-pending targets: {e.Message}");
				return false;
			}

			DateTime serverNowUtc;
			try {
				serverNowUtc = await GetServerTimeUtc();
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
					$"Unable to determine report age; report processing will be retried before the scan is committed: {e.Message}");
				return false;
			}

			bool allReportsProcessed = true;
			bool processAllReports = IsProcessAllReportsEnabled();
			// OGame returns the message list in traversal order (newest to oldest).
			// Preserve that order: report IDs are opaque identifiers and must not be
			// used as a timestamp surrogate. The first AutoFarm-owned report remains
			// the durable read boundary, while external reports can be preserved.
			foreach (var summary in summaryReports
				.Where(report => report != null && report.Type != EspionageReportType.Action)
				.GroupBy(report => report.ID)
				.Select(group => group.First())) {
				try {
					if (IsManualReviewDefenseReport(summary.ID)) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"Skipping preserved espionage report {summary.ID}: planetary defenses are reserved for manual review.");
						continue;
					}

					if (IsPreservedOutsideConfiguredRange(summary.ID, processAllReports)) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"Encountered preserved espionage report {summary.ID}; it is outside the configured AutoFarm range. " +
							"Preserving it and continuing to look for older AutoFarm-owned reports.");
						continue;
					}

					if (IsKnownAutoFarmReport(summary.ID) &&
						!(processAllReports && IsMarkedAsNotScanned(summary.ID))) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"Encountered already handled espionage report {summary.ID}; checking cleanup before older reports.");
						var deleted = await TryDeleteEspionageReport(summary.ID, "already handled");
						if (!deleted)
							allReportsProcessed = false;
						if (AutoFarmReportPolicy.ShouldStopAfterKnownReport(deleted)) {
							// The known report is the durable read boundary. If cleanup
							// failed, keep the report phase pending so the next execution
							// retries cleanup instead of starting another galaxy scan.
							break;
						}
						continue;
					}

					var report = await _ogameService.GetEspionageReport(summary.ID);
					if (report == null || report.ID <= 0 || report.Coordinate == null) {
						var malformedReport = report ?? new EspionageReport { ID = summary.ID };
						if (malformedReport.ID <= 0)
							malformedReport.ID = summary.ID;
						RecordProcessedReport(malformedReport, "Malformed");
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm,
							$"Ignoring malformed espionage report {summary.ID}: no coordinate was returned.");
						var malformedDeleted = await TryDeleteEspionageReport(summary.ID, "malformed report");
						allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, malformedDeleted);
						if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(malformedDeleted))
							break;
						continue;
					}

					if (ShouldPreserveDefenseReport(report)) {
						PreserveReportForManualReview(report, processAllReports);
						continue;
					}

					if (IsManualReviewDefenseReport(report.ID)) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"Skipping preserved espionage report {report.ID}: planetary defenses are reserved for manual review.");
						continue;
					}
					if (IsKnownAutoFarmReport(report.ID) &&
						!(processAllReports && IsMarkedAsNotScanned(report.ID))) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"Ignoring consumed espionage report {report.ID} for {report.Coordinate}.");
						var deleted = await TryDeleteEspionageReport(report.ID, "already consumed");
						if (!deleted)
							allReportsProcessed = false;
						if (AutoFarmReportPolicy.ShouldStopAfterKnownReport(deleted)) {
							// The report was already accounted for, but a failed cleanup
							// must keep the report phase pending for a retry.
							break;
						}
						continue;
					}
					if (AutoFarmReportPolicy.IsExpired(report.Date, serverNowUtc, GetReportCooldown())) {
						RecordProcessedReport(report, "Expired");
						var expiredDeleted = await TryDeleteEspionageReport(report.ID, "expired report");
						allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, expiredDeleted);
						if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(expiredDeleted))
							break;
						continue;
					}

					if (_tbotInstance.UserData.farmTargets.Any(t => t.HasCoords(report.Coordinate)) ||
						(processAllReports && report.IsInactive)) {
						FarmTarget target;
						var matchingTarget = _tbotInstance.UserData.farmTargets.Where(t => t.HasCoords(report.Coordinate));
						if (matchingTarget.Count() == 0) {
							if (!report.IsInactive)
								continue;
							var resolved = await ResolveInactivePlanet(report);
							if (resolved.EligiblePlanet == null) {
								var outcome = resolved.TargetExists ? "TargetNoLongerEligible" : "TargetGone";
								var deleted = await ConsumeUnresolvableReport(report, outcome, outcome);
								allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, deleted);
								if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(deleted))
									break;
								continue;
							}

							target = GetFarmTarget(resolved.EligiblePlanet);
							if (target == null) {
								var deleted = await ConsumeUnresolvableReport(report, "Blacklisted", "blacklisted target");
								allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, deleted);
								if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(deleted))
									break;
								continue;
							}
						} else {
							target = matchingTarget.First();
							if (!AutoFarmReportPolicy.IsOwnedByAutoFarm(target, report, processAllReports)) {
								RecordProcessedReport(report, "NotScanned");
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
									$"Target {report.Coordinate} has a matching AutoFarm entry, but report {report.ID} was not produced by an active AutoFarm probe; preserving the report and caching the read boundary.");
								if (AutoFarmReportPolicy.ShouldStopAfterUnscannedReport(processAllReports))
									break;
								continue;
							}
						}

						if (!AutoFarmAttackPolicy.CanProcessReport(target, report)) {
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
								$"Ignoring already consumed or still active espionage report {report.ID} for {report.Coordinate}.");
							RecordProcessedReport(report, "AlreadyConsumedOrActive");
							var activeReportDeleted = await TryDeleteEspionageReport(report.ID, "already consumed or active");
							allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, activeReportDeleted);
							if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(activeReportDeleted))
								break;
							continue;
						}

						if (AutoFarmAttackPolicy.IsPreviouslyStoredReport(target, report)) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
							$"Ignoring previously handled non-actionable espionage report {report.ID} for {report.Coordinate}; attempting cleanup.");
						var deleted = await TryDeleteEspionageReport(report.ID, "previously handled report");
						if (!deleted)
							allReportsProcessed = false;
						if (AutoFarmReportPolicy.ShouldStopAfterKnownReport(deleted)) {
							// A duplicate is the read boundary. A failed delete keeps the
							// report phase pending and is retried on the next execution.
							break;
						}
						continue;
						}

					var newFarmTarget = target;

					if (target.Report != null && report.Date.ToUniversalTime() < target.Report.Date.ToUniversalTime()) {
						RecordProcessedReport(report, "OlderThanStoredReport");
						var deleted = await TryDeleteEspionageReport(report.ID, "older than stored report");
						if (!deleted)
							allReportsProcessed = false;
						if (AutoFarmReportPolicy.ShouldStopAfterKnownReport(deleted)) {
							break;
						}
						continue;
						}

						Buildables cargoShip;
						Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip);
						bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1 ? true : false;
						newFarmTarget.Report = report;
						newFarmTarget.ProbeFleetId = null;
						if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Metal" && report.Loot(_tbotInstance.UserData.userInfo.Class).Metal > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources
							|| _tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Crystal" && report.Loot(_tbotInstance.UserData.userInfo.Class).Crystal > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources
							|| _tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Deuterium" && report.Loot(_tbotInstance.UserData.userInfo.Class).Deuterium > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources
							|| (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "" && report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources)) {
							if (!report.HasFleetInformation || !report.HasDefensesInformation) {
								var probeLevel = target.ProbeLevel;
								if (probeLevel == 0 && target.State == FarmState.ProbesRequired)
									probeLevel = 1;
								else if (probeLevel == 0 && target.State == FarmState.FailedProbesRequired)
									probeLevel = 2;

								if (probeLevel <= 0) {
									newFarmTarget.State = FarmState.ProbesRequired;
									newFarmTarget.ProbeLevel = 1;
								} else if (probeLevel == 1) {
									newFarmTarget.State = FarmState.FailedProbesRequired;
									newFarmTarget.ProbeLevel = 2;
								} else {
									newFarmTarget.State = FarmState.NotSuitable;
									newFarmTarget.ProbeLevel = 0;
								}

								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Need more probes on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else if (report.IsDefenceless(isUsingProbes)) {
								newFarmTarget.State = FarmState.AttackPending;
								newFarmTarget.ProbeLevel = 0;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else {
								newFarmTarget.State = FarmState.NotSuitable;
								newFarmTarget.ProbeLevel = 0;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - defences present.");
								bool blacklistActive = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
									SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
									(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
								if (blacklistActive) {
									int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
									_blacklist.AddTarget(report.Coordinate, BlacklistReason.HasDefense, hoursUntilReset);
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (defenses present).");
								}
							}
						} else {
							newFarmTarget.State = FarmState.NotSuitable;
							newFarmTarget.ProbeLevel = 0;
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - insufficient loot ({report.Loot(_tbotInstance.UserData.userInfo.Class)})");
							bool blacklistActiveLowRes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
								SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
							if (blacklistActiveLowRes) {
								long minResources = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist", (long) 500000);
								if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < minResources) {
									int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
									_blacklist.AddTarget(report.Coordinate, BlacklistReason.LowResources, hoursUntilReset);
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (low resources: {report.Loot(_tbotInstance.UserData.userInfo.Class)}).");
								}
							}
						}

						// Keep the legacy report parsing below as a compatibility guard for
						// old settings, but make the centralized policy the final authority.
						ApplyReportToTarget(newFarmTarget, report);
						_tbotInstance.UserData.farmTargets.Remove(target);
						_tbotInstance.UserData.farmTargets.Add(newFarmTarget);
						PersistTargets();
						RecordProcessedReport(report, newFarmTarget.State.ToString());
						var processedReportDeleted = await TryDeleteEspionageReport(report.ID, "processed");
						allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, processedReportDeleted);
						if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(processedReportDeleted))
							break;
					} else {
						if (processAllReports && report.IsInactive) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Processing report for {report.Coordinate} not scanned by TBot (ProcessAllReports enabled)...");
							var resolved = await ResolveInactivePlanet(report);
							if (resolved.EligiblePlanet != null) {
								var target = GetFarmTarget(resolved.EligiblePlanet);
								if (target != null) {
									if (!AutoFarmAttackPolicy.CanProcessReport(target, report)) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
											$"Ignoring already consumed or still active espionage report {report.ID} for {report.Coordinate}.");
										RecordProcessedReport(report, "AlreadyConsumedOrActive");
										var processAllActiveReportDeleted = await TryDeleteEspionageReport(report.ID, "already consumed or active");
										allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, processAllActiveReportDeleted);
										if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(processAllActiveReportDeleted))
											break;
										continue;
									}

								if (AutoFarmAttackPolicy.IsPreviouslyStoredReport(target, report)) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm,
											$"Ignoring previously handled non-actionable espionage report {report.ID} for {report.Coordinate}; attempting cleanup.");
										RecordProcessedReport(report, "PreviouslyHandled");
										var processAllDuplicateDeleted = await TryDeleteEspionageReport(report.ID, "previously handled report");
										allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, processAllDuplicateDeleted);
										if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(processAllDuplicateDeleted))
											break;
										continue;
									}

									var newFarmTarget = target;
									Buildables cargoShip;
									Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip);
									bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1 ? true : false;
									newFarmTarget.Report = report;
									newFarmTarget.ProbeFleetId = null;
									newFarmTarget.ProbeLevel = 0;
									if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources) {
										if (report.HasFleetInformation && report.HasDefensesInformation) {
											if (report.IsDefenceless(isUsingProbes)) {
												newFarmTarget.State = FarmState.AttackPending;
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
											} else {
												newFarmTarget.State = FarmState.NotSuitable;
												bool blacklistActiveDefense2 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
													(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
												if (blacklistActiveDefense2) {
													int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
													_blacklist.AddTarget(report.Coordinate, BlacklistReason.HasDefense, hoursUntilReset);
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (defenses present).");
												}
											}
										}
										} else {
											newFarmTarget.State = FarmState.NotSuitable;
											bool blacklistActiveLowRes2 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
											(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
										if (blacklistActiveLowRes2) {
											long minResources = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist", (long) 500000);
											if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < minResources) {
												int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
												_blacklist.AddTarget(report.Coordinate, BlacklistReason.LowResources, hoursUntilReset);
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (low resources: {report.Loot(_tbotInstance.UserData.userInfo.Class)}).");
											}
										}
									}
					// ProcessAllReports must use exactly the same decision rules as reports
					// produced by AutoFarm probes.
					ApplyReportToTarget(newFarmTarget, report);
					_tbotInstance.UserData.farmTargets.Remove(target);
									_tbotInstance.UserData.farmTargets.Add(newFarmTarget);
									PersistTargets();
									RecordProcessedReport(report, newFarmTarget.State.ToString());
									var processAllReportDeleted = await TryDeleteEspionageReport(report.ID, "processed from all reports");
									allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, processAllReportDeleted);
									if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(processAllReportDeleted))
										break;
								} else {
									var blacklistedDeleted = await ConsumeUnresolvableReport(report, "Blacklisted", "blacklisted target");
									allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, blacklistedDeleted);
									if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(blacklistedDeleted))
										break;
								}
							} else {
								var outcome = resolved.TargetExists ? "TargetNoLongerEligible" : "TargetGone";
								var unresolvedDeleted = await ConsumeUnresolvableReport(report, outcome, outcome);
								allReportsProcessed = AutoFarmReportPolicy.CombineProcessingResult(allReportsProcessed, unresolvedDeleted);
								if (AutoFarmReportPolicy.ShouldStopAfterDeletionFailure(unresolvedDeleted))
									break;
							}
					} else {
						if (!processAllReports) {
							RecordProcessedReport(report, "NotScanned");
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
								$"Target {report.Coordinate} not scanned by TBot; preserving the report and continuing with older reports.");
							if (AutoFarmReportPolicy.ShouldStopAfterUnscannedReport(processAllReports))
								break;
						} else {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not scanned by TBot, ignoring...");
						}
					}
					}
				} catch (OperationCanceledException) when (_ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarmProcessReports Exception: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
					// One broken report must not prevent newer/older reports from being
					// handled in the same batch. Leave this report uncommitted so the
					// next execution can retry it, but keep the scan checkpoint pending.
					allReportsProcessed = false;
					continue;
				}
			}

			return allReportsProcessed;
		}
	}
}
