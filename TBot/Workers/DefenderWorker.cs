using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TBot.Common.Logging;
using Tbot.Helpers;
using TBot.Model;
using Tbot.Services;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Tbot.Includes;
using System.Timers;
using TBot.Ogame.Infrastructure;
using Tbot.Common.Settings;

namespace Tbot.Workers {
	internal class DefenderWorker : WorkerBase {
		private readonly ConcurrentDictionary<int, DateTime> _handledAttackIds = new();
		private static readonly TimeSpan _handledAttackTtl = TimeSpan.FromMinutes(60);

		// Tracks the pending fleetsave for each celestial (keyed by celestial ID), so we only
		// keep a single scheduled save per celestial and can reschedule it earlier if a faster
		// attack shows up later.
		private readonly ConcurrentDictionary<int, ScheduledFleetSave> _scheduledFleetSaves = new();

		private class ScheduledFleetSave {
			public DateTime ScheduledAtUtc; // when the fleet should actually be sent
			public DateTime ApproxArrivalUtc; // approximate arrival time of the attack that triggered this schedule
			public CancellationTokenSource Cts;
		}

		// Tracks fleet IDs that were sent away by a fleetsave, per celestial, so they can be
		// recalled once the attacker disappears from the attack list.
		private readonly ConcurrentDictionary<int, List<int>> _awaySavedFleets = new();

		// Tracks a pending recall per celestial, so we don't schedule the same recall twice and
		// can cancel it if a new attack shows up before it fires.
		private readonly ConcurrentDictionary<int, ScheduledRecall> _scheduledRecalls = new();

		private class ScheduledRecall {
			public DateTime ScheduledAtUtc;
			public CancellationTokenSource Cts;
		}

		private readonly object _backgroundLock = new();
		private readonly List<Task> _backgroundTasks = new();

		private void TrackBackgroundTask(Task task) {
			lock (_backgroundLock) {
				_backgroundTasks.RemoveAll(completed => completed.IsCompleted);
				_backgroundTasks.Add(task);
			}
		}

		public override async Task StopWorker() {
			await base.StopWorker();
			foreach (var scheduled in _scheduledFleetSaves.Values) {
				try { scheduled.Cts.Cancel(); } catch (ObjectDisposedException) { }
			}
			foreach (var scheduled in _scheduledRecalls.Values) {
				try { scheduled.Cts.Cancel(); } catch (ObjectDisposedException) { }
			}
			Task[] tasks;
			lock (_backgroundLock) tasks = _backgroundTasks.ToArray();
			await Task.WhenAll(tasks);
			lock (_backgroundLock) _backgroundTasks.Clear();
			_handledAttackIds.Clear();
		}

		private readonly IFleetScheduler _fleetScheduler;
		private readonly IOgameService _ogameService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		public DefenderWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ITBotOgamedBridge tbotOgameBridge)
			: base(parentInstance) {
			_fleetScheduler = fleetScheduler;
			_ogameService = ogameService;
			_tbotOgameBridge = tbotOgameBridge;
		}

