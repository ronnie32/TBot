namespace TBot.Model {
	/// <summary>
	/// Decides when a completed scan may be committed as the next cycle.
	/// Report failures and pending attacks leave scan completion uncommitted so
	/// the next execution can finish the pending work before starting over.
	/// </summary>
	public static class AutoFarmCyclePolicy {
		public static bool CanFinalizeFullScan(bool fullScanCompleted, bool reportPhaseCompleted, bool attacksPending = false) {
			return fullScanCompleted && reportPhaseCompleted && !attacksPending;
		}
	}
}
