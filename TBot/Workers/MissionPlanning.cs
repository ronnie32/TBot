using System;
using System.Collections.Generic;
using System.Linq;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	/// <summary>
	/// Chooses expedition origins one dispatch at a time. An origin that failed in
	/// the current execution is never selected again until the next execution, while
	/// successful origins can be used in another fair round when their cap allows it.
	/// </summary>
	public sealed class ExpeditionDispatchPlanner {
		private readonly int[] _initialActiveCounts;
		private readonly int[] _localDispatchCounts;
		private readonly int[] _activeCounts;
		private readonly bool[] _unavailable;
		private readonly bool[] _attemptedThisRound;
		private readonly int _maxPerOrigin;
		private readonly int[] _selectionOrder;
		private readonly int[] _selectionRanks;
		private int _cursor;
		private int _successfulDispatchesThisRound;

		public ExpeditionDispatchPlanner(
			IReadOnlyList<int> activeCounts,
			int maxPerOrigin,
			int startIndex = 0,
			IReadOnlyList<int> selectionOrder = null) {
			if (activeCounts == null || activeCounts.Count == 0)
				throw new ArgumentException("At least one expedition origin is required.", nameof(activeCounts));

			_maxPerOrigin = Math.Max(0, maxPerOrigin);
			_initialActiveCounts = activeCounts.Select(count => Math.Max(0, count)).ToArray();
			_localDispatchCounts = new int[activeCounts.Count];
			_activeCounts = (int[])_initialActiveCounts.Clone();
			_unavailable = new bool[activeCounts.Count];
			_attemptedThisRound = new bool[activeCounts.Count];
			_selectionOrder = NormalizeSelectionOrder(selectionOrder, activeCounts.Count);
			_selectionRanks = new int[activeCounts.Count];
			for (var rank = 0; rank < _selectionOrder.Length; rank++)
				_selectionRanks[_selectionOrder[rank]] = rank;
			_cursor = _selectionRanks[NormalizeIndex(startIndex, activeCounts.Count)];
		}

		private ExpeditionDispatchPlanner(ExpeditionDispatchPlanner source) {
			_initialActiveCounts = (int[])source._initialActiveCounts.Clone();
			_localDispatchCounts = (int[])source._localDispatchCounts.Clone();
			_activeCounts = (int[])source._activeCounts.Clone();
			_unavailable = (bool[])source._unavailable.Clone();
			_attemptedThisRound = (bool[])source._attemptedThisRound.Clone();
			_selectionOrder = (int[])source._selectionOrder.Clone();
			_selectionRanks = (int[])source._selectionRanks.Clone();
			_maxPerOrigin = source._maxPerOrigin;
			_cursor = source._cursor;
			_successfulDispatchesThisRound = source._successfulDispatchesThisRound;
		}

		/// <summary>
		/// Counts this origin's pending sends, including its already selected current
		/// send. Forecasts successful dispatches on a copy, preserving live round,
		/// cursor, exclusions and local confirmations without any network requests.
		/// </summary>
		public int CountPendingDispatches(int originIndex, int requested) {
			ValidateIndex(originIndex);
			if (requested <= 0 || _unavailable[originIndex] || _activeCounts[originIndex] >= _maxPerOrigin)
				return 0;

			var forecast = new ExpeditionDispatchPlanner(this);
			var count = 1;
			forecast.MarkSent(originIndex);
			for (var sent = 1; sent < requested;) {
				var next = forecast.SelectNextOrigin();
				if (!next.HasValue) {
					if (!forecast.BeginNextRound())
						break;
					continue;
				}
				if (next.Value == originIndex)
					count++;
				forecast.MarkSent(next.Value);
				sent++;
			}
			return count;
		}

		public int? SelectNextOrigin() {
			var selectedIndex = -1;
			var selectedLoad = int.MaxValue;
			var selectedDistance = int.MaxValue;

			for (var index = 0; index < _activeCounts.Length; index++) {
				if (_unavailable[index] || _attemptedThisRound[index] || _activeCounts[index] >= _maxPerOrigin)
					continue;

				var distance = (_selectionRanks[index] - _cursor + _activeCounts.Length) % _activeCounts.Length;
				if (_activeCounts[index] < selectedLoad ||
					(_activeCounts[index] == selectedLoad && distance < selectedDistance)) {
					selectedIndex = index;
					selectedLoad = _activeCounts[index];
					selectedDistance = distance;
				}
			}

			if (selectedIndex < 0)
				return null;

			_attemptedThisRound[selectedIndex] = true;
			return selectedIndex;
		}

		public void MarkUnavailable(int originIndex) {
			ValidateIndex(originIndex);
			_unavailable[originIndex] = true;
		}

		public void MarkSent(int originIndex) {
			ValidateIndex(originIndex);
			_activeCounts[originIndex]++;
			_localDispatchCounts[originIndex]++;
			_successfulDispatchesThisRound++;
			_cursor = (_selectionRanks[originIndex] + 1) % _activeCounts.Length;
		}

		/// <summary>
		/// Synchronizes with the server without forgetting local successful dispatches.
		/// A stale fleet response must not make the planner send a duplicate fleet.
		/// </summary>
		public void SynchronizeActiveCounts(IReadOnlyList<int> activeCounts) {
			if (activeCounts == null || activeCounts.Count != _activeCounts.Length)
				throw new ArgumentException("The active-origin count must match the planner.", nameof(activeCounts));

			for (var index = 0; index < _activeCounts.Length; index++)
				_activeCounts[index] = Math.Max(
					Math.Max(0, activeCounts[index]),
					_initialActiveCounts[index] + _localDispatchCounts[index]);
		}

		/// <summary>
		/// Starts another fair round only if the previous round actually dispatched a
		/// fleet. Failed origins remain unavailable for this execution.
		/// </summary>
		public bool BeginNextRound() {
			if (_successfulDispatchesThisRound == 0)
				return false;

			_successfulDispatchesThisRound = 0;
			for (var index = 0; index < _attemptedThisRound.Length; index++)
				_attemptedThisRound[index] = false;

			return HasSelectableOrigin();
		}

		private bool HasSelectableOrigin() {
			for (var index = 0; index < _activeCounts.Length; index++) {
				if (!_unavailable[index] && !_attemptedThisRound[index] && _activeCounts[index] < _maxPerOrigin)
					return true;
			}

			return false;
		}

		private void ValidateIndex(int originIndex) {
			if (originIndex < 0 || originIndex >= _activeCounts.Length)
				throw new ArgumentOutOfRangeException(nameof(originIndex));
		}

		private static int NormalizeIndex(int index, int count) {
			var normalized = index % count;
			return normalized < 0 ? normalized + count : normalized;
		}

		private static int[] NormalizeSelectionOrder(IReadOnlyList<int> selectionOrder, int count) {
			var order = new List<int>(count);
			if (selectionOrder != null) {
				foreach (var index in selectionOrder) {
					if (index >= 0 && index < count && !order.Contains(index))
						order.Add(index);
				}
			}

			for (var index = 0; index < count; index++) {
				if (!order.Contains(index))
					order.Add(index);
			}

			return order.ToArray();
		}
	}

	/// <summary>
	/// Gives each discovery origin one dispatch opportunity per round. Origins that
	/// cannot provide discovery ships are removed for the current execution; a
	/// position that is merely unavailable is allowed to advance and be retried in
	/// the next round.
	/// </summary>
	public sealed class DiscoveryDispatchPlanner {
		private readonly bool[] _unavailable;
		private readonly bool[] _attemptedThisRound;
		private int _nextIndex;
		private int _successfulDispatchesThisRound;
		private bool _retryablePositionThisRound;

		public DiscoveryDispatchPlanner(int originCount, int startIndex = 0) {
			if (originCount <= 0)
				throw new ArgumentOutOfRangeException(nameof(originCount));

			_unavailable = new bool[originCount];
			_attemptedThisRound = new bool[originCount];
			_nextIndex = NormalizeIndex(startIndex, originCount);
		}

		public int? SelectNextOrigin() {
			for (var offset = 0; offset < _attemptedThisRound.Length; offset++) {
				var index = (_nextIndex + offset) % _attemptedThisRound.Length;
				if (_unavailable[index] || _attemptedThisRound[index])
					continue;

				_attemptedThisRound[index] = true;
				return index;
			}

			return null;
		}

		public void MarkUnavailable(int originIndex) {
			ValidateIndex(originIndex);
			_unavailable[originIndex] = true;
		}

		public void MarkSent(int originIndex) {
			ValidateIndex(originIndex);
			_successfulDispatchesThisRound++;
			_nextIndex = (originIndex + 1) % _attemptedThisRound.Length;
		}

		public void MarkPositionUnavailable(int originIndex) {
			ValidateIndex(originIndex);
			_retryablePositionThisRound = true;
		}

		/// <summary>
		/// Starts another round after a successful dispatch or a position rejection
		/// that advanced an origin cursor. An all-empty cycle therefore ends without
		/// repeatedly querying the same systems.
		/// </summary>
		public bool BeginNextRound() {
			if (_successfulDispatchesThisRound == 0 && !_retryablePositionThisRound)
				return false;

			_successfulDispatchesThisRound = 0;
			_retryablePositionThisRound = false;
			for (var index = 0; index < _attemptedThisRound.Length; index++)
				_attemptedThisRound[index] = false;

			return HasSelectableOrigin();
		}

		private bool HasSelectableOrigin() {
			for (var index = 0; index < _unavailable.Length; index++) {
				if (!_unavailable[index])
					return true;
			}

			return false;
		}

		private void ValidateIndex(int originIndex) {
			if (originIndex < 0 || originIndex >= _unavailable.Length)
				throw new ArgumentOutOfRangeException(nameof(originIndex));
		}

		private static int NormalizeIndex(int index, int count) {
			var normalized = index % count;
			return normalized < 0 ? normalized + count : normalized;
		}
	}

	public static class ExpeditionPayloadPolicy {
		public static long GetDeuteriumPayload(long requested, long available, long flightFuel, long cargoCapacity) {
			var remainingAfterFuel = Math.Max(0, available - Math.Max(0, flightFuel));
			return Math.Max(0, Math.Min(Math.Max(0, requested), Math.Min(remainingAfterFuel, Math.Max(0, cargoCapacity))));
		}
	}

	/// <summary>
	/// Publishes a new fleet snapshot without mutating a list that another worker may
	/// currently be enumerating. Fleet snapshots are shared between workers, so a
	/// successful dispatch must be represented by replacement rather than Add().
	/// </summary>
	public static class FleetSnapshotPolicy {
		public static List<Fleet> AddDispatchedFleet(IEnumerable<Fleet> existing, Fleet dispatched) {
			var snapshot = (existing ?? Enumerable.Empty<Fleet>())
				.Where(fleet => fleet != null)
				.ToList();

			if (dispatched != null && !snapshot.Any(fleet => fleet.ID == dispatched.ID))
				snapshot.Add(dispatched);

			return snapshot;
		}
	}

	/// <summary>
	/// Selects discovery positions reported as available by the system view.
	/// Positions not returned by that view must never be sent blindly.
	/// </summary>
	public static class DiscoverySystemPlanner {
		public static IReadOnlyList<int> SelectAvailablePositions(
			IEnumerable<int> availablePositions,
			int nextPosition,
			IReadOnlySet<int> blacklistedPositions,
			int maximum) {
			if (availablePositions == null || maximum <= 0)
				return Array.Empty<int>();

			var minimum = Math.Clamp(nextPosition, 1, 15);
			return availablePositions
				.Where(position => position >= minimum && position <= 15)
				.Where(position => blacklistedPositions == null || !blacklistedPositions.Contains(position))
				.Distinct()
				.OrderBy(position => position)
				.Take(maximum)
				.ToList();
		}
	}

	/// <summary>
	/// Selects the closest configured origin for one shared discovery-system cursor.
	/// Discovery positions are account-wide, so origins must not carry independent
	/// target progress for the same galaxy.
	/// </summary>
	public static class DiscoveryOriginSelectionPolicy {
		public static int? SelectClosest(
			IReadOnlyList<int> originSystems,
			int targetSystem,
			int maxSystem,
			int tieBreakStartIndex = 0,
			ISet<int> excludedOrigins = null) {
			if (originSystems == null || originSystems.Count == 0 || maxSystem <= 0)
				return null;

			var normalizedTarget = NormalizeSystem(targetSystem, maxSystem);
			var normalizedStart = ((tieBreakStartIndex % originSystems.Count) + originSystems.Count) % originSystems.Count;
			return originSystems
				.Select((system, index) => new { system, index })
				.Where(item => excludedOrigins == null || !excludedOrigins.Contains(item.index))
				.Select(item => new {
					item.index,
					distance = CircularDistance(NormalizeSystem(item.system, maxSystem), normalizedTarget, maxSystem),
					tieBreak = (item.index - normalizedStart + originSystems.Count) % originSystems.Count
				})
				.OrderBy(item => item.distance)
				.ThenBy(item => item.tieBreak)
				.Select(item => (int?)item.index)
				.FirstOrDefault();
		}

		private static int NormalizeSystem(int system, int maxSystem) {
			if (system < 1)
				return 1;
			return ((system - 1) % maxSystem) + 1;
		}

		private static int CircularDistance(int first, int second, int maxSystem) {
			var direct = Math.Abs(first - second);
			return Math.Min(direct, maxSystem - direct);
		}
	}

	/// <summary>
	/// Cursor for one discovery origin. Every examined position must be committed,
	/// including blacklisted and rejected positions; otherwise the worker can loop
	/// forever on the last position of a system.
	/// </summary>
	public sealed class DiscoveryCursorState {
		public int System { get; private set; }
		public int NextPosition { get; private set; }

		public DiscoveryCursorState(int system, int nextPosition = 1) {
			System = system;
			NextPosition = nextPosition;
		}

		public void Normalize(int fallbackSystem, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;

			if (System < 1 || System > maxSystem)
				System = NormalizeSystem(fallbackSystem, maxSystem);

			if (NextPosition < 1 || NextPosition > 15)
				NextPosition = 1;
		}

		public void CommitPosition(int system, int position, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;

			if (position >= 15) {
				System = system >= maxSystem ? 1 : system + 1;
				NextPosition = 1;
				return;
			}

			System = NormalizeSystem(system, maxSystem);
			NextPosition = Math.Max(1, position + 1);
		}

		public void SkipSystem(int system, int maxSystem) {
			if (maxSystem < 1)
				maxSystem = 1;

			System = system >= maxSystem ? 1 : Math.Max(1, system + 1);
			NextPosition = 1;
		}

		private static int NormalizeSystem(int system, int maxSystem) {
			if (system < 1)
				return 1;
			if (system > maxSystem)
				return ((system - 1) % maxSystem) + 1;
			return system;
		}
	}
}
