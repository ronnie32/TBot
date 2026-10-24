using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tbot.Includes;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using TBot.Ogame.Infrastructure;
using Microsoft.Extensions.Logging;
using TBot.Common.Logging;
using Tbot.Exceptions;
using Tbot.Models;
using System.Collections;
using TBot.Ogame.Infrastructure.Enums;
using Tbot.Common.Settings;

namespace Tbot.Services {
	internal class InitInstanceData {
		public string Alias;
		public string SettingsPath;

		public InitInstanceData(string alias, string setPath) {
			Alias = alias;
			SettingsPath = setPath;
		}
	}
	internal class InstanceManager : IInstanceManager {
		public string SettingsAbsoluteFilepath { get; set; } = "";
		static dynamic _mainSettings;

		private readonly IOgameService _ogameService;
		private readonly ILoggerService<InstanceManager> _logger;
		private readonly ILoggerService<TelegramMessenger> _telegramLogger;
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly ICalculationService _helpersService;
		private readonly Func<Task> _delayBetweenInstanceStarts;

		static ITelegramMessenger telegramMessenger = null;
		static SemaphoreSlim instancesSem = new SemaphoreSlim(1, 1);
		static List<TbotInstanceData> instances = new();
		static SettingsFileWatcher settingsWatcher = null;
		public InstanceManager(ILoggerService<InstanceManager> logger,
			IOgameService ogameService,
			IServiceScopeFactory serviceScopeFactory,
			ILoggerService<TelegramMessenger> telegramLogger,
			ICalculationService helpersService)
			: this(logger, ogameService, serviceScopeFactory, telegramLogger, helpersService,
				() => Task.Delay(Random.Shared.Next(30, 60) * 1000)) {
		}

		public InstanceManager(ILoggerService<InstanceManager> logger,
			IOgameService ogameService,
			IServiceScopeFactory serviceScopeFactory,
			ILoggerService<TelegramMessenger> telegramLogger,
			ICalculationService helpersService,
			Func<Task> delayBetweenInstanceStarts) {
			_logger = logger;
			_ogameService = ogameService;
			_scopeFactory = serviceScopeFactory;
			_telegramLogger = telegramLogger;
			_helpersService = helpersService;
			_delayBetweenInstanceStarts = delayBetweenInstanceStarts;
		}

		public async void OnSettingsChanged() {
			await instancesSem.WaitAsync();
			try {
			_logger.WriteLog(LogLevel.Information, LogSender.Main, $"Reading settings \"{SettingsAbsoluteFilepath}\"");

			// Read settings first
			_mainSettings = await SettingsService.GetSettings(SettingsAbsoluteFilepath);

			// Initialize TelegramMessenger if enabled on main settings
			await InitializeTelegramMessenger();

			// Detect settings versioning by checking existence of "Instances" key
			SettingsVersion settingVersion = SettingsVersion.Invalid;

			List<TbotInstanceData> newInstances = new();				// Instances which settings were already present
			// We are going to gather valid Instances to be inited. The execution will be like this:
			//	Await deinit of any removed instances
			//	Async init of good instances
			List<Task<TbotInstanceData>> awaitingInstances = new();		// Instances to be awaited
			List<InitInstanceData> instancesToBeInited = new();     // Key -> Alias, Value -> settings

			if (SettingsService.IsSettingSet(_mainSettings, "Instances") == false) {
				_logger.WriteLog(LogLevel.Information, LogSender.Main, "Single instance settings detected");
				settingVersion = SettingsVersion.AllInOne;

				// Check if FilePath is one of the already inited instances
					if (instances.Any(c => c.IsInitialized && c._botSettingsPath == SettingsAbsoluteFilepath) == true) {
						var foundInstance = instances.First(c => c.IsInitialized && c._botSettingsPath == SettingsAbsoluteFilepath);

					newInstances.Add(foundInstance);
				} else {
					// Start only an instance of TBot
					instancesToBeInited.Add(new InitInstanceData("MAIN", SettingsAbsoluteFilepath));
				}
			} else {
				// In this case we need a json formatted like follows:
				//	"Instances": [
				//		{
				//			"Settings": "<relative to main settings path>",
				//			"Alias": "<Custom name for this instance>"
				//		}
				// ]
				_logger.WriteLog(LogLevel.Information, LogSender.Main, "Multiples instances settings detected");
				settingVersion = SettingsVersion.MultipleInstances;

				// Initialize all the instances of TBot found in main settings
				ICollection json_instances = _mainSettings.Instances;
				List<InitInstanceData> uniqueInstances = new();

				// Extract unique values
				foreach (var instance in _mainSettings.Instances) {
					string cInstanceSettingPath = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(SettingsAbsoluteFilepath), instance.Settings)).FullName;
					string alias = instance.Alias;

					if(uniqueInstances.Any(c => c.SettingsPath == cInstanceSettingPath) == false) {
						uniqueInstances.Add(new InitInstanceData(alias, cInstanceSettingPath));
					}
				}

				_logger.WriteLog(LogLevel.Information, LogSender.Main, $"Read {json_instances.Count} instances. Unique instaces #{uniqueInstances.Count}");

				// Check if they are already inited or must be inited
				foreach (var instance in uniqueInstances) {
					string cInstanceSettingPath = instance.SettingsPath;
					string alias = instance.Alias;

					// Check if already initialized. if that so, update alias and keep going
						if (instances.Any(c => c.IsInitialized && c._botSettingsPath == cInstanceSettingPath) == true) {
						_logger.WriteLog(LogLevel.Information, LogSender.Main, $"Instance \"{alias}\" \"{cInstanceSettingPath}\" already inited.");
							var foundInstance = instances.First(c => c.IsInitialized && c._botSettingsPath == cInstanceSettingPath);
						foundInstance._alias = alias;

						newInstances.Add(foundInstance);
					} else {
						_logger.WriteLog(LogLevel.Information, LogSender.Main, $"Enqueueing initialization of instance \"{alias}\" \"{cInstanceSettingPath}\"");
						instancesToBeInited.Add(instance);
					}
				}
			}

