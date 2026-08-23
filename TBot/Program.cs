using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serilog;
using Tbot.Common.Settings;
using Tbot.Exceptions;
using Tbot.Helpers;
using Tbot.Includes;
using Tbot.Services;
using Tbot.Workers;
using TBot.Common.Logging;
using TBot.Common.Logging.Enrichers;
using TBot.Common.Logging.Hooks;
using TBot.Common.Logging.Hubs;
using TBot.Common.Logging.Sinks;
using TBot.Common.Logging.TextFormatters;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.WebUI;

namespace Tbot {

	class Program {
		private static ILoggerService<Program> _logger;
		private static IInstanceManager _instanceManager;
		static DateTime startTime = DateTime.UtcNow;

		static void Main(string[] args) {
			MainAsync(args).Wait();
		}
		static async Task MainAsync(string[] args) {

			Serilog.Debugging.SelfLog.Enable(msg => {
				try { System.IO.File.AppendAllText("serilog-selflog.txt", DateTime.UtcNow + " " + msg + Environment.NewLine); } catch { }
			});

			var serviceCollection = WebApp.GetServiceCollection()
				.AddSingleton(typeof(ILoggerService<>), typeof(LoggerService<>))
				.AddScoped<ICalculationService, CalculationService>()
				.AddScoped<IOgameService, OgameService>()
				.AddScoped<ITBotMain, TBotMain>()
				.AddScoped<ITBotOgamedBridge, TBotOgamedBridge>()
				.AddScoped<IFleetScheduler, FleetScheduler>()
				.AddScoped<IWorkerFactory, WorkerFactory>()
				.AddScoped<ITelegramMessenger, TelegramMessenger>()
				.AddScoped<IInstanceManager, InstanceManager>();

			ConsoleHelpers.SetTitle();

			CmdLineArgsService.DoParse(args);
			if (CmdLineArgsService.printHelp) {
				ColoredConsoleWriter.LogToConsole(LogLevel.Information, LogSender.Tbot, $"{System.AppDomain.CurrentDomain.FriendlyName} {CmdLineArgsService.helpStr}");
				Environment.Exit(0);
			}

			string settingsPath = Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "settings.json");
			if (CmdLineArgsService.settingsPath.IsPresent) {
				settingsPath = Path.GetFullPath(CmdLineArgsService.settingsPath.Get());
			}

			SettingsService.GlobalSettingsPath = settingsPath;

			var logPath = Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "log");
			if (CmdLineArgsService.logPath.IsPresent == true) {
				logPath = Path.GetFullPath(CmdLineArgsService.logPath.Get());
			}

			SettingsService.LogsPath = logPath;

			var serviceProvider = await WebApp.Build();

			_logger = serviceProvider.GetRequiredService<ILoggerService<Program>>();
			_instanceManager = serviceProvider.GetRequiredService<IInstanceManager>();
			var ogameService = serviceProvider.GetRequiredService<IOgameService>();

			_logger.ConfigureLogging(logPath);
			_instanceManager.SettingsAbsoluteFilepath = settingsPath;

