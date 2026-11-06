using System;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	/// <summary>
	/// Builds the fleet used for an AutoFarm attack. Cargo is always the primary
	/// component; escort ships are added only when the corresponding opt-in is
	/// enabled and the report contains the information needed to size them.
	/// </summary>
	public static class AutoFarmAttackComposition {
		private const long PointsPerStep = 100000;

		public static Ships Build(
			long cargoCount,
			Buildables cargoType,
			EspionageReport report,
			bool attackDefendedTargets,
			bool attackTargetsWithFleet,
			bool ignoreMissiles,
			long bombersPer100kDefense,
			long reapersPer100kFleet) {
			var ships = new Ships().Add(cargoType, Math.Max(0, cargoCount));
			if (report == null)
				return ships;

			if (attackDefendedTargets) {
				var defensePoints = report.GetDefenseResourceValue(includeMissiles: !ignoreMissiles);
				ships.Add(Buildables.Bomber, CalculateEscortCount(defensePoints, bombersPer100kDefense));
			}

			if (attackTargetsWithFleet)
				ships.Add(Buildables.Reaper, CalculateEscortCount(report.GetFleetResourceValue(), reapersPer100kFleet));

			return ships;
		}

		private static long CalculateEscortCount(long points, long shipsPerStep) {
			if (points <= 0 || shipsPerStep <= 0)
				return 0;

			var steps = (decimal) points / PointsPerStep;
			var count = Math.Ceiling(steps) * shipsPerStep;
			return count >= long.MaxValue ? long.MaxValue : (long) count;
		}
	}
}
