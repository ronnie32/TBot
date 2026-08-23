using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Sinks;
using TBot.Common.Logging;
using TBot.Common.Logging.Enrichers;
using TBot.Common.Logging.Hooks;
using TBot.Common.Logging.Sinks;
using TBot.Common.Logging.TextFormatters;
using System.Reflection;
using System.Runtime.InteropServices;
using Serilog.Context;
using Serilog.Core;
using Serilog.Filters;
using Microsoft.AspNetCore.SignalR;
using TBot.Common.Logging.Hubs;
using Serilog.Sinks.AspNetCore.SignalR.Extensions;
using System.Globalization;
using Microsoft.AspNetCore.Routing.Template;

namespace TBot.Common.Logging {

	// LoggerService<T> is registered as an open-generic singleton, so each closed type (LoggerService<Program>,
	// LoggerService<TelegramMessenger>, etc) gets its OWN copy of any ` static` field declared inside it - a classic
	// generic-static-per-closed-type gotcha. All the state below is meant to describe ONE global logging pipeline
	// (mirroring Serilog.Log.Logger, which really is process-global), so it lives in this non-generic holder instead.
	public static class LoggerServiceSharedState {
		public static readonly object SyncObject = new object();
		public static string LogPath = "";
		public static readonly LoggingLevelSwitch TelegramLevelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);
		public static bool TelegramAdded = false;
		// Mirrors TBot.Ogame.Infrastructure.Models.LogPrivacy.HideAccountInfo (can't reference it
		// directly here - TBot.Common doesn't depend on TBot.Ogame.Infrastructure). Set from
		// TBotMain alongside the other LogPrivacy flags. When true, log timestamps drop to
		// date-only instead of full HH:mm:ss.fff, so exact activity times aren't exposed in
		// shared logs/screenshots.
		public static bool HideTimestampPrecision = false;
	}

	public class LoggerService<T> : ILoggerService<T> {

		private readonly IHubContext<WebHub, IWebHub> _hub;
		private readonly IServiceProvider _serviceProvider;

		public LoggerService(IHubContext<WebHub, IWebHub> hub,
			IServiceProvider serviceProvider) {
			_hub = hub;
			_serviceProvider = serviceProvider;
		}


		public void WriteLog(LogLevel level, LogSender sender, string message) {
			IDisposable? telegram = null;

			if (LoggerServiceSharedState.TelegramAdded == true && sender != LogSender.OGameD) {
				telegram = LogContext.PushProperty("TelegramEnabled", true);
			}
			using (LogContext.PushProperty("LogSender", sender))
			using (LogContext.PushProperty("LogLevel", level))
			using (LogContext.PushProperty("LogSenderEmoji", EmojiFormatter.GetEmoji(sender.ToString())))
			using (LogContext.PushProperty("LogLevelEmoji", EmojiFormatter.GetEmoji(level.ToString())))
			{
				switch (level) {
					case LogLevel.Trace:
						Log.Logger.Verbose(message);
						break;
					case LogLevel.Debug:
						Log.Logger.Debug(message);
						break;
					case LogLevel.Information:
						Log.Logger.Information(message);
						break;
					case LogLevel.Warning:
						Log.Logger.Warning(message);
						break;
					case LogLevel.Error:
						Log.Logger.Error(message);
						break;
					case LogLevel.Critical:
						Log.Logger.Fatal(message);
						break;
				}
			}

			if (telegram != null) {
				telegram.Dispose();
			}
		}

		private LoggerConfiguration GetDefaultConfiguration() {
			// Full precision by default; date-only when HideTimestampPrecision is set, so shared
			// logs/screenshots don't reveal exact activity times (hour/minute/second).
			string timestampToken = LoggerServiceSharedState.HideTimestampPrecision ? "{Timestamp:yyyy-MM-dd}" : "{Timestamp:HH:mm:ss.fff zzz}";
			string outTemplate = $"[{timestampToken} {{ThreadId}} {{Level:u3}} {{LogSender}}] {{Message:lj}}{{NewLine}}{{Exception}}";
			string fileTemplate = $"{timestampToken} [{{Level:u3}}] {{Message:lj}}{{NewLine}}{{Exception}}";
			long maxFileSize = 1 * 1024 * 1024 * 10;

			var logConfig = new LoggerConfiguration()
				.Enrich.With(new ThreadIdEnricher())
				.Enrich.FromLogContext()
			// Console
				.WriteTo.TBotColoredConsole(
					outputTemplate: outTemplate
				)
				// Log file
				.WriteTo.File(
					path: Path.Combine(LoggerServiceSharedState.LogPath, "TBot.log"),
					outputTemplate: fileTemplate,
					buffered: false,
					shared: true,
					flushToDiskInterval: TimeSpan.FromSeconds(1),
					rollOnFileSizeLimit: true,
					fileSizeLimitBytes: maxFileSize,
					retainedFileCountLimit: 10,
					rollingInterval: RollingInterval.Day)
				// CSV
				.WriteTo.File(
					path: Path.Combine(LoggerServiceSharedState.LogPath, "TBot.csv"),
					buffered: false,
					hooks: new SerilogCSVHeaderHooks(),
					formatter: new SerilogCSVTextFormatter(),
					flushToDiskInterval: TimeSpan.FromSeconds(1),
					rollOnFileSizeLimit: true,
					fileSizeLimitBytes: maxFileSize,
					rollingInterval: RollingInterval.Day)
				.WriteTo.SignalRTBotSink<WebHub, IWebHub>(
					LogEventLevel.Verbose,
					_serviceProvider,
					CultureInfo.InvariantCulture, // can be null
					new string[] { },        // can be null
					new string[] { },        // can be null
					new string[] { },        // can be null
					false) // false is the default value
				.MinimumLevel.Verbose();

			return logConfig;
		}

		public void ConfigureLogging(string logPath) {
			lock (LoggerServiceSharedState.SyncObject) {
				LoggerServiceSharedState.LogPath = logPath;

				var logConfig = GetDefaultConfiguration();

				// Telegram default values
				LoggerServiceSharedState.TelegramLevelSwitch.MinimumLevel = LogEventLevel.Verbose;
				LoggerServiceSharedState.TelegramAdded = false;

				(Log.Logger as IDisposable)?.Dispose();
				Log.Logger = logConfig.CreateLogger();

			}
		}

		public void AddTelegramLogger(string botToken, string chatId) {
			lock (LoggerServiceSharedState.SyncObject) {
				if (LoggerServiceSharedState.TelegramAdded == false) {
					var previousLogger = Log.Logger;

					// Building the Telegram sink can block on a synchronous network call to the
					// Telegram API (bot/chat validation). If that call hangs due to network
					// flakiness, it must not be allowed to freeze the whole instance startup -
					// bound it with a timeout and fall back to logging without Telegram.
				var buildTask = Task.Run(() => {
					var logConfig = GetDefaultConfiguration();
					return logConfig.WriteTo.Logger(
							c => c.Filter.ByIncludingOnly(Matching.WithProperty<bool>("TelegramEnabled", p => p == true))
							.MinimumLevel.ControlledBy(LoggerServiceSharedState.TelegramLevelSwitch)
							.WriteTo.Telegram(botToken: botToken,
								chatId: chatId,
								dateFormat: null,
								outputTemplate: "{LogLevelEmoji:l}{LogSenderEmoji:l} {Message:lj}{NewLine}{Exception}")
						)
						.CreateLogger();
				});

					if (buildTask.Wait(TimeSpan.FromSeconds(15))) {
						Serilog.ILogger newLogger = buildTask.Result;
						(previousLogger as IDisposable)?.Dispose();
						Log.Logger = newLogger;
						LoggerServiceSharedState.TelegramAdded = true;
					} else {
						previousLogger.Warning("Timed out initializing the Telegram logger (Telegram API unreachable/slow) - continuing without Telegram logging");
					}
				}
			}
		}

		public void RemoveTelegramLogger() {
			ConfigureLogging(LoggerServiceSharedState.LogPath);
		}

		public bool IsTelegramLoggerEnabled() {
			return LoggerServiceSharedState.TelegramAdded;
		}

		public void SetTelegramLoggerLogLevel(LogEventLevel logLevel) {
			lock (LoggerServiceSharedState.SyncObject) {
				if (logLevel != LoggerServiceSharedState.TelegramLevelSwitch.MinimumLevel) {
					WriteLog(LogLevel.Warning, LogSender.Main, $"Telegram log level changed from {LoggerServiceSharedState.TelegramLevelSwitch.MinimumLevel.ToString()}" +
						$" into {logLevel.ToString()}");
				}
				LoggerServiceSharedState.TelegramLevelSwitch.MinimumLevel = logLevel;
			}
		}

		public LogEventLevel GetTelegramLoggerLevel() {
			return LoggerServiceSharedState.TelegramLevelSwitch.MinimumLevel;
		}
	}
}
