# AutoFarm combat planner

> **Beta:** Safe and Balanced are experimental. Both can lose ships. Start in observation mode; user feedback is needed before production reliability can be established.

## Enable

Update TBot and OGameD together. Merge into your existing `AutoFarm` settings:

```json
{
  "AttackTargetsWithFleet": true,
  "AttackDefendedTargets": true,
  "CombatPlanner": {
    "Active": true,
    "ObservationOnly": true,
    "Profile": "Safe",
    "Objective": "ProfitPerHour",
    "MinimumNetProfit": 100000,
    "MaxReportAgeSeconds": 300
  }
}
```

Observation records predictions without AutoFarm attacks, probes or report deletion. Set `ObservationOnly: false` to allow attacks. Other enabled bot features run independently. The planner is disabled by default; when enabled, a planning failure never switches to ratio-based attacks.

## Profiles

- **Safe:** all confirmation simulations must win without losses.
- **Balanced:** all must win; sampled losses are capped at the smaller of 1% of deployed value and 100,000 resources by default.
- **Ranking:** `ProfitPerHour` accounts for return flight time; `NetProfit` ranks profit per attack. Both deduct fuel and sampled losses.

The planner compares available fleets, origins and speeds, then checks surviving cargo capacity. Existing fleet/defence limits, reserves and slot limits still apply. No feasible plan leaves the target pending.

## Limits

Defender combat research, classes and per-unit bonuses must be present in a recent espionage report. HTML decimals are preserved; missing, malformed or unsupported data blocks the attack. Default report age: five minutes.

At least 299 independent confirmation simulations run by default. Successful samples do not guarantee the next battle: random results, changed targets and model limits remain. ACS, missile strikes and the General fighter/Deathstar special shot are unsupported; automatic compositions involving that special shot are rejected.

Optional settings: `MaxLossPercent`, `MaxAbsoluteLoss` (100000), `MaxFailureProbabilityPercent` (1), `SimulationRuns` (128) and `MaxPlanningSeconds` (30). The probability setting controls sampling within the model, not real-world certainty.

## Feedback

Predictions are stored in `data/autofarm_combat_<instance-alias>.jsonl`. Share the profile, settings, matching prediction and combat result; remove credentials and private player information. Journal entries are predictions, not dispatch receipts.

To compare a result, provide `PredictionId`, `Source`, the unchanged `SimulationInput`, and `Observed` with `Outcome`, `AttackerLoss` and `Rounds`:

```powershell
python scripts/compare-combat-observation.py --journal journal.jsonl --result result.json --output comparison.json
```

Exit codes: 0 within sampled bounds, 2 outside, 1 invalid data. A match does not certify accuracy.
