using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Tbot.Includes;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public sealed class AutoFarmCombatSettings {
		public bool Active { get; set; }
		public bool ObservationOnly { get; set; }
		public string Profile { get; set; } = "Safe";
		public string Objective { get; set; } = "ProfitPerHour";
		public long MinimumNetProfit { get; set; } = 100000;
		public decimal? MaxLossPercent { get; set; }
		// Reject obsolete estimates explicitly instead of silently ignoring them.
		public int? DefenderTechnologyMargin { get; set; }
		public decimal? DefenderLifeformBonus { get; set; }
		public int SimulationRuns { get; set; } = 128;
		public decimal MaxFailureProbabilityPercent { get; set; } = 1M;
		public long MaxAbsoluteLoss { get; set; } = 100000;
		// Exact one-sided 95% zero-failure binomial bound, conditional on the model.
		public int ConfirmationRuns => Math.Max(SimulationRuns,
			(int)Math.Ceiling(Math.Log(0.05) / Math.Log(1 - (double)MaxFailureProbabilityPercent / 100)));
		public static double FailureUpperBoundPercent(int runs) => 100 * (1 - Math.Pow(0.05, 1.0 / runs));
		public int MaxPlanningSeconds { get; set; } = 30;
		public int MaxReportAgeSeconds { get; set; } = 300;
		public decimal AllowedLossPercent => MaxLossPercent ?? (Profile == "Balanced" ? 1M : 0M);
		public void Validate() {
			if (ObservationOnly && !Active) throw new ArgumentException("CombatPlanner.ObservationOnly requires Active=true.");
			if (DefenderTechnologyMargin.GetValueOrDefault() != 0 || DefenderLifeformBonus.GetValueOrDefault() != 0)
				throw new ArgumentException("Estimated defender bonuses are unsupported. Remove DefenderTechnologyMargin and DefenderLifeformBonus; reported combat bonuses are required.");
			if (Profile != "Safe" && Profile != "Balanced")
				throw new ArgumentException("CombatPlanner.Profile must be Safe or Balanced.");
			if (Objective != "ProfitPerHour" && Objective != "NetProfit")
				throw new ArgumentException("CombatPlanner.Objective must be ProfitPerHour or NetProfit.");
			if (MaxFailureProbabilityPercent < 0.6M || MaxFailureProbabilityPercent > 10M || MaxAbsoluteLoss < 0 ||
				MinimumNetProfit < 0 || AllowedLossPercent < 0 || AllowedLossPercent > 100 ||
				SimulationRuns < 32 || SimulationRuns > 512 || MaxPlanningSeconds < 1 || MaxPlanningSeconds > 120 ||
				MaxReportAgeSeconds < 1 || MaxReportAgeSeconds > 3600)
				throw new ArgumentException("CombatPlanner contains an out-of-range setting.");
		}
	}

	public sealed class AutoFarmCombatContext {
		public Researches Researches { get; init; }
		public ServerData Server { get; init; }
		public CharacterClass PlayerClass { get; init; }
		public AllianceClass AllianceClass { get; init; }
		public Buildables CargoType { get; init; }
		public long MinCargosToKeep { get; init; }
		public long MinCargosToSend { get; init; } = 1;
		public decimal CargoSurplusPercentage { get; init; } = 10;
		public int FleetSpeed { get; init; } = 100;
		public long MaxFlightTime { get; init; }
		public decimal MinLootFuelRatio { get; init; }
	}

	public sealed record AutoFarmCombatPlan(Celestial Origin, Ships Ships, decimal Speed, long Fuel, long FlightTime,
		decimal NetProfit, decimal ProfitPerHour, decimal DeployedValue, CombatSimulationResult Simulation, string Strategy) {
		public CombatSimulationRequest SimulationInput { get; init; }
	}
	public sealed record AutoFarmCombatPlanningResult(AutoFarmCombatPlan Plan, string Reason, int EvaluatedCandidates);

	/// <summary>
	/// Bounded portfolio search, local fleet-size/cargo refinement, then independent
	/// Monte Carlo confirmation. The best sampled feasible plan is returned; this
	/// does not claim a global optimum or certainty about a future OGame battle.
	/// </summary>
	public sealed class AutoFarmCombatPlanner {
		private readonly IOgameService _ogame;
		private readonly ICalculationService _calculation;
		private static readonly Buildables[] CombatShips = {
			Buildables.Battlecruiser, Buildables.Cruiser, Buildables.Battleship, Buildables.Bomber,
			Buildables.Destroyer, Buildables.Reaper, Buildables.HeavyFighter, Buildables.LightFighter
		};
		private sealed record Portfolio(string Name, (Buildables Ship, decimal Share)[] Mix);
		private static readonly Portfolio[] Portfolios = CombatShips.Select(ship => new Portfolio(ship.ToString(), new[] { (ship, 1M) }))
			.Concat(new[] {
				new Portfolio("Battlecruiser/Bomber",new[]{(Buildables.Battlecruiser,0.6M),(Buildables.Bomber,0.4M)}),
				new Portfolio("Cruiser/Bomber",new[]{(Buildables.Cruiser,0.4M),(Buildables.Bomber,0.6M)}),
				new Portfolio("Battlecruiser/Destroyer",new[]{(Buildables.Battlecruiser,0.6M),(Buildables.Destroyer,0.4M)}),
				new Portfolio("LightFighter/Battlecruiser",new[]{(Buildables.LightFighter,0.3M),(Buildables.Battlecruiser,0.7M)}),
				new Portfolio("LightFighter/Bomber",new[]{(Buildables.LightFighter,0.2M),(Buildables.Bomber,0.8M)})
			}).ToArray();
		public AutoFarmCombatPlanner(IOgameService ogame, ICalculationService calculation) { _ogame = ogame; _calculation = calculation; }

		public async Task<AutoFarmCombatPlanningResult> PlanAsync(EspionageReport report, IReadOnlyList<Celestial> origins,
			AutoFarmCombatContext context, AutoFarmCombatSettings settings, CancellationToken cancellationToken) {
			settings.Validate();
			ArgumentNullException.ThrowIfNull(report);
			ArgumentNullException.ThrowIfNull(context.Researches);
			ArgumentNullException.ThrowIfNull(context.Server);
			if (context.MinCargosToKeep < 0 || context.MinCargosToSend < 0 || context.CargoSurplusPercentage < 0 || context.CargoSurplusPercentage > 1000 ||
				context.FleetSpeed < 0 || context.FleetSpeed > 100 || context.MaxFlightTime < 0 || context.MinLootFuelRatio < 0)
				throw new ArgumentException("Combat planner requires valid cargo reserves, surplus, speed and flight limits.");
			if (!report.HasFleetInformation || !report.HasDefensesInformation)
				return new(null, "IncompleteCombatReport", 0);
			if (!ReportIsRecent(report, settings))
				return new(null, "CombatReportExpiredOrFutureDated", 0);
			var defender = Defender(report, out var missingInformation);
			if (defender == null)
				return new(null, missingInformation, 0);
			if (context.CargoType != Buildables.LargeCargo && context.CargoType != Buildables.SmallCargo && context.CargoType != Buildables.EspionageProbe)
				return new(null, "UnsupportedCargoType", 0);
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(TimeSpan.FromSeconds(settings.MaxPlanningSeconds));
			var ct = deadline.Token;
			var feasible = new List<AutoFarmCombatPlan>();
			var seen = new HashSet<string>();
			var evaluations = 0;
			var unsupportedSpecialShot = false;
			var loot = report.Loot(context.PlayerClass);
			var threat = Math.Max(1M, report.GetFleetResourceValue() + report.GetDefenseResourceValue(false));
			var eligible = (origins ?? Array.Empty<Celestial>()).Where(origin => origin?.Ships != null && origin.Resources != null && origin.LFBonuses != null).ToList();
			// Round robin across origins and tactics so one origin cannot consume
			// the entire search budget before alternative positions are considered.
			foreach (var factor in new[] { 0M, 0.1M, 0.25M, 0.5M, 1M, 2M, 4M, 8M }) {
				foreach (var portfolio in factor == 0 ? new[] { new Portfolio("CargoOnly", Array.Empty<(Buildables, decimal)>()) } : Portfolios) {
					foreach (var origin in eligible) {
						ct.ThrowIfCancellationRequested();
						if (evaluations >= 120)
							break;
						if (portfolio.Mix.Any(part => origin.Ships.GetAmount(part.Ship) <= 0))
							continue;
						var ships = new Ships();
						foreach (var part in portfolio.Mix) {
							var cost = ShipValue(new Ships().Add(part.Ship, 1));
							ships.Add(part.Ship, Math.Min(origin.Ships.GetAmount(part.Ship), Math.Max(1, (long) Math.Ceiling(threat * factor * part.Share / cost))));
						}
						AddCargo(ships, loot, origin, context);
						if (HasUnsupportedSpecialShot(ships, defender, report, context)) {
							unsupportedSpecialShot = true;
							continue;
						}
						var key = origin.ID + ":" + JsonConvert.SerializeObject(ships);
						if (!seen.Add(key) || !Available(ships, origin, context))
							continue;
						evaluations++;
						var plan = await EvaluateAsync(report, origin, ships, context, settings, defender, 4, portfolio.Name, ct);
						if (plan != null)
							feasible.Add(plan);
					}
				}
			}
			// Refine the escort scale locally. Non-monotone combat outcomes are
			// re-simulated; a failed smaller fleet never proves larger ones safe.
			foreach (var candidate in Rank(feasible, settings).Take(4).ToList()) {
				var upper = candidate;
				decimal low = 0, high = 1;
				for (int step = 0; step < 5; step++) {
					var middle = (low + high) / 2;
					var ships = new Ships();
					foreach (var ship in CombatShips)
						ships.Add(ship, (long) Math.Ceiling(candidate.Ships.GetAmount(ship) * middle));
					AddCargo(ships, loot, candidate.Origin, context);
					if (!Available(ships, candidate.Origin, context)) { low = middle; continue; }
					evaluations++;
					var refined = await EvaluateAsync(report, candidate.Origin, ships, context, settings, defender, 8, candidate.Strategy, ct);
					if (refined == null)
						low = middle;
					else { high = middle; upper = refined; feasible.Add(refined); }
				}
				// Recalculate cargo after the escort contributes its own hold space.
				// This preserves a configurable surplus and includes fuel headroom.
				var reduced = new Ships();
				foreach (var ship in CombatShips)
					reduced.Add(ship, upper.Ships.GetAmount(ship));
				AddCargo(reduced, new Resources(metal: checked(loot.TotalResources + upper.Fuel)), upper.Origin, context);
				if (Available(reduced, upper.Origin, context)) {
					evaluations++;
					var refined = await EvaluateAsync(report, upper.Origin, reduced, context, settings, defender, 8, upper.Strategy, ct);
					if (refined != null)
						feasible.Add(refined);
				}
			}
			// Fresh random draws confirm the finalists, independently of the
			// screening draws used to choose them (avoids selection optimism).
			var confirmed = new List<AutoFarmCombatPlan>();
			foreach (var candidate in Rank(feasible, settings).DistinctBy(plan => plan.Origin.ID + ":" + JsonConvert.SerializeObject(plan.Ships)).Take(3)) {
				var ships = candidate.Ships;
				for (int confirmation = 0; confirmation < 5; confirmation++) {
					evaluations++;
					var final = await EvaluateAsync(report, candidate.Origin, ships, context, settings, defender, settings.ConfirmationRuns, candidate.Strategy, ct);
					if (final != null) { confirmed.Add(final); break; }
					// A four-run screening win can be optimistic. Increase this
					// finalist's escort and confirm again with independent draws.
					var reinforced = new Ships();
					foreach (var ship in CombatShips)
						reinforced.Add(ship, Math.Min(candidate.Origin.Ships.GetAmount(ship),
						(long) Math.Ceiling(ships.GetAmount(ship) * 1.5M)));
					AddCargo(reinforced, new Resources(metal: checked(loot.TotalResources + candidate.Fuel)), candidate.Origin, context);
					if (!Available(reinforced, candidate.Origin, context) || JsonConvert.SerializeObject(reinforced) == JsonConvert.SerializeObject(ships))
						break;
					ships = reinforced;
				}
			}
			var best = Rank(confirmed, settings).FirstOrDefault();
			return new(best, best == null ? unsupportedSpecialShot ? "UnsupportedGeneralSpecialShot" : "NoSafeProfitableFleet" : "Selected", evaluations);
		}

		public async Task<AutoFarmCombatPlan> RevalidateAsync(AutoFarmCombatPlan plan, EspionageReport report,
			AutoFarmCombatContext context, AutoFarmCombatSettings settings, CancellationToken cancellationToken) {
			settings.Validate();
			if (report == null || !report.HasFleetInformation || !report.HasDefensesInformation || !ReportIsRecent(report, settings))
				return null;
			if (!Available(plan.Ships, plan.Origin, context))
				return null;
			var defender = Defender(report, out _);
			if (defender == null)
				return null;
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(TimeSpan.FromSeconds(settings.MaxPlanningSeconds));
			// The zero-loss boundary has no numerical loss buffer. Add fixed
			// escort reserves after selection, before drawing the held-out sample.
			// This transformation does not depend on that sample's outcome.
			var ships = new Ships();
			var headroom = settings.Profile == "Safe" ? 2M : 1.5M;
			foreach (var ship in CombatShips)
				ships.Add(ship, Math.Min(plan.Origin.Ships.GetAmount(ship),
					(long)Math.Ceiling(plan.Ships.GetAmount(ship) * headroom)));
			ships.Add(context.CargoType, plan.Ships.GetAmount(context.CargoType));
			// A single held-out confirmation after candidate selection. A failed
			// confirmation defers the target; retrying until it passes biases the bound.
			return await EvaluateAsync(report, plan.Origin, ships, context, settings, defender,
				settings.ConfirmationRuns, plan.Strategy, deadline.Token, requireRoundMargin: false);
		}

		private async Task<AutoFarmCombatPlan> EvaluateAsync(EspionageReport report, Celestial origin, Ships ships,
			AutoFarmCombatContext context, AutoFarmCombatSettings settings, CombatForce defender, int runs, string strategy, CancellationToken ct, bool requireRoundMargin = true) {
			ct.ThrowIfCancellationRequested();
			if (HasUnsupportedSpecialShot(ships, defender, report, context))
				return null;
			var loot = report.Loot(context.PlayerClass).TotalResources;
			var value = ShipValue(ships);
			var predictions = new List<(decimal Speed, FleetPrediction Prediction)>();
			foreach (var speed in _calculation.GetValidSpeedsForClass(context.PlayerClass).Where(speed => context.FleetSpeed == 0 || speed * 10 <= context.FleetSpeed)) {
				var prediction = _calculation.CalcFleetPrediction(origin.Coordinate, report.Coordinate, ships, Missions.Attack, speed,
					context.Researches, context.Server, origin.LFBonuses, context.PlayerClass, context.AllianceClass);
				if (prediction.Time <= 0 || prediction.Fuel < 0 || prediction.Fuel > origin.Resources.Deuterium ||
					(context.MaxFlightTime > 0 && prediction.Time > context.MaxFlightTime) || loot - prediction.Fuel < settings.MinimumNetProfit ||
					(context.MinLootFuelRatio > 0 && prediction.Fuel > 0 && (decimal) loot / prediction.Fuel < context.MinLootFuelRatio))
					continue;
				predictions.Add((speed, prediction));
			}
			if (predictions.Count == 0)
				return null;
			var bonus = ClassTechnologyBonus(context.PlayerClass, context.AllianceClass);
			var simulationInput = new CombatSimulationRequest {
				Attacker = new CombatForce {
					Ships = ships, Weapons = context.Researches.WeaponsTechnology + bonus,
					Shielding = context.Researches.ShieldingTechnology + bonus, Armour = context.Researches.ArmourTechnology + bonus,
					Bonuses = (origin.LFBonuses.LfShipBonuses ?? new()).ToDictionary(entry => entry.Key,
						entry => new CombatUnitBonus {
							StructuralIntegrity = (decimal) entry.Value.StructuralIntegrity,
							ShieldPower = (decimal) entry.Value.ShieldPower, WeaponPower = (decimal) entry.Value.WeaponPower
						})
				}, Defender = defender, Runs = runs, RapidFire = context.Server.RapidFire
			};
			var result = await _ogame.SimulateCombat(simulationInput, ct);
			if (result == null || result.Runs != runs || result.AttackerWins < 0 || result.DefenderWins < 0 || result.Draws < 0 ||
				result.AttackerWins + result.DefenderWins + result.Draws != runs || result.MinSurvivors == null ||
				result.MaxAttackerLoss < 0 || result.MaxAttackerLoss > value || result.MeanAttackerLoss < 0 || result.MeanAttackerLoss > result.MaxAttackerLoss ||
				result.MaxRounds < 0 || result.MaxRounds > 6 || result.MeanRounds < 0 || result.MeanRounds > result.MaxRounds)
				throw new InvalidOperationException("Combat simulator returned inconsistent statistics.");
			if (result.AttackerWins != runs || result.MaxAttackerLoss > Math.Min(settings.MaxAbsoluteLoss, value * settings.AllowedLossPercent / 100))
				return null;
			if (requireRoundMargin && result.MaxRounds > (settings.Profile == "Safe" ? 4 : 5))
				return null;
			var survivors = new Ships();
			decimal sumOfPerTypeWorstLosses = 0;
			foreach (var entry in result.MinSurvivors) {
				if (!Enum.TryParse<Buildables>(entry.Key, out var type) || !Enum.IsDefined(type) || type.ToString() != entry.Key ||
					ships.GetAmount(type) <= 0 || entry.Value < 0 || entry.Value > ships.GetAmount(type))
					throw new InvalidOperationException("Combat simulator returned invalid surviving ships.");
				var lossForType = ShipValue(new Ships().Add(type, ships.GetAmount(type) - entry.Value));
				if (lossForType > result.MaxAttackerLoss)
					throw new InvalidOperationException("Combat simulator losses contradict surviving ships.");
				sumOfPerTypeWorstLosses += lossForType;
				survivors.Add(type, entry.Value);
			}
			if (typeof(Ships).GetProperties().Any(property => (long) property.GetValue(ships) > 0 && !result.MinSurvivors.ContainsKey(property.Name)) ||
				result.MaxAttackerLoss > sumOfPerTypeWorstLosses ||
				((!defender.Ships.IsEmpty() || report.HasDefensiveStructures(false)) && (result.MaxRounds == 0 || result.MeanRounds < 1)))
				throw new InvalidOperationException("Combat simulator returned incomplete or contradictory combat statistics.");
			var capacity = Capacity(survivors, origin, context);
			var plans = new List<AutoFarmCombatPlan>();
			foreach (var item in predictions) {
				if ((decimal) capacity < (decimal) loot + item.Prediction.Fuel)
					continue;
				var net = loot - result.MaxAttackerLoss - item.Prediction.Fuel;
				if (net < settings.MinimumNetProfit)
					continue;
				plans.Add(new(origin, ships, item.Speed, item.Prediction.Fuel, item.Prediction.Time, net,
					net * 3600 / (2M * item.Prediction.Time), value, result, strategy) { SimulationInput = simulationInput });
			}
			return Rank(plans, settings).FirstOrDefault();
		}

		private static bool HasUnsupportedSpecialShot(Ships ships, CombatForce defender, EspionageReport report, AutoFarmCombatContext context) =>
			(context.PlayerClass == CharacterClass.General && ships.LightFighter > 0 && defender.Ships.Deathstar > 0) ||
			(report.CharacterClass == CharacterClass.General && defender.Ships.LightFighter > 0 && ships.Deathstar > 0);

		private void AddCargo(Ships ships, Resources loot, Celestial origin, AutoFarmCombatContext context) {
			var escortCapacity = Capacity(ships, origin, context);
			var missing = Math.Max(0, loot.TotalResources - escortCapacity);
			var cargo = _calculation.CalcShipNumberForPayload(new Resources(metal: missing), context.CargoType,
				context.Researches.HyperspaceTechnology, context.Server, origin.LFBonuses.GetShipCargoBonus(context.CargoType), context.PlayerClass, context.Server.ProbeCargo);
			cargo = (long) Math.Ceiling(cargo * (1 + context.CargoSurplusPercentage / 100));
			ships.Add(context.CargoType, Math.Max(1, Math.Max(context.MinCargosToSend, cargo)));
		}
		private long Capacity(Ships ships, Celestial origin, AutoFarmCombatContext context) => _calculation.CalcFleetCapacity(
			ships, context.Server, context.Researches.HyperspaceTechnology, origin.LFBonuses, context.PlayerClass, context.Server.ProbeCargo);
		private static bool Available(Ships ships, Celestial origin, AutoFarmCombatContext context) =>
			ships.GetAmount(context.CargoType) + context.MinCargosToKeep <= origin.Ships.GetAmount(context.CargoType) && origin.Ships.HasAtLeast(ships) &&
			typeof(Ships).GetProperties().Sum(property => (long) property.GetValue(ships)) <= 50000;
		private static decimal ShipValue(Ships ships) => checked(ships.GetFleetPoints() * 1000M);
		private static IOrderedEnumerable<AutoFarmCombatPlan> Rank(IEnumerable<AutoFarmCombatPlan> plans, AutoFarmCombatSettings settings) =>
			plans.OrderByDescending(plan => settings.Objective == "NetProfit" ? plan.NetProfit : plan.ProfitPerHour)
				.ThenBy(plan => plan.DeployedValue).ThenBy(plan => plan.Fuel).ThenBy(plan => plan.Origin.ID);
		private static int ClassTechnologyBonus(CharacterClass playerClass, AllianceClass allianceClass) =>
			(playerClass == CharacterClass.General ? 2 : 0) + (allianceClass == AllianceClass.Warrior ? 1 : 0);
		public static bool ReportIsRecent(EspionageReport report, AutoFarmCombatSettings settings) {
			var age = DateTime.UtcNow - report.Date.ToUniversalTime();
			return age >= TimeSpan.FromSeconds(-30) && age <= TimeSpan.FromSeconds(settings.MaxReportAgeSeconds);
		}
		private static CombatForce Defender(EspionageReport report, out string reason) {
			reason = "MissingCombatTechnologies";
			var ships = new Ships();
			foreach (var property in typeof(Ships).GetProperties()) {
				var value = report.GetType().GetProperty(property.Name)?.GetValue(report);
				property.SetValue(ships, value == null ? 0L : Convert.ToInt64(value));
			}
			var defenses = new Defences();
			foreach (var property in typeof(Defences).GetProperties())
				property.SetValue(defenses, Convert.ToInt64(report.GetType().GetProperty(property.Name)?.GetValue(report) ?? 0L));
			var hasOpposition = !ships.IsEmpty() || report.HasDefensiveStructures(false);
			if (hasOpposition && (!report.WeaponsTechnology.HasValue || !report.ShieldingTechnology.HasValue || !report.ArmourTechnology.HasValue))
				return null;
			if (hasOpposition && new[] { report.WeaponsTechnology.Value, report.ShieldingTechnology.Value, report.ArmourTechnology.Value }
				.Any(value => value < 0 || value > 100 - ClassTechnologyBonus(report.CharacterClass, report.AllianceClass))) {
				reason = "InvalidDefenderCombatTechnologies";
				return null;
			}
			var bonuses = new Dictionary<string, CombatUnitBonus>();
			if (hasOpposition) {
				reason = "UnknownDefenderClasses";
				if (!report.HasClassInformation || !Enum.IsDefined(report.CharacterClass) || !Enum.IsDefined(report.AllianceClass))
					return null;
				reason = "MissingDefenderCombatBonuses";
				if (report.CombatBonuses?.Source != "EspionageReportTable" || report.CombatBonuses.Units == null)
					return null;
				foreach (var type in Enum.GetValues<Buildables>().Where(type => ships.GetAmount(type) > 0 ||
					(typeof(Defences).GetProperty(type.ToString())?.GetValue(defenses) is long count && count > 0 &&
					 type != Buildables.AntiBallisticMissiles && type != Buildables.InterplanetaryMissiles))) {
					if (!report.CombatBonuses.Units.TryGetValue(((int) type).ToString(), out var entry) || entry == null ||
						!entry.StructuralIntegrity.HasValue || !entry.ShieldPower.HasValue || !entry.WeaponPower.HasValue ||
						new[] { entry.StructuralIntegrity.Value, entry.ShieldPower.Value, entry.WeaponPower.Value }.Any(value => value < 0 || value > 10))
						return null;
					bonuses[((int) type).ToString()] = new CombatUnitBonus {
						StructuralIntegrity = entry.StructuralIntegrity.Value,
						ShieldPower = entry.ShieldPower.Value, WeaponPower = entry.WeaponPower.Value
					};
				}
			}
			var bonus = ClassTechnologyBonus(report.CharacterClass, report.AllianceClass);
			return new CombatForce {
				Ships = ships, Defenses = defenses, Weapons = report.WeaponsTechnology.GetValueOrDefault() + bonus,
				Shielding = report.ShieldingTechnology.GetValueOrDefault() + bonus, Armour = report.ArmourTechnology.GetValueOrDefault() + bonus,
				Bonuses = bonuses
			};
		}
	}
}
