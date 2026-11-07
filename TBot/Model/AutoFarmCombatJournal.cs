using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	/// <summary>Append-only prediction evidence; writing failures propagate to the caller.</summary>
	public sealed class AutoFarmCombatJournal {
		private readonly string _path;
		public AutoFarmCombatJournal(string alias) {
			if (string.IsNullOrWhiteSpace(alias) || alias != Path.GetFileName(alias) || alias.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				throw new ArgumentException("Invalid combat journal alias.");
			_path = Path.Combine(AppContext.BaseDirectory, "data", $"autofarm_combat_{alias}.jsonl");
		}

		public string Append(EspionageReport report, AutoFarmCombatSettings settings, AutoFarmCombatContext context,
			AutoFarmCombatPlan plan, string decision) {
			var id = Guid.NewGuid().ToString("N");
			var record = new {
				SchemaVersion = 1, Id = id, RecordedAtUtc = DateTime.UtcNow,
				Mode = settings.ObservationOnly ? "Observe" : "DispatchCandidate", Decision = decision,
				Report = report, Settings = settings, Context = context,
				Origin = plan?.Origin?.Coordinate, Ships = plan?.Ships, Speed = plan?.Speed,
				Fuel = plan?.Fuel, FlightTime = plan?.FlightTime, NetProfit = plan?.NetProfit,
				SimulationInput = plan?.SimulationInput, Simulation = plan?.Simulation,
				ModelFailureUpperBoundPercent = plan == null ? (double?)null : AutoFarmCombatSettings.FailureUpperBoundPercent(plan.Simulation.Runs),
				ConfidencePercent = 95,
				Limitations = "Conditional on the ordinary-combat model and HTML precision; sampled maxima are not guarantees."
			};
			var bytes = new UTF8Encoding(false, true).GetBytes(JsonConvert.SerializeObject(record) + "\n");
			Directory.CreateDirectory(Path.GetDirectoryName(_path));
			using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
			stream.Write(bytes);
			stream.Flush(flushToDisk: true);
			return id;
		}
	}
}