		protected override async Task Execute() {
			try {
				DoLog(LogLevel.Information, "Checking attacks...");

				await FakeActivity();
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				bool isUnderAttack = await _ogameService.IsUnderAttack();
				DateTime time = await _tbotOgameBridge.GetDateTime();
				if (isUnderAttack) {
					if ((bool) _tbotInstance.InstanceSettings.Defender.Alarm.Active)
						await Task.Run(() => ConsoleHelpers.PlayAlarm(), _ct);
					DoLog(LogLevel.Warning, "ENEMY ACTIVITY!!!");
					_tbotInstance.UserData.attacks = await _ogameService.GetAttacks();
					foreach (AttackerFleet attack in _tbotInstance.UserData.attacks) {
						await HandleAttack(attack);
					}
					await CheckForFleetsToRecall(_tbotInstance.UserData.attacks);
				} else {
					DoLog(LogLevel.Information, "Your empire is safe");
					await CheckForFleetsToRecall(new List<AttackerFleet>());
				}
				long interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Defender.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Defender.CheckIntervalMax);
				if (interval <= 0)
					interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);

				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
				await _tbotOgameBridge.CheckCelestials();
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"An error has occurred while checking for attacks: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				DateTime time = await _tbotOgameBridge.GetDateTime();
				long interval = RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds);
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
				await _tbotOgameBridge.CheckCelestials();
			} finally {

			}
		}
		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool) _tbotInstance.InstanceSettings.Defender.Active;
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "Defender";
		}
		public override Feature GetFeature() {
			return Feature.Defender;
		}

		public override LogSender GetLogSender() {
			return LogSender.Defender;
		}


		private async Task FakeActivity() {

			Celestial celestial;
			Celestial randomCelestial;
			var randomActivity = (bool) _tbotInstance.InstanceSettings.Defender.RandomActivity;

			if (randomActivity == false) {
				celestial = _tbotInstance.UserData.celestials
				.Unique()
				.Where(c => c.Coordinate.Galaxy == (int) _tbotInstance.InstanceSettings.Defender.Home.Galaxy)
				.Where(c => c.Coordinate.System == (int) _tbotInstance.InstanceSettings.Defender.Home.System)
				.Where(c => c.Coordinate.Position == (int) _tbotInstance.InstanceSettings.Defender.Home.Position)
				.Where(c => c.Coordinate.Type == Enum.Parse<Celestials>((string) _tbotInstance.InstanceSettings.Defender.Home.Type))
				.SingleOrDefault() ?? new() { ID = 0 };

				if (celestial.ID != 0) {
					DoLog(LogLevel.Information, $"Check from Home ({celestial.Coordinate.Galaxy}:{celestial.Coordinate.System}:{celestial.Coordinate.Position} {celestial.Coordinate.Type})");
					celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Defences);
				}
			} else {
				randomCelestial = _tbotInstance.UserData.celestials.Shuffle().FirstOrDefault() ?? new() { ID = 0 };

				if (randomCelestial.ID != 0) {
					DoLog(LogLevel.Information, $"Check from Random Celestial");
					randomCelestial = await _tbotOgameBridge.UpdatePlanet(randomCelestial, UpdateTypes.Defences);
				}
			}
			return;
		}

		private async Task HandleAttack(AttackerFleet attack) {
			try {
				var nowUtc = DateTime.UtcNow;
				foreach (var kv in _handledAttackIds.ToArray()) {
					if (nowUtc - kv.Value > _handledAttackTtl)
						_handledAttackIds.TryRemove(kv.Key, out _);
				}
				if (attack != null && attack.ID != 0 &&
					_handledAttackIds.TryGetValue(attack.ID, out var seenAt) &&
					(nowUtc - seenAt) <= _handledAttackTtl) {
					DoLog(LogLevel.Information, $"Attack {attack.ID} already handled recently; skipping duplicate actions.");
					return;
				}
				if (attack != null && attack.ID != 0) {
					_handledAttackIds[attack.ID] = nowUtc;
				}

			if (_tbotInstance.UserData.celestials.Count() == 0) {
				DateTime time = await _tbotOgameBridge.GetDateTime();
				long interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Warning, "Unable to handle attack at the moment: bot is still getting account info.");
				DoLog(LogLevel.Information,  $"Next check at {newTime.ToString()}");
				return;
			}

			Celestial attackedCelestial = _tbotInstance.UserData.celestials.Unique().FirstOrDefault(planet => planet.HasCoords(attack.Destination));
			if (attackedCelestial == null) {
				DoLog(LogLevel.Warning, $"Unable to handle attack {attack.ID}: attacked celestial not found in account data.");
				return;
			}
			attackedCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Ships);
			try {
				if ((bool)_tbotInstance.InstanceSettings.Defender.IgnoreAttackIfIHave.Active) {
					attackedCelestial = await _tbotOgameBridge.UpdatePlanet(attackedCelestial, UpdateTypes.Resources);
				}
			} catch {
			}


			try {
				var wlObj = _tbotInstance.InstanceSettings.Defender.WhiteList;
				IEnumerable<long> whiteListIds = wlObj switch {
					long[] a => a,
					int[] a => a.Select(x => (long)x),
					IEnumerable<long> e => e,
					IEnumerable<int> e => e.Select(x => (long)x),
					_ => Enumerable.Empty<long>()
				};

				if (!whiteListIds.Any() && wlObj != null) {
					DoLog(LogLevel.Debug, $"Defender WhiteList present but unsupported type: {wlObj.GetType().FullName}");
				}

				foreach (var playerId in whiteListIds) {
					if (attack.AttackerID == playerId) {
						DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: attacker {attack.AttackerName} whitelisted.");
						return;
					}
				}
			} catch (Exception ex) {
				DoLog(LogLevel.Warning, $"An error has occurred while checking Defender WhiteList: {ex.Message}");
			}

			try {
				if (attack.MissionType == Missions.MissileAttack) {
					await _tbotInstance.SendFeatureNotification(_tbotInstance.InstanceSettings.Defender,
						$"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attack.Destination.ToString()} with IPM!",
						NotificationKind.MissileAttack);
					DoLog(LogLevel.Information, $"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attack.Destination.ToString()} with IPM!");
					if (
						!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "DefendFromMissiles") ||
						(SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "DefendFromMissiles") && (bool) _tbotInstance.InstanceSettings.Defender.DefendFromMissiles)
					) {
						Celestial defenderCelestial = attackedCelestial;
						if (attackedCelestial.Coordinate.Type == Celestials.Moon) {
							 defenderCelestial = _tbotInstance.UserData.celestials.Unique().SingleOrDefault(planet => planet.HasCoords(new Coordinate {
								 Galaxy = attackedCelestial.Coordinate.Galaxy,
								 System = attackedCelestial.Coordinate.System,
								 Position = attackedCelestial.Coordinate.Position,
								 Type = Celestials.Planet
							}));
						}
						if (defenderCelestial == null) {
							DoLog(LogLevel.Warning, $"Missile attack detected on {attack.Destination.ToString()} but planet celestial was not found in account data. Skipping missile defence.");
							return;
						}
						defenderCelestial = await _tbotOgameBridge.UpdatePlanet(defenderCelestial, UpdateTypes.Facilities);
						if (defenderCelestial.Facilities.MissileSilo >= 2) {
							defenderCelestial = await _tbotOgameBridge.UpdatePlanet(defenderCelestial, UpdateTypes.Defences);
							defenderCelestial = await _tbotOgameBridge.UpdatePlanet(defenderCelestial, UpdateTypes.Productions);
							if (defenderCelestial.Productions.Count == 0) {
								var availableSpace = defenderCelestial.Facilities.MissileSilo - defenderCelestial.Defences.AntiBallisticMissiles - (2 * defenderCelestial.Defences.InterplanetaryMissiles);
								defenderCelestial = await _tbotOgameBridge.UpdatePlanet(defenderCelestial, UpdateTypes.Resources);
								if (availableSpace > 0) {
									DoLog(LogLevel.Information, $"Building {availableSpace} AntiBallisticMissiles on {defenderCelestial.ToString()}");
									await _ogameService.BuildDefences(defenderCelestial, Buildables.AntiBallisticMissiles, availableSpace);
								}
								else {
									DoLog(LogLevel.Information, $"Unable to build AntiBallisticMissiles on {defenderCelestial.ToString()}: there is no space");
								}
							}
							else {
								DoLog(LogLevel.Information, $"Unable to build AntiBallisticMissiles on {defenderCelestial.ToString()}: a production is ongoing");
							}
						}
						else {
							DoLog(LogLevel.Information, $"No MissileSilo level >= 2 on {defenderCelestial.ToString()}");
						}
					}
					return;
				}
				bool fleetCompositionKnown = attack.Ships != null && _tbotInstance.UserData.researches.EspionageTechnology >= 8;
				if (fleetCompositionKnown) {
					if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "IgnoreProbes") && (bool) _tbotInstance.InstanceSettings.Defender.IgnoreProbes && attack.IsOnlyProbes()) {
						if (attack.MissionType == Missions.Spy)
							DoLog(LogLevel.Information, "Attacker sent only Probes! Espionage action skipped.");
						else
							DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: only Espionage Probes.");

						return;
					}
					if (
						(bool) _tbotInstance.InstanceSettings.Defender.IgnoreWeakAttack &&
						attack.Ships.GetFleetPoints() < (attackedCelestial.Ships.GetFleetPoints() / (int) _tbotInstance.InstanceSettings.Defender.WeakAttackRatio)
					) {
						DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: weak attack.");
						return;
					}
				} else {
					DoLog(LogLevel.Information, "Unable to detect fleet composition.");
				}
				var ignoreAttackIfIHaveActive = (bool) _tbotInstance.InstanceSettings.Defender.IgnoreAttackIfIHave.Active;
				var totalResources = attackedCelestial.Resources?.TotalResources ?? 0;
				var fleetPoints = attackedCelestial.Ships?.GetFleetPoints() ?? 0;

				if (
					fleetCompositionKnown &&
					ignoreAttackIfIHaveActive &&
					totalResources < (long) _tbotInstance.InstanceSettings.Defender.IgnoreAttackIfIHave.MinResourcesToSave &&
					(fleetPoints * 1000) < (long) _tbotInstance.InstanceSettings.Defender.IgnoreAttackIfIHave.MinFleetToSave
				) {
					DoLog(LogLevel.Information, $"Attack {attack.ID.ToString()} skipped: it's not worth it.");
					return;
				}
			} catch {
				DoLog(LogLevel.Warning, "An error has occurred while checking attacker fleet composition");
			}

			await _tbotInstance.SendFeatureNotification(_tbotInstance.InstanceSettings.Defender,
				$"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attack.Destination.ToString()} arriving at {attack.ArrivalTime.ToString()}",
				NotificationKind.Attack);
			if (attack.Ships != null) {
				await Task.Delay(1000, _ct);
				await _tbotInstance.SendFeatureNotification(_tbotInstance.InstanceSettings.Defender,
					$"The attack is composed by: {attack.Ships.ToString()}",
					NotificationKind.Attack);
			}
			DoLog(LogLevel.Warning, $"Player {attack.AttackerName} ({attack.AttackerID}) is attacking your planet {attackedCelestial.ToString()} arriving at {attack.ArrivalTime.ToString()}");
			if (attack.Ships != null) {
				await Task.Delay(1000, _ct);
				DoLog(LogLevel.Warning, $"The attack is composed by: {attack.Ships.ToString()}");
			}

			if ((bool) _tbotInstance.InstanceSettings.Defender.SpyAttacker.Active) {
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				if (attackedCelestial.Ships.EspionageProbe == 0) {
					DoLog(LogLevel.Warning, "Could not spy attacker: no probes available.");
				} else {
					try {
						Coordinate destination = attack.Origin;
						Ships ships = new() { EspionageProbe = (int) _tbotInstance.InstanceSettings.Defender.SpyAttacker.Probes };
						int fleetId = await _fleetScheduler.SendFleet(attackedCelestial, ships, destination, Missions.Spy, Speeds.HundredPercent, new Resources(), _tbotInstance.UserData.userInfo.Class);
						var fleet = _tbotInstance.UserData.fleets.SingleOrDefault(f => f.ID == fleetId);
						if (fleet == null) {
							DoLog(LogLevel.Warning, $"SpyAttacker: SendFleet returned id={fleetId}, but fleet was not found in current fleet list (send may have failed or list not updated yet).");
						} else {
							DoLog(LogLevel.Information, $"Spying attacker from {attackedCelestial.ToString()} to {destination.ToString()} with {_tbotInstance.InstanceSettings.Defender.SpyAttacker.Probes} probes. Arrival at {fleet.ArrivalTime.ToString()}");
						}
					} catch (Exception e) {
						DoLog(LogLevel.Error, $"Could not spy attacker: an exception has occurred: {e.Message}");
						DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
					}
				}
			}

			if ((bool) _tbotInstance.InstanceSettings.Defender.MessageAttacker.Active) {
				try {
					if (attack.AttackerID != 0) {
						Random random = new();
						string[] messages = _tbotInstance.InstanceSettings.Defender.MessageAttacker.Messages;
						string message = messages.ToList().Shuffle().First();
						DoLog(LogLevel.Information, $"Sending message \"{message}\" to attacker {attack.AttackerName}");
						try {
							await _ogameService.SendMessage(attack.AttackerID, message);
							DoLog(LogLevel.Information, "Message succesfully sent.");
						} catch {
							DoLog(LogLevel.Warning, "Unable send message.");
						}
					} else {
						DoLog(LogLevel.Warning, "Unable send message.");
					}

				} catch (Exception e) {
					DoLog(LogLevel.Error, $"Could not message attacker: an exception has occurred: {e.Message}");
					DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				}
			}

			if ((bool) _tbotInstance.InstanceSettings.Defender.Autofleet.Active) {
				try {
					ScheduleFleetSave(attackedCelestial, attack);
				} catch (Exception e) {
					DoLog(LogLevel.Error, $"Could not fleetsave: an exception has occurred: {e.Message}");
					DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				}
			}
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"HandleAttack error for attack {attack?.ID}: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			}
		}
		/// <summary>
		/// Schedules a fleetsave for the given celestial to be sent 30-60 seconds before the
		/// attack lands, instead of sending it immediately. If a save is already scheduled for
		/// this celestial, it is only rescheduled when the new attack arrives earlier than the
		/// one currently driving the schedule.
		/// </summary>
		private void ScheduleFleetSave(Celestial celestial, AttackerFleet attack) {
			var nowUtc = DateTime.UtcNow;
			long arriveInSeconds = attack.ArriveIn;
			int bufferSeconds = new Random().Next(30, 61); // send fleet 30-60s before the attacker lands
			long sendDelaySeconds = Math.Max(0, arriveInSeconds - bufferSeconds);

			var candidate = new ScheduledFleetSave {
				ScheduledAtUtc = nowUtc.AddSeconds(sendDelaySeconds),
				ApproxArrivalUtc = nowUtc.AddSeconds(arriveInSeconds),
				Cts = CancellationTokenSource.CreateLinkedTokenSource(_ct)
			};

			bool shouldSchedule = false;
			ScheduledFleetSave replaced = null;

			_scheduledFleetSaves.AddOrUpdate(celestial.ID,
				addValueFactory: _ => {
					shouldSchedule = true;
					return candidate;
				},
				updateValueFactory: (_, existing) => {
					// Only reschedule if this attack lands earlier than whatever is currently
					// driving the schedule; a later-arriving attack doesn't need to move anything.
					if (candidate.ApproxArrivalUtc < existing.ApproxArrivalUtc) {
						replaced = existing;
						shouldSchedule = true;
						return candidate;
					}
					shouldSchedule = false;
					return existing;
				});

			if (!shouldSchedule) {
				DoLog(LogLevel.Information, $"Fleetsave for {celestial.ToString()} already scheduled for an earlier or equal arrival; keeping existing schedule.");
				candidate.Cts.Dispose();
				return;
			}

			if (replaced != null) {
				replaced.Cts.Cancel();
				replaced.Cts.Dispose();
			}

			DoLog(LogLevel.Information, $"Scheduling fleetsave for {celestial.ToString()}: attacker arrives in ~{arriveInSeconds}s, fleet will be sent in ~{sendDelaySeconds}s (about {bufferSeconds}s before impact).");

			TrackBackgroundTask(RunScheduledFleetSave(celestial, candidate));
		}

		private async Task RunScheduledFleetSave(Celestial celestial, ScheduledFleetSave scheduled) {
			try {
				var delay = scheduled.ScheduledAtUtc - DateTime.UtcNow;
				if (delay > TimeSpan.Zero)
					await Task.Delay(delay, scheduled.Cts.Token);

				if (scheduled.Cts.Token.IsCancellationRequested)
					return;

				double remainingSeconds = Math.Max(0, (scheduled.ApproxArrivalUtc - DateTime.UtcNow).TotalSeconds);
				long minFlightTime = (long) (remainingSeconds + (remainingSeconds / 100 * 30) + (RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds) / 1000));

				DoLog(LogLevel.Warning, $"Sending fleet away from {celestial.ToString()} ahead of incoming attack.");
				var confirmedFleetId = await _fleetScheduler.AutoFleetSaveWithResult(celestial, minFlightTime);
				if (confirmedFleetId > 0) {
					var savedFleetId = confirmedFleetId.Value;
					_awaySavedFleets.AddOrUpdate(celestial.ID,
						_ => new List<int> { savedFleetId },
						(_, existing) => existing.Union(new[] { savedFleetId }).ToList());
					DoLog(LogLevel.Information, $"Tracking confirmed defensive fleet {savedFleetId} from {celestial.ToString()} for later recall.");
				} else {
					DoLog(LogLevel.Warning, $"Fleetsave triggered for {celestial.ToString()} but returned no confirmed fleet ID; recall tracking skipped.");
				}
			} catch (TaskCanceledException) {
				DoLog(LogLevel.Information, $"Scheduled fleetsave for {celestial.ToString()} was cancelled (superseded by an earlier-arriving attack, or the bot is shutting down).");
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"Could not fleetsave: an exception has occurred: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			} finally {
				// Only remove the entry if it still points at this exact scheduled instance -
				// avoids clobbering a newer schedule that superseded this one.
				_scheduledFleetSaves.TryRemove(new KeyValuePair<int, ScheduledFleetSave>(celestial.ID, scheduled));
				scheduled.Cts.Dispose();
	}
}

		/// <summary>
		/// Goes through every celestial that currently has fleets away on a defensive save and
		/// checks whether the attacker is still present in the current attack list. If not,
		/// schedules a recall; if a new attack just showed up on a celestial with a pending
		/// recall, cancels that recall so the fleet stays away.
		/// </summary>
		private async Task CheckForFleetsToRecall(List<AttackerFleet> currentAttacks) {
			if (_awaySavedFleets.IsEmpty)
				return;

			foreach (var celestialId in _awaySavedFleets.Keys.ToArray()) {
				Celestial celestial = _tbotInstance.UserData.celestials.Unique().FirstOrDefault(c => c.ID == celestialId);
				if (celestial == null)
					continue;

				bool stillTargeted = currentAttacks != null && currentAttacks.Any(a => celestial.HasCoords(a.Destination));
				if (stillTargeted) {
					if (_scheduledRecalls.TryRemove(celestialId, out var pending)) {
						pending.Cts.Cancel();
						pending.Cts.Dispose();
						DoLog(LogLevel.Information, $"Cancelled pending recall for {celestial.ToString()}: attacker is back.");
					}
					continue;
				}

				ScheduleFleetRecall(celestial);
			}
			await Task.CompletedTask;
		}

		/// <summary>
		/// Schedules a recall of the fleet(s) previously saved from this celestial, after a short
		/// random delay, so a fresh attack has a moment to cancel the recall before it fires.
		/// </summary>
		private void ScheduleFleetRecall(Celestial celestial) {
			if (_scheduledRecalls.ContainsKey(celestial.ID))
				return; // a recall is already pending for this celestial

			int delaySeconds = new Random().Next(30, 91);
			var scheduled = new ScheduledRecall {
				ScheduledAtUtc = DateTime.UtcNow.AddSeconds(delaySeconds),
				Cts = CancellationTokenSource.CreateLinkedTokenSource(_ct)
			};

			if (!_scheduledRecalls.TryAdd(celestial.ID, scheduled)) {
				scheduled.Cts.Dispose();
				return; // another thread beat us to scheduling it
			}

			DoLog(LogLevel.Information, $"Attacker gone from {celestial.ToString()}; recalling saved fleet(s) in ~{delaySeconds}s.");
			TrackBackgroundTask(RunScheduledRecall(celestial, scheduled));
		}

		private async Task RunScheduledRecall(Celestial celestial, ScheduledRecall scheduled) {
			try {
				var delay = scheduled.ScheduledAtUtc - DateTime.UtcNow;
				if (delay > TimeSpan.Zero)
					await Task.Delay(delay, scheduled.Cts.Token);

				if (scheduled.Cts.Token.IsCancellationRequested)
					return;

				// Re-check right before recalling: make sure nothing new has targeted this
				// celestial while we were waiting out the delay.
				var freshAttacks = await _ogameService.GetAttacks();
				if (freshAttacks != null && freshAttacks.Any(a => celestial.HasCoords(a.Destination))) {
					DoLog(LogLevel.Information, $"Recall for {celestial.ToString()} aborted: a new attack showed up before the recall fired.");
					return;
				}

				if (!_awaySavedFleets.TryRemove(celestial.ID, out var fleetIds) || fleetIds == null || fleetIds.Count == 0)
					return;

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				foreach (var fleetId in fleetIds) {
					var fleet = _tbotInstance.UserData.fleets.SingleOrDefault(f => f.ID == fleetId);
					if (fleet == null) {
						DoLog(LogLevel.Information, $"Saved fleet {fleetId} from {celestial.ToString()} is no longer active (already returned or landed); nothing to recall.");
						continue;
					}
					if (fleet.ReturnFlight) {
						DoLog(LogLevel.Information, $"Saved fleet {fleetId} from {celestial.ToString()} is already on its way back; nothing to recall.");
						continue;
					}
					try {
						DoLog(LogLevel.Warning, $"Recalling saved fleet {fleetId} back to {celestial.ToString()}.");
						await _fleetScheduler.CancelFleet(fleet);
					} catch (Exception e) {
						DoLog(LogLevel.Error, $"Could not recall fleet {fleetId}: an exception has occurred: {e.Message}");
						DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
					}
				}
			} catch (TaskCanceledException) {
				DoLog(LogLevel.Information, $"Scheduled recall for {celestial.ToString()} was cancelled.");
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"Could not process scheduled recall: an exception has occurred: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			} finally {
				_scheduledRecalls.TryRemove(new KeyValuePair<int, ScheduledRecall>(celestial.ID, scheduled));
				scheduled.Cts.Dispose();
			}
		}
	}
}
