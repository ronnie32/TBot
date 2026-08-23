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

namespace Tbot.Workers.Brain {
	public class LifeformsAutoMineWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;

		public LifeformsAutoMineWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOGameBridge,
			IWorkerFactory workerFactory) :
			base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOGameBridge;
			_workerFactory = workerFactory;
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return (
					(bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active
				);
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "LifeformsAutoMine";
		}
		public override Feature GetFeature() {
			return Feature.BrainLifeformAutoMine;
		}

		public override LogSender GetLogSender() {
			return LogSender.LifeformsAutoMine;
		}

		protected override async Task Execute() {
			// Serializes this worker's whole run against the other 3 Brain item types (AutoMine,
			// AutoResearch, LifeformAutoResearch) - see BrainTransportCoordinator and project memory
			// 2026-08-03.
			if (!await BrainTransportCoordinator.ResourceDecisionLock.WaitAsync(BrainTransportCoordinator.ResourceDecisionLockTimeout)) {
				DoLog(LogLevel.Warning, "Skipping this cycle: resource-decision lock still held by another Brain item after 10 minutes (likely stuck) - not waiting further.");
				return;
			}
			try {
			try {
				DoLog(LogLevel.Information, "Running Lifeform automine...");

				if (_tbotInstance.UserData.isSleeping) {
					DoLog(LogLevel.Information, "Skipping: Sleep Mode Active!");
					return;
				}

				if (((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active)) {
					AutoMinerSettings autoMinerSettings = new() {
						DeutToLeaveOnMoons = (int) _tbotInstance.InstanceSettings.Brain.AutoMine.DeutToLeaveOnMoons
					};

					List<Celestial> celestialsToExclude = _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Exclude, _tbotInstance.UserData.celestials);
					List<Celestial> celestialsToMine = new();
					List<ConstructionCandidate> constructionCandidates = new();
					LFBuildings maxLFBuildings = new();
					foreach (Celestial celestial in _tbotInstance.UserData.celestials.Where(p => p is Planet)) {
						var cel = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Buildings);

						if ((int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.StartFromCrystalMineLvl > (int) cel.Buildings.CrystalMine) {
							DoLog(LogLevel.Debug, $"Celestial {cel.ToString()} did not reach required CrystalMine level. Skipping..");
							continue;
						}
						bool preventIfMoreExpensiveThanNextMine = (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.PreventIfMoreExpensiveThanNextMine;

						cel = await _tbotOgameBridge.UpdatePlanet(cel, UpdateTypes.LFBuildings);
						cel = await _tbotOgameBridge.UpdatePlanet(cel, UpdateTypes.Resources);
						cel = await _tbotOgameBridge.UpdatePlanet(cel, UpdateTypes.ResourcesProduction);
						maxLFBuildings = new();
						switch (cel.LFtype) {
							case LFTypes.Humans:
								maxLFBuildings.ResidentialSector = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBasePopulationBuilding;
								maxLFBuildings.BiosphereFarm = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseFoodBuilding;
								maxLFBuildings.ResearchCentre = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseTechBuilding;
								maxLFBuildings.AcademyOfSciences = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT2Building;
								maxLFBuildings.NeuroCalibrationCentre = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT3Building;
								maxLFBuildings.HighEnergySmelting = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding6;
								maxLFBuildings.FoodSilo = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding7;
								maxLFBuildings.FusionPoweredProduction = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding8;
								maxLFBuildings.Skyscraper = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding9;
								maxLFBuildings.BiotechLab = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding10;
								maxLFBuildings.Metropolis = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding11;
								maxLFBuildings.PlanetaryShield = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding12;
								break;
							case LFTypes.Mechas:
								maxLFBuildings.AssemblyLine = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBasePopulationBuilding;
								maxLFBuildings.FusionCellFactory = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseFoodBuilding;
								maxLFBuildings.RoboticsResearchCentre = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseTechBuilding;
								maxLFBuildings.UpdateNetwork = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT2Building;
								maxLFBuildings.QuantumComputerCentre = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT3Building;
								maxLFBuildings.AutomatisedAssemblyCentre = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding6;
								maxLFBuildings.HighPerformanceTransformer = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding7;
								maxLFBuildings.MicrochipAssemblyLine = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding8;
								maxLFBuildings.ProductionAssemblyHall = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding9;
								maxLFBuildings.HighPerformanceSynthesiser = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding10;
								maxLFBuildings.ChipMassProduction = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding11;
								maxLFBuildings.NanoRepairBots = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding12;
								break;
							case LFTypes.Rocktal:
								maxLFBuildings.MeditationEnclave = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBasePopulationBuilding;
								maxLFBuildings.CrystalFarm = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseFoodBuilding;
								maxLFBuildings.RuneTechnologium = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseTechBuilding;
								maxLFBuildings.RuneForge = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT2Building;
								maxLFBuildings.Oriktorium = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT3Building;
								maxLFBuildings.MagmaForge = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding6;
								maxLFBuildings.DisruptionChamber = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding7;
								maxLFBuildings.Megalith = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding8;
								maxLFBuildings.CrystalRefinery = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding9;
								maxLFBuildings.DeuteriumSynthesiser = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding10;
								maxLFBuildings.MineralResearchCentre = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding11;
								maxLFBuildings.AdvancedRecyclingPlant = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding12;
								break;
							case LFTypes.Kaelesh:
								maxLFBuildings.Sanctuary = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBasePopulationBuilding;
								maxLFBuildings.AntimatterCondenser = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseFoodBuilding;
								maxLFBuildings.VortexChamber = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseTechBuilding;
								maxLFBuildings.HallsOfRealisation = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT2Building;
								maxLFBuildings.ForumOfTranscendence = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT3Building;
								maxLFBuildings.AntimatterConvector = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding6;
								maxLFBuildings.CloningLaboratory = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding7;
								maxLFBuildings.ChrysalisAccelerator = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding8;
								maxLFBuildings.BioModifier = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding9;
								maxLFBuildings.PsionicModulator = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding10;
								maxLFBuildings.ShipManufacturingHall = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding11;
								maxLFBuildings.SupraRefractor = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding12;
								break;
							default:
								break;
						}
						var nextLFBuilding = _calculationService.GetNextLFBuildingToBuild(cel, maxLFBuildings, preventIfMoreExpensiveThanNextMine);
						if (nextLFBuilding != LFBuildables.None) {
							var lv = _calculationService.GetNextLevel(cel, nextLFBuilding);
							DoLog(LogLevel.Debug, $"Celestial {cel.ToString()}: Next Lifeform building: {nextLFBuilding.ToString()} lv {lv.ToString()}.");
							constructionCandidates.Add(new ConstructionCandidate {
								CelestialId = cel.ID,
								Score = _calculationService.CalcPrice(
									nextLFBuilding,
									lv,
									_calculationService.CalcLFBuildingsResourcesCostBonus(cel),
									0,
									_calculationService.CalcLFBuildingsPopulationCostBonus(cel)
								).ConvertedDeuterium
							});
							celestialsToMine.Add(cel);
						} else {
							DoLog(LogLevel.Debug, $"Celestial {cel.ToString()}: No Next Lifeform building to build found.");
						}
					}

					celestialsToMine = AccountConstructionPlanner.OrderCelestials(celestialsToMine, constructionCandidates).ToList();

					var dueTime = TimeSpan.Zero;
					Task previousExecution = Task.CompletedTask;
					foreach (Celestial celestial in celestialsToMine) {
						if (celestialsToExclude.Has(celestial)) {
							DoLog(LogLevel.Information, $"Skipping {celestial.ToString()}: celestial in exclude list.");
							continue;
						}
						var celestialWorker = _workerFactory.InitializeCelestialWorker(this, Feature.BrainCelestialLifeformAutoMine, _tbotInstance, _tbotOgameBridge, celestial);
						celestialWorker.StartAfter(previousExecution);
						dueTime = OrderedWorkerSchedule.NextDueTime(dueTime, TimeSpan.FromMilliseconds(RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds)));
						await celestialWorker.StartWorker(new CancellationTokenSource().Token, dueTime);
						previousExecution = celestialWorker.FirstExecutionCompleted;
					}
				} else {
					DoLog(LogLevel.Information, "Skipping: feature disabled");
				}
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"Lifeform AutoMine Exception: {e.Message}");
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
