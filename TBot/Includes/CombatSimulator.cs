using System;
using System.Collections.Generic;
using System.Linq;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Includes {
	// Result of a simulated OGame battle.
	public class CombatSimulationResult {
		public bool DefenderDestroyed { get; set; }
		public bool AttackerDestroyed { get; set; }
		public Ships AttackerShipsLost { get; set; } = new();
		public Ships DefenderShipsLost { get; set; } = new();
		public Defences DefencesLost { get; set; } = new();
		// Percentage (0-100) of the attacker's fleet value (metal+crystal) lost in the battle.
		public double AttackerLossPercentage { get; set; }
		public int Rounds { get; set; }
	}

	// Approximate simulator for OGame combat, used by AutoFarm to estimate fleet losses against a
	// (partially known) defender composition. Works on stacks (one entry per ship/defense type) instead
	// of resolving individual shots, distributing each round's damage proportionally across the
	// defender's stacks. Explosion chance isn't modeled; rapid fire uses its expected-value multiplier.
	public static class CombatSimulator {
		private class UnitStats {
			public long BaseAttack;
			public long BaseShield;
			public long BaseIntegrity;
		}

		private static readonly Dictionary<Buildables, UnitStats> _shipStats = new() {
			{ Buildables.LightFighter, new() { BaseAttack = 50, BaseShield = 10, BaseIntegrity = 4000 } },
			{ Buildables.HeavyFighter, new() { BaseAttack = 150, BaseShield = 25, BaseIntegrity = 10000 } },
			{ Buildables.Cruiser, new() { BaseAttack = 400, BaseShield = 50, BaseIntegrity = 27000 } },
			{ Buildables.Battleship, new() { BaseAttack = 1000, BaseShield = 200, BaseIntegrity = 60000 } },
			{ Buildables.Battlecruiser, new() { BaseAttack = 700, BaseShield = 400, BaseIntegrity = 70000 } },
			{ Buildables.Bomber, new() { BaseAttack = 1000, BaseShield = 500, BaseIntegrity = 75000 } },
			{ Buildables.Destroyer, new() { BaseAttack = 2000, BaseShield = 500, BaseIntegrity = 110000 } },
			{ Buildables.Deathstar, new() { BaseAttack = 200000, BaseShield = 50000, BaseIntegrity = 9000000 } },
			{ Buildables.SmallCargo, new() { BaseAttack = 5, BaseShield = 10, BaseIntegrity = 4000 } },
			{ Buildables.LargeCargo, new() { BaseAttack = 5, BaseShield = 25, BaseIntegrity = 12000 } },
			{ Buildables.ColonyShip, new() { BaseAttack = 50, BaseShield = 100, BaseIntegrity = 30000 } },
			{ Buildables.Recycler, new() { BaseAttack = 1, BaseShield = 10, BaseIntegrity = 16000 } },
			{ Buildables.EspionageProbe, new() { BaseAttack = 0, BaseShield = 0, BaseIntegrity = 1000 } },
			{ Buildables.SolarSatellite, new() { BaseAttack = 1, BaseShield = 1, BaseIntegrity = 2000 } },
			{ Buildables.Crawler, new() { BaseAttack = 1, BaseShield = 4, BaseIntegrity = 4000 } },
			{ Buildables.Reaper, new() { BaseAttack = 700, BaseShield = 700, BaseIntegrity = 140000 } },
			{ Buildables.Pathfinder, new() { BaseAttack = 200, BaseShield = 100, BaseIntegrity = 22000 } },
		};

		private static readonly Dictionary<Buildables, UnitStats> _defenceStats = new() {
			{ Buildables.RocketLauncher, new() { BaseAttack = 80, BaseShield = 20, BaseIntegrity = 2000 } },
			{ Buildables.LightLaser, new() { BaseAttack = 100, BaseShield = 25, BaseIntegrity = 2000 } },
			{ Buildables.HeavyLaser, new() { BaseAttack = 250, BaseShield = 100, BaseIntegrity = 8000 } },
			{ Buildables.GaussCannon, new() { BaseAttack = 1100, BaseShield = 200, BaseIntegrity = 35000 } },
			{ Buildables.IonCannon, new() { BaseAttack = 150, BaseShield = 500, BaseIntegrity = 8000 } },
			{ Buildables.PlasmaTurret, new() { BaseAttack = 3000, BaseShield = 300, BaseIntegrity = 100000 } },
			{ Buildables.SmallShieldDome, new() { BaseAttack = 1, BaseShield = 2000, BaseIntegrity = 20000 } },
			{ Buildables.LargeShieldDome, new() { BaseAttack = 1, BaseShield = 10000, BaseIntegrity = 100000 } },
		};

		// Rapid fire: attacker ship type -> (target type -> extra-shots value). Expected number of shots
		// fired at that target type equals this value (official rule: chance to fire again = (R-1)/R).
		private static readonly Dictionary<Buildables, Dictionary<Buildables, int>> _rapidFire = new() {
			{ Buildables.SmallCargo, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.Crawler, 5 } } },
			{ Buildables.LargeCargo, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.Crawler, 5 } } },
			{ Buildables.LightFighter, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.Crawler, 5 } } },
			{ Buildables.HeavyFighter, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.SmallCargo, 3 }, { Buildables.Crawler, 5 } } },
			{ Buildables.Cruiser, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.LightFighter, 6 }, { Buildables.RocketLauncher, 10 }, { Buildables.Crawler, 5 } } },
			{ Buildables.Battleship, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.Crawler, 5 } } },
			{ Buildables.Battlecruiser, new() { { Buildables.SmallCargo, 3 }, { Buildables.LargeCargo, 3 }, { Buildables.HeavyFighter, 4 }, { Buildables.Cruiser, 4 }, { Buildables.Battleship, 7 }, { Buildables.Recycler, 6 }, { Buildables.Pathfinder, 5 }, { Buildables.Crawler, 5 } } },
			{ Buildables.Bomber, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.RocketLauncher, 20 }, { Buildables.LightLaser, 20 }, { Buildables.HeavyLaser, 10 }, { Buildables.IonCannon, 10 }, { Buildables.Crawler, 5 } } },
			{ Buildables.Destroyer, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.LightLaser, 10 }, { Buildables.Battlecruiser, 2 }, { Buildables.Crawler, 5 } } },
			{ Buildables.Reaper, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.Battlecruiser, 7 }, { Buildables.Crawler, 5 } } },
			{ Buildables.Pathfinder, new() { { Buildables.EspionageProbe, 5 }, { Buildables.SolarSatellite, 5 }, { Buildables.Crawler, 5 } } },
			{ Buildables.Deathstar, new() {
				{ Buildables.EspionageProbe, 1250 }, { Buildables.SolarSatellite, 1250 }, { Buildables.Crawler, 1250 },
				{ Buildables.SmallCargo, 250 }, { Buildables.LargeCargo, 250 }, { Buildables.LightFighter, 200 },
				{ Buildables.HeavyFighter, 100 }, { Buildables.Cruiser, 33 }, { Buildables.Battleship, 30 },
				{ Buildables.Bomber, 25 }, { Buildables.Destroyer, 5 }, { Buildables.Recycler, 250 },
				{ Buildables.ColonyShip, 250 }, { Buildables.Pathfinder, 250 },
				{ Buildables.RocketLauncher, 200 }, { Buildables.LightLaser, 200 }, { Buildables.HeavyLaser, 100 },
				{ Buildables.GaussCannon, 50 }, { Buildables.IonCannon, 100 }, { Buildables.PlasmaTurret, 25 },
			} },
		};

		private class Stack {
			public Buildables Type;
			public long Count;
			public long UnitAttack;
			public long UnitShield;
			public long UnitIntegrity;
			public long RemainingHp => Count * UnitIntegrity;
		}

		private static List<Stack> BuildStacks(Dictionary<Buildables, UnitStats> statsTable, Func<Buildables, long> getCount, int weaponTechLevel, int shieldTechLevel, int armourTechLevel) {
			List<Stack> stacks = new();
			foreach (var kv in statsTable) {
				long count = getCount(kv.Key);
				if (count <= 0)
					continue;
				stacks.Add(new Stack {
					Type = kv.Key,
					Count = count,
					UnitAttack = (long) Math.Round(kv.Value.BaseAttack * (1 + 0.1 * weaponTechLevel)),
					UnitShield = (long) Math.Round(kv.Value.BaseShield * (1 + 0.1 * shieldTechLevel)),
					UnitIntegrity = (long) Math.Round(kv.Value.BaseIntegrity * (1 + 0.1 * armourTechLevel)),
				});
			}
			return stacks;
		}

		// Simulates up to 6 combat rounds. Only ship/defence types the game reported are considered -
		// unknown (unprobed) composition should be treated as untrustworthy, not as "no defence".
		public static CombatSimulationResult SimulateBattle(
			Ships attackerShips, Researches attackerResearches,
			Ships defenderShips, Defences defenderDefences, Researches defenderResearches
		) {
			var attackerStacks = BuildStacks(_shipStats, t => attackerShips.GetAmount(t), attackerResearches.WeaponsTechnology, attackerResearches.ShieldingTechnology, attackerResearches.ArmourTechnology);
			List<Stack> defenderStacks = new();
			defenderStacks.AddRange(BuildStacks(_shipStats, t => defenderShips.GetAmount(t), defenderResearches.WeaponsTechnology, defenderResearches.ShieldingTechnology, defenderResearches.ArmourTechnology));
			defenderStacks.AddRange(BuildStacks(_defenceStats, t => defenderDefences.GetAmount(t), defenderResearches.WeaponsTechnology, defenderResearches.ShieldingTechnology, defenderResearches.ArmourTechnology));

			Ships originalAttacker = attackerShips;
			int round = 1;
			for (; round <= 6; round++) {
				long attackerUnits = attackerStacks.Sum(s => s.Count);
				long defenderUnits = defenderStacks.Sum(s => s.Count);
				if (attackerUnits == 0 || defenderUnits == 0)
					break;

				FireRound(attackerStacks, defenderStacks);
				FireRound(defenderStacks, attackerStacks);

				attackerStacks.RemoveAll(s => s.Count <= 0);
				defenderStacks.RemoveAll(s => s.Count <= 0);
			}

			Ships attackerLost = new();
			foreach (var kv in _shipStats) {
				long remaining = attackerStacks.FirstOrDefault(s => s.Type == kv.Key)?.Count ?? 0;
				long lost = originalAttacker.GetAmount(kv.Key) - remaining;
				if (lost > 0)
					attackerLost.SetAmount(kv.Key, lost);
			}
			Ships defenderShipsLost = new();
			Defences defencesLost = new();
			foreach (var kv in _shipStats) {
				long remaining = defenderStacks.FirstOrDefault(s => s.Type == kv.Key)?.Count ?? 0;
				long lost = defenderShips.GetAmount(kv.Key) - remaining;
				if (lost > 0)
					defenderShipsLost.SetAmount(kv.Key, lost);
			}
			foreach (var kv in _defenceStats) {
				long remaining = defenderStacks.FirstOrDefault(s => s.Type == kv.Key)?.Count ?? 0;
				long lost = defenderDefences.GetAmount(kv.Key) - remaining;
				if (lost > 0)
					SetDefenceAmount(defencesLost, kv.Key, lost);
			}

			double lostValue = _shipStats.Sum(kv => attackerLost.GetAmount(kv.Key) * ApproxUnitValue(kv.Key));
			double totalValue = _shipStats.Sum(kv => originalAttacker.GetAmount(kv.Key) * ApproxUnitValue(kv.Key));

			return new CombatSimulationResult {
				AttackerShipsLost = attackerLost,
				DefenderShipsLost = defenderShipsLost,
				DefencesLost = defencesLost,
				DefenderDestroyed = defenderStacks.Sum(s => s.Count) == 0,
				AttackerDestroyed = attackerStacks.Sum(s => s.Count) == 0,
				AttackerLossPercentage = totalValue > 0 ? Math.Min(100, lostValue / totalValue * 100) : 0,
				Rounds = round - 1,
			};
		}

		// Rough metal+crystal cost per unit, used only to weigh the loss percentage - doesn't need to be
		// exact since it's a relative measure across the attacker's own fleet composition.
		private static double ApproxUnitValue(Buildables type) => type switch {
			Buildables.LightFighter => 4200,
			Buildables.HeavyFighter => 7600,
			Buildables.Cruiser => 29000,
			Buildables.Battleship => 65000,
			Buildables.Battlecruiser => 90000,
			Buildables.Bomber => 90000,
			Buildables.Destroyer => 175000,
			Buildables.Deathstar => 5375000,
			Buildables.SmallCargo => 4000,
			Buildables.LargeCargo => 12000,
			Buildables.ColonyShip => 40000,
			Buildables.Recycler => 18000,
			Buildables.EspionageProbe => 1000,
			Buildables.Reaper => 145000,
			Buildables.Pathfinder => 35000,
			_ => 0,
		};

		private static void SetDefenceAmount(Defences defences, Buildables type, long value) {
			var prop = typeof(Defences).GetProperty(type.ToString());
			prop?.SetValue(defences, value);
		}

		private static void FireRound(List<Stack> attackers, List<Stack> defenders) {
			long totalDefenderUnits = defenders.Sum(s => s.Count);
			if (totalDefenderUnits == 0)
				return;

			// Accumulate damage-to-apply per defender stack, then apply once at the end of the round so all
			// shots this round are resolved against the round's starting state (matches the game resolving a
			// round "at once" rather than shooters reacting mid-round).
			Dictionary<Buildables, double> damageByStack = defenders.ToDictionary(s => s.Type, s => 0.0);

			foreach (var attacker in attackers) {
				foreach (var defender in defenders) {
					double shareOfShots = (double) defender.Count / totalDefenderUnits;
					int rapidFire = 1;
					if (_rapidFire.TryGetValue(attacker.Type, out var rfTable) && rfTable.TryGetValue(defender.Type, out var rf))
						rapidFire = rf;
					double shots = attacker.Count * rapidFire * shareOfShots;
					double perShotDamage = Math.Max(0, attacker.UnitAttack - defender.UnitShield);
					damageByStack[defender.Type] += shots * perShotDamage;
				}
			}

			foreach (var defender in defenders) {
				long destroyed = defender.UnitIntegrity > 0 ? (long) (damageByStack[defender.Type] / defender.UnitIntegrity) : 0;
				defender.Count = Math.Max(0, defender.Count - destroyed);
			}
		}
	}
}
