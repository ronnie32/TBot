using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tbot.Common.Settings;
using Tbot.Helpers;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using static Microsoft.AspNetCore.Razor.Language.TagHelperMetadata;

namespace Tbot.Workers.Brain {
	public class AutoMineWorker : WorkerBase {

		private readonly ICalculationService _calculationService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly IOgameService _ogameService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		public AutoMineWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge,
			IWorkerFactory workerFactory) :
			base(parentInstance) {
			_calculationService = calculationService;
			_fleetScheduler = fleetScheduler;
			_ogameService = ogameService;
			_tbotOgameBridge = tbotOgameBridge;
			_workerFactory = workerFactory;
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return ((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.AutoMine.Active);
			} catch (Exception) {
				return false;
			}
		}

		public override string GetWorkerName() {
			return "AutoMine";
		}
		public override Feature GetFeature() {
			return Feature.BrainAutoMine;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoMine;
		}

		protected override async Task Execute() {
			// Serializes this worker's whole run against the other 3 Brain item types (AutoResearch,
			// LifeformAutoMine, LifeformAutoResearch) - they all read/decide/spend resources from the
			// same origin celestials independently, and without this they can race to spend the same
			// resources or starve each other of the shared Transports.MaxSlots budget. See
			// BrainTransportCoordinator and project memory 2026-08-03.
			if (!await BrainTransportCoordinator.ResourceDecisionLock.WaitAsync(BrainTransportCoordinator.ResourceDecisionLockTimeout)) {
				DoLog(LogLevel.Warning, "Skipping this cycle: resource-decision lock still held by another Brain item after 10 minutes (likely stuck) - not waiting further.");
				return;
			}
			try {
			try {
				DoLog(LogLevel.Information, "Running automine...");

				Buildings maxBuildings = new() {
					MetalMine = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxMetalMine,
					CrystalMine = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxCrystalMine,
					DeuteriumSynthesizer = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxDeuteriumSynthetizer,
					SolarPlant = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxSolarPlant,
					FusionReactor = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxFusionReactor,
					MetalStorage = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxMetalStorage,
					CrystalStorage = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxCrystalStorage,
					DeuteriumTank = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxDeuteriumTank
				};
				Facilities maxFacilities = new() {
					RoboticsFactory = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxRoboticsFactory,
					Shipyard = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxShipyard,
					ResearchLab = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxResearchLab,
					MissileSilo = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxMissileSilo,
					NaniteFactory = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxNaniteFactory,
					Terraformer = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxTerraformer,
					SpaceDock = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxSpaceDock
				};
				Facilities maxLunarFacilities = new() {
					LunarBase = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxLunarBase,
					RoboticsFactory = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxLunarRoboticsFactory,
					SensorPhalanx = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxSensorPhalanx,
					JumpGate = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxJumpGate,
					Shipyard = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxLunarShipyard
				};
				AutoMinerSettings autoMinerSettings = new() {
					OptimizeForStart = (bool) _tbotInstance.InstanceSettings.Brain.AutoMine.OptimizeForStart,
					PrioritizeRobotsAndNanites = (bool) _tbotInstance.InstanceSettings.Brain.AutoMine.PrioritizeRobotsAndNanites,
					MaxDaysOfInvestmentReturn = (float) _tbotInstance.InstanceSettings.Brain.AutoMine.MaxDaysOfInvestmentReturn,
					DepositHours = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.DepositHours,
					BuildDepositIfFull = (bool) _tbotInstance.InstanceSettings.Brain.AutoMine.BuildDepositIfFull,
					DeutToLeaveOnMoons = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.DeutToLeaveOnMoons,
					BuildSolarSatellites = (bool) _tbotInstance.InstanceSettings.Brain.AutoMine.BuildSolarSatellites
				};
				Fields fieldsSettings = new() {
					Total = (int) _tbotInstance.InstanceSettings.AutoColonize.Abandon.MinFields
				};
				Temperature temperaturesSettings = new() {
					Min = (int) _tbotInstance.InstanceSettings.AutoColonize.Abandon.MinTemperatureAcceptable,
					Max = (int) _tbotInstance.InstanceSettings.AutoColonize.Abandon.MaxTemperatureAcceptable
				};

				_tbotInstance.UserData.researches = await _ogameService.GetResearches();
				List<Celestial> celestialsToExclude = _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.Brain.AutoMine.Exclude, _tbotInstance.UserData.celestials);
				List<Celestial> celestialsToMine = new();
				List<ConstructionCandidate> constructionCandidates = new();
				foreach (Celestial celestial in _tbotInstance.UserData.celestials.Where(p => p is Planet)) {
					var cel = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Buildings);
					cel = await _tbotOgameBridge.UpdatePlanet(cel, UpdateTypes.LFBuildings);
					cel = await _tbotOgameBridge.UpdatePlanet(cel, UpdateTypes.LFBonuses);

					Planet abaCelestial = await _tbotOgameBridge.UpdatePlanet(cel, UpdateTypes.Fast) as Planet;
					var nextMine = _calculationService.GetNextMineToBuild(cel as Planet, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData.Speed, maxBuildings.MetalMine, maxBuildings.CrystalMine, maxBuildings.DeuteriumSynthesizer, 1, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff.Geologist, _tbotInstance.UserData.staff.IsFull, true, int.MaxValue);
					if (nextMine != Buildables.Null) {
						var lv = _calculationService.GetNextLevel(cel, nextMine);
						var DOIR = _calculationService.CalcNextDaysOfInvestmentReturn(cel as Planet, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData.Speed, 1, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff.Geologist, _tbotInstance.UserData.staff.IsFull);
						DoLog(LogLevel.Debug, $"Celestial {cel.ToString()}: Next Mine: {nextMine.ToString()} lv {lv.ToString()}; DOIR: {DOIR.ToString()}.");
						if (DOIR < _tbotInstance.UserData.nextDOIR || _tbotInstance.UserData.nextDOIR == 0) {
							_tbotInstance.UserData.nextDOIR = DOIR;
						}
					}
					bool includeCelestial = true;
					if (cel.Coordinate.Type == Celestials.Planet && cel.Fields.Built == 0 && (bool) _tbotInstance.InstanceSettings.AutoColonize.Abandon.Active) {
						if (_calculationService.ShouldAbandon(cel as Planet, cel.Fields.Total, abaCelestial.Temperature.Max, fieldsSettings, temperaturesSettings)) {
							DoLog(LogLevel.Debug, $"Skipping {cel.ToString()}: planet should be abandoned.");
							includeCelestial = false;
						} else {
							//DoLog(LogLevel.Debug, $"Confirm AutoMine on {celestial.ToString()}.");
						}
						//DoLog(LogLevel.Debug, $"Because: cases -> {abaCelestial.Fields.Total.ToString()}/{fieldsSettings.Total.ToString()}, MinimumTemp -> {abaCelestial.Temperature.Max.ToString()}>={temperaturesSettings.Min.ToString()}, MaximumTemp -> {abaCelestial.Temperature.Max.ToString()}<={temperaturesSettings.Max.ToString()}");
					}
					if (includeCelestial) {
						celestialsToMine.Add(cel);
						var shouldBuildCrawlers =
								(!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.AutoMine, "BuildCrawlers") || (bool) _tbotInstance.InstanceSettings.Brain.AutoMine.BuildCrawlers) &&
								cel.Coordinate.Type == Celestials.Planet &&
								cel.Facilities != null &&
								cel.Productions != null &&
								cel.Ships != null &&
								cel.Constructions != null &&
								cel.Resources != null &&
								cel.ResourcesProduction != null &&
								cel.Resources.Energy >= 0 &&
								_tbotInstance.UserData.userInfo.Class == CharacterClass.Collector &&
								cel.Facilities.Shipyard >= 5 &&
								_tbotInstance.UserData.researches.CombustionDrive >= 4 &&
								_tbotInstance.UserData.researches.ArmourTechnology >= 4 &&
								_tbotInstance.UserData.researches.LaserTechnology >= 4 &&
								!cel.Productions.Any(production => production.ID == (int) Buildables.Crawler) &&
								cel.Constructions.BuildingID != (int) Buildables.Shipyard &&
								cel.Constructions.BuildingID != (int) Buildables.NaniteFactory &&
								cel.Ships.Crawler < _calculationService.CalcMaxCrawlers(cel as Planet, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff.Geologist) &&
								_calculationService.CalcOptimalCrawlers(cel as Planet, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData) > cel.Ships.Crawler;
						var planningDataAvailable = cel.Fields != null && cel.Constructions != null && cel.Facilities != null && cel.Resources != null && cel.ResourcesProduction != null && cel.Productions != null;
						if (shouldBuildCrawlers || (planningDataAvailable && cel.Fields.Free > 0 && cel.Constructions.BuildingID == 0)) {
							var nextConstruction = shouldBuildCrawlers ? Buildables.Crawler : _calculationService.GetNextBuildingToBuild(cel as Planet, _tbotInstance.UserData.researches, maxBuildings, maxFacilities, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff, _tbotInstance.UserData.serverData, autoMinerSettings);
							if (nextConstruction != Buildables.Null) {
								var nextConstructionLevel = shouldBuildCrawlers
									? _calculationService.CalcOptimalCrawlers(cel as Planet, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData)
									: _calculationService.GetNextLevel(cel, nextConstruction, _tbotInstance.UserData.userInfo.Class == CharacterClass.Collector, _tbotInstance.UserData.staff.Engineer, _tbotInstance.UserData.staff.IsFull);
								var isMine = nextConstruction == Buildables.MetalMine || nextConstruction == Buildables.CrystalMine || nextConstruction == Buildables.DeuteriumSynthesizer;
								var score = isMine
									? _calculationService.CalcDaysOfInvestmentReturn(cel as Planet, nextConstruction, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData.Speed, 1, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.staff.Geologist, _tbotInstance.UserData.staff.IsFull)
									: _calculationService.CalcPrice(nextConstruction, nextConstructionLevel, cel.LFBonuses).ConvertedDeuterium;
								var priority = AccountConstructionPlanner.GetAutoMinePriority(nextConstruction);
								constructionCandidates.Add(new ConstructionCandidate {
									CelestialId = cel.ID,
									Priority = priority,
									Score = score
								});
								DoLog(LogLevel.Debug, $"{cel}: Planned construction: {nextConstruction} level {nextConstructionLevel}; priority {priority}; score {score}.");
							}
						}
					}
				}
				celestialsToMine = AccountConstructionPlanner.OrderCelestials(celestialsToMine, constructionCandidates).ToList();
				celestialsToMine.AddRange(_tbotInstance.UserData.celestials.Where(c => c is Moon));

