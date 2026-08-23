using System;
using System.Collections.Generic;

namespace TBot.Ogame.Infrastructure.Models {
	// Persistent record of a player encountered by AutoFarm or Defender, keyed by player (not coordinate),
	// so it survives across all of that player's planets.
	public class PlayerRecord {
		public int PlayerID { get; set; }
		public string PlayerName { get; set; }
		public string LastKnownCoordinate { get; set; }
		public DateTime FirstSeen { get; set; }
		public DateTime LastSeen { get; set; }

		// Every coordinate this player has been sighted at (attacking, spying or farmed by us).
		public List<string> KnownCoordinates { get; set; } = new();

		public int TimesFarmedByUs { get; set; }
		public DateTime? LastFarmedDate { get; set; }
		public long TotalConfirmedLoot { get; set; }

		public bool RetaliatedAgainstUs { get; set; }
		public int RetaliationCount { get; set; }
		public DateTime? LastRetaliationDate { get; set; }
	}
}
