using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tbot.Common.Settings;
using Tbot.Includes;
using Tbot.Services;
using Tbot.Workers;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Xunit;

namespace TBot.Tests {
	public class AutoFarmReportProcessingTests {
		[Fact]
		public async Task AutoFarmReadsPagesUntilItsFirstKnownBoundaryAndNeverReadsOlderPages() {
			var alias = $"report-processing-pagination-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var externalCoordinate = new Coordinate(2, 275, 8, Celestials.Planet);
			var ownedCoordinate = new Coordinate(2, 275, 9, Celestials.Planet);
			var knownCoordinate = new Coordinate(2, 275, 10, Celestials.Planet);
			var externalReport = new EspionageReport {
				ID = 990001,
				Coordinate = externalCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = false
			};
			var ownedReport = new EspionageReport {
				ID = 990002,
				Coordinate = ownedCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 5000000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var knownReport = new EspionageReport {
				ID = 990003,
				Coordinate = knownCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = true
			};
			var olderReport = new EspionageReport {
				ID = 990004,
				Coordinate = new Coordinate(2, 275, 11, Celestials.Planet),
				Date = DateTime.UtcNow,
				IsInactive = true
			};
			var reports = new Dictionary<int, EspionageReport> {
				[externalReport.ID] = externalReport,
				[ownedReport.ID] = ownedReport,
				[knownReport.ID] = knownReport,
				[olderReport.ID] = olderReport
			};
			var pages = new Dictionary<int, List<EspionageReportSummary>> {
				[1] = new() {
					new EspionageReportSummary { ID = externalReport.ID, Type = EspionageReportType.Report, Target = externalCoordinate }
				},
				[2] = new() {
					new EspionageReportSummary { ID = ownedReport.ID, Type = EspionageReportType.Report, Target = ownedCoordinate }
				},
				[3] = new() {
					new EspionageReportSummary { ID = knownReport.ID, Type = EspionageReportType.Report, Target = knownCoordinate },
					new EspionageReportSummary { ID = olderReport.ID, Type = EspionageReportType.Report, Target = olderReport.Coordinate }
				}
			};
			var pageCalls = new List<int>();
			var deletedReports = new List<int>();
			var userData = CreateUserData();
			userData.farmTargets.Add(new FarmTarget(
				new Planet { Name = "Owned target", Coordinate = ownedCoordinate, Inactive = true },
				FarmState.ProbesSent) {
				ProbeFleetId = 7001,
				ProbeSentAtUtc = DateTime.UtcNow
			});
			userData.farmTargets.Add(new FarmTarget(
				new Planet { Name = "Known target", Coordinate = knownCoordinate, Inactive = true },
				FarmState.AttackSent,
				knownReport) {
				ConsumedReportId = knownReport.ID
			});
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":false,\"KeepReportFor\":0,\"MinimumResources\":0,\"PreferedResource\":\"\",\"CargoType\":\"LargeCargo\",\"Blacklist\":{\"Active\":false}}}",
				new List<string> { "{}" });
			var parent = CreateParent(alias, settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => {
				if (method.Name == nameof(IOgameService.GetEspionageReportsPage)) {
					var page = (int)args[0];
					pageCalls.Add(page);
					if (page > 3)
						throw new InvalidOperationException("A page older than the known boundary was requested.");
					return Task.FromResult(pages.TryGetValue(page, out var pageReports)
						? pageReports
						: new List<EspionageReportSummary>());
				}
				if (method.Name == nameof(IOgameService.GetEspionageReport))
					return Task.FromResult(reports[(int)args[0]]);
				if (method.Name == nameof(IOgameService.DeleteReport)) {
					deletedReports.Add((int)args[0]);
					return Task.CompletedTask;
				}
				return Proxy<IOgameService>.Default(method.ReturnType);
			});

			try {
				var worker = new AutoFarmWorker(parent, ogame, CreateScheduler(), CreateCalculation(), CreateBridge());
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>)processReports.Invoke(worker, null);
				Assert.True(await task);

				Assert.Equal(new[] { 1, 2, 3 }, pageCalls);
				Assert.Contains(ownedReport.ID, deletedReports);
				Assert.Contains(knownReport.ID, deletedReports);
				Assert.DoesNotContain(externalReport.ID, deletedReports);
				Assert.DoesNotContain(olderReport.ID, deletedReports);
				Assert.Equal(FarmState.AttackPending, userData.farmTargets.Single(target => target.HasCoords(ownedCoordinate)).State);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmReadsOnlyTheFirstEspionagePageAtTheProcessingBoundary() {
			var alias = $"report-page-boundary-{Guid.NewGuid():N}";
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":false,\"KeepReportFor\":0}}",
				new List<string> { "{}" });
			var pageCalls = 0;
			var fullListCalls = 0;
			var parent = CreateParent(alias, settings, CreateUserData());
			var ogame = Proxy<IOgameService>.Create((method, _) => {
				if (method.Name == nameof(IOgameService.GetEspionageReportsPage)) {
					pageCalls++;
					return Task.FromResult(new List<EspionageReportSummary>());
				}
				if (method.Name == nameof(IOgameService.GetEspionageReports)) {
					fullListCalls++;
					return Task.FromResult(new List<EspionageReportSummary>());
				}
				return Proxy<IOgameService>.Default(method.ReturnType);
			});

			try {
				var worker = new AutoFarmWorker(parent, ogame, CreateScheduler(), CreateCalculation(), CreateBridge());
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>)processReports.Invoke(worker, null);
				Assert.True(await task);
				Assert.Equal(1, pageCalls);
				Assert.Equal(0, fullListCalls);
			} finally {
				var databasePath = Path.Combine(AppContext.BaseDirectory, "data", $"autofarm_{alias}.db");
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmKeepsDefenceReportsAndProcessesFollowingReportsOnlyOnce() {
			var alias = $"report-processing-defense-retention-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var defenseCoordinate = new Coordinate(2, 275, 20, Celestials.Planet);
			var defencelessCoordinate = new Coordinate(2, 275, 21, Celestials.Planet);
			var defenseReport = new EspionageReport {
				ID = 991001,
				Coordinate = defenseCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 1000000,
				HasFleetInformation = true,
				HasDefensesInformation = true,
				RocketLauncher = 1
			};
			var defencelessReport = new EspionageReport {
				ID = 991002,
				Coordinate = defencelessCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 1000000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var reports = new Dictionary<int, EspionageReport> {
				[defenseReport.ID] = defenseReport,
				[defencelessReport.ID] = defencelessReport
			};
			var summaries = new List<EspionageReportSummary> {
				new EspionageReportSummary { ID = defenseReport.ID, Type = EspionageReportType.Report, Target = defenseCoordinate },
				new EspionageReportSummary { ID = defencelessReport.ID, Type = EspionageReportType.Report, Target = defencelessCoordinate }
			};
			var deletedReports = new List<int>();
			var readReports = new List<int>();
			var reportPagePass = 0;
			var userData = CreateUserData();
			userData.farmTargets.Add(new FarmTarget(
				new Planet { Name = "Defended target", Inactive = true, Coordinate = defenseCoordinate },
				FarmState.ProbesSent) {
				ProbeFleetId = 991101,
				ProbeSentAtUtc = DateTime.UtcNow
			});
			userData.farmTargets.Add(new FarmTarget(
				new Planet { Name = "Defenceless target", Inactive = true, Coordinate = defencelessCoordinate },
				FarmState.ProbesSent) {
				ProbeFleetId = 991102,
				ProbeSentAtUtc = DateTime.UtcNow
			});
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"KeepReportsWithDefense\":true,\"ProcessAllReports\":false,\"KeepReportFor\":0,\"MinimumResources\":0,\"PreferedResource\":\"\",\"CargoType\":\"LargeCargo\",\"Blacklist\":{\"Active\":false}}}",
				new List<string> { "{}" });
			var parent = CreateParent(alias, settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => {
				if (method.Name == nameof(IOgameService.GetEspionageReportsPage)) {
					var page = (int)args[0];
					if (page == 1) {
						reportPagePass++;
						return Task.FromResult(reportPagePass == 1
							? summaries
							: new List<EspionageReportSummary> {
								new EspionageReportSummary { ID = defenseReport.ID, Type = EspionageReportType.Report, Target = defenseCoordinate }
							});
					}
					return Task.FromResult(new List<EspionageReportSummary>());
				}
				if (method.Name == nameof(IOgameService.GetEspionageReport)) {
					var reportId = (int)args[0];
					readReports.Add(reportId);
					return Task.FromResult(reports[reportId]);
				}
				if (method.Name == nameof(IOgameService.DeleteReport)) {
					deletedReports.Add((int)args[0]);
					return Task.CompletedTask;
				}
				return Proxy<IOgameService>.Default(method.ReturnType);
			});

			try {
				var worker = new AutoFarmWorker(parent, ogame, CreateScheduler(), CreateCalculation(), CreateBridge());
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var firstPass = (Task<bool>)processReports.Invoke(worker, null);
				Assert.True(await firstPass);
				var secondPass = (Task<bool>)processReports.Invoke(worker, null);
				Assert.True(await secondPass);

				Assert.Equal(new[] { defenseReport.ID, defencelessReport.ID }, readReports);
				Assert.DoesNotContain(defenseReport.ID, deletedReports);
				Assert.Contains(defencelessReport.ID, deletedReports);
				Assert.Equal(FarmState.NotSuitable, userData.farmTargets.Single(target => target.HasCoords(defenseCoordinate)).State);
				Assert.Equal(FarmState.AttackPending, userData.farmTargets.Single(target => target.HasCoords(defencelessCoordinate)).State);
				using var store = new AutoFarmStateStore($"autofarm_{alias}.db", dataFolder);
				Assert.Equal(AutoFarmReportPolicy.ManualReviewDefenseOutcome, store.GetProcessedReportOutcome(defenseReport.ID));
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmKeepsTheCursorWhenARealSystemScanFails() {
			var alias = $"scan-failure-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var blacklistPath = Path.Combine(dataFolder, $"autofarm_blacklist_{alias}.json");
			var successfulPath = Path.Combine(dataFolder, $"autofarm_successful_{alias}.json");
			var userData = new UserData {
				celestials = new List<Celestial> {
					new Planet {
						ID = 1,
						Coordinate = new Coordinate(1, 1, 8, Celestials.Planet),
						Ships = new Ships(espionageProbe: 100)
					}
				},
				farmTargets = new List<FarmTarget>(),
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 1, InUse = 0 },
				serverData = new ServerData { Galaxies = 6, Systems = 499 },
				userInfo = new UserInfo(),
				researches = new Researches()
			};
			var settings = SettingsService.GetMergedSettingsFromContents(
				"""{"General":{"SlotsToLeaveFree":0},"AutoFarm":{"Active":true,"ScanRange":[{"Galaxy":1,"StartSystem":1,"EndSystem":1}],"Origin":[],"Exclude":[],"MaxSlots":1,"SlotsToLeaveFree":0,"TargetsProbedBeforeAttack":0,"KeepReportFor":0,"CheckIntervalMin":0,"CheckIntervalMax":0,"StopAfterFullScan":false}}""",
				new List<string> { "{}" });
			var logs = new List<string>();
			var parent = Proxy<ITBotMain>.Create((method, args) => {
				if (method.Name == "get_InstanceAlias")
					return alias;
				if (method.Name == "get_InstanceSettings")
					return settings;
				if (method.Name == "get_UserData")
					return userData;
				if (method.Name == "log") {
					logs.Add(args[2]?.ToString() ?? string.Empty);
					return null;
				}
				return Proxy<ITBotMain>.Default(method.ReturnType);
			});
			var ogame = Proxy<IOgameService>.Create((method, _) => method.Name switch {
				nameof(IOgameService.GetGalaxyInfo) => throw new InvalidOperationException("simulated incomplete galaxy response"),
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult(new List<EspionageReportSummary>()),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, _) => method.Name switch {
				nameof(IFleetScheduler.UpdateFleets) => Task.FromResult(new List<Fleet>()),
				"get_LastFleetSnapshotWasFresh" => true,
				_ => Proxy<IFleetScheduler>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				nameof(ITBotOgamedBridge.UpdateSlots) => Task.FromResult(new Slots { Total = 1, InUse = 0 }),
				nameof(ITBotOgamedBridge.CheckCelestials) => Task.FromResult(true),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
			var calculation = Proxy<ICalculationService>.Create((method, _) => Proxy<ICalculationService>.Default(method.ReturnType));

			try {
				var worker = new AutoFarmWorker(parent, ogame, scheduler, calculation, bridge);
				var execute = typeof(AutoFarmWorker).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(execute);
				await (Task)execute.Invoke(worker, null);

				using var store = new AutoFarmStateStore($"autofarm_{alias}.db", dataFolder);
				var cursor = store.LoadScanCursor();
				Assert.True(logs.Any(log => log.Contains("scan failure", StringComparison.OrdinalIgnoreCase)),
					string.Join(Environment.NewLine, logs));
				Assert.NotNull(cursor);
				Assert.Equal(new AutoFarmScanCursor(0, 1, 1), cursor);
				Assert.False(cursor.ReportPhasePending);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
				if (File.Exists(blacklistPath))
					File.Delete(blacklistPath);
				if (File.Exists(successfulPath))
					File.Delete(successfulPath);
			}
		}

		[Fact]
		public async Task AutoFarmConsumesAProcessAllReportWhenThePlanetIsGone() {
			var alias = $"report-processing-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var coordinate = new Coordinate(2, 275, 6, Celestials.Planet);
			var report = new EspionageReport {
				ID = 991001,
				Coordinate = coordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 500000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var deletedReports = new List<int>();
			var summary = new EspionageReportSummary {
				ID = report.ID,
				Type = EspionageReportType.Report,
				Target = coordinate
			};

			var userData = new UserData {
				celestials = new List<Celestial>(),
				farmTargets = new List<FarmTarget>(),
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 10, InUse = 0 },
				serverData = new ServerData(),
				userInfo = new UserInfo(),
				researches = new Researches()
			};

			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":true,\"KeepReportFor\":0,\"MinimumResources\":0,\"CargoType\":\"LargeCargo\",\"Blacklist\":{\"Active\":false,\"ProcessAllReports\":false}}}",
				new List<string> { "{}" });

			var parent = Proxy<ITBotMain>.Create((method, _) => method.Name switch {
				"get_InstanceAlias" => alias,
				"get_InstanceSettings" => settings,
				"get_UserData" => userData,
				_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult((int)args[0] == 1 ? new List<EspionageReportSummary> { summary } : new List<EspionageReportSummary>()),
				nameof(IOgameService.GetEspionageReport) => Task.FromResult(report),
				nameof(IOgameService.GetGalaxyInfo) => Task.FromResult(new GalaxyInfo { Planets = new List<Planet>() }),
				nameof(IOgameService.DeleteReport) => DeleteReport(deletedReports, (int)args[0]),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, _) => Proxy<IFleetScheduler>.Default(method.ReturnType));
			var calculation = Proxy<ICalculationService>.Create((method, _) => Proxy<ICalculationService>.Default(method.ReturnType));

			try {
				var worker = new AutoFarmWorker(parent, ogame, scheduler, calculation, bridge);
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>)processReports.Invoke(worker, null);
				Assert.True(await task);

				Assert.Contains(report.ID, deletedReports);
				Assert.True(userData.farmTargets.Count == 0);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmConsumesAProcessAllReportWhenTheTargetIsBlacklisted() {
			var alias = $"report-processing-blacklist-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var blacklistPath = Path.Combine(dataFolder, $"autofarm_blacklist_{alias}.json");
			var coordinate = new Coordinate(2, 275, 7, Celestials.Planet);
			var report = new EspionageReport {
				ID = 991002,
				Coordinate = coordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 500000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var deletedReports = new List<int>();
			var summary = new EspionageReportSummary {
				ID = report.ID,
				Type = EspionageReportType.Report,
				Target = coordinate
			};

			var userData = CreateUserData();
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":true,\"KeepReportFor\":0,\"MinimumResources\":0,\"CargoType\":\"LargeCargo\",\"Blacklist\":{\"Active\":true,\"ProcessAllReports\":false}}}",
				new List<string> { "{}" });

			try {
				var blacklist = new AutoFarmBlacklist($"autofarm_blacklist_{alias}.json");
				blacklist.AddTarget(coordinate, BlacklistReason.ManuallyAdded, 24);

				var parent = CreateParent(alias, settings, userData);
				var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult((int)args[0] == 1 ? new List<EspionageReportSummary> { summary } : new List<EspionageReportSummary>()),
					nameof(IOgameService.GetEspionageReport) => Task.FromResult(report),
					nameof(IOgameService.GetGalaxyInfo) => Task.FromResult(new GalaxyInfo {
						Planets = new List<Planet> {
							new Planet { Coordinate = coordinate, Inactive = true }
						}
					}),
					nameof(IOgameService.DeleteReport) => DeleteReport(deletedReports, (int) args[0]),
					_ => Proxy<IOgameService>.Default(method.ReturnType)
				});
				var bridge = CreateBridge();
				var scheduler = Proxy<IFleetScheduler>.Create((method, _) => Proxy<IFleetScheduler>.Default(method.ReturnType));
				var calculation = Proxy<ICalculationService>.Create((method, _) => Proxy<ICalculationService>.Default(method.ReturnType));

				var worker = new AutoFarmWorker(parent, ogame, scheduler, calculation, bridge);
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>) processReports.Invoke(worker, null);
				Assert.True(await task);

				Assert.Contains(report.ID, deletedReports);
				Assert.Empty(userData.farmTargets);
				using var store = new AutoFarmStateStore($"autofarm_{alias}.db", dataFolder);
				Assert.Equal("Blacklisted", store.GetProcessedReportOutcome(report.ID));
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
				if (File.Exists(blacklistPath))
					File.Delete(blacklistPath);
			}
		}

		[Fact]
		public async Task AutoFarmKeepsAnUnresolvedReportRetryableWhenGalaxyLookupFails() {
			var alias = $"report-processing-error-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var coordinate = new Coordinate(2, 275, 8, Celestials.Planet);
			var report = new EspionageReport {
				ID = 991003,
				Coordinate = coordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 5000000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var deletedReports = new List<int>();
			var summary = new EspionageReportSummary {
				ID = report.ID,
				Type = EspionageReportType.Report,
				Target = coordinate
			};

			var userData = CreateUserData();
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":true,\"KeepReportFor\":0,\"MinimumResources\":0,\"CargoType\":\"LargeCargo\"}}",
				new List<string> { "{}" });
			var parent = CreateParent(alias, settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult((int)args[0] == 1 ? new List<EspionageReportSummary> { summary } : new List<EspionageReportSummary>()),
				nameof(IOgameService.GetEspionageReport) => Task.FromResult(report),
				nameof(IOgameService.GetGalaxyInfo) => throw new InvalidOperationException("temporary galaxy lookup failure"),
				nameof(IOgameService.DeleteReport) => DeleteReport(deletedReports, (int) args[0]),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = CreateBridge();
			var scheduler = Proxy<IFleetScheduler>.Create((method, _) => Proxy<IFleetScheduler>.Default(method.ReturnType));
			var calculation = Proxy<ICalculationService>.Create((method, _) => Proxy<ICalculationService>.Default(method.ReturnType));

			try {
				var worker = new AutoFarmWorker(parent, ogame, scheduler, calculation, bridge);
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>) processReports.Invoke(worker, null);
				Assert.False(await task);

				Assert.Empty(deletedReports);
				using var store = new AutoFarmStateStore($"autofarm_{alias}.db", dataFolder);
				Assert.False(store.HasProcessedReport(report.ID));
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmPreservesAnExternalReportForAKnownTargetWhenProcessAllReportsIsDisabled() {
			var alias = $"report-processing-external-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var coordinate = new Coordinate(2, 275, 6, Celestials.Planet);
			var storedReport = new EspionageReport {
				ID = 991004,
				Coordinate = coordinate,
				Date = DateTime.UtcNow.AddHours(-2),
				IsInactive = true,
				Metal = 100000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var externalReport = new EspionageReport {
				ID = 991005,
				Coordinate = coordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 5000000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var deletedReports = new List<int>();
			var userData = CreateUserData();
			userData.farmTargets.Add(new FarmTarget(
				new Planet { Coordinate = coordinate, Inactive = true },
				FarmState.NotSuitable,
				storedReport));
			var summary = new EspionageReportSummary {
				ID = externalReport.ID,
				Type = EspionageReportType.Report,
				Target = coordinate
			};
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":false,\"KeepReportFor\":0,\"MinimumResources\":0,\"CargoType\":\"LargeCargo\",\"Blacklist\":{\"Active\":false}}}",
				new List<string> { "{}" });

			var parent = CreateParent(alias, settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult((int)args[0] == 1 ? new List<EspionageReportSummary> { summary } : new List<EspionageReportSummary>()),
				nameof(IOgameService.GetEspionageReport) => Task.FromResult(externalReport),
				nameof(IOgameService.DeleteReport) => DeleteReport(deletedReports, (int) args[0]),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = CreateBridge();
			var scheduler = Proxy<IFleetScheduler>.Create((method, _) => Proxy<IFleetScheduler>.Default(method.ReturnType));
			var calculation = Proxy<ICalculationService>.Create((method, _) => Proxy<ICalculationService>.Default(method.ReturnType));

			try {
				var worker = new AutoFarmWorker(parent, ogame, scheduler, calculation, bridge);
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>) processReports.Invoke(worker, null);
				Assert.True(await task);

				Assert.Empty(deletedReports);
				var target = Assert.Single(userData.farmTargets);
				Assert.Equal(FarmState.NotSuitable, target.State);
				Assert.Equal(storedReport.ID, target.Report.ID);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmDoesNotTreatBlacklistProcessAllReportsAsGlobalFarmScope() {
			var alias = $"report-processing-blacklist-scope-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var coordinate = new Coordinate(2, 275, 9, Celestials.Planet);
			var report = new EspionageReport {
				ID = 991008,
				Coordinate = coordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 5000000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var deletedReports = new List<int>();
			var userData = CreateUserData();
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":false,\"MinimumResources\":0,\"CargoType\":\"LargeCargo\",\"Blacklist\":{\"Active\":false,\"ProcessAllReports\":true}}}",
				new List<string> { "{}" });
			var summary = new EspionageReportSummary {
				ID = report.ID,
				Type = EspionageReportType.Report,
				Target = coordinate
			};

			var parent = CreateParent(alias, settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult((int)args[0] == 1 ? new List<EspionageReportSummary> { summary } : new List<EspionageReportSummary>()),
				nameof(IOgameService.GetEspionageReport) => Task.FromResult(report),
				nameof(IOgameService.GetGalaxyInfo) => Task.FromResult(new GalaxyInfo {
					Planets = new List<Planet> { new Planet { Coordinate = coordinate, Inactive = true } }
				}),
				nameof(IOgameService.DeleteReport) => DeleteReport(deletedReports, (int) args[0]),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});

			try {
				var worker = new AutoFarmWorker(parent, ogame, CreateScheduler(), CreateCalculation(), CreateBridge());
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>) processReports.Invoke(worker, null);
				Assert.True(await task);

				Assert.Empty(deletedReports);
				Assert.Empty(userData.farmTargets);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmProcessesItsOwnedReportBehindAnExternalReport() {
			var alias = $"report-processing-behind-external-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var ownedCoordinate = new Coordinate(2, 275, 8, Celestials.Planet);
			var externalCoordinate = new Coordinate(2, 275, 9, Celestials.Planet);
			var ownedReport = new EspionageReport {
				ID = 991006,
				Coordinate = ownedCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 5000000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var externalReport = new EspionageReport {
				ID = 991007,
				Coordinate = externalCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 5000000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var reports = new Dictionary<int, EspionageReport> {
				[ownedReport.ID] = ownedReport,
				[externalReport.ID] = externalReport
			};
			var deletedReports = new List<int>();
			var userData = CreateUserData();
			userData.farmTargets.Add(new FarmTarget(
				new Planet { Name = "Owned target", Coordinate = ownedCoordinate, Inactive = true },
				FarmState.ProbesSent) {
				ProbeFleetId = 7001,
				ProbeSentAtUtc = DateTime.UtcNow
			});
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":false,\"KeepReportFor\":0,\"MinimumResources\":0,\"PreferedResource\":\"\",\"CargoType\":\"LargeCargo\",\"Blacklist\":{\"Active\":false}}}",
				new List<string> { "{}" });
			var parent = CreateParent(alias, settings, userData);
			var summaries = new List<EspionageReportSummary> {
				new EspionageReportSummary { ID = externalReport.ID, Type = EspionageReportType.Report, Target = externalCoordinate },
				new EspionageReportSummary { ID = ownedReport.ID, Type = EspionageReportType.Report, Target = ownedCoordinate }
			};
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult((int)args[0] == 1 ? summaries : new List<EspionageReportSummary>()),
				nameof(IOgameService.GetEspionageReport) => Task.FromResult(reports[(int)args[0]]),
				nameof(IOgameService.DeleteReport) => DeleteReport(deletedReports, (int)args[0]),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = CreateBridge();
			var scheduler = Proxy<IFleetScheduler>.Create((method, _) => Proxy<IFleetScheduler>.Default(method.ReturnType));
			var calculation = Proxy<ICalculationService>.Create((method, _) => Proxy<ICalculationService>.Default(method.ReturnType));

			try {
				var worker = new AutoFarmWorker(parent, ogame, scheduler, calculation, bridge);
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>)processReports.Invoke(worker, null);
				Assert.True(await task);

				Assert.DoesNotContain(externalReport.ID, deletedReports);
				Assert.Contains(ownedReport.ID, deletedReports);
				Assert.Equal(FarmState.AttackPending, Assert.Single(userData.farmTargets).State);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmPreservesTheReportOrderReturnedByOGame() {
			var alias = $"report-processing-order-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var ownedCoordinate = new Coordinate(2, 275, 8, Celestials.Planet);
			var externalCoordinate = new Coordinate(2, 275, 9, Celestials.Planet);
			var externalReport = new EspionageReport {
				ID = 100,
				Coordinate = externalCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = false,
				Metal = 5000000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var ownedReport = new EspionageReport {
				ID = 200,
				Coordinate = ownedCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 5000000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var reports = new Dictionary<int, EspionageReport> {
				[externalReport.ID] = externalReport,
				[ownedReport.ID] = ownedReport
			};
			var reportReadOrder = new List<int>();
			var deletedReports = new List<int>();
			var userData = CreateUserData();
			userData.farmTargets.Add(new FarmTarget(
				new Planet { Name = "Owned target", Coordinate = ownedCoordinate, Inactive = true },
				FarmState.ProbesSent) {
				ProbeFleetId = 7001,
				ProbeSentAtUtc = DateTime.UtcNow
			});
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":false,\"KeepReportFor\":0,\"MinimumResources\":0,\"PreferedResource\":\"\",\"CargoType\":\"LargeCargo\",\"Blacklist\":{\"Active\":false}}}",
				new List<string> { "{}" });
			var parent = CreateParent(alias, settings, userData);
			var summaries = new List<EspionageReportSummary> {
				// Deliberately use an ID order different from OGame's returned order.
				// IDs are an identity, not a chronological ordering contract.
				new EspionageReportSummary { ID = externalReport.ID, Type = EspionageReportType.Report, Target = externalCoordinate },
				new EspionageReportSummary { ID = ownedReport.ID, Type = EspionageReportType.Report, Target = ownedCoordinate }
			};
			var ogame = Proxy<IOgameService>.Create((method, args) => method.Name switch {
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult((int)args[0] == 1 ? summaries : new List<EspionageReportSummary>()),
				nameof(IOgameService.GetEspionageReport) => ReadReport(reports, reportReadOrder, (int)args[0]),
				nameof(IOgameService.DeleteReport) => DeleteReport(deletedReports, (int)args[0]),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});

			try {
				var worker = new AutoFarmWorker(parent, ogame, CreateScheduler(), CreateCalculation(), CreateBridge());
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>)processReports.Invoke(worker, null);
				Assert.True(await task);

				Assert.Equal(new[] { externalReport.ID, ownedReport.ID }, reportReadOrder);
				Assert.Contains(ownedReport.ID, deletedReports);
				Assert.DoesNotContain(externalReport.ID, deletedReports);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmStopsAtTheKnownReportWhenCleanupFailsWithoutReadingOlderReports() {
			var alias = $"report-processing-boundary-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var knownCoordinate = new Coordinate(2, 275, 8, Celestials.Planet);
			var olderCoordinate = new Coordinate(2, 275, 9, Celestials.Planet);
			var knownReport = new EspionageReport { ID = 992001, Coordinate = knownCoordinate };
			var olderReport = new EspionageReport { ID = 992000, Coordinate = olderCoordinate };
			var summaries = new List<EspionageReportSummary> {
				new EspionageReportSummary { ID = knownReport.ID, Type = EspionageReportType.Report, Target = knownCoordinate },
				new EspionageReportSummary { ID = olderReport.ID, Type = EspionageReportType.Report, Target = olderCoordinate }
			};
			var userData = CreateUserData();
			userData.farmTargets.Add(new FarmTarget(
				new Planet { Coordinate = knownCoordinate },
				FarmState.AttackSent,
				knownReport) {
				ConsumedReportId = knownReport.ID
			});
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":false,\"KeepReportFor\":0}}",
				new List<string> { "{}" });
			var getReportCalls = new List<int>();
			var parent = CreateParent(alias, settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => {
				if (method.Name == nameof(IOgameService.GetEspionageReportsPage))
					return Task.FromResult(summaries);
				if (method.Name == nameof(IOgameService.GetEspionageReport)) {
					getReportCalls.Add((int) args[0]);
					return Task.FromResult((int) args[0] == knownReport.ID ? knownReport : olderReport);
				}
				if (method.Name == nameof(IOgameService.DeleteReport))
					throw new InvalidOperationException("cleanup failed permanently");
				return Proxy<IOgameService>.Default(method.ReturnType);
			});

			try {
				var worker = new AutoFarmWorker(parent, ogame, CreateScheduler(), CreateCalculation(), CreateBridge());
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>) processReports.Invoke(worker, null);

				Assert.False(await task);
				Assert.Empty(getReportCalls);
				Assert.Equal(FarmState.AttackSent, Assert.Single(userData.farmTargets).State);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmBoundsTransientCleanupRetriesAtTheCurrentReportBoundary() {
			var alias = $"report-processing-transient-boundary-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var knownCoordinate = new Coordinate(2, 275, 12, Celestials.Planet);
			var olderCoordinate = new Coordinate(2, 275, 13, Celestials.Planet);
			var knownReport = new EspionageReport { ID = 992101, Coordinate = knownCoordinate };
			var olderReport = new EspionageReport { ID = 992100, Coordinate = olderCoordinate };
			var summaries = new List<EspionageReportSummary> {
				new EspionageReportSummary { ID = knownReport.ID, Type = EspionageReportType.Report, Target = knownCoordinate },
				new EspionageReportSummary { ID = olderReport.ID, Type = EspionageReportType.Report, Target = olderCoordinate }
			};
			var deleteAttempts = 0;
			var getReportCalls = new List<int>();
			var userData = CreateUserData();
			userData.farmTargets.Add(new FarmTarget(
				new Planet { Coordinate = knownCoordinate },
				FarmState.AttackSent,
				knownReport) {
				ConsumedReportId = knownReport.ID
			});
			var settings = SettingsService.GetMergedSettingsFromContents(
				"{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":false,\"KeepReportFor\":0}}",
				new List<string> { "{}" });
			var parent = CreateParent(alias, settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, args) => {
				if (method.Name == nameof(IOgameService.GetEspionageReportsPage))
					return Task.FromResult(summaries);
				if (method.Name == nameof(IOgameService.GetEspionageReport)) {
					getReportCalls.Add((int)args[0]);
					return Task.FromResult((int)args[0] == knownReport.ID ? knownReport : olderReport);
				}
				if (method.Name == nameof(IOgameService.DeleteReport)) {
					deleteAttempts++;
					throw new InvalidOperationException("HTTP 503 Service Unavailable");
				}
				return Proxy<IOgameService>.Default(method.ReturnType);
			});

			try {
				var worker = new AutoFarmWorker(parent, ogame, CreateScheduler(), CreateCalculation(), CreateBridge());
				var processReports = typeof(AutoFarmWorker).GetMethod(
					"AutoFarmProcessReports",
					BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(processReports);

				var task = (Task<bool>)processReports.Invoke(worker, null);

				Assert.False(await task);
				Assert.Equal(3, deleteAttempts);
				Assert.Empty(getReportCalls);
				Assert.Equal(FarmState.AttackSent, Assert.Single(userData.farmTargets).State);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmDispatchesPendingAttacksAfterACompletedScanWithoutWaitingForTheNextScan() {
			var alias = $"attack-after-scan-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var origin = new Moon {
				ID = 10,
				Name = "Farm origin",
				Coordinate = new Coordinate(1, 1, 8, Celestials.Moon),
				Ships = new Ships(largeCargo: 100),
				Resources = new Resources(deuterium: 100000),
				LFBonuses = new LFBonuses()
			};
			origin.LFBonuses.LfShipBonuses[((int)Buildables.LargeCargo).ToString()] = new LFBonusesShip(
				id: (int)Buildables.LargeCargo,
				cargo: 1);
			var closerOrigin = new Moon {
				ID = 11,
				Name = "Closer origin",
				Coordinate = new Coordinate(1, 1, 9, Celestials.Moon),
				Ships = new Ships(largeCargo: 1),
				Resources = new Resources(deuterium: 100000),
				LFBonuses = new LFBonuses()
			};
			var targetCoordinate = new Coordinate(1, 2, 7, Celestials.Planet);
			var report = new EspionageReport {
				ID = 993001,
				Coordinate = targetCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 100000,
				Crystal = 100000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var target = new FarmTarget(
				new Planet { Name = "Farm target", Inactive = true, Coordinate = targetCoordinate },
				FarmState.AttackPending,
				report);
			var userData = CreateUserData();
			userData.celestials = new List<Celestial> { origin, closerOrigin };
			userData.farmTargets.Add(target);
			userData.autoFarmReportPhasePending = true;
			userData.slots = new Slots { Total = 10, InUse = 0 };
			userData.serverData = new ServerData { Galaxies = 6, Systems = 499, ProbeCargo = 0 };
			userData.researches = new Researches { HyperspaceTechnology = 10 };

			var settings = SettingsService.GetMergedSettingsFromContents(
				"""
{
  "General": {
    "SlotsToLeaveFree": 0,
    "SlotPriorityLevel": { "Brain": 1, "Expeditions": 1, "AutoFarm": 1, "Colonize": 1, "AutoDiscovery": 1, "AutoHarvest": 1 }
  },
  "SleepMode": { "Active": false },
  "Brain": {
    "Active": false,
    "Transports": { "Active": false, "MaxSlots": 0 },
    "AutoMine": { "Active": false }, "AutoResearch": { "Active": false },
    "LifeformAutoMine": { "Active": false }, "LifeformAutoResearch": { "Active": false }
  },
  "Expeditions": { "Active": false },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoDiscovery": { "Active": false, "MaxSlots": 0 },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "AutoFarm": {
    "Active": true, "ExcludeMoons": true, "ScanRange": [], "Exclude": [], "Origin": [],
    "KeepReportFor": 0, "NumProbes": 1, "TargetsProbedBeforeAttack": 100,
    "CargoType": "LargeCargo", "FleetSpeed": 100, "MinCargosToKeep": 0, "MinCargosToSend": 1,
    "CargoSurplusPercentage": 0, "BuildCargos": false, "BuildProbes": false,
    "MinimumResources": 1, "MinimumPlayerRank": 0, "MaxFlightTime": 0, "MinLootFuelRatio": 0,
    "PreferedResource": "", "ProcessAllReports": false, "MaxSlots": 1,
    "SlotsToLeaveFree": 0, "CheckIntervalMin": 1, "CheckIntervalMax": 1,
    "StopAfterFullScan": false, "Blacklist": { "Active": false }
  }
}
""",
				new List<string> { "{}" });
			var sentTargets = new List<Coordinate>();
			var sentOrigins = new List<Coordinate>();
			var fleets = new List<Fleet>();
			var parent = CreateParent(alias, settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, _) => method.Name switch {
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult(new List<EspionageReportSummary>()),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, args) => {
				if (method.Name == nameof(ITBotOgamedBridge.GetDateTime))
					return Task.FromResult(DateTime.UtcNow);
				if (method.Name == nameof(ITBotOgamedBridge.UpdateResearches))
					return Task.FromResult(userData.researches);
				if (method.Name == nameof(ITBotOgamedBridge.UpdateCelestials))
					return Task.FromResult(userData.celestials);
				if (method.Name == nameof(ITBotOgamedBridge.UpdateSlots))
					return Task.FromResult(new Slots { Total = 10, InUse = fleets.Count });
				if (method.Name == nameof(ITBotOgamedBridge.CheckCelestials))
					return Task.FromResult(true);
				if (method.Name == nameof(ITBotOgamedBridge.UpdatePlanet))
					return Task.FromResult((Celestial)args[0]);
				return Proxy<ITBotOgamedBridge>.Default(method.ReturnType);
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, args) => {
				if (method.Name == nameof(IFleetScheduler.UpdateFleets))
					return Task.FromResult(fleets.ToList());
				if (method.Name == "get_LastFleetSnapshotWasFresh")
					return true;
				if (method.Name == nameof(IFleetScheduler.SendValidatedFleet)) {
					sentOrigins.Add(((Celestial)args[0]).Coordinate);
					var destination = (Coordinate)args[2];
					sentTargets.Add(destination);
					fleets.Add(new Fleet {
						ID = 993002,
						Mission = Missions.Attack,
						Origin = ((Celestial)args[0]).Coordinate,
						Destination = destination,
						Ships = (Ships)args[1]
					});
					return Task.FromResult(993002);
				}
				return Proxy<IFleetScheduler>.Default(method.ReturnType);
			});
			var calculation = Proxy<ICalculationService>.Create((method, args) => {
				if (method.Name == nameof(ICalculationService.CalcSlotsPriority))
					return 1;
				if (method.Name == nameof(ICalculationService.CalcDistance))
					return ((Coordinate)args[0]).Position == closerOrigin.Coordinate.Position ? 1 : 2;
				if (method.Name == nameof(ICalculationService.CalcShipNumberForPayload))
					return Convert.ToSingle(args[4]) > 0 ? 1L : 2L;
				if (method.Name == nameof(ICalculationService.CalcFleetCapacity)) {
					var ships = (Ships)args[0];
					var bonuses = (LFBonuses)args[3];
					return ships.GetAmount(Buildables.LargeCargo) *
						(bonuses.GetShipCargoBonus(Buildables.LargeCargo) > 0 ? 200000L : 100000L);
				}
				if (method.Name == nameof(ICalculationService.CalcFleetPrediction))
					return new FleetPrediction { Time = 1, Fuel = 1 };
				if (method.Name == nameof(ICalculationService.GetMissionsInProgress))
					return new List<Fleet>();
				if (method.Name == nameof(ICalculationService.GetValidSpeedsForClass))
					return new List<decimal> { Speeds.HundredPercent };
				return Proxy<ICalculationService>.Default(method.ReturnType);
			});

			try {
				var worker = new AutoFarmWorker(parent, ogame, scheduler, calculation, bridge);
				var execute = typeof(AutoFarmWorker).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(execute);

				await (Task)execute.Invoke(worker, null);

				Assert.Equal(new[] { targetCoordinate }, sentTargets);
				Assert.Equal(new[] { origin.Coordinate }, sentOrigins);
				Assert.Equal(1, fleets.Single(fleet => fleet.Mission == Missions.Attack).Ships.GetAmount(Buildables.LargeCargo));
				Assert.Equal(FarmState.AttackSent, Assert.Single(userData.farmTargets).State);
				Assert.Equal(report.ID, userData.farmTargets[0].ConsumedReportId);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		[Fact]
		public async Task AutoFarmDispatchesFromAMoonWhenEnoughSmallCargoIsAvailable() {
			var alias = $"attack-from-moon-{Guid.NewGuid():N}";
			var dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
			var databasePath = Path.Combine(dataFolder, $"autofarm_{alias}.db");
			var origin = new Planet {
				ID = 12,
				Name = "Moon farm origin",
				Coordinate = new Coordinate(1, 1, 8, Celestials.Planet),
				Ships = new Ships(smallCargo: 100),
				Resources = new Resources(deuterium: 100000),
				LFBonuses = new LFBonuses()
			};
			var targetCoordinate = new Coordinate(1, 2, 7, Celestials.Planet);
			var report = new EspionageReport {
				ID = 993101,
				Coordinate = targetCoordinate,
				Date = DateTime.UtcNow,
				IsInactive = true,
				Metal = 100000,
				HasFleetInformation = true,
				HasDefensesInformation = true
			};
			var target = new FarmTarget(
				new Planet { Name = "Farm target", Inactive = true, Coordinate = targetCoordinate },
				FarmState.AttackPending,
				report);
			var userData = CreateUserData();
			userData.celestials = new List<Celestial> { origin };
			userData.farmTargets.Add(target);
			userData.autoFarmReportPhasePending = true;
			userData.slots = new Slots { Total = 10, InUse = 0 };
			userData.serverData = new ServerData { Galaxies = 6, Systems = 499, ProbeCargo = 0 };
			userData.researches = new Researches { HyperspaceTechnology = 10 };

			var settings = SettingsService.GetMergedSettingsFromContents(
				"""
{
  "General": {
    "SlotsToLeaveFree": 0,
    "SlotPriorityLevel": { "Brain": 1, "Expeditions": 1, "AutoFarm": 1, "Colonize": 1, "AutoDiscovery": 1, "AutoHarvest": 1 }
  },
  "SleepMode": { "Active": false },
  "Brain": {
    "Active": false,
    "Transports": { "Active": false, "MaxSlots": 0 },
    "AutoMine": { "Active": false }, "AutoResearch": { "Active": false },
    "LifeformAutoMine": { "Active": false }, "LifeformAutoResearch": { "Active": false }
  },
  "Expeditions": { "Active": false },
  "AutoColonize": { "Active": false, "IntensiveResearch": { "Active": false, "MaxSlots": 0 } },
  "AutoDiscovery": { "Active": false, "MaxSlots": 0 },
  "AutoHarvest": { "Active": false, "MaxSlots": 0 },
  "AutoFarm": {
    "Active": true, "ExcludeMoons": true, "ScanRange": [], "Exclude": [], "Origin": [],
    "KeepReportFor": 0, "NumProbes": 1, "TargetsProbedBeforeAttack": 100,
    "CargoType": "SmallCargo", "FleetSpeed": 100, "MinCargosToKeep": 0, "MinCargosToSend": 1,
    "CargoSurplusPercentage": 0, "BuildCargos": false, "BuildProbes": false,
    "MinimumResources": 1, "MinimumPlayerRank": 0, "MaxFlightTime": 0, "MinLootFuelRatio": 0,
    "PreferedResource": "", "ProcessAllReports": false, "MaxSlots": 1,
    "SlotsToLeaveFree": 0, "CheckIntervalMin": 1, "CheckIntervalMax": 1,
    "StopAfterFullScan": false, "Blacklist": { "Active": false }
  }
}
""",
				new List<string> { "{}" });
			var sentOrigins = new List<Coordinate>();
			var sentTargets = new List<Coordinate>();
			var fleets = new List<Fleet>();
			var parent = CreateParent(alias, settings, userData);
			var ogame = Proxy<IOgameService>.Create((method, _) => method.Name switch {
				nameof(IOgameService.GetEspionageReportsPage) => Task.FromResult(new List<EspionageReportSummary>()),
				_ => Proxy<IOgameService>.Default(method.ReturnType)
			});
			var bridge = Proxy<ITBotOgamedBridge>.Create((method, args) => {
				if (method.Name == nameof(ITBotOgamedBridge.GetDateTime))
					return Task.FromResult(DateTime.UtcNow);
				if (method.Name == nameof(ITBotOgamedBridge.UpdateResearches))
					return Task.FromResult(userData.researches);
				if (method.Name == nameof(ITBotOgamedBridge.UpdateCelestials))
					return Task.FromResult(userData.celestials);
				if (method.Name == nameof(ITBotOgamedBridge.UpdateSlots))
					return Task.FromResult(new Slots { Total = 10, InUse = fleets.Count });
				if (method.Name == nameof(ITBotOgamedBridge.CheckCelestials))
					return Task.FromResult(true);
				if (method.Name == nameof(ITBotOgamedBridge.UpdatePlanet))
					return Task.FromResult((Celestial)args[0]);
				return Proxy<ITBotOgamedBridge>.Default(method.ReturnType);
			});
			var scheduler = Proxy<IFleetScheduler>.Create((method, args) => {
				if (method.Name == nameof(IFleetScheduler.UpdateFleets))
					return Task.FromResult(fleets.ToList());
				if (method.Name == "get_LastFleetSnapshotWasFresh")
					return true;
				if (method.Name == nameof(IFleetScheduler.SendValidatedFleet)) {
					sentOrigins.Add(((Celestial)args[0]).Coordinate);
					sentTargets.Add((Coordinate)args[2]);
					fleets.Add(new Fleet {
						ID = 993102,
						Mission = Missions.Attack,
						Origin = ((Celestial)args[0]).Coordinate,
						Destination = (Coordinate)args[2],
						Ships = (Ships)args[1]
					});
					return Task.FromResult(993102);
				}
				return Proxy<IFleetScheduler>.Default(method.ReturnType);
			});
			var calculation = Proxy<ICalculationService>.Create((method, args) => {
				if (method.Name == nameof(ICalculationService.CalcSlotsPriority))
					return 1;
				if (method.Name == nameof(ICalculationService.CalcDistance))
					return 1;
				if (method.Name == nameof(ICalculationService.CalcShipNumberForPayload))
					return 1L;
				if (method.Name == nameof(ICalculationService.CalcFleetCapacity))
					return ((Ships)args[0]).GetAmount(Buildables.SmallCargo) * 100000L;
				if (method.Name == nameof(ICalculationService.CalcFleetPrediction))
					return new FleetPrediction { Time = 1, Fuel = 1 };
				if (method.Name == nameof(ICalculationService.GetMissionsInProgress))
					return new List<Fleet>();
				if (method.Name == nameof(ICalculationService.GetValidSpeedsForClass))
					return new List<decimal> { Speeds.HundredPercent };
				return Proxy<ICalculationService>.Default(method.ReturnType);
			});

			try {
				var worker = new AutoFarmWorker(parent, ogame, scheduler, calculation, bridge);
				var execute = typeof(AutoFarmWorker).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.NotNull(execute);

				await (Task)execute.Invoke(worker, null);

				Assert.Equal(new[] { targetCoordinate }, sentTargets);
				Assert.Equal(new[] { origin.Coordinate }, sentOrigins);
				Assert.Equal(1, fleets.Single(fleet => fleet.Mission == Missions.Attack).Ships.GetAmount(Buildables.SmallCargo));
				Assert.Equal(FarmState.AttackSent, Assert.Single(userData.farmTargets).State);
			} finally {
				if (File.Exists(databasePath))
					File.Delete(databasePath);
			}
		}

		private static UserData CreateUserData() {
			return new UserData {
				celestials = new List<Celestial>(),
				farmTargets = new List<FarmTarget>(),
				fleets = new List<Fleet>(),
				slots = new Slots { Total = 10, InUse = 0 },
				serverData = new ServerData(),
				userInfo = new UserInfo(),
				researches = new Researches()
			};
		}

		private static ITBotMain CreateParent(string alias, dynamic settings, UserData userData) {
			return Proxy<ITBotMain>.Create((method, _) => method.Name switch {
					"get_InstanceAlias" => alias,
					"get_InstanceSettings" => settings,
					"get_UserData" => userData,
					_ => Proxy<ITBotMain>.Default(method.ReturnType)
			});
		}

		private static ITBotOgamedBridge CreateBridge() {
			return Proxy<ITBotOgamedBridge>.Create((method, _) => method.Name switch {
				nameof(ITBotOgamedBridge.GetDateTime) => Task.FromResult(DateTime.UtcNow),
				_ => Proxy<ITBotOgamedBridge>.Default(method.ReturnType)
			});
		}

		private static IFleetScheduler CreateScheduler() {
			return Proxy<IFleetScheduler>.Create((method, _) => Proxy<IFleetScheduler>.Default(method.ReturnType));
		}

		private static ICalculationService CreateCalculation() {
			return Proxy<ICalculationService>.Create((method, _) => Proxy<ICalculationService>.Default(method.ReturnType));
		}

		private static Task DeleteReport(List<int> deletedReports, int reportId) {
			deletedReports.Add(reportId);
			return Task.CompletedTask;
		}

		private static Task<EspionageReport> ReadReport(
			Dictionary<int, EspionageReport> reports,
			List<int> readOrder,
			int reportId) {
			readOrder.Add(reportId);
			return Task.FromResult(reports[reportId]);
		}

		private class Proxy<T> : DispatchProxy where T : class {
			private Func<MethodInfo, object[], object> _handler;

			public static T Create(Func<MethodInfo, object[], object> handler) {
				var proxy = DispatchProxy.Create<T, Proxy<T>>();
				((Proxy<T>)(object)proxy)._handler = handler;
				return proxy;
			}

			public static object Default(Type returnType) {
				if (returnType == typeof(void))
					return null;
				if (returnType == typeof(Task))
					return Task.CompletedTask;
				if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)) {
					var valueType = returnType.GetGenericArguments()[0];
					var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
					return typeof(Task)
						.GetMethod(nameof(Task.FromResult))
						.MakeGenericMethod(valueType)
						.Invoke(null, new[] { value });
				}
				if (returnType.IsValueType)
					return Activator.CreateInstance(returnType);
				return null;
			}

			protected override object Invoke(MethodInfo targetMethod, object[] args) {
				var result = _handler?.Invoke(targetMethod, args ?? Array.Empty<object>());
				return result ?? Default(targetMethod.ReturnType);
			}
		}
	}
}
