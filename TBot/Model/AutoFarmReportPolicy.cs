using System;
using System.Net;
using System.Net.Http;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public enum AutoFarmReportDecisionKind {
		NeedMoreInformation,
		AttackPending,
		NotSuitable
	}

	public sealed record AutoFarmReportDecision(
		AutoFarmReportDecisionKind Kind,
		string Reason,
		BlacklistReason? BlacklistReason = null);

	/// <summary>
	/// Keeps report-list traversal independent from report deletion. Reports are
	/// returned newest first, so the first report already known to AutoFarm is the
	/// durable read boundary. Deletion is best-effort cleanup and must not decide
	/// whether older reports are processed.
	/// </summary>
	public static class AutoFarmReportPolicy {
		public const string ManualReviewDefenseOutcome = "ManualReviewDefense";

		/// <summary>
		/// A report with known planetary defences is intentionally left in OGame
		/// for manual handling. This is separate from AutoFarm's eligibility
		/// decision: the target may be unsuitable for automated cargo attacks, but
		/// the report itself remains useful to the player.
		/// </summary>
		public static bool ShouldPreserveForManualReview(
			EspionageReport report,
			bool keepReportsWithDefense) {
			return ShouldPreserveForManualReview(report, keepReportsWithDefense, false);
		}

		public static bool ShouldPreserveForManualReview(
			EspionageReport report,
			bool keepReportsWithDefense,
			bool ignoreMissiles) {
			return keepReportsWithDefense && report?.HasDefensiveStructures(!ignoreMissiles) == true;
		}

		public static AutoFarmReportDecision Evaluate(
			EspionageReport report,
			long minimumResources,
			string preferredResource,
			CharacterClass playerClass,
			bool attackDefendedTargets,
			bool attackTargetsWithFleet,
			bool ignoreMissiles,
			long maxDefensePoints,
			long maxFleetPoints) {
			if (report == null)
				return new(AutoFarmReportDecisionKind.NotSuitable, "MissingReport");

			if (report.HasInvalidThreatCounts())
				return new(AutoFarmReportDecisionKind.NotSuitable, "MalformedThreatCount");

			var loot = report.Loot(playerClass);
			var meetsMinimum = preferredResource switch {
				"Metal" => loot.Metal > minimumResources,
				"Crystal" => loot.Crystal > minimumResources,
				"Deuterium" => loot.Deuterium > minimumResources,
				_ => loot.TotalResources > minimumResources
			};
			if (!meetsMinimum)
				return new(AutoFarmReportDecisionKind.NotSuitable, "InsufficientResources", BlacklistReason.LowResources);

			if (!report.HasFleetInformation || !report.HasDefensesInformation)
				return new(AutoFarmReportDecisionKind.NeedMoreInformation, "MoreProbeInformationRequired");

			var defensePoints = report.GetDefensePoints(!ignoreMissiles);
			if (defensePoints > 0 && (!attackDefendedTargets || maxDefensePoints > 0 && defensePoints > maxDefensePoints))
				return new(AutoFarmReportDecisionKind.NotSuitable,
					maxDefensePoints > 0 && defensePoints > maxDefensePoints ? "DefenseLimitExceeded" : "DefensesPresent",
					BlacklistReason.HasDefense);

			var fleetPoints = report.GetFleetPoints();
			if (fleetPoints > 0 && (!attackTargetsWithFleet || maxFleetPoints > 0 && fleetPoints > maxFleetPoints))
				return new(AutoFarmReportDecisionKind.NotSuitable,
					maxFleetPoints > 0 && fleetPoints > maxFleetPoints ? "FleetLimitExceeded" : "FleetPresent",
					BlacklistReason.HasFleet);

			return new(AutoFarmReportDecisionKind.AttackPending, "AttackPending");
		}

		public static bool IsManualReviewDefense(string outcome) {
			return string.Equals(outcome, ManualReviewDefenseOutcome, StringComparison.OrdinalIgnoreCase);
		}

		public static bool ShouldStopAfterKnownReport(bool deletionSucceeded) {
			return true;
		}

		/// <summary>
		/// When ProcessAllReports is disabled, a report that was not produced by
		/// AutoFarm must be preserved, but it is not a read boundary. A manual
		/// report can be newer than an AutoFarm report that is still waiting behind
		/// it in the message list.
		/// </summary>
		public static bool ShouldStopAfterUnscannedReport(bool processAllReports) {
			return false;
		}

		/// <summary>
		/// A report that could not be deleted remains the current read boundary.
		/// Stop traversing immediately so the next execution can retry cleanup
		/// before it considers older reports.
		/// </summary>
		public static bool ShouldStopAfterDeletionFailure(bool deletionSucceeded) {
			return !deletionSucceeded;
		}

		/// <summary>
		/// A report is not fully processed while its cleanup is still failing.
		/// Keeping the accumulated result false makes the completed scan remain
		/// pending until the same report can be deleted on a later execution.
		/// </summary>
		public static bool CombineProcessingResult(bool allReportsProcessed, bool deletionSucceeded) {
			return allReportsProcessed && deletionSucceeded;
		}

		public static bool IsPreservedOutsideConfiguredRange(string outcome, bool processAllReports) {
			return !processAllReports
				&& string.Equals(outcome, "NotScanned", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// With ProcessAllReports disabled, a coordinate in the AutoFarm target list
		/// is not enough to prove that a report was produced by AutoFarm. The target
		/// must still be waiting for its own probe, or the report ID must already be
		/// recorded as AutoFarm-owned. ProcessAllReports deliberately keeps the old
		/// opt-in behaviour for inactive reports.
		/// </summary>
		public static bool IsOwnedByAutoFarm(
			FarmTarget target,
			EspionageReport report,
			bool processAllReports) {
			if (target == null || report == null || report.ID <= 0)
				return false;

			if (processAllReports)
				return report.IsInactive;

			if (target.Report?.ID == report.ID || target.ConsumedReportId == report.ID)
				return true;

			return target.State == FarmState.ProbesSent && target.ProbeFleetId.HasValue;
		}

		/// <summary>
		/// A zero retention/cooldown means that a report can be consumed immediately;
		/// it must not make every newly returned report look expired.
		/// </summary>
		public static bool IsExpired(DateTime reportDate, DateTime nowUtc, TimeSpan keepFor) {
			if (keepFor <= TimeSpan.Zero)
				return false;

			return reportDate.ToUniversalTime().Add(keepFor) < nowUtc.ToUniversalTime();
		}

		public static bool IsTransientDeletionFailure(Exception exception) {
			for (var current = exception; current != null; current = current.InnerException) {
				if (current is HttpRequestException httpException && IsTransientStatus(httpException.StatusCode))
					return true;

				var message = current.Message ?? string.Empty;
				if (message.Contains("429", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("500", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("502", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("503", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("504", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("service unavailable", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("temporar", StringComparison.OrdinalIgnoreCase)
					|| message.Contains("connection reset", StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		private static bool IsTransientStatus(HttpStatusCode? statusCode) {
			if (!statusCode.HasValue)
				return false;

			var status = (int) statusCode.Value;
			return status == 429 || status >= 500 && status <= 599;
		}
	}
}
