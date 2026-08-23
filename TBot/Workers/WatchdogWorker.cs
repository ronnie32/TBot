using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure.Enums;
using Tbot.Common.Settings;
using Tbot.Helpers;
using Tbot.Services;

namespace Tbot.Workers {
	// Watches every other worker's heartbeat and restarts + alerts on any that looks stuck.
	// Runs during SleepMode too, since a hang can happen at any time.
	public class WatchdogWorker : WorkerBase {
		public WatchdogWorker(ITBotMain parentInstance) : base(parentInstance) {
		}

		// Guards against restarting the same worker on every check if it keeps getting stuck right away.
		private readonly Dictionary<Feature, DateTime> _lastRestartAttempt = new();

		protected override bool RunsDuringSleep => true;

		// Checks every few minutes by design - logging every tick would flood the log.
		protected override bool LogNextExecution => false;

		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool) _tbotInstance.InstanceSettings.Watchdog.Active;
			} catch (Exception) {
				return false;
			}
		}

		public override string GetWorkerName() {
			return "Watchdog";
		}

		public override Feature GetFeature() {
			return Feature.Watchdog;
		}

		public override LogSender GetLogSender() {
			return LogSender.Watchdog;
		}

		private int GetMaxStuckMinutes() {
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Watchdog, "MaxStuckMinutes"))
				return (int) _tbotInstance.InstanceSettings.Watchdog.MaxStuckMinutes;
			return 30;
		}

		// Proof-of-life file for the external watchdog (TBot.Watchdog.exe). One file per instance alias.
		private void WriteHeartbeat() {
			try {
				Directory.CreateDirectory("data");
				File.WriteAllText(Path.Combine("data", $"watchdog_heartbeat_{_tbotInstance.InstanceAlias}.txt"), DateTime.UtcNow.ToString("O"));
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"Watchdog: failed to write heartbeat file: {e.Message}");
			}
		}

		protected override async Task Execute() {
			WriteHeartbeat();

			var maxStuck = TimeSpan.FromMinutes(GetMaxStuckMinutes());
			var now = DateTime.UtcNow;

			// ToList(): GetAllWorkers() also walks celestialWorkers, which a stuck worker's own
			// RestartWorker() call below could mutate mid-iteration.
			foreach (var worker in _tbotInstance.GetAllWorkers().ToList()) {
				if (worker.GetFeature() == Feature.Watchdog) continue; // don't watch itself
				if (!worker.IsWorkerRunning()) continue; // disabled/not started: nothing to check

				var start = worker.LastExecutionStart;
				if (start == null) continue; // never ran yet, nothing stuck

				bool stillExecuting = worker.LastExecutionEnd == null || worker.LastExecutionEnd < start;
				if (!stillExecuting) continue;

				var stuckFor = now - start.Value;
				if (stuckFor < maxStuck) continue;

				DoLog(LogLevel.Critical, $"Worker \"{worker.GetWorkerName()}\" looks stuck: still running an Execute() started {stuckFor} ago (limit {maxStuck}).");

				bool onCooldown = _lastRestartAttempt.TryGetValue(worker.GetFeature(), out var lastAttempt) && (now - lastAttempt) < maxStuck;
				if (onCooldown) {
					// Already tried restarting this one recently and it's stuck again (or still) -
					// keep alerting so it doesn't go unnoticed, but stop hammering it with restarts.
					await _tbotInstance.SendTelegramMessage($"🚨 Watchdog: worker \"{worker.GetWorkerName()}\" continua travado há {stuckFor:hh\\:mm\\:ss} mesmo após reinício recente. Pode ser um bug real - verifique manualmente!");
					continue;
				}
				_lastRestartAttempt[worker.GetFeature()] = now;

				await _tbotInstance.SendTelegramMessage($"🚨 Watchdog: worker \"{worker.GetWorkerName()}\" travado há {stuckFor:hh\\:mm\\:ss} (limite {maxStuck:hh\\:mm\\:ss}). Reiniciando automaticamente.");

				try {
					// Restarting the timer alone isn't enough: if Execute() is truly hung (not just
					// slow), it's still out there holding the worker's semaphore forever, and the new
					// timer tick would just queue up behind it via WaitWorker() and never run either.
					// Handing the worker a fresh semaphore unblocks it immediately; the abandoned task
					// (if it ever completes) releases the old, now-unused one harmlessly.
					worker.SetSemaphore(new SemaphoreSlim(1, 1));
					var period = worker.Period != System.Threading.Timeout.InfiniteTimeSpan ? worker.Period : TimeSpan.FromMinutes(1);
					worker.RestartWorker(_ct, period, TimeSpan.FromSeconds(5));
				} catch (Exception e) {
					DoLog(LogLevel.Error, $"Watchdog: failed to restart worker \"{worker.GetWorkerName()}\": {e.Message}");
					await _tbotInstance.SendTelegramMessage($"⚠️ Watchdog: falha ao tentar reiniciar \"{worker.GetWorkerName()}\" automaticamente: {e.Message}. Verifique manualmente!");
				}
			}

			int checkMin = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Watchdog, "CheckIntervalMin")
				? (int) _tbotInstance.InstanceSettings.Watchdog.CheckIntervalMin : 1;
			int checkMax = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Watchdog, "CheckIntervalMax")
				? (int) _tbotInstance.InstanceSettings.Watchdog.CheckIntervalMax : 2;
			ChangeWorkerPeriod(RandomizeHelper.CalcRandomInterval(checkMin, checkMax));
		}
	}
}
