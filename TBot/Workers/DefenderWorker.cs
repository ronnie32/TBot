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
        private readonly HashSet<int> _notifiedAttackIds = new();
        private readonly Dictionary<int, DateTime> _pendingSaves = new();
        private bool UseCurrentCelestial => !SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender, "UseCurrentCelestial") || (bool)_tbotInstance.InstanceSettings.Defender.UseCurrentCelestial;
        private bool DelayEnabled => SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Defender.Autofleet, "DelayedFleetSave") &&
            (bool)_tbotInstance.InstanceSettings.Defender.Autofleet.DelayedFleetSave.Active;
		private static readonly TimeSpan _handledAttackTtl = TimeSpan.FromMinutes(60);

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

				if (!UseCurrentCelestial) {
                    await FakeActivity();
                    _tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
                }
                // The empire-wide event list needs no cp parameter. Do not navigate to
                // Home/a random origin or restore a stale selection over the player's click.
                DateTime time = (await _ogameService.GetServerTime()).ToUniversalTime();
                _tbotInstance.UserData.attacks = await _ogameService.GetAttacks()
                    ?? throw new InvalidOperationException("OGame returned no attack snapshot.");
                var activeIds = _tbotInstance.UserData.attacks.Select(a => a.ID).ToHashSet();
                foreach (var id in _pendingSaves.Keys.Where(id => !activeIds.Contains(id)).ToArray()) {
                    _pendingSaves.Remove(id);
                    _notifiedAttackIds.Remove(id);
                    DoLog(LogLevel.Information, $"Delayed fleetsave cancelled: attack {id} is no longer present.");
                }
                _notifiedAttackIds.RemoveWhere(id => !activeIds.Contains(id));
                bool isUnderAttack = _tbotInstance.UserData.attacks.Count > 0;
				if (isUnderAttack) {
					if ((bool) _tbotInstance.InstanceSettings.Defender.Alarm.Active)
						await Task.Run(() => ConsoleHelpers.PlayAlarm(), _ct);
					DoLog(LogLevel.Warning, "ENEMY ACTIVITY!!!");
					foreach (AttackerFleet attack in _tbotInstance.UserData.attacks.OrderBy(a => a.ArriveIn)) {
                        _ct.ThrowIfCancellationRequested();
                        await HandleAttack(attack);
					}
				} else {
					DoLog(LogLevel.Information, "Your empire is safe");
				}
				long interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Defender.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Defender.CheckIntervalMax);
				if (interval <= 0)
					interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);

				time = (await _ogameService.GetServerTime()).ToUniversalTime();
                interval = BoundCheckInterval(interval, time);
                DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
				if (!UseCurrentCelestial) await _tbotOgameBridge.CheckCelestials();
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"An error has occurred while checking for attacks: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				DateTime time = DateTime.UtcNow; // Local retry time only, never used to plan an impact.
                long interval = _pendingSaves.Count > 0 ? 5000 : RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds);
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(TimeSpan.FromMilliseconds(interval));
				DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
				if (!UseCurrentCelestial) await _tbotOgameBridge.CheckCelestials();
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
				if (attack == null || attack.ID <= 0) throw new InvalidOperationException("Attack has no valid ID.");
                if (_pendingSaves.ContainsKey(attack.ID)) {
                    await TryPendingFleetSave(attack);
                    return;
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
				if (attack.Ships != null && _tbotInstance.UserData.researches.EspionageTechnology >= 8) {
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

            if (_notifiedAttackIds.Add(attack.ID)) {
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

            }
            if ((bool)_tbotInstance.InstanceSettings.Defender.Autofleet.Active) {
                var now = (await _ogameService.GetServerTime()).ToUniversalTime();
                var due = now;
                if (DelayEnabled) {
                    var delay = ReadDelaySeconds("DelaySeconds", 0, 86400);
                    var safety = ReadDelaySeconds("SafetySeconds", 1, 3600);
                    due = new[] { now.AddSeconds(delay), AttackArrival(attack, now).AddSeconds(-safety) }.Min();
                }
                _pendingSaves[attack.ID] = due;
                if (due > now) {
                    DoLog(LogLevel.Warning, $"Fleetsave scheduled for attack {attack.ID} on {attack.Destination} at {due:O}; attack will be rechecked before dispatch.");
                    await _tbotInstance.SendFeatureNotification(_tbotInstance.InstanceSettings.Defender,
                        $"Fleetsave scheduled on {attack.Destination} at {due:O}. You can move the fleet yourself; the bot will recheck the attack and ships before sending.", NotificationKind.Attack);
                } else await TryPendingFleetSave(attack);
            }
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"HandleAttack error for attack {attack?.ID}: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			}
		}
        private int ReadDelaySeconds(string field, int min, int max) {
            var value = (int)_tbotInstance.InstanceSettings.Defender.Autofleet.DelayedFleetSave[field];
            if (value < min || value > max) throw new InvalidOperationException($"Defender.Autofleet.DelayedFleetSave.{field} must be between {min} and {max} seconds.");
            return value;
        }

        private static DateTime AttackArrival(AttackerFleet attack, DateTime now) {
            if (attack.ArrivalTime == default || attack.ArriveIn <= 0)
                throw new InvalidOperationException($"Attack {attack.ID} has no future arrival time; fleetsave was not scheduled.");
            return new[] { attack.ArrivalTime.ToUniversalTime(), now.AddSeconds(attack.ArriveIn) }.Min();
        }

        private long BoundCheckInterval(long interval, DateTime now) {
            if (_pendingSaves.Count == 0) return interval;
            var nextDue = _pendingSaves.Values.Min();
            return Math.Min(interval, nextDue <= now ? 5000 : Math.Max(1000, (long)(nextDue - now).TotalMilliseconds));
        }

        private async Task TryPendingFleetSave(AttackerFleet attack) {
            if (!(bool)_tbotInstance.InstanceSettings.Defender.Autofleet.Active) {
                _pendingSaves.Remove(attack.ID);
                DoLog(LogLevel.Information, $"Delayed fleetsave cancelled for attack {attack.ID}: Defender.Autofleet.Active is false.");
                return;
            }
            var now = (await _ogameService.GetServerTime()).ToUniversalTime();
            var arrival = AttackArrival(attack, now);
            if (arrival <= now) {
                _pendingSaves.Remove(attack.ID);
                DoLog(LogLevel.Error, $"Attack {attack.ID} arrival has passed; no fleetsave sent for this expired event.");
                return;
            }
            var deadline = DelayEnabled ? arrival.AddSeconds(-ReadDelaySeconds("SafetySeconds", 1, 3600)) : now;
            var due = new[] { _pendingSaves[attack.ID], deadline }.Min();
            _pendingSaves[attack.ID] = due;
            if (due > now) return;
            _ct.ThrowIfCancellationRequested();
            var origin = _tbotInstance.UserData.celestials.Unique().FirstOrDefault(c => c.HasCoords(attack.Destination))
                ?? throw new InvalidOperationException($"Cannot fleetsave attack {attack.ID}: origin {attack.Destination} missing.");
            // Do not use UpdatePlanet here: its catch retains stale stock after an API error.
            origin.Ships = await _ogameService.GetShips(origin)
                ?? throw new InvalidOperationException("OGame returned no ships for fleetsave.");
            if (origin.Ships.GetMovableShips().IsEmpty()) {
                _pendingSaves.Remove(attack.ID);
                _handledAttackIds[attack.ID] = DateTime.UtcNow;
                DoLog(LogLevel.Information, $"Delayed fleetsave cancelled for attack {attack.ID}: no movable ships remain on {origin.Coordinate}.");
                return;
            }
            var remaining = Math.Max(1L, (long)Math.Ceiling((arrival - now).TotalSeconds));
            var fleetId = await _fleetScheduler.AutoFleetSaveWithResult(origin, remaining + remaining * 30 / 100 + 30);
            if (fleetId > 0) {
                _pendingSaves.Remove(attack.ID);
                _handledAttackIds[attack.ID] = DateTime.UtcNow;
                DoLog(LogLevel.Information, $"Fleetsave dispatch confirmed for attack {attack.ID} from {origin.Coordinate}, fleet={fleetId}.");
            } else {
                // Keep the original deadline: an unsuccessful send must not restart the grace period.
                DoLog(LogLevel.Error, $"Fleetsave dispatch not confirmed for attack {attack.ID}; retrying on the next check.");
            }
        }

	}
}