			// Last line of defense: log a crash (with stack trace) to file before the process dies,
			// instead of losing the reason entirely - re-ported 2026-08-06 after being lost in the
			// 3.4.6 rebase, same regression pattern as AsyncTimer.StopAsync/OnSettingsChanged.
			AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
				var ex = e.ExceptionObject as Exception;
				_logger.WriteLog(LogLevel.Critical, LogSender.Main, $"FATAL UNHANDLED EXCEPTION (IsTerminating={e.IsTerminating}): {ex?.Message}\n{ex?.StackTrace}");
				if (e.IsTerminating && !CmdLineArgsService.noCrashRestart) {
					// A hard crash skips all normal cleanup, so ogamed.exe (this process' child) can still
					// be alive holding its HTTP port - kill it now so the fresh instance we're about to spawn
					// doesn't immediately fail to bind that same port.
					try {
						ogameService.KillOgamedExecutable();
					} catch { /* best-effort - the process may already be gone or never started */ }
					TryRelaunchSelfAfterCrash(args);
				}
				Log.CloseAndFlush();
			};

			TaskScheduler.UnobservedTaskException += (sender, e) => {
				_logger.WriteLog(LogLevel.Error, LogSender.Main, $"Unobserved task exception: {e.Exception.Message}\n{e.Exception.StackTrace}");
				e.SetObserved();
			};

			// Context validation
			//	a - Ogamed binary is present on same directory ?
			//	b - Settings file does exist ?
			if (!ogameService.ValidatePrerequisites()) {
				Environment.Exit(-1);
			} else if (File.Exists(_instanceManager.SettingsAbsoluteFilepath) == false) {
				_logger.WriteLog(LogLevel.Error, LogSender.Main, $"\"{Path.GetFileName(_instanceManager.SettingsAbsoluteFilepath)}\" not found. Cannot proceed...");
				Environment.Exit(-1);
			}

			// Manage settings
			_instanceManager.OnSettingsChanged();

			// Wait for CTRL + C event
			var tcs = new TaskCompletionSource();
			CancellationTokenSource cts = new CancellationTokenSource();

			

			Console.CancelKeyPress += (sender, e) => {
				_logger.WriteLog(LogLevel.Information, LogSender.Main, "CTRL+C pressed!");
				cts.Cancel();
				tcs.SetResult();
			};

			// Manage WebUI
			var settings = await SettingsService.GetSettings(settingsPath);
			if (SettingsService.IsSettingSet(settings, "WebUI")
				&& SettingsService.IsSettingSet(settings.WebUI, "Enable")
				&& (bool) settings.WebUI.Enable) {
				await WebApp.Main(cts.Token);
			}
			await tcs.Task;

			await _instanceManager.DisposeAsync();
			_logger.WriteLog(LogLevel.Information, LogSender.Main, "Goodbye!");
		}

		// Self-relaunch on crash: replaces the old separate TBot.Watchdog.exe companion process (removed
		// so no second executable ships alongside TBot.exe). This only catches actual unhandled
		// exceptions - a true hang (frozen but no exception thrown) can only ever be detected by a
		// process other than the one that's frozen, which is exactly what we're choosing not to have.
		// Per-worker hangs (a single worker's HttpClient call stuck forever, the common case seen in
		// production logs) are still caught by the internal WatchdogWorker, which runs inside this same
		// process and restarts just that worker - no second executable needed for that either.
		// Re-ported 2026-08-06 after being lost in the 3.4.6 rebase along with the rest of this file's
		// crash-handling.
		private const int MaxCrashRestartsPerWindow = 5;
		private static readonly TimeSpan CrashRestartWindow = TimeSpan.FromMinutes(10);

		private static void TryRelaunchSelfAfterCrash(string[] originalArgs) {
			try {
				string exeDir = AppContext.BaseDirectory;
				string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
				if (string.IsNullOrEmpty(exePath)) {
					_logger.WriteLog(LogLevel.Warning, LogSender.Main, "Cannot self-relaunch after crash: unable to determine own executable path.");
					return;
				}

				string guardPath = Path.Combine(exeDir, "data", "crash_restarts.txt");
				var recentRestarts = new List<DateTime>();
				try {
					if (File.Exists(guardPath)) {
						foreach (var line in File.ReadAllLines(guardPath)) {
							if (DateTime.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var ts)
								&& DateTime.UtcNow - ts < CrashRestartWindow) {
								recentRestarts.Add(ts);
							}
						}
					}
				} catch { /* corrupt/missing guard file - treat as no recent restarts */ }

				if (recentRestarts.Count >= MaxCrashRestartsPerWindow) {
					_logger.WriteLog(LogLevel.Critical, LogSender.Main, $"Crashed {recentRestarts.Count} times in the last {CrashRestartWindow.TotalMinutes} minutes - giving up on self-relaunch to avoid a crash loop. Manual intervention needed.");
					return;
				}

				recentRestarts.Add(DateTime.UtcNow);
				try {
					File.WriteAllLines(guardPath, recentRestarts.ConvertAll(ts => ts.ToString("O")));
				} catch { /* best-effort guard persistence, don't block the relaunch on it */ }

				// Give the OS a moment to fully release the just-killed ogamed.exe's port (and this
				// process' own WebUI port) before the fresh instance tries to bind them again.
				System.Threading.Thread.Sleep(3000);

				var psi = new System.Diagnostics.ProcessStartInfo {
					FileName = exePath,
					WorkingDirectory = exeDir,
					UseShellExecute = false,
				};
				foreach (var arg in originalArgs) {
					psi.ArgumentList.Add(arg);
				}
				System.Diagnostics.Process.Start(psi);
				_logger.WriteLog(LogLevel.Information, LogSender.Main, "Relaunched a fresh instance of myself after the crash.");
			} catch (Exception e) {
				_logger.WriteLog(LogLevel.Warning, LogSender.Main, $"Failed to self-relaunch after crash: {e.Message}");
			}
		}
	}
}
