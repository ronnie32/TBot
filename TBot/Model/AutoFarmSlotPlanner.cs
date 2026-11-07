using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public sealed record AutoFarmSlotBudget(int MaxSlots, int OwnedSlots, int AvailableSlots);

	/// <summary>
	/// Calculates the AutoFarm worker's fleet-slot budget without confusing
	/// AutoFarm fleets with unrelated mixed fleets.
	/// </summary>
	public static class AutoFarmSlotPlanner {
		public static AutoFarmSlotBudget Calculate(
			int maxSlots,
			int totalFreeSlots,
			int slotsToLeaveFree,
			IEnumerable<Fleet> fleets,
			Buildables cargoType,
			ISet<int> ownedEspionageFleetIds = null,
			bool allowBombers = false,
			bool allowReapers = false,
			bool allowCombatEscorts = false) {
			var ownedSlots = (fleets ?? Enumerable.Empty<Fleet>())
				.Count(fleet => IsOwnedFleet(fleet, cargoType, ownedEspionageFleetIds, allowBombers, allowReapers, allowCombatEscorts));

			var availableByWorker = Math.Max(0, maxSlots - ownedSlots);
			var availableGlobally = Math.Max(0, totalFreeSlots - slotsToLeaveFree);

			return new AutoFarmSlotBudget(
				Math.Max(0, maxSlots),
				ownedSlots,
				Math.Min(availableByWorker, availableGlobally));
		}

		public static bool IsOwnedFleet(Fleet fleet, Buildables cargoType) {
			return IsOwnedFleet(fleet, cargoType, null, false, false);
		}

		public static bool IsOwnedFleet(Fleet fleet, Buildables cargoType, ISet<int> ownedEspionageFleetIds) {
			return IsOwnedFleet(fleet, cargoType, ownedEspionageFleetIds, false, false);
		}

		public static bool IsOwnedFleet(
			Fleet fleet,
			Buildables cargoType,
			ISet<int> ownedEspionageFleetIds,
			bool allowBombers,
			bool allowReapers,
			bool allowCombatEscorts = false) {
			if (fleet?.Ships == null)
				return false;

			if (fleet.Mission == Missions.Spy)
				return fleet.Ships.IsOnlyProbes() &&
					(ownedEspionageFleetIds == null || ownedEspionageFleetIds.Contains(fleet.ID));

			return fleet.Mission == Missions.Attack && IsOnlyConfiguredAttackFleet(
				fleet.Ships, cargoType, allowBombers, allowReapers, allowCombatEscorts);
		}

		private static bool IsOnlyConfiguredAttackFleet(
			Ships ships,
			Buildables cargoType,
			bool allowBombers,
			bool allowReapers,
			bool allowCombatEscorts) {
			if (cargoType == Buildables.Null || ships.GetAmount(cargoType) <= 0)
				return false;

			return typeof(Ships)
				.GetProperties(BindingFlags.Instance | BindingFlags.Public)
				.Where(property => property.PropertyType == typeof(long))
				.Where(property => (long)property.GetValue(ships) > 0)
				.All(property => string.Equals(property.Name, cargoType.ToString(), StringComparison.Ordinal)
					|| allowBombers && string.Equals(property.Name, nameof(Buildables.Bomber), StringComparison.Ordinal)
					|| allowReapers && string.Equals(property.Name, nameof(Buildables.Reaper), StringComparison.Ordinal)
					|| allowCombatEscorts && property.Name is nameof(Buildables.LightFighter) or nameof(Buildables.HeavyFighter)
						or nameof(Buildables.Cruiser) or nameof(Buildables.Battleship) or nameof(Buildables.Battlecruiser)
						or nameof(Buildables.Bomber) or nameof(Buildables.Destroyer) or nameof(Buildables.Reaper));
		}
	}
}
