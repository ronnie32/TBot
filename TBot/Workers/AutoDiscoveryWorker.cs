using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Extensions.Logging;
using Tbot.Helpers;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Tbot.Common.Settings;

namespace Tbot.Workers {
	public class AutoDiscoveryWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		private readonly AutoDiscoveryCursorStore _cursorStore;
		private readonly DiscoverySystemCacheStore _systemCacheStore;
		private readonly ArtifactStorageStateStore _artifactStorageStateStore;
		private ArtifactStorageState _artifactStorageState;

		// Discovery positions belong to a galaxy, not to an origin. Keeping one
		// cursor per galaxy prevents another configured origin from restarting the
		// same system and generating needless system-view activity.
		private readonly Dictionary<int, DiscoveryCursorState> _galaxyCursors = new();
		private int _nextOriginIndex;

		private enum DiscoveryOriginAttemptResult {
			NoCandidate,
			Sent,
			PositionUnavailable,
			NoShips,
			Unavailable,
			Stopped
		}

		private sealed class DiscoveryRunContext {
			public int FleetsToSend;
			public int DiscoveriesRemaining;
			public bool Stop;
			public int Skips;
			public int GlobalAttempts;
			public int MaxGlobalAttempts;
			public int Sent;
			public Dictionary<DiscoveryDispatchStatus, int> Outcomes { get; } = new();

			public void Record(DiscoveryDispatchStatus status, int amount = 1) {
				if (amount <= 0)
					return;
				Outcomes[status] = Outcomes.TryGetValue(status, out var count) ? count + amount : amount;
				if (status == DiscoveryDispatchStatus.Sent)
					Sent += amount;
			}
		}

