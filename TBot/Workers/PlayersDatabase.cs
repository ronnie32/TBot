using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	// Persistent record of every player AutoFarm has farmed and/or Defender has seen attacking us, keyed
	// by player. Used for Anti-Bashing: a player who retaliates after being farmed gets blacklisted.
	public class PlayersDatabase {
		private readonly Dictionary<int, PlayerRecord> _byId = new();
		// Fallback for reports/attacks without a numeric PlayerID.
		private readonly Dictionary<string, PlayerRecord> _byName = new();
		private readonly string _filePath;

		private PlayersDatabase(string filePath) {
			_filePath = filePath;
		}

		public static string GetDatabaseFilePath(string instanceSettingsPath, string instanceAlias) {
			var directory = Path.GetDirectoryName(Path.GetFullPath(instanceSettingsPath));
			var dataDir = Path.Combine(directory ?? ".", "data");
			Directory.CreateDirectory(dataDir);
			return Path.Combine(dataDir, $"players_db_{instanceAlias}.json");
		}

		public static async Task<PlayersDatabase> Load(string instanceSettingsPath, string instanceAlias) {
			var filePath = GetDatabaseFilePath(instanceSettingsPath, instanceAlias);
			var db = new PlayersDatabase(filePath);
			if (File.Exists(filePath)) {
				try {
					var json = await File.ReadAllTextAsync(filePath);
					var records = JsonConvert.DeserializeObject<List<PlayerRecord>>(json) ?? new();
					foreach (var record in records) {
						if (record.PlayerID > 0)
							db._byId[record.PlayerID] = record;
						else if (!string.IsNullOrEmpty(record.PlayerName))
							db._byName[record.PlayerName] = record;
					}
				} catch {
				}
			}
			return db;
		}

		public async Task Save() {
			var all = _byId.Values.Concat(_byName.Values).ToList();
			var json = JsonConvert.SerializeObject(all, Formatting.Indented);
			await File.WriteAllTextAsync(_filePath, json);
		}

		private PlayerRecord GetOrCreate(int playerId, string playerName) {
			PlayerRecord record = null;
			if (playerId > 0)
				_byId.TryGetValue(playerId, out record);
			if (record == null && !string.IsNullOrEmpty(playerName))
				_byName.TryGetValue(playerName, out record);

			if (record == null) {
				record = new PlayerRecord {
					PlayerID = playerId,
					PlayerName = playerName,
					FirstSeen = DateTime.UtcNow,
				};
				if (playerId > 0)
					_byId[playerId] = record;
				else if (!string.IsNullOrEmpty(playerName))
					_byName[playerName] = record;
			}
			return record;
		}

		public PlayerRecord Get(int playerId, string playerName) {
			if (playerId > 0 && _byId.TryGetValue(playerId, out var byId))
				return byId;
			if (!string.IsNullOrEmpty(playerName) && _byName.TryGetValue(playerName, out var byName))
				return byName;
			return null;
		}

		public bool IsBlacklisted(int playerId, string playerName) {
			var record = Get(playerId, playerName);
			return record != null && record.RetaliatedAgainstUs;
		}

		public void RecordSighting(int playerId, string playerName, string coordinate) {
			if (playerId <= 0 && string.IsNullOrEmpty(playerName))
				return;
			var record = GetOrCreate(playerId, playerName);
			record.LastKnownCoordinate = coordinate;
			record.LastSeen = DateTime.UtcNow;
			RecordKnownCoordinate(record, coordinate);
		}

		public void RecordFarmed(int playerId, string playerName, string coordinate) {
			if (playerId <= 0 && string.IsNullOrEmpty(playerName))
				return;
			var record = GetOrCreate(playerId, playerName);
			record.LastKnownCoordinate = coordinate;
			record.LastSeen = DateTime.UtcNow;
			record.TimesFarmedByUs++;
			record.LastFarmedDate = DateTime.UtcNow;
			RecordKnownCoordinate(record, coordinate);
		}

		private static void RecordKnownCoordinate(PlayerRecord record, string coordinate) {
			if (string.IsNullOrEmpty(coordinate))
				return;
			record.KnownCoordinates ??= new List<string>();
			if (!record.KnownCoordinates.Contains(coordinate))
				record.KnownCoordinates.Add(coordinate);
		}

		// All distinct coordinates ever observed for this player (attacks, spying, farming).
		public List<string> GetKnownCoordinates(int playerId, string playerName) {
			return Get(playerId, playerName)?.KnownCoordinates ?? new List<string>();
		}

		public void RecordConfirmedLoot(int playerId, string playerName, long loot) {
			if (playerId <= 0 && string.IsNullOrEmpty(playerName))
				return;
			var record = GetOrCreate(playerId, playerName);
			record.TotalConfirmedLoot += loot;
		}

		// Returns true only the first time this player retaliates, so the caller alerts once.
		public bool RecordRetaliation(int playerId, string playerName) {
			if (playerId <= 0 && string.IsNullOrEmpty(playerName))
				return false;
			var record = GetOrCreate(playerId, playerName);
			bool wasAlreadyBlacklisted = record.RetaliatedAgainstUs;
			record.RetaliatedAgainstUs = true;
			record.RetaliationCount++;
			record.LastRetaliationDate = DateTime.UtcNow;
			return !wasAlreadyBlacklisted;
		}
	}
}
