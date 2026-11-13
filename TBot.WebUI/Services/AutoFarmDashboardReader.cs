using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Tbot.Common.Settings;
using TBot.Ogame.Infrastructure.Enums;
using TBot.WebUI.Models;

namespace TBot.WebUI.Services {
	public sealed class AutoFarmDashboardReader {
		private const string ManualReviewDefenseOutcome = "ManualReviewDefense";
		private static readonly Regex SlotBudget = new(@"AutoFarm will use (?<available>\d+) slots \((?<used>\d+)/(?<max>\d+) already used\)", RegexOptions.Compiled);
		private static readonly Regex ExhaustedBudget = new(@"AutoFarm slot budget exhausted \((?<used>\d+)/(?<max>\d+)\)", RegexOptions.Compiled);
		private static readonly Regex CoordinatePattern = new(@"\[(?<type>[A-Za-z]+):(?<galaxy>\d+):(?<system>\d+):(?<position>\d+)\]", RegexOptions.Compiled);
		private static readonly Regex RequiredCargo = new(@"require (?<amount>[\d ]+) (?<ship>[A-Za-z]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		public AutoFarmDashboardStorage Read(string instanceAlias) {
			var storage = new AutoFarmDashboardStorage();
			var settings = ReadSettings(instanceAlias);
			var logs = ReadLogs();
			var autoFarmLogs = logs.Where(IsAutoFarmLog).ToList();

			storage.Logs.AddRange(ToLogRows(autoFarmLogs.TakeLast(120).Reverse()));
			storage.Errors.AddRange(ToLogRows(autoFarmLogs
				.Where(log => IsError(log) || log.Message.Contains("503", StringComparison.OrdinalIgnoreCase) || log.Message.Contains("302", StringComparison.OrdinalIgnoreCase))
				.TakeLast(40)
				.Reverse()));
			storage.Worker = BuildWorkerStatus(logs);
			storage.Slots = BuildSlotStatus(logs, settings.MaxSlots);

			var databasePath = GetDatabasePath(instanceAlias);
			if (!File.Exists(databasePath)) {
				storage.Scan = BuildScanStatus(storage, settings, null, null);
				return storage;
			}

			try {
				using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
					DataSource = databasePath,
					Pooling = false
				}.ToString());
				connection.Open();

				if (HasTable(connection, "systems"))
					ReadSystems(connection, storage, settings);
				if (HasTable(connection, "targets"))
					ReadTargets(connection, storage, instanceAlias, logs);
				ReadAttacks(connection, storage);
				var cursor = HasTable(connection, "scan_cursor") ? ReadCursor(connection) : null;

				storage.DatabaseAvailable = true;
				storage.Scan = BuildScanStatus(storage, settings, cursor, storage.LastSystemObservedAtUtc);
			} catch {
				storage.DatabaseAvailable = false;
			}