			// Deinitialize instances that are no more valid (not present in newInstances)
				var instancesToRemove = instances.Where(deInstance =>
					!newInstances.Any(c => string.Compare(c._botSettingsPath, deInstance._botSettingsPath) == 0)).ToList();
				await DeinitializeInstances(instancesToRemove);

			// Now Async initialize "validated" instances
				try {
			foreach (var instanceToBeInited in instancesToBeInited) {
				string cInstanceSettingPath = instanceToBeInited.SettingsPath;
				string alias = instanceToBeInited.Alias;
				_logger.WriteLog(LogLevel.Information, LogSender.Main, $"Asynchronously initializing instance \"{alias}\" \"{cInstanceSettingPath}\"");
				awaitingInstances.Add(StartTBotMain(cInstanceSettingPath, alias));
						await _delayBetweenInstanceStarts();
			}
				} finally {
					// Every started task must transfer ownership, even if another
					// startup or the stagger delay fails.
			foreach (Task<TbotInstanceData> awaitingInstance in awaitingInstances) {
				try {
					TbotInstanceData uniqueInstance = await awaitingInstance;
							if (uniqueInstance != null)
								instances.Add(uniqueInstance);
						} catch (Exception) {
							// StartTBotMain already logged the failure and disposed
							// its scope. Continue observing the remaining results.
					continue;
				}
			}
				}

				_logger.WriteLog(LogLevel.Information, LogSender.Main, $"Instances stats: Initialized {instances.Count(instance => instance.IsInitialized)} - Deinitialized {instancesToRemove.Count}");

