using System;
using System.Collections.Generic;
using Tbot.Workers;
using Xunit;

namespace TBot.Tests {
	public class ExpeditionSharingPlannerTests {
		[Fact]
		public void ForecastIncludesTheSelectedOriginAndDoesNotChangeItsLiveRound() {
			var planner = new ExpeditionDispatchPlanner(new[] { 0, 0 }, 3);
			Assert.Equal(0, planner.SelectNextOrigin());
			Assert.Equal(2, Pending(planner, 0, 3));
			Assert.Equal(2, Pending(planner, 0, 3));
			planner.MarkSent(0);
			Assert.Equal(1, planner.SelectNextOrigin());
			Assert.Equal(1, Pending(planner, 1, 2));
			planner.MarkSent(1);
			Assert.Null(planner.SelectNextOrigin());
			Assert.True(planner.BeginNextRound());
			Assert.Equal(0, planner.SelectNextOrigin());
		}

		[Fact]
		public void ForecastKeepsOriginCapsExcludedOriginsAndConfirmedLocalDispatches() {
			var planner = new ExpeditionDispatchPlanner(new[] { 1, 0, 0 }, 2);
			Assert.Equal(1, planner.SelectNextOrigin());
			planner.MarkUnavailable(1);
			Assert.Equal(2, planner.SelectNextOrigin());
			Assert.Equal(2, Pending(planner, 2, 10));
			planner.MarkSent(2);
			planner.SynchronizeActiveCounts(new[] { 1, 0, 0 });
			Assert.Equal(0, planner.SelectNextOrigin());
			Assert.Equal(1, Pending(planner, 0, 10));
		}

		[Fact]
		public void ForecastMatchesActualDispatchesAcrossLoadAndTieOrder() {
			foreach (var order in new[] { new[] { 0, 1, 2 }, new[] { 2, 0, 1 }, new[] { 1, 2, 0 } }) {
				for (var requested = 1; requested <= 8; requested++) {
					var planner = new ExpeditionDispatchPlanner(new[] { 1, 0, 1 }, 3, 1, order);
					var chosen = planner.SelectNextOrigin().Value;
					var expected = Pending(planner, chosen, requested);
					var sent = new List<int> { chosen };
					planner.MarkSent(chosen);
					while (sent.Count < requested) {
						var next = planner.SelectNextOrigin();
						if (!next.HasValue) {
							if (!planner.BeginNextRound()) break;
							continue;
						}
						sent.Add(next.Value);
						planner.MarkSent(next.Value);
					}
					Assert.Equal(sent.FindAll(index => index == chosen).Count, expected);
				}
			}
		}

		private static int Pending(ExpeditionDispatchPlanner planner, int origin, int requested) {
			return planner.CountPendingDispatches(origin, requested);
		}
	}
}
