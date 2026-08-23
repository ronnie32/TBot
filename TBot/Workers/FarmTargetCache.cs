using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	/// <summary>
	/// Persistent storage for AutoFarm's FastFarm target cache and coordinate blacklist, backed by
	/// SQLite instead of the single JSON file / in-memory dictionary used before (P20). Each entry is
	/// upserted individually and immediately (a single-row transaction), instead of rewriting one giant
	/// JSON file on every Save() - a crash mid-cycle no longer loses everything written so far, and the
	/// blacklist now survives a bot restart instead of resetting to empty every time.
	///
	/// The FarmTargetCacheEntry itself (a small object with a few nested types - Temperature, Buildings,
	/// Resources) is stored as a single JSON blob per row rather than mapped column-by-column: this keeps
	/// the schema simple and avoids having to keep a hand-written column mapping in sync with the model
	/// every time a field is added there, while still getting SQLite's real benefits (atomic per-row
	/// writes, indexed lookups instead of loading everything into memory to filter).
	/// </summary>
	public class FarmTargetCache : IDisposable {
		private readonly SqliteConnection _db;
		// Small in-memory read cache so Get()/GetInRange() (called very frequently, once per scanned
		// target) don't hit SQLite on every call - refreshed from the DB once at Load(), kept in sync by
		// Upsert() writing through to both.
		private readonly Dictionary<string, FarmTargetCacheEntry> _entries = new();

		private FarmTargetCache(SqliteConnection db) {
			_db = db;
		}

		private static string GetKey(Coordinate coord) {
			return $"{coord.Galaxy}:{coord.System}:{coord.Position}:{coord.Type}";
		}

		public static string GetDatabaseFilePath(string instanceSettingsPath, string instanceAlias) {
			var directory = Path.GetDirectoryName(Path.GetFullPath(instanceSettingsPath));
			var dataDir = Path.Combine(directory ?? ".", "data");
			Directory.CreateDirectory(dataDir);
			return Path.Combine(dataDir, $"autofarm_{instanceAlias}.db");
		}

		/// <summary>
		/// Used by the Telegram /clearcache command. Deletes only the FastFarm target cache rows, not
		/// the blacklist - same scope as the old command back when it just deleted the JSON cache file.
		/// Clears via a fresh short-lived connection and DELETE FROM (not File.Delete on the .db) since
		/// AutoFarmWorker may have its own connection to this same file open concurrently on Windows.
		/// </summary>
		public static async Task<bool> ClearTargetCache(string instanceSettingsPath, string instanceAlias) {
			var dbPath = GetDatabaseFilePath(instanceSettingsPath, instanceAlias);
			if (!File.Exists(dbPath)) return false;

			using var db = new SqliteConnection($"Data Source={dbPath}");
			await db.OpenAsync();
			using var cmd = db.CreateCommand();
			cmd.CommandText = "DELETE FROM farm_target_cache WHERE 1=1";
			try {
				await cmd.ExecuteNonQueryAsync();
			} catch (SqliteException) {
				// Table doesn't exist yet (DB file present but never fully initialized) - nothing to clear.
				return false;
			}
			return true;
		}

		// Pre-P20 cache file - if found on first load, its entries are imported into the new SQLite
		// database once, then the JSON file is left alone (not deleted, in case something goes wrong
		// with the migration and a rollback to the old code is needed).
		private static string GetLegacyCacheFilePath(string instanceSettingsPath, string instanceAlias) {
			var directory = Path.GetDirectoryName(Path.GetFullPath(instanceSettingsPath));
			return Path.Combine(directory ?? ".", $"fastfarm_cache_{instanceAlias}.json");
		}

		public static async Task<FarmTargetCache> Load(string instanceSettingsPath, string instanceAlias) {
			var dbPath = GetDatabaseFilePath(instanceSettingsPath, instanceAlias);
			bool isNewDatabase = !File.Exists(dbPath);

			var db = new SqliteConnection($"Data Source={dbPath}");
			await db.OpenAsync();

			using (var cmd = db.CreateCommand()) {
				cmd.CommandText = @"
					CREATE TABLE IF NOT EXISTS farm_target_cache (
						coordinate_key TEXT PRIMARY KEY,
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						last_seen_date TEXT,
						last_report_date TEXT,
						entry_json TEXT NOT NULL
					);
					CREATE INDEX IF NOT EXISTS idx_farm_target_cache_galaxy_system
						ON farm_target_cache (galaxy, system);

					CREATE TABLE IF NOT EXISTS farm_blacklist (
						coordinate_key TEXT PRIMARY KEY,
						blacklisted_until TEXT NOT NULL
					);

					CREATE TABLE IF NOT EXISTS player_blacklist (
						player_name TEXT PRIMARY KEY,
						blacklisted_until TEXT NOT NULL
					);

					CREATE TABLE IF NOT EXISTS system_scan_cache (
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						scanned_at TEXT NOT NULL,
						PRIMARY KEY (galaxy, system)
					);

					CREATE TABLE IF NOT EXISTS attack_history (
						id INTEGER PRIMARY KEY AUTOINCREMENT,
						coordinate_key TEXT NOT NULL,
						galaxy INTEGER NOT NULL,
						system INTEGER NOT NULL,
						position INTEGER NOT NULL,
						player_name TEXT,
						metal INTEGER NOT NULL,
						crystal INTEGER NOT NULL,
						deuterium INTEGER NOT NULL,
						attacked_at TEXT NOT NULL
					);
					CREATE INDEX IF NOT EXISTS idx_attack_history_coordinate
						ON attack_history (coordinate_key);
					CREATE INDEX IF NOT EXISTS idx_attack_history_player
						ON attack_history (player_name);
				";
				await cmd.ExecuteNonQueryAsync();
			}

			var cache = new FarmTargetCache(db);

			if (isNewDatabase) {
				await cache.ImportLegacyJsonCacheIfPresent(GetLegacyCacheFilePath(instanceSettingsPath, instanceAlias));
			}

			using (var cmd = db.CreateCommand()) {
				cmd.CommandText = "SELECT entry_json FROM farm_target_cache";
				using var reader = await cmd.ExecuteReaderAsync();
				while (await reader.ReadAsync()) {
					try {
						var entry = JsonConvert.DeserializeObject<FarmTargetCacheEntry>(reader.GetString(0));
						if (entry != null)
							cache._entries[GetKey(entry.Coordinate)] = entry;
					} catch {
						// Corrupt row - skip it rather than fail the whole load.
					}
				}
			}

			return cache;
		}

		private async Task ImportLegacyJsonCacheIfPresent(string legacyFilePath) {
			if (!File.Exists(legacyFilePath)) return;
			try {
				var json = await File.ReadAllTextAsync(legacyFilePath);
				var entries = JsonConvert.DeserializeObject<List<FarmTargetCacheEntry>>(json) ?? new();
				foreach (var entry in entries) {
					await UpsertAndPersist(entry);
				}
			} catch {
				// Legacy file unreadable/corrupt - start fresh in SQLite rather than fail the load.
			}
		}

		/// <summary>
		/// Prunes stale entries (same 7-day TTL rule as before P20) and reconciles the in-memory read
		/// cache with the DB. Individual entries are already persisted immediately by Upsert(), so this
		/// no longer needs to rewrite everything - it only needs to delete what's now stale.
		/// </summary>
		public async Task Save() {
			var cutoff = DateTime.UtcNow.AddDays(-7);
			var staleKeys = _entries.Values
				.Where(e => e.LastSeenDate < cutoff && e.LastReportDate == null)
				.Select(e => GetKey(e.Coordinate))
				.ToList();

			foreach (var key in staleKeys) {
				_entries.Remove(key);
				using var cmd = _db.CreateCommand();
				cmd.CommandText = "DELETE FROM farm_target_cache WHERE coordinate_key = $key";
				cmd.Parameters.AddWithValue("$key", key);
				await cmd.ExecuteNonQueryAsync();
			}
		}

		public async Task Upsert(FarmTargetCacheEntry entry) {
			_entries[GetKey(entry.Coordinate)] = entry;
			// Awaited (not fire-and-forget): the connection gets disposed and replaced at the start of
			// the next AutoFarm cycle (see AutoFarmWorker.Execute()), so a write that's still in flight
			// at that point would race the Dispose() - a local SQLite insert is sub-millisecond, so
			// awaiting here has no meaningful cost.
			await UpsertAndPersist(entry);
		}

		private async Task UpsertAndPersist(FarmTargetCacheEntry entry) {
			try {
				using var cmd = _db.CreateCommand();
				cmd.CommandText = @"
					INSERT INTO farm_target_cache (coordinate_key, galaxy, system, last_seen_date, last_report_date, entry_json)
					VALUES ($key, $galaxy, $system, $lastSeen, $lastReport, $json)
					ON CONFLICT(coordinate_key) DO UPDATE SET
						galaxy = excluded.galaxy,
						system = excluded.system,
						last_seen_date = excluded.last_seen_date,
						last_report_date = excluded.last_report_date,
						entry_json = excluded.entry_json;
				";
				cmd.Parameters.AddWithValue("$key", GetKey(entry.Coordinate));
				cmd.Parameters.AddWithValue("$galaxy", entry.Coordinate.Galaxy);
				cmd.Parameters.AddWithValue("$system", entry.Coordinate.System);
				cmd.Parameters.AddWithValue("$lastSeen", entry.LastSeenDate.ToString("O"));
				cmd.Parameters.AddWithValue("$lastReport", (object) entry.LastReportDate?.ToString("O") ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$json", JsonConvert.SerializeObject(entry));
				await cmd.ExecuteNonQueryAsync();
			} catch {
				// Best-effort persistence - the in-memory read cache already has the up-to-date value,
				// so a transient DB write failure doesn't lose data for the rest of this run.
			}
		}

		public FarmTargetCacheEntry Get(Coordinate coord) {
			return _entries.TryGetValue(GetKey(coord), out var entry) ? entry : null;
		}

		public List<FarmTargetCacheEntry> GetInRange(int galaxy, int startSystem, int endSystem) {
			return _entries.Values
				.Where(e => e.Coordinate.Galaxy == galaxy && e.Coordinate.System >= startSystem && e.Coordinate.System <= endSystem)
				.ToList();
		}

		// --- System scan cache (FastFarmMode: lets AutoFarm skip a live galaxy scan for a system
		// it already scanned recently, reusing what a normal AutoFarm cycle found instead) ---

		public void MarkSystemScanned(int galaxy, int system) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO system_scan_cache (galaxy, system, scanned_at)
				VALUES ($galaxy, $system, $scannedAt)
				ON CONFLICT(galaxy, system) DO UPDATE SET scanned_at = excluded.scanned_at;
			";
			cmd.Parameters.AddWithValue("$galaxy", galaxy);
			cmd.Parameters.AddWithValue("$system", system);
			cmd.Parameters.AddWithValue("$scannedAt", DateTime.UtcNow.ToString("O"));
			cmd.ExecuteNonQuery();
		}

		/// <summary>Time since this system was last scanned, or null if never scanned.</summary>
		public TimeSpan? GetSystemScanAge(int galaxy, int system) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT scanned_at FROM system_scan_cache WHERE galaxy = $galaxy AND system = $system";
			cmd.Parameters.AddWithValue("$galaxy", galaxy);
			cmd.Parameters.AddWithValue("$system", system);
			var result = cmd.ExecuteScalar();
			if (result == null) return null;
			var scannedAt = DateTime.Parse((string) result, null, System.Globalization.DateTimeStyles.RoundtripKind);
			return DateTime.UtcNow - scannedAt;
		}

		// --- Blacklist (P20: replaces AutoFarmWorker's in-memory-only _farmBlacklist dictionary) ---

		public bool IsBlacklisted(Coordinate coord, out DateTime blacklistedUntil) {
			blacklistedUntil = default;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT blacklisted_until FROM farm_blacklist WHERE coordinate_key = $key";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			var result = cmd.ExecuteScalar();
			if (result == null) return false;
			blacklistedUntil = DateTime.Parse((string) result, null, System.Globalization.DateTimeStyles.RoundtripKind);
			return true;
		}

		public void Blacklist(Coordinate coord, DateTime until) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO farm_blacklist (coordinate_key, blacklisted_until)
				VALUES ($key, $until)
				ON CONFLICT(coordinate_key) DO UPDATE SET blacklisted_until = excluded.blacklisted_until;
			";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			cmd.Parameters.AddWithValue("$until", until.ToString("O"));
			cmd.ExecuteNonQuery();
		}

		public void RemoveFromBlacklist(Coordinate coord) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "DELETE FROM farm_blacklist WHERE coordinate_key = $key";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			cmd.ExecuteNonQuery();
		}

		// --- Player blacklist (#10: same idea as coordinate blacklist, but keyed by player ID so an
		// inactive who resettles/reactivates on a different planet is still recognized) ---

		public bool IsPlayerBlacklisted(string playerName, out DateTime blacklistedUntil) {
			blacklistedUntil = default;
			if (string.IsNullOrEmpty(playerName)) return false;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT blacklisted_until FROM player_blacklist WHERE player_name = $name";
			cmd.Parameters.AddWithValue("$name", playerName);
			var result = cmd.ExecuteScalar();
			if (result == null) return false;
			blacklistedUntil = DateTime.Parse((string) result, null, System.Globalization.DateTimeStyles.RoundtripKind);
			return true;
		}

		public void BlacklistPlayer(string playerName, DateTime until) {
			if (string.IsNullOrEmpty(playerName)) return;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO player_blacklist (player_name, blacklisted_until)
				VALUES ($name, $until)
				ON CONFLICT(player_name) DO UPDATE SET blacklisted_until = excluded.blacklisted_until;
			";
			cmd.Parameters.AddWithValue("$name", playerName);
			cmd.Parameters.AddWithValue("$until", until.ToString("O"));
			cmd.ExecuteNonQuery();
		}

		public void RemoveFromPlayerBlacklist(string playerName) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "DELETE FROM player_blacklist WHERE player_name = $name";
			cmd.Parameters.AddWithValue("$name", playerName);
			cmd.ExecuteNonQuery();
		}

		// --- Attack history (#8: real loot collected per coordinate, used both to feed the weighted
		// score (#6) and to decide poor-farm blacklisting by real results instead of just estimates) ---

		public async Task RecordAttack(Coordinate coord, string playerName, long metal, long crystal, long deuterium, DateTime attackedAt) {
			try {
				using var cmd = _db.CreateCommand();
				cmd.CommandText = @"
					INSERT INTO attack_history (coordinate_key, galaxy, system, position, player_name, metal, crystal, deuterium, attacked_at)
					VALUES ($key, $galaxy, $system, $position, $playerName, $metal, $crystal, $deuterium, $attackedAt);
				";
				cmd.Parameters.AddWithValue("$key", GetKey(coord));
				cmd.Parameters.AddWithValue("$galaxy", coord.Galaxy);
				cmd.Parameters.AddWithValue("$system", coord.System);
				cmd.Parameters.AddWithValue("$position", coord.Position);
				cmd.Parameters.AddWithValue("$playerName", string.IsNullOrEmpty(playerName) ? (object) DBNull.Value : playerName);
				cmd.Parameters.AddWithValue("$metal", metal);
				cmd.Parameters.AddWithValue("$crystal", crystal);
				cmd.Parameters.AddWithValue("$deuterium", deuterium);
				cmd.Parameters.AddWithValue("$attackedAt", attackedAt.ToString("O"));
				await cmd.ExecuteNonQueryAsync();
			} catch {
				// Best-effort - a failed history write must never abort the attack flow that triggered it.
			}
		}

		/// <summary>Average real total loot (metal+crystal+deuterium) collected from this coordinate across its recorded raids, or null if never recorded.</summary>
		public double? GetAverageLoot(Coordinate coord) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT AVG(metal + crystal + deuterium) FROM attack_history WHERE coordinate_key = $key";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			var result = cmd.ExecuteScalar();
			return result == null || result == DBNull.Value ? null : Convert.ToDouble(result);
		}

		/// <summary>Number of recorded raids against this coordinate.</summary>
		public int GetAttackCount(Coordinate coord) {
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM attack_history WHERE coordinate_key = $key";
			cmd.Parameters.AddWithValue("$key", GetKey(coord));
			return Convert.ToInt32(cmd.ExecuteScalar());
		}

		/// <summary>Average real total loot collected across all recorded raids against this player (any coordinate), or null if never recorded. Used for poor-farm blacklisting by player (#10) - recognizes an inactive even after they resettle on a different planet.</summary>
		public double? GetAveragePlayerLoot(string playerName) {
			if (string.IsNullOrEmpty(playerName)) return null;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT AVG(metal + crystal + deuterium) FROM attack_history WHERE player_name = $name";
			cmd.Parameters.AddWithValue("$name", playerName);
			var result = cmd.ExecuteScalar();
			return result == null || result == DBNull.Value ? null : Convert.ToDouble(result);
		}

		public int GetPlayerAttackCount(string playerName) {
			if (string.IsNullOrEmpty(playerName)) return 0;
			using var cmd = _db.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM attack_history WHERE player_name = $name";
			cmd.Parameters.AddWithValue("$name", playerName);
			return Convert.ToInt32(cmd.ExecuteScalar());
		}

		public void Dispose() {
			_db?.Dispose();
		}
	}
}
