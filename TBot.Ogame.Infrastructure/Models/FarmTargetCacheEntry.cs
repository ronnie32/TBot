using System;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	/// <summary>
	/// A previously-discovered farm target, persisted across bot restarts so that
	/// FastFarmMode can attack known inactive players without a live galaxy re-scan.
	/// </summary>
	public class FarmTargetCacheEntry {
		public Coordinate Coordinate { get; set; }
		public string PlayerName { get; set; }
		public int PlayerRank { get; set; }
		public bool IsInactive { get; set; }

		/// Last time this target was observed in a galaxy scan.
		public DateTime LastSeenDate { get; set; }

		/// Needed to extrapolate deuterium production; not present on EspionageReport.
		public Temperature Temperature { get; set; }

		/// Last known mine levels. Null if the target was never probed.
		public Buildings Buildings { get; set; }

		/// Resources known at LastReportDate, used as the extrapolation baseline.
		public Resources LastKnownResources { get; set; }
		public DateTime? LastReportDate { get; set; }
		public bool? HasDefenses { get; set; }
		public bool? HasFleet { get; set; }

		// Target's own character class (Collector/General/etc.), as last reported - used when
		// extrapolating resource growth between reports, since it affects their mine production.
		public CharacterClass? PlayerClass { get; set; }

		public bool HasCoords(Coordinate coords) {
			return coords.Galaxy == Coordinate.Galaxy
				&& coords.System == Coordinate.System
				&& coords.Position == Coordinate.Position
				&& coords.Type == Coordinate.Type;
		}
	}
}