				var dueTime = TimeSpan.Zero;
				Task previousExecution = Task.CompletedTask;
				foreach (Celestial celestial in (bool) _tbotInstance.InstanceSettings.Brain.AutoMine.RandomOrder ? celestialsToMine.Shuffle().ToList() : celestialsToMine) {
					if (celestialsToExclude.Has(celestial)) {
						DoLog(LogLevel.Information, $"Skipping {celestial.ToString()}: celestial in exclude list.");
						continue;
					}

					var celestialWorker = _workerFactory.InitializeCelestialWorker(this, Feature.BrainCelestialAutoMine, _tbotInstance, _tbotOgameBridge, celestial);
					celestialWorker.StartAfter(previousExecution);
					dueTime = OrderedWorkerSchedule.NextDueTime(dueTime, TimeSpan.FromMilliseconds(RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds)));
					await celestialWorker.StartWorker(new CancellationTokenSource().Token, dueTime);
					previousExecution = celestialWorker.FirstExecutionCompleted;

				}
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"AutoMine Exception: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			} finally {
				if (!_tbotInstance.UserData.isSleeping) {
					await _tbotOgameBridge.CheckCelestials();
				}
			}
			} finally {
				BrainTransportCoordinator.ResourceDecisionLock.Release();
			}
		}
	}
}