			return storage;
		}

		private static void ReadSystems(SqliteConnection connection, AutoFarmDashboardStorage storage, DashboardSettings settings) {
			var hasPlanetsJson = HasColumn(connection, "systems", "planets_json");
			using var command = connection.CreateCommand();
			command.CommandText = @"
SELECT galaxy, system, observed_at_utc, is_empty, " + (hasPlanetsJson ? "planets_json" : "NULL") + @"
FROM systems
ORDER BY observed_at_utc DESC;";

			using var reader = command.ExecuteReader();
			while (reader.Read()) {
				var galaxy = reader.GetInt32(0);
				var system = reader.GetInt32(1);
				var observedAt = ParseDate(reader.GetString(2));
				var isEmpty = reader.GetInt64(3) != 0;
				var eligibleTargetCount = reader.IsDBNull(4) ? -1 : CountEligibleTargets(reader.GetString(4));
				var nextRefresh = observedAt.HasValue
					? observedAt.Value.Add(isEmpty
						? TimeSpan.FromDays(settings.EmptySystemCooldownDays)
						: TimeSpan.FromDays(settings.SystemDataDays))
					: (DateTime?)null;

				storage.Systems.Add(new AutoFarmSystemRow {
					Coordinate = $"{galaxy}:{system}",
					Status = isEmpty ? "Cached empty" : eligibleTargetCount == 0 ? "Cached no targets" : "Cached",
					Reason = isEmpty
						? "No planets or only vacation-mode inactives"
						: eligibleTargetCount == 0
							? "Planets exist, but none currently meet AutoFarm requirements"
							: "System data is reusable",
					LastScannedUtc = observedAt,
					NextRefreshUtc = nextRefresh
				});
			}

			storage.CachedSystemCount = storage.Systems.Count;
			var observedTimes = storage.Systems
				.Select(system => system.LastScannedUtc)
				.Where(value => value.HasValue)
				.Select(value => value!.Value)
				.ToList();
			storage.LastSystemObservedAtUtc = observedTimes.Count == 0 ? null : observedTimes.Max();
		}

		private static int CountEligibleTargets(string json) {
			try {
				return JArray.Parse(json).OfType<JObject>().Count(planet =>
					planet.Value<bool?>("Inactive") == true &&
					planet.Value<bool?>("Administrator") != true &&
					planet.Value<bool?>("Banned") != true &&
					planet.Value<bool?>("Vacation") != true);
			} catch {
				return -1;
			}
		}

		private static void ReadTargets(SqliteConnection connection, AutoFarmDashboardStorage storage, string instanceAlias, List<LogRecord> logs) {
			var blacklist = ReadBlacklist(instanceAlias);
			var logReasons = BuildTargetReasons(logs, instanceAlias);
			var consumedReportIds = ReadConsumedReportIds(connection);
			var processedReportOutcomes = ReadProcessedReportOutcomes(connection);
			using var command = connection.CreateCommand();
			var hasConsumedReportColumn = HasColumn(connection, "targets", "consumed_report_id");
			command.CommandText = @"
SELECT name, state, galaxy, system, position, celestial_type, report_json,
  " + (hasConsumedReportColumn ? "consumed_report_id" : "NULL") + @", updated_at_utc
FROM targets
WHERE report_json IS NOT NULL
ORDER BY updated_at_utc DESC;";

			using var reader = command.ExecuteReader();
			while (reader.Read()) {
				var report = TryParseReport(reader.GetString(6));
				if (report == null)
					continue;

				var galaxy = reader.GetInt32(2);
				var system = reader.GetInt32(3);
				var position = reader.GetInt32(4);
				var coordinate = FormatCoordinate(galaxy, system, position, reader.GetInt32(5));
				var state = GetStateName(reader.GetInt32(1));
				var reportId = report.Value<int?>("ID") ?? 0;
				var consumedReportId = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
				var reason = ResolveTargetReason(state, coordinate, reportId, consumedReportId, consumedReportIds, processedReportOutcomes, blacklist, logReasons);

				storage.Reports.Add(new AutoFarmReportRow {
					TargetName = reader.GetString(0),
					State = state,
					Coordinate = coordinate,
					ReportId = reportId,
					ReportDateUtc = ParseDate(report.Value<string>("Date")),
					UpdatedAtUtc = ParseDate(reader.GetString(8)),
					Metal = report.Value<long?>("Metal") ?? 0,
					Crystal = report.Value<long?>("Crystal") ?? 0,
					Deuterium = report.Value<long?>("Deuterium") ?? 0,
					ConsumedReportId = consumedReportId,
					Reason = reason.Label,
					ReasonDetail = reason.Detail
				});
			}
		}

		private static HashSet<int> ReadConsumedReportIds(SqliteConnection connection) {
			var result = new HashSet<int>();
			if (!HasTable(connection, "consumed_reports"))
				return result;

			using var command = connection.CreateCommand();
			command.CommandText = "SELECT report_id FROM consumed_reports WHERE report_id > 0;";
			using var reader = command.ExecuteReader();
			while (reader.Read())
				result.Add(reader.GetInt32(0));
			return result;
		}

		private static Dictionary<int, string> ReadProcessedReportOutcomes(SqliteConnection connection) {
			var result = new Dictionary<int, string>();
			var queries = new[] {
				"SELECT report_id, outcome, processed_at_utc FROM processed_reports;",
				"SELECT report_id, outcome, processed_at_utc FROM processed_report_ids;"
			};

			foreach (var query in queries) {
				try {
					using var command = connection.CreateCommand();
					command.CommandText = query;
					using var reader = command.ExecuteReader();
					while (reader.Read()) {
						var reportId = reader.GetInt32(0);
						if (reportId <= 0 || result.ContainsKey(reportId))
							continue;

						result[reportId] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
					}
				} catch {
					// Older AutoFarm databases may not contain both report tables.
				}
			}

			return result;
		}

		private static void ReadAttacks(SqliteConnection connection, AutoFarmDashboardStorage storage) {
			if (!HasTable(connection, "attacks"))
				return;

			var hasOrigin = HasColumn(connection, "attacks", "origin_galaxy") &&
				HasColumn(connection, "attacks", "origin_system") &&
				HasColumn(connection, "attacks", "origin_position") &&
				HasColumn(connection, "attacks", "origin_celestial_type");
			var hasMission = HasColumn(connection, "attacks", "mission");
			using var command = connection.CreateCommand();
			command.CommandText = @"
SELECT report_id, galaxy, system, position, celestial_type, target_name,
  dispatched_at_utc, metal, crystal, deuterium, "
				+ (hasOrigin ? "origin_galaxy, origin_system, origin_position, origin_celestial_type" : "NULL, NULL, NULL, NULL")
				+ ", "
				+ (hasMission ? "mission" : "'Attack'")
				+ @"
FROM attacks
ORDER BY dispatched_at_utc DESC, report_id DESC
LIMIT 100;";

			using var reader = command.ExecuteReader();
			while (reader.Read()) {
				var origin = reader.IsDBNull(10) || reader.IsDBNull(11) || reader.IsDBNull(12) || reader.IsDBNull(13)
					? "Unknown origin"
					: FormatCoordinate(reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13));

				storage.Attacks.Add(new AutoFarmAttackRow {
					ReportId = reader.GetInt32(0),
					Coordinate = FormatCoordinate(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)),
					TargetName = reader.GetString(5),
					DispatchedAtUtc = ParseDate(reader.GetString(6)) ?? DateTime.MinValue,
					Metal = reader.GetInt64(7),
					Crystal = reader.GetInt64(8),
					Deuterium = reader.GetInt64(9),
					Origin = origin,
					Mission = reader.IsDBNull(14) ? "Attack" : reader.GetString(14)
				});
			}
		}

		private static AutoFarmScanCursorRow? ReadCursor(SqliteConnection connection) {
			using var command = connection.CreateCommand();
			command.CommandText = @"
SELECT range_index, galaxy, system, updated_at_utc
FROM scan_cursor
WHERE id = 1;";
			using var reader = command.ExecuteReader();
			if (!reader.Read())
				return null;

			return new AutoFarmScanCursorRow(
				reader.GetInt32(0),
				reader.GetInt32(1),
				reader.GetInt32(2),
				ParseDate(reader.GetString(3)));
		}

		private static AutoFarmScanStatus BuildScanStatus(AutoFarmDashboardStorage storage, DashboardSettings settings, AutoFarmScanCursorRow? cursor, DateTime? lastObserved) {
			var scanned = storage.Systems.Count(system => {
				var parts = system.Coordinate.Split(':');
				return parts.Length == 2 && int.TryParse(parts[0], out var galaxy) && int.TryParse(parts[1], out var systemNumber) &&
					settings.Ranges.Any(range => range.Galaxy == galaxy && systemNumber >= range.StartSystem && systemNumber <= range.EndSystem);
			});
			var total = settings.Ranges.Sum(range => Math.Max(0, range.EndSystem - range.StartSystem + 1));
			return new AutoFarmScanStatus {
				CursorGalaxy = cursor?.Galaxy ?? 0,
				CursorSystem = cursor?.System ?? 0,
				CursorUpdatedAtUtc = cursor?.UpdatedAtUtc,
				CachedSystems = storage.CachedSystemCount,
				ScannedSystemsInRange = scanned,
				TotalSystemsInRange = total,
				ProgressPercent = total == 0 ? 0 : Math.Min(100, (int)Math.Round(scanned * 100d / total))
			};
		}

		private static AutoFarmWorkerStatus BuildWorkerStatus(List<LogRecord> logs) {
			var autoFarmLogs = logs.Where(log => log.Sender.Equals("AutoFarm", StringComparison.OrdinalIgnoreCase)).ToList();
			var last = autoFarmLogs.LastOrDefault();
			var lastExecution = autoFarmLogs.LastOrDefault(log => log.Message.Contains("Running autofarm", StringComparison.OrdinalIgnoreCase));
			var next = autoFarmLogs.LastOrDefault(log => log.Message.Contains("Next autofarm check at", StringComparison.OrdinalIgnoreCase));
			DateTime? nextExecution = next == null ? null : ParseNextExecution(next.Message);

			var state = "Unknown";
			if (last != null) {
				if (last.Message.Contains("not enabled by settings", StringComparison.OrdinalIgnoreCase))
					state = "Disabled";
				else if (IsError(last))
					state = "Error";
				else if (DateTime.UtcNow - last.DateTimeUtc.ToUniversalTime() <= TimeSpan.FromMinutes(5))
					state = "Active";
				else
					state = "Stale";
			}

			return new AutoFarmWorkerStatus {
				State = state,
				LastExecutionUtc = lastExecution?.DateTimeUtc.ToUniversalTime(),
				LastActivityUtc = last?.DateTimeUtc.ToUniversalTime(),
				NextExecutionUtc = nextExecution?.ToUniversalTime(),
				NextExecutionText = nextExecution.HasValue ? nextExecution.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Unknown"
			};
		}

		private static AutoFarmSlotStatus BuildSlotStatus(List<LogRecord> logs, int configuredMaxSlots) {
			var autoFarmLogs = logs.Where(log => log.Sender.Equals("AutoFarm", StringComparison.OrdinalIgnoreCase)).ToList();
			int? maxSlots = configuredMaxSlots > 0 ? configuredMaxSlots : null;
			int? usedSlots = null;
			int? availableSlots = null;
			foreach (var log in autoFarmLogs) {
				var match = SlotBudget.Match(log.Message);
				if (match.Success) {
					availableSlots = int.Parse(match.Groups["available"].Value, CultureInfo.InvariantCulture);
					usedSlots = int.Parse(match.Groups["used"].Value, CultureInfo.InvariantCulture);
					maxSlots = int.Parse(match.Groups["max"].Value, CultureInfo.InvariantCulture);
					continue;
				}

				match = ExhaustedBudget.Match(log.Message);
				if (match.Success) {
					usedSlots = int.Parse(match.Groups["used"].Value, CultureInfo.InvariantCulture);
					maxSlots = int.Parse(match.Groups["max"].Value, CultureInfo.InvariantCulture);
					availableSlots = 0;
				}
			}

			var lastExecution = autoFarmLogs.LastOrDefault(log => log.Message.Contains("Running autofarm", StringComparison.OrdinalIgnoreCase));
			var afterExecution = lastExecution == null ? new List<LogRecord>() : logs.Where(log => log.DateTimeUtc >= lastExecution.DateTimeUtc).ToList();
			var latestReportProcessing = afterExecution.LastOrDefault(log => log.Message.Contains("Processing espionage reports", StringComparison.OrdinalIgnoreCase));
			var probes = afterExecution.Count(log => log.Sender.Equals("FleetScheduler", StringComparison.OrdinalIgnoreCase) && log.Message.Contains("Mission: Spy", StringComparison.OrdinalIgnoreCase));
			if (latestReportProcessing != null && latestReportProcessing.DateTimeUtc > (lastExecution?.DateTimeUtc ?? DateTime.MinValue))
				probes = 0;

			var attacks = afterExecution.Count(log => log.Sender.Equals("FleetScheduler", StringComparison.OrdinalIgnoreCase) && log.Message.Contains("Mission: Attack", StringComparison.OrdinalIgnoreCase));
			return new AutoFarmSlotStatus {
				MaxSlots = maxSlots,
				UsedSlots = usedSlots,
				AvailableSlots = availableSlots,
				ProbesInFlight = probes,
				AttacksInFlight = attacks
			};
		}

		private static List<LogRecord> ReadLogs() {
			var result = new List<LogRecord>();
			var logsPath = string.IsNullOrWhiteSpace(SettingsService.LogsPath)
				? Path.Combine(AppContext.BaseDirectory, "log")
				: SettingsService.LogsPath;
			var filePath = Path.Combine(logsPath, $"TBot{DateTime.Now:yyyyMMdd}.csv");
			if (!File.Exists(filePath))
				return result;

			try {
				using var file = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				using var stream = new StreamReader(file, Encoding.Default);
				using var csv = new CsvReader(stream, CultureInfo.InvariantCulture);
				foreach (var entry in csv.GetRecords<LogEntry>()) {
					if (!DateTime.TryParse(entry.datetime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var localTime))
						continue;
					result.Add(new LogRecord(entry.type ?? "", entry.sender ?? "", localTime, entry.message ?? ""));
				}
			} catch {
				return new List<LogRecord>();
			}

			return result.OrderBy(log => log.DateTimeUtc).ToList();
		}

		private static List<AutoFarmLogRow> ToLogRows(IEnumerable<LogRecord> logs) {
			return logs.Select(log => new AutoFarmLogRow {
				Level = log.Level,
				Sender = log.Sender,
				DateTimeUtc = log.DateTimeUtc.ToUniversalTime(),
				Message = log.Message,
				CopyText = $"[{log.DateTimeUtc:yyyy-MM-dd HH:mm:ss zzz}] [{log.Sender}] [{log.Level}] {log.Message}"
			}).ToList();
		}

        private static Dictionary<string, string> BuildTargetReasons(List<LogRecord> logs, string instanceAlias) {
            var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var log in logs.Where(log => log.Sender.Equals("AutoFarm", StringComparison.OrdinalIgnoreCase))) {
                var instance = Regex.Match(log.Message, @"\binstance=(?<alias>""(?:\\.|[^""\\])*"")");
                if (!instance.Success || JToken.Parse(instance.Groups["alias"].Value).Value<string>() != instanceAlias) continue;
                var target = Regex.Match(log.Message, @"\bTarget (?<coord>\[[MP]:\d+:\d+:\d+\])");
                var coordinate = target.Success ? target.Groups["coord"].Value : null;
                var report = Regex.Match(log.Message, @"\breport=(?<id>\d+)\b");
                // A coordinate or the last global log alone cannot identify a report.
                if (coordinate == null || !report.Success) continue;
                string reason = log.Message.Contains("Cargo minimum not met", StringComparison.OrdinalIgnoreCase)
                    ? "Target cargo requirement below MinCargosToSend"
                    : log.Message.Contains("Cargo stock empty", StringComparison.OrdinalIgnoreCase)
                    ? "No cargo ships read at origin"
                    : log.Message.Contains("Cargo reserve blocks dispatch", StringComparison.OrdinalIgnoreCase)
                    ? "Cargo stock reserved by MinCargosToKeep"
                    : log.Message.Contains("Cargo stock below minimum", StringComparison.OrdinalIgnoreCase)
                    ? "Available cargo stock below MinCargosToSend" : null;
                if (reason != null) reasons[coordinate + "#" + report.Groups["id"].Value] = reason;
            }
            return reasons;
        }

		private static (string Label, string Detail) ResolveTargetReason(string state, string coordinate, int reportId, int? consumedReportId, ISet<int> consumedReportIds, IReadOnlyDictionary<int, string> processedReportOutcomes, Dictionary<string, BlacklistEntry> blacklist, Dictionary<string, string> logReasons) {
			if (state == "AttackPending" && logReasons.TryGetValue(coordinate + "#" + reportId, out var loggedReason))
				return (loggedReason, "Recorded for this report in the AutoFarm log");
			if (state == "AttackSent")
				return ("Attack sent", "Fleet dispatch was recorded");
			if (reportId > 0 && processedReportOutcomes.TryGetValue(reportId, out var processedOutcome) &&
				string.Equals(processedOutcome, ManualReviewDefenseOutcome, StringComparison.OrdinalIgnoreCase))
				return ("Manual review: defense detected", "AutoFarm kept this espionage report for manual handling");
			if (blacklist.TryGetValue(coordinate, out var entry))
				return ($"Blacklisted until {entry.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}", $"Reason: {entry.Reason}");
			if (reportId > 0 && ((consumedReportId.HasValue && consumedReportId.Value == reportId) || consumedReportIds.Contains(reportId)))
				return ("Report already processed", $"Report #{reportId} is recorded as consumed");

			return state switch {
				"AttackPending" => ("Attack pending", "Waiting for a valid attack dispatch"),
				"NotSuitable" => ("Not suitable", "Report did not meet AutoFarm requirements"),
				"ProbesSent" => ("Waiting for report", "Espionage fleet is returning"),
				"ProbesRequired" => ("More probes required", "The report did not contain enough information"),
				"FailedProbesRequired" => ("More probes required", "The previous probe attempt was insufficient"),
				_ => (state, "No additional reason was recorded")
			};
		}

		private static Dictionary<string, BlacklistEntry> ReadBlacklist(string instanceAlias) {
			var result = new Dictionary<string, BlacklistEntry>(StringComparer.OrdinalIgnoreCase);
			var path = Path.Combine(AppContext.BaseDirectory, "data", $"autofarm_blacklist_{Path.GetFileName(instanceAlias)}.json");
			if (!File.Exists(path))
				return result;

			try {
				var entries = JArray.Parse(File.ReadAllText(path));
				foreach (var item in entries.OfType<JObject>()) {
					var coordinate = item["Coordinate"] as JObject;
					if (coordinate == null)
						continue;
					var galaxy = coordinate.Value<int?>("Galaxy");
					var system = coordinate.Value<int?>("System");
					var position = coordinate.Value<int?>("Position");
					var celestialType = coordinate.Value<int?>("Type") ?? coordinate.Value<int?>("CelestialType") ?? (int)Celestials.Planet;
					if (!galaxy.HasValue || !system.HasValue || !position.HasValue)
						continue;
					if (!DateTime.TryParse(item.Value<string>("ExpiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt))
						continue;
					result[FormatCoordinate(galaxy.Value, system.Value, position.Value, celestialType)] = new BlacklistEntry(item.Value<string>("Reason") ?? "Unknown", expiresAt);
				}
			} catch {
				return new Dictionary<string, BlacklistEntry>(StringComparer.OrdinalIgnoreCase);
			}
			return result;
		}

		private static DashboardSettings ReadSettings(string instanceAlias) {
			var settings = new DashboardSettings();
			try {
				var globalPath = SettingsService.GlobalSettingsPath;
				if (string.IsNullOrWhiteSpace(globalPath) || !File.Exists(globalPath))
					globalPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
				var global = JObject.Parse(File.ReadAllText(globalPath));
				var instancePath = ResolveInstanceSettingsPath(global, instanceAlias, Path.GetDirectoryName(globalPath));
				if (!File.Exists(instancePath))
					return settings;
				var instance = JObject.Parse(File.ReadAllText(instancePath));
				var autoFarm = instance["AutoFarm"] as JObject;
				if (autoFarm == null)
					return settings;

				settings.MaxSlots = autoFarm.Value<int?>("MaxSlots") ?? 0;
				settings.SystemDataDays = Math.Max(1, autoFarm.Value<int?>("DaysToKeepOldSystemData") ?? 7);
				settings.EmptySystemCooldownDays = Math.Max(1, autoFarm.Value<int?>("EmptySystemCooldownDays") ?? 30);
				foreach (var range in autoFarm["ScanRange"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) {
					var galaxy = range.Value<int?>("Galaxy");
					var start = range.Value<int?>("StartSystem");
					var end = range.Value<int?>("EndSystem");
					if (galaxy.HasValue && start.HasValue && end.HasValue)
						settings.Ranges.Add(new DashboardRange(galaxy.Value, Math.Min(start.Value, end.Value), Math.Max(start.Value, end.Value)));
				}
			} catch {
				return settings;
			}
			return settings;
		}

		private static string ResolveInstanceSettingsPath(JObject global, string instanceAlias, string? globalDirectory) {
			foreach (var instance in global["Instances"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) {
				if (!string.Equals(instance.Value<string>("Alias"), instanceAlias, StringComparison.OrdinalIgnoreCase))
					continue;
				var configured = instance.Value<string>("Settings");
				if (string.IsNullOrWhiteSpace(configured))
					break;
				return Path.IsPathRooted(configured) ? configured : Path.GetFullPath(Path.Combine(globalDirectory ?? AppContext.BaseDirectory, configured));
			}
			return Path.Combine(AppContext.BaseDirectory, "instance_settings.json");
		}

		private static DateTime? ParseNextExecution(string message) {
			const string marker = "Next autofarm check at ";
			var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
			if (start < 0)
				return null;
			start += marker.Length;
			var end = message.IndexOf(" (", start, StringComparison.Ordinal);
			var value = end > start ? message[start..end] : message[start..];
			return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
		}

		private static bool IsError(LogRecord log) => log.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) || log.Level.Equals("Warning", StringComparison.OrdinalIgnoreCase) || log.Message.Contains("Exception", StringComparison.OrdinalIgnoreCase);

		private static bool IsAutoFarmLog(LogRecord log) => log.Sender.Equals("AutoFarm", StringComparison.OrdinalIgnoreCase);

		private static string? ExtractCoordinate(string message) {
			var match = CoordinatePattern.Match(message ?? string.Empty);
			return match.Success ? $"[{NormalizeCoordinateType(match.Groups["type"].Value)}:{match.Groups["galaxy"].Value}:{match.Groups["system"].Value}:{match.Groups["position"].Value}]" : null;
		}

		private static string NormalizeCoordinateType(string type) => type.Equals("M", StringComparison.OrdinalIgnoreCase) ? "M" : "P";

		private static JObject? TryParseReport(string json) {
			try { return JObject.Parse(json); } catch { return null; }
		}

		private static DateTime? ParseDate(string? value) {
			return string.IsNullOrWhiteSpace(value) || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
				? null
				: parsed.ToUniversalTime();
		}

		private static string GetStateName(int state) => Enum.IsDefined(typeof(FarmState), state) ? ((FarmState)state).ToString() : "Unknown";

		private static string FormatCoordinate(int galaxy, int system, int position, int celestialType) {
			var code = (Celestials)celestialType == Celestials.Moon ? "M" : "P";
			return $"[{code}:{galaxy}:{system}:{position}]";
		}

		private static string GetDatabasePath(string instanceAlias) {
			return Path.Combine(AppContext.BaseDirectory, "data", Path.GetFileName($"autofarm_{instanceAlias}.db"));
		}

		private static bool HasTable(SqliteConnection connection, string tableName) {
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
			command.Parameters.AddWithValue("$name", tableName);
			return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
		}

		private static bool HasColumn(SqliteConnection connection, string tableName, string columnName) {
			using var command = connection.CreateCommand();
			command.CommandText = $"PRAGMA table_info([{tableName}]);";
			using var reader = command.ExecuteReader();
			while (reader.Read())
				if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
					return true;
			return false;
		}

		private sealed class LogEntry {
			public string type { get; set; } = string.Empty;
			public string sender { get; set; } = string.Empty;
			public string datetime { get; set; } = string.Empty;
			public string message { get; set; } = string.Empty;
		}

		private sealed record LogRecord(string Level, string Sender, DateTime DateTimeUtc, string Message);
		private sealed record BlacklistEntry(string Reason, DateTime ExpiresAtUtc);
		private sealed record AutoFarmScanCursorRow(int RangeIndex, int Galaxy, int System, DateTime? UpdatedAtUtc);
		private sealed record DashboardRange(int Galaxy, int StartSystem, int EndSystem);

		private sealed class DashboardSettings {
			public int MaxSlots { get; set; }
			public int SystemDataDays { get; set; } = 7;
			public int EmptySystemCooldownDays { get; set; } = 30;
			public List<DashboardRange> Ranges { get; } = new();
		}
	}

	public sealed class AutoFarmDashboardStorage {
		public bool DatabaseAvailable { get; set; }
		public int CachedSystemCount { get; set; }
		public DateTime? LastSystemObservedAtUtc { get; set; }
		public AutoFarmWorkerStatus Worker { get; set; } = new();
		public AutoFarmSlotStatus Slots { get; set; } = new();
		public AutoFarmScanStatus Scan { get; set; } = new();
		public List<AutoFarmSystemRow> Systems { get; } = new();
		public List<AutoFarmReportRow> Reports { get; } = new();
		public List<AutoFarmAttackRow> Attacks { get; } = new();
		public List<AutoFarmLogRow> Logs { get; } = new();
		public List<AutoFarmLogRow> Errors { get; } = new();
	}
}