			// Initialize settingsWatcher 
			if (settingsWatcher == null)
				settingsWatcher = new SettingsFileWatcher(OnSettingsChanged, SettingsAbsoluteFilepath);

			} catch (Exception exception) {
				_logger.WriteLog(LogLevel.Error, LogSender.Main, $"Error reloading settings: {exception.Message}");
			} finally {
			instancesSem.Release();
			}
		}

		public async ValueTask DisposeAsync() {
			await instancesSem.WaitAsync();
			try {
				await DeinitializeInstances(instances.ToList());
			} finally {
				instancesSem.Release();
			}
		}

		private async Task DeinitializeInstances(IEnumerable<TbotInstanceData> selectedInstances) {
			List<Exception> failures = new();
			foreach (var instance in selectedInstances) {
				try {
				_logger.WriteLog(LogLevel.Information, LogSender.Main, $"Deinitializing instance \"{instance._alias}\" \"{instance._botSettingsPath}\"");
					await instance.Deinitialize();
					instances.Remove(instance);
				} catch (Exception exception) {
					failures.Add(exception);
			}
			}
			if (failures.Count > 0)
				throw new AggregateException("One or more instances could not be deinitialized.", failures);
		}

		private async Task InitializeTelegramMessenger() {
			if (
				SettingsService.IsSettingSet(_mainSettings, "TelegramMessenger") &&
				SettingsService.IsSettingSet(_mainSettings.TelegramMessenger, "Active") &&
				(bool) _mainSettings.TelegramMessenger.Active
			) {
				if (telegramMessenger == null) {
					_logger.WriteLog(LogLevel.Information, LogSender.Main, "Activating Telegram Messenger");
					var logEnabled = SettingsService.IsSettingSet(_mainSettings.TelegramMessenger, "Logging") && (bool) _mainSettings.TelegramMessenger.Logging;

					telegramMessenger = new TelegramMessenger(_telegramLogger, _helpersService,
						(string) _mainSettings.TelegramMessenger.API, (string) _mainSettings.TelegramMessenger.ChatId, logEnabled);
					await telegramMessenger.TelegramBot();
				}

				// Check autoping
				if (
					SettingsService.IsSettingSet(_mainSettings.TelegramMessenger, "TelegramAutoPing") &&
					SettingsService.IsSettingSet(_mainSettings.TelegramMessenger.TelegramAutoPing, "Active") &&
					SettingsService.IsSettingSet(_mainSettings.TelegramMessenger.TelegramAutoPing, "EveryHours") &&
					(bool) _mainSettings.TelegramMessenger.TelegramAutoPing.Active
				) {
					_logger.WriteLog(LogLevel.Information, LogSender.Main, "Telegram Messenger AutoPing is enabled!");
					long everyHours = (long) _mainSettings.TelegramMessenger.TelegramAutoPing.EveryHours;
					if (everyHours == 0) {
						_logger.WriteLog(LogLevel.Information, LogSender.Main, "Telegram Messenger AutoPing EveryHours is 0. Setting to 1 hour.");
						everyHours = 1;
					}

					telegramMessenger.StartAutoPing(everyHours);
				} else {
					_logger.WriteLog(LogLevel.Information, LogSender.Main, "Telegram Messenger AutoPing disabled.");
				}
			} else {
				_logger.WriteLog(LogLevel.Information, LogSender.Main, "Telegram Messenger disabled");

				if (telegramMessenger != null) {
					await telegramMessenger.TelegramBotDisable();
					telegramMessenger = null;
				}
			}
		}


		private async Task<TbotInstanceData> StartTBotMain(string settingsPath, string alias) {
			_logger.WriteLog(LogLevel.Information, LogSender.Main, $"Initializing instance \"{alias}\" \"{settingsPath}\"");

			TbotInstanceData ownedInstance = null;
			try {
				if (File.Exists(settingsPath) == false) {
					_logger.WriteLog(LogLevel.Warning, LogSender.Main, $"Instance \"{alias}\" cannot be initialized. \"{settingsPath}\" does not exist");
					throw new MissingConfigurationException($"Instance \"{alias}\" cannot be initialized. \"{settingsPath}\" does not exist");
				} else {

					var scope = _scopeFactory.CreateScope();
					ownedInstance = new TbotInstanceData(null, scope, settingsPath, alias) { IsInitialized = false };
					var tBotInstance = scope.ServiceProvider.GetRequiredService<ITBotMain>();
					ownedInstance._botMain = tBotInstance;

					await tBotInstance.Init(settingsPath, alias, telegramMessenger);	// This may throw

					_logger.WriteLog(LogLevel.Information, LogSender.Main, $"Instance \"{alias}\" initialized successfully!");
					// Add a OnError callback so we can remove it from our list if an error occurred
					tBotInstance.OnError += TBotInstance_OnError;

					ownedInstance.IsInitialized = true;
					return ownedInstance;
				}
			} catch (Exception e) {
				_logger.WriteLog(LogLevel.Error, LogSender.Main, $"Error initializing instance \"{alias}\": {e.Message}");
				if (ownedInstance != null) {
					try {
						await ownedInstance.Deinitialize();
					} catch (Exception cleanupException) {
						_logger.WriteLog(LogLevel.Error, LogSender.Main, $"Error cleaning up instance \"{alias}\": {cleanupException.Message}. Ownership retained; a replacement is blocked.");
						return ownedInstance;
					}
				}
				throw;
			}
		}

		private async void TBotInstance_OnError(object sender, EventArgs e) {
			await instancesSem.WaitAsync();
			try {
				TbotInstanceData fInstance = instances.FirstOrDefault(c => ReferenceEquals(c._botMain, sender));
				if (fInstance == null)
					return;
				_logger.WriteLog(LogLevel.Error, LogSender.Main, $"Removing TBot instance \"{fInstance._alias}\"");
				await fInstance.Deinitialize();
				instances.Remove(fInstance);
			} catch (Exception exception) {
				_logger.WriteLog(LogLevel.Error, LogSender.Main, $"Error removing TBot instance: {exception.Message}");
			} finally {
				instancesSem.Release();
			}
		}
	}
}