		public AutoDiscoveryWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) :
			base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
			_cursorStore = new AutoDiscoveryCursorStore(
				$"autodiscovery_cursors_{_tbotInstance.InstanceAlias}.json");
			_systemCacheStore = new DiscoverySystemCacheStore(_tbotInstance.InstanceAlias);
			_artifactStorageStateStore = new ArtifactStorageStateStore(_tbotInstance.InstanceAlias);
			_artifactStorageState = _artifactStorageStateStore.Load();
		}

		private sealed class CoordinateComparer : IEqualityComparer<Coordinate> {
			public bool Equals(Coordinate x, Coordinate y) {
				if (ReferenceEquals(x, y)) return true;
				if (x is null || y is null) return false;
				return x.Galaxy == y.Galaxy && x.System == y.System && x.Position == y.Position && x.Type == y.Type;
			}

			public int GetHashCode(Coordinate obj) {
				if (obj is null) return 0;
				unchecked {
					int hash = 17;
					hash = hash * 31 + obj.Galaxy.GetHashCode();
					hash = hash * 31 + obj.System.GetHashCode();
					hash = hash * 31 + obj.Position.GetHashCode();
					hash = hash * 31 + obj.Type.GetHashCode();
					return hash;
				}
			}
		}

		private static readonly IEqualityComparer<Coordinate> _coordinateComparer = new CoordinateComparer();

		private void EnsureBlacklistInitialized() {
			if (_tbotInstance?.UserData == null) return;

			if (_tbotInstance.UserData.discoveryBlackList == null) {
				_tbotInstance.UserData.discoveryBlackList = new Dictionary<Coordinate, DateTime>(_coordinateComparer);
				return;
			}

			if (!ReferenceEquals(_tbotInstance.UserData.discoveryBlackList.Comparer, _coordinateComparer)) {
				var old = _tbotInstance.UserData.discoveryBlackList;
				var fresh = new Dictionary<Coordinate, DateTime>(_coordinateComparer);
				foreach (var kv in old) {

					if (fresh.TryGetValue(kv.Key, out var existing)) {
						if (kv.Value > existing) fresh[kv.Key] = kv.Value;
					} else {
						fresh[kv.Key] = kv.Value;
					}
				}
				_tbotInstance.UserData.discoveryBlackList = fresh;
			}
		}

		private void CleanupExpiredBlacklistAll(DateTime now) {
			if (_tbotInstance?.UserData?.discoveryBlackList == null) return;
			var toRemove = _tbotInstance.UserData.discoveryBlackList
				.Where(kv => kv.Value <= now)
				.Select(kv => kv.Key)
				.ToList();
			foreach (var k in toRemove)
				_tbotInstance.UserData.discoveryBlackList.Remove(k);
		}

		private bool IsBlacklistedAndActive(Coordinate c, DateTime now) {
			if (_tbotInstance?.UserData?.discoveryBlackList == null) return false;
			return _tbotInstance.UserData.discoveryBlackList.TryGetValue(c, out var until) && until > now;
		}

		private void UpsertBlacklist(Coordinate c, DateTime until) {
			if (_tbotInstance?.UserData?.discoveryBlackList == null) return;
			_tbotInstance.UserData.discoveryBlackList[c] = until;
		}

		private List<Celestial> GetConfiguredOrigins(List<Celestial> celestials) {
			var result = new List<Celestial>();
			if (celestials == null || celestials.Count == 0) return result;

			var originsCfg = _tbotInstance?.InstanceSettings?.AutoDiscovery?.Origin;
			if (originsCfg == null) return result;

			foreach (var o in originsCfg) {
				int galaxy = Convert.ToInt32(o.Galaxy);
				int system = Convert.ToInt32(o.System);
				int position = Convert.ToInt32(o.Position);
				string typeStr = Convert.ToString(o.Type) ?? "Planet";

				Celestials wantedType = Celestials.Planet;
				if (Enum.TryParse(typeStr, true, out Celestials parsed))
					wantedType = parsed;

				var found = celestials.FirstOrDefault(c =>
					c?.Coordinate != null &&
					c.Coordinate.Galaxy == galaxy &&
					c.Coordinate.System == system &&
					c.Coordinate.Position == position &&
					c.Coordinate.Type == wantedType
				);

				if (found != null) {
					if (!result.Any(existing => existing?.Coordinate != null && existing.Coordinate.IsSame(found.Coordinate)))
						result.Add(found);
					else
						DoLog(LogLevel.Debug, $"Duplicate AutoDiscovery origin ignored: {found.Coordinate}.");
				} else
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoDiscovery,
						$"AutoDiscovery origin not found in user celestials: {galaxy}:{system}:{position} ({typeStr})");
			}

			return result;
		}

		private DiscoveryCursorState GetOrInitCursor(Celestial origin) {
			if (origin?.Coordinate == null)
				return null;

			var galaxy = origin.Coordinate.Galaxy;
			if (!_galaxyCursors.TryGetValue(galaxy, out var cursor) || cursor == null) {
				var fallbackSystem = (_tbotInstance.UserData.celestials ?? new List<Celestial>())
					.Where(celestial => celestial?.Coordinate?.Galaxy == galaxy)
					.Select(celestial => celestial.Coordinate.System)
					.DefaultIfEmpty(origin.Coordinate.System)
					.Min();
				cursor = _cursorStore.LoadShared(galaxy, fallbackSystem);
				_galaxyCursors[galaxy] = cursor;
			}

			var maxSystem = Math.Max(1, _tbotInstance.UserData.serverData?.Systems ?? 1);
			cursor.Normalize(origin.Coordinate.System, maxSystem);
			return cursor;
		}

		private void SaveCursor(Celestial origin, DiscoveryCursorState cursor) {
			if (origin?.Coordinate == null || cursor == null)
				return;
			_cursorStore.SaveShared(origin.Coordinate.Galaxy, cursor);
		}

		private async Task<DiscoveryOriginAttemptResult> ProcessOneSystemForOrigin(
			Celestial origin,
			DiscoveryCursorState cursor,
			DiscoveryRunContext ctx,
			int systemToDo) {

			if (origin?.Coordinate == null) return DiscoveryOriginAttemptResult.Unavailable;
			if (ctx.DiscoveriesRemaining <= 0 || ctx.FleetsToSend <= 0 || ctx.Stop)
				return DiscoveryOriginAttemptResult.Stopped;

			DateTime now = await _tbotOgameBridge.GetDateTime();
			int maxSystem = Math.Max(1, _tbotInstance.UserData.serverData?.Systems ?? 1);
			bool isCursorSystem = cursor.System == systemToDo;
			List<Coordinate> availableCoordinates;
			try {
				availableCoordinates = await _ogameService.GetPositionsAvailableForDiscoveryFleet(origin, new Coordinate {
					Galaxy = origin.Coordinate.Galaxy,
					System = systemToDo,
					Position = 0,
					Type = Celestials.Planet
				});
			} catch (Exception e) {
				DoLog(LogLevel.Warning,
					$"System view failed for {origin.Coordinate.Galaxy}:{systemToDo}; no discovery positions will be probed this cycle: {e.Message}");
				return DiscoveryOriginAttemptResult.Unavailable;
			}

			var availablePositions = new HashSet<int>(
				(availableCoordinates ?? new List<Coordinate>())
					.Where(c => c != null && c.Galaxy == origin.Coordinate.Galaxy && c.System == systemToDo && c.Position >= 1 && c.Position <= 15)
					.Select(c => c.Position));
			_systemCacheStore.Observe(
				origin.Coordinate.Galaxy,
				systemToDo,
				availablePositions.Where(position => !isCursorSystem || position >= cursor.NextPosition),
				now);

			var blacklistedPositions = new HashSet<int>();
			for (int position = 1; position <= 15; position++) {
				var positionCoordinate = new Coordinate {
					Galaxy = origin.Coordinate.Galaxy,
					System = systemToDo,
					Position = position,
					Type = Celestials.Planet
				};
				if (IsBlacklistedAndActive(positionCoordinate, now))
					blacklistedPositions.Add(position);
			}

			var plannedPositions = DiscoverySystemPlanner.SelectAvailablePositions(
				availablePositions,
				isCursorSystem ? cursor.NextPosition : 1,
				blacklistedPositions,
				maximum: 15);

			DoLog(LogLevel.Debug,
				$"System {origin.Coordinate.Galaxy}:{systemToDo} reports {availablePositions.Count} available discovery positions; planned {plannedPositions.Count}.");

			if (plannedPositions.Count == 0) {
				_systemCacheStore.Invalidate(origin.Coordinate.Galaxy, systemToDo);
				if (isCursorSystem) {
					cursor.SkipSystem(systemToDo, maxSystem);
					SaveCursor(origin, cursor);
				}
				DoLog(LogLevel.Debug,
					$"Origin {origin} found no usable discovery position in system {origin.Coordinate.Galaxy}:{systemToDo}; cursor advanced to {cursor.System}:{cursor.NextPosition}.");
				return DiscoveryOriginAttemptResult.NoCandidate;
			}

			int dispatchLimit = Math.Min(ctx.FleetsToSend, ctx.DiscoveriesRemaining);
			if (dispatchLimit <= 0)
				return DiscoveryOriginAttemptResult.Stopped;

			// Preserve the system-first planner while binding the server action to
			// this exact set of positions and this cycle's reserved slot count.
			bool canUseSystemDispatch = plannedPositions.Count <= dispatchLimit &&
				plannedPositions.Count == availablePositions.Count &&
				availablePositions.All(position => position >= (isCursorSystem ? cursor.NextPosition : 1)) &&
				availablePositions.All(position => !blacklistedPositions.Contains(position));
			if (canUseSystemDispatch) {
				if (++ctx.GlobalAttempts > ctx.MaxGlobalAttempts) {
					ctx.Stop = true;
					return DiscoveryOriginAttemptResult.Stopped;
				}

				DiscoverySystemDispatchResult systemResult;
				try {
					// Only the reserved coordinates may be sent, even if eligibility
					// changes after this view was read.
					systemResult = await _fleetScheduler.SendSystemDiscovery(
						origin, origin.Coordinate.Galaxy, systemToDo, plannedPositions.Count, plannedPositions);
				} catch (Exception e) {
					systemResult = new DiscoverySystemDispatchResult(
						DiscoveryDispatchStatus.Failed,
						message: e.Message);
				}

				if (systemResult == null) {
					// A production FleetScheduler always returns a result. Treat a null
					// result as a hard failure rather than falling back to blind sends.
					// Blind fallback could duplicate a request whose response was lost.
					systemResult = new DiscoverySystemDispatchResult(
						DiscoveryDispatchStatus.Failed,
						message: "FleetScheduler returned no system-dispatch result");
				}

				var plannedSet = plannedPositions.ToHashSet();
				var confirmed = (systemResult.Coordinates ?? Array.Empty<Coordinate>())
					.Where(c => c != null && c.Galaxy == origin.Coordinate.Galaxy &&
						c.System == systemToDo && plannedSet.Contains(c.Position))
					.Select(c => c.Position)
					.Distinct()
					.ToList();

				if (systemResult.IsConfirmed && confirmed.Count == 0) {
					DoLog(LogLevel.Warning,
						$"Discovery system {origin.Coordinate.Galaxy}:{systemToDo} returned success without confirming a planned position; stopping without advancing the cursor.");
					ctx.Stop = true;
					return DiscoveryOriginAttemptResult.Stopped;
				}

				ctx.Record(systemResult.Status, systemResult.IsConfirmed ? confirmed.Count : 1);
				if (confirmed.Count > 0) {
					if (confirmed.Count != plannedPositions.Count)
						DoLog(LogLevel.Warning,
							$"Discovery system {origin.Coordinate.Galaxy}:{systemToDo} confirmed {confirmed.Count}/{plannedPositions.Count} planned positions; leaving the cursor recoverable for the next cycle.");

					foreach (var position in confirmed)
						UpsertBlacklist(new Coordinate(origin.Coordinate.Galaxy, systemToDo, position, Celestials.Planet), now.AddDays(7));

					// A complete response can advance linearly. A partial response may
					// contain holes, so only advance through the contiguous confirmed
					// prefix; otherwise a valid position could be skipped permanently.
					int contiguous = isCursorSystem ? cursor.NextPosition : 1;
					while (plannedSet.Contains(contiguous) && confirmed.Contains(contiguous))
						contiguous++;
					if (isCursorSystem && contiguous > cursor.NextPosition)
						cursor.CommitPosition(systemToDo, contiguous - 1, maxSystem);
					_systemCacheStore.Consume(origin.Coordinate.Galaxy, systemToDo, confirmed);
					if (isCursorSystem)
						SaveCursor(origin, cursor);
					ctx.FleetsToSend -= confirmed.Count;
					ctx.DiscoveriesRemaining -= confirmed.Count;
					DoLog(LogLevel.Information,
						$"Discovery system {origin.Coordinate.Galaxy}:{systemToDo} dispatched {confirmed.Count} fleet(s) from {origin}.");
					if (!systemResult.IsConfirmed || confirmed.Count != plannedPositions.Count) {
						ctx.Stop = true;
						return DiscoveryOriginAttemptResult.Stopped;
					}
					return DiscoveryOriginAttemptResult.Sent;
				}

				DoLog(systemResult.Status == DiscoveryDispatchStatus.PositionUnavailable
					? LogLevel.Debug
					: LogLevel.Warning,
					$"Discovery system {origin.Coordinate.Galaxy}:{systemToDo} was not sent from {origin}: {systemResult.Status} — {systemResult.Message}.");
				if (systemResult.Status == DiscoveryDispatchStatus.NoShips)
					return DiscoveryOriginAttemptResult.NoShips;
				if (systemResult.Status == DiscoveryDispatchStatus.PositionUnavailable ||
					systemResult.Status == DiscoveryDispatchStatus.Rejected)
					_systemCacheStore.Invalidate(origin.Coordinate.Galaxy, systemToDo);
				if (systemResult.Status == DiscoveryDispatchStatus.PositionUnavailable ||
					systemResult.Status == DiscoveryDispatchStatus.Rejected)
					return DiscoveryOriginAttemptResult.PositionUnavailable;

				_systemCacheStore.Invalidate(origin.Coordinate.Galaxy, systemToDo);
				ctx.Stop = true;
				return DiscoveryOriginAttemptResult.Stopped;
			}

			// There are more positions available than this cycle's budget, or the
			// system endpoint cannot safely be used because its result would not be
			// limited to the worker's plan. Stay on this system and send positions
			// one by one until the budget is exhausted.
			bool sentAny = false;
			bool positionWasUnavailable = false;
			foreach (int pos in plannedPositions.Take(dispatchLimit)) {
				if (ctx.DiscoveriesRemaining <= 0 || ctx.FleetsToSend <= 0 || ctx.Stop)
					return DiscoveryOriginAttemptResult.Stopped;

				if (++ctx.GlobalAttempts > ctx.MaxGlobalAttempts) {
					ctx.Stop = true;
					return DiscoveryOriginAttemptResult.Stopped;
				}

				var dest = new Coordinate {
					Galaxy = origin.Coordinate.Galaxy,
					System = systemToDo,
					Position = pos,
					Type = Celestials.Planet
				};

				if (IsBlacklistedAndActive(dest, now)) {
					ctx.Skips++;
					if (isCursorSystem) {
						cursor.CommitPosition(systemToDo, pos, maxSystem);
						SaveCursor(origin, cursor);
					}
					_systemCacheStore.Consume(origin.Coordinate.Galaxy, systemToDo, new[] { pos });
					return DiscoveryOriginAttemptResult.NoCandidate;
				}

				DiscoveryDispatchResult dispatchResult;
				try {
					// Discovery dispatches share FleetScheduler's account-wide gate with
					// expeditions, farming and harvesting. The scheduler also prevents a
					// concurrent worker from observing and consuming the same slot.
					dispatchResult = await _fleetScheduler.SendDiscovery(origin, dest);
				} catch (Exception e) {
					dispatchResult = new DiscoveryDispatchResult(DiscoveryDispatchStatus.Failed, e.Message);
					DoLog(LogLevel.Warning, $"Discovery dispatch failed for {dest} from {origin}: {e.Message}");
				}
				ctx.Record(dispatchResult.Status);

				// The cursor records that this position was examined, not that it was
				// accepted. This prevents a failed position from trapping the worker on
				// the same coordinate while allowing it to be retried on a later pass.
				if (isCursorSystem) {
					cursor.CommitPosition(systemToDo, pos, maxSystem);
					SaveCursor(origin, cursor);
				}

				if (dispatchResult.IsConfirmed) {
					_systemCacheStore.Consume(origin.Coordinate.Galaxy, systemToDo, new[] { pos });
					DoLog(LogLevel.Information, $"Discovery fleet sent to {dest} from {origin}.");
					ctx.DiscoveriesRemaining--;
					ctx.FleetsToSend--;
					sentAny = true;
					// Only a confirmed dispatch earns a long cooldown. A rejected or
					// transiently failed request must not make a valid position disappear
					// for seven days.
					UpsertBlacklist(dest, now.AddDays(7));
					// Keep consuming the positions from this system before the outer
					// selector is allowed to choose another origin. This is the
					// system-first policy; the next iteration also re-checks the
					// remaining budget through the loop guard.
					continue;
				} else {
					if (dispatchResult.Status != DiscoveryDispatchStatus.NoShips)
						_systemCacheStore.Invalidate(origin.Coordinate.Galaxy, systemToDo);
					DoLog(dispatchResult.Status == DiscoveryDispatchStatus.PositionUnavailable ||
						dispatchResult.Status == DiscoveryDispatchStatus.NoShips
						? LogLevel.Debug
						: LogLevel.Warning,
						$"Discovery not sent to {dest} from {origin}: {dispatchResult.Status} — {dispatchResult.Message}.");
					if (dispatchResult.Status == DiscoveryDispatchStatus.NoShips) {
						DoLog(LogLevel.Information,
							$"Origin {origin} has no usable discovery ships; skipping this origin for the current cycle.");
						return DiscoveryOriginAttemptResult.NoShips;
					}
					if (dispatchResult.Status == DiscoveryDispatchStatus.NoSlots ||
						dispatchResult.Status == DiscoveryDispatchStatus.RequestPending ||
						dispatchResult.Status == DiscoveryDispatchStatus.SessionExpired ||
						dispatchResult.Status == DiscoveryDispatchStatus.Failed) {
						ctx.Stop = true;
						return sentAny ? DiscoveryOriginAttemptResult.Sent : DiscoveryOriginAttemptResult.Stopped;
					}

					positionWasUnavailable = true;
					continue;
				}
			}

			if (sentAny)
				return DiscoveryOriginAttemptResult.Sent;
			return positionWasUnavailable
				? DiscoveryOriginAttemptResult.PositionUnavailable
				: DiscoveryOriginAttemptResult.NoCandidate;
		}

		private DiscoveryOriginSystemPair SelectClosestDiscoveryPair(
			IReadOnlyList<Celestial> origins,
			ISet<int> unavailableOrigins,
			ISet<string> excludedSystems) {
			if (origins == null || origins.Count == 0)
				return null;

			int maxSystem = Math.Max(1, _tbotInstance.UserData.serverData?.Systems ?? 1);
			var originCoordinates = origins
				.Select((origin, index) => (origin, index))
				.Where(item => item.origin?.Coordinate != null)
				.Select(item => (item.origin.Coordinate.Galaxy, item.origin.Coordinate.System))
				.ToList();
			if (originCoordinates.Count != origins.Count)
				return null;

			var cachedSystems = _systemCacheStore.LoadAvailable()
				.Select(entry => (entry.Galaxy, entry.System))
				.ToList();
			var cachedPair = DiscoveryOriginSystemSelectionPolicy.SelectClosest(
				originCoordinates,
				cachedSystems,
				maxSystem,
				_nextOriginIndex,
				unavailableOrigins,
				excludedSystems);
			if (cachedPair != null)
				return cachedPair;

			// No indexed system is usable. Probe only one next system per galaxy to
			// seed the index; the closest origin to that system wins the tie-break.
			var nextSystems = origins
				.Select(origin => new { Origin = origin, Cursor = GetOrInitCursor(origin) })
				.Where(item => item.Origin?.Coordinate != null && item.Cursor != null)
				.Select(item => (Galaxy: item.Origin.Coordinate.Galaxy, System: item.Cursor.System))
				.GroupBy(system => (system.Galaxy, system.System))
				.Select(group => group.Key)
				.ToList();
			return DiscoveryOriginSystemSelectionPolicy.SelectClosest(
				originCoordinates,
				nextSystems,
				maxSystem,
				_nextOriginIndex,
				unavailableOrigins,
				excludedSystems);
		}


		private long CalcFallbackIntervalMs() {
			return RandomizeHelper.CalcRandomInterval(
				(int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMin,
				(int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMax
			);
		}

		private long CalcNextIntervalFromDiscoveryFleetsOrFallback() {
			var discFleet = _tbotInstance.UserData.fleets
				.Where(f => f.Mission == Missions.Discovery && f.BackIn.HasValue)
				.OrderBy(f => f.BackIn.Value)
				.FirstOrDefault();

			if (discFleet == null) return CalcFallbackIntervalMs();

			long backInSec = discFleet.BackIn ?? 0;
			long interval = backInSec * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
			if (interval <= 0) interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
			return interval;
		}

		private bool IsArtifactStorageControlEnabled() {
			var control = _tbotInstance.InstanceSettings?.AutoDiscovery?.ArtifactStorage;
			return SettingsService.IsSettingSet(control, "Active") && (bool)control.Active;
		}

		private async Task<bool> CanSendBecauseOfArtifactStorage(List<Celestial> origins) {
			if (!IsArtifactStorageControlEnabled())
				return true;

			var control = _tbotInstance.InstanceSettings.AutoDiscovery.ArtifactStorage;
			long stopAt = SettingsService.GetSetting(control, "StopAt", 0L);
			long resumeBelow = SettingsService.GetSetting(control, "ResumeBelow", stopAt);
			if (stopAt <= 0) {
				DoLog(LogLevel.Warning, "ArtifactStorage control is enabled but StopAt is not positive; keeping AutoDiscovery enabled.");
				return true;
			}

			var inventoryOrigin = (origins ?? new List<Celestial>())
				.FirstOrDefault(origin => origin?.Coordinate?.Type == Celestials.Planet)
				?? _tbotInstance.UserData.celestials?.FirstOrDefault(celestial => celestial?.Coordinate?.Type == Celestials.Planet);
			if (inventoryOrigin == null) {
				DoLog(LogLevel.Warning, "Unable to check artifact storage: no planet origin is available; pausing AutoDiscovery for safety.");
				return false;
			}

			try {
				var inventory = await _ogameService.GetArtifactInventory(inventoryOrigin);
				if (inventory == null || inventory.ArtifactsLimit <= 0) {
					DoLog(LogLevel.Warning,
						$"Unable to read a valid artifact capacity; keeping the previous AutoDiscovery artifact state ({_artifactStorageState}).");
					return _artifactStorageState == ArtifactStorageState.Active;
				}

				var nextState = ArtifactStoragePolicy.NextState(
					_artifactStorageState,
					inventory.ArtifactsCollected,
					stopAt,
					resumeBelow);
				if (nextState != _artifactStorageState) {
					DoLog(LogLevel.Information,
						$"AutoDiscovery artifact state changed: {_artifactStorageState} -> {nextState} at " +
						$"{inventory.ArtifactsCollected}/{inventory.ArtifactsLimit} (stop {stopAt}, resume below {resumeBelow}).");
					_artifactStorageState = nextState;
					_artifactStorageStateStore.Save(nextState);
				}

				if (_artifactStorageState == ArtifactStorageState.Paused) {
					DoLog(LogLevel.Information,
						$"AutoDiscovery paused: artifact storage is {inventory.ArtifactsCollected}/{inventory.ArtifactsLimit} " +
						$"(stop threshold {stopAt}, resume at or below {Math.Min(Math.Max(0, resumeBelow), stopAt - 1)}).");
					return false;
				}

				DoLog(LogLevel.Debug,
					$"Artifact storage allows AutoDiscovery: {inventory.ArtifactsCollected}/{inventory.ArtifactsLimit} (resume threshold {resumeBelow}).");
				return true;
			} catch (Exception e) {
				DoLog(LogLevel.Warning,
					$"Unable to check artifact storage; keeping the previous AutoDiscovery artifact state ({_artifactStorageState}): {e.Message}");
				return _artifactStorageState == ArtifactStorageState.Active;
			}
		}

		protected override async Task Execute() {
			bool delay = false;
			bool stop = false;
			int skips = 0;

			try {
				DoLog(LogLevel.Information, $"Starting AutoDiscovery...");

				EnsureBlacklistInitialized();

				if (_tbotInstance.UserData.isSleeping) {
					stop = true;
					return;
				}

				if ((bool)_tbotInstance.InstanceSettings.SleepMode.Active) {
					DateTime.TryParse((string)_tbotInstance.InstanceSettings.SleepMode.GoToSleep, out DateTime goToSleep);
					DateTime.TryParse((string)_tbotInstance.InstanceSettings.SleepMode.WakeUp, out DateTime wakeUp);
					DateTime timeSleep = await _tbotOgameBridge.GetDateTime();
					if (GeneralHelper.ShouldSleep(timeSleep, goToSleep, wakeUp)) {
						DoLog(LogLevel.Warning, "Unable to send discovery fleet: bed time has passed");
						stop = true;
						return;
					}
				}

				DateTime now = await _tbotOgameBridge.GetDateTime();
				CleanupExpiredBlacklistAll(now);

				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

				List<RankSlotsPriority> rankSlotsPriority = new() {
					new RankSlotsPriority(Feature.BrainAutoMine, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
						((bool)_tbotInstance.InstanceSettings.Brain.Active && (bool)_tbotInstance.InstanceSettings.Brain.Transports.Active &&
						((bool)_tbotInstance.InstanceSettings.Brain.AutoMine.Active || (bool)_tbotInstance.InstanceSettings.Brain.AutoResearch.Active ||
						 (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active || (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
						(int)_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)
					),
					new RankSlotsPriority(Feature.Expeditions, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
						(bool)_tbotInstance.InstanceSettings.Expeditions.Active,
						(int)_tbotInstance.UserData.slots.ExpTotal,
						(int)_tbotInstance.UserData.slots.ExpInUse
					),
					new RankSlotsPriority(Feature.AutoFarm, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
						(bool)_tbotInstance.InstanceSettings.AutoFarm.Active,
						(int)_tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Attack)
					),
					new RankSlotsPriority(Feature.Colonize, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Colonize,
						(bool)_tbotInstance.InstanceSettings.AutoColonize.Active,
						(bool)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ? (int)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots : 1,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)
					),
					new RankSlotsPriority(Feature.AutoDiscovery, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
						(bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active,
						(int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)
					),
					new RankSlotsPriority(Feature.Harvest, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
						(bool)_tbotInstance.InstanceSettings.AutoHarvest.Active,
						(int)_tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest)
					)
				};

				int fleetsToSend = _calculationService.CalcSlotsPriority(
					Feature.AutoDiscovery, rankSlotsPriority,
					_tbotInstance.UserData.slots, _tbotInstance.UserData.fleets,
					(int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree
				);

				if (fleetsToSend <= 0) {
					delay = true;
					return;
				}

				var celestials = await _ogameService.GetCelestials();
				var origins = GetConfiguredOrigins(celestials);

				if (origins == null || origins.Count == 0) {
					stop = true;
					DoLog(LogLevel.Warning, "Unable to parse AutoDiscovery origin.");
					return;
				}

				if (!await CanSendBecauseOfArtifactStorage(origins)) {
					delay = true;
					return;
				}

				// OGame exposes discovery availability through the planet-scoped endpoint.
				// The returned count is account-wide, so query it once instead of asking
				// every configured origin and creating unnecessary activity.
				var availabilityOrigin = origins.FirstOrDefault(origin =>
					origin?.Coordinate?.Type == Celestials.Planet) ?? origins[0];
				int discoveries = await _ogameService.GetAvailableDiscoveries(availabilityOrigin);
				if (discoveries <= 0) {
					delay = true;
					DoLog(LogLevel.Information, "No discoveries available right now.");
					return;
				}

				var ctx = new DiscoveryRunContext {
					FleetsToSend = Math.Min(fleetsToSend, discoveries),
					DiscoveriesRemaining = discoveries,
					Stop = false,
					Skips = 0,
					GlobalAttempts = 0,
					MaxGlobalAttempts = 600
				};

				// Discovery positions are account-wide. Use the persisted positive
				// system index first, then scan one new cursor system only when the
				// index is empty. Distance is the primary optimization; the cursor is
				// only a round-robin tie-breaker.
				var unavailableOrigins = new HashSet<int>();
				var excludedSystems = new HashSet<string>(StringComparer.Ordinal);
				while (!ctx.Stop && ctx.FleetsToSend > 0 && ctx.DiscoveriesRemaining > 0) {
					var pair = SelectClosestDiscoveryPair(origins, unavailableOrigins, excludedSystems);
					if (pair == null)
						break;

					var origin = origins[pair.OriginIndex];
					if (origin?.Coordinate == null) {
						unavailableOrigins.Add(pair.OriginIndex);
						continue;
					}

					var cursor = GetOrInitCursor(origin);
					DoLog(LogLevel.Information,
						$"Origin {origin} selected for nearest system {pair.Galaxy}:{pair.System} (distance={pair.Distance}, cursor position {cursor?.NextPosition ?? 1}, remaining={ctx.FleetsToSend}).");

					var attempt = await ProcessOneSystemForOrigin(origin, cursor, ctx, pair.System);
					var systemKey = $"{pair.Galaxy}:{pair.System}";
					switch (attempt) {
						case DiscoveryOriginAttemptResult.Sent:
							_nextOriginIndex = (pair.OriginIndex + 1) % origins.Count;
							excludedSystems.Remove(systemKey);
							break;
						case DiscoveryOriginAttemptResult.NoShips:
						case DiscoveryOriginAttemptResult.Unavailable:
							unavailableOrigins.Add(pair.OriginIndex);
							// A ship shortage belongs to this origin, not to the
							// system. Another origin may still dispatch the cached
							// positions in the same cycle.
							if (attempt == DiscoveryOriginAttemptResult.Unavailable)
								excludedSystems.Add(systemKey);
							DoLog(LogLevel.Information,
								$"Origin {origin} skipped for the remainder of this AutoDiscovery cycle: {attempt}.");
							break;
						case DiscoveryOriginAttemptResult.NoCandidate:
						case DiscoveryOriginAttemptResult.PositionUnavailable:
							excludedSystems.Add(systemKey);
							break;
						case DiscoveryOriginAttemptResult.Stopped:
							ctx.Stop = true;
							break;
					}
				}

				stop = ctx.Stop;
				skips = ctx.Skips;

				if (skips > 0)
					DoLog(LogLevel.Information, $"{skips} positions skipped (blacklisted)");
				if (ctx.Outcomes.Count > 0) {
					var outcomeSummary = string.Join(", ", ctx.Outcomes
						.OrderBy(entry => entry.Key.ToString())
						.Select(entry => $"{entry.Key}={entry.Value}"));
					DoLog(LogLevel.Information,
						$"AutoDiscovery cycle summary: sent={ctx.Sent}, remaining={ctx.FleetsToSend}; {outcomeSummary}.");
				}

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

				long interval = CalcNextIntervalFromDiscoveryFleetsOrFallback();
				DateTime time = await _tbotOgameBridge.GetDateTime();
				var nextTime = time.AddMilliseconds(interval);

				ChangeWorkerPeriod(interval);
				DoLog(LogLevel.Information, $"Next AutoDiscovery check at {nextTime.ToString()}");
				await _tbotOgameBridge.CheckCelestials();
		    	} catch (Exception e) {
				DoLog(LogLevel.Error, $"An error occured: {e.Message}");
				DoLog(LogLevel.Debug, e.StackTrace);
		    	} finally {
				if (stop) {
					DoLog(LogLevel.Information, $"Stopping feature.");
					await EndExecution();
				}

				if (delay) {
					DoLog(LogLevel.Information, $"Delaying...");
					var timeDelay = await _tbotOgameBridge.GetDateTime();
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

					long intervalDelay = CalcNextIntervalFromDiscoveryFleetsOrFallback();
					var newTimeDelay = timeDelay.AddMilliseconds(intervalDelay);

					ChangeWorkerPeriod(intervalDelay);
					DoLog(LogLevel.Information, $"Next AutoDiscovery check at {newTimeDelay.ToString()}");
				}

				await _tbotOgameBridge.CheckCelestials();
			}
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active;
			} catch (Exception) {
				return false;
			}
		}

		public override string GetWorkerName() {
			return "AutoDiscovery";
		}
		public override Feature GetFeature() {
			return Feature.AutoDiscovery;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoDiscovery;
		}
	}
}
