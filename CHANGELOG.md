# TBot (fork Outgrow1827) — Changelog

Log of changes in our fork. History below v3.5.0-beta4 is inherited from `ronnie32/TBot`'s `fix/ogame-v13-tbot-3.4.7` branch (the base this fork now builds on); the `[Unreleased]` section at the top lists fixes made in this fork that aren't in that upstream history yet.

## [Unreleased] — post-3.5.0-beta5 (Outgrow1827 fork)

- **fix**: deposit-building priority (`CalculationService.GetNextDepositToBuild`) picked Deuterium first every time due to a fixed check order, so Metal Storage never got built even when it was the most urgent one. Now computes each eligible resource's relative deficit (`needed`/`capacity`) and picks whichever is furthest behind its own `DepositHours` target.
- **fix**: AutoFarm's `MinimumPlayerRank` filter (`IsTargetInMinimumRank`) silently defaulted to rank 1 (always passes the filter) when the target was a moon whose sibling planet wasn't in the current scan. Now falls back to the persistent `FarmTargetCache`'s last known player name, then resolves a fresh rank via a new `HighscorePlayerRankCache` (fetches/caches `api/highscore.xml`, category=1/type=0) before giving up.
- **chore**: `autofarm_*.db` (`FarmTargetCache`) and `players_db_*.json` (`PlayersDatabase`) moved into a `data/` subfolder next to `instance_settings.json`, instead of sitting loose in the deploy folder's root.
- **chore**: reordered `General.SlotPriorityLevel` keys in the template (`Brain, Expeditions, AutoFarm, AutoHarvest, AutoColonize, AutoDiscovery`) — no functional effect, just consistency.
- **fix**: `ColonizeWorker`'s static `_galaxyScanCache` (galaxy-occupancy lookup cache added in 3.4.7) only ever overwrote stale entries, never removed them, so a wide `AutoColonize.Targets` range left it growing for the whole process lifetime. `GetGalaxyInfoCached` now actively evicts a stale entry on read instead of leaving it in place, and a 1000-entry hard cap drops the oldest entries if hit, bounding worst-case memory regardless of how wide the configured scan range is.

## v3.5.0-beta4

> **Beta warning:** This release contains a substantial AutoHarvest rework and an Expeditions cache fix. It is still experimental and edge-case bugs may remain. If you need the known functional version, use **v3.4.9**.

### Fixed

#### AutoHarvest

- Reworked target discovery and fleet planning into a deterministic planning phase followed by a separate dispatch phase.
- Fixed `Collection was modified; enumeration operation may not execute` during harvest slot budgeting.
- Prevented the worker from mutating its target collection while deciding which harvest fleets to send.
- Enforced the harvest slot budget before planning and dispatching fleets.
- Prevented duplicate harvest targets and destinations already covered by an active harvest fleet.
- Reused the best available Recycler or Pathfinder origin instead of forcing each target to use the origin that discovered it.
- Reserved ships during planning so one cycle cannot schedule more ships than an origin actually has.
- Used the sending origin’s lifeform cargo bonus when calculating the required fleet size.
- Supported partial harvesting when no origin has enough ships to collect a complete debris field.
- Continued planning other targets when one target has no usable origin or fleet capacity.
- Improved next-cycle scheduling so the worker can continue using available slots instead of waiting unnecessarily for every harvest fleet to return.

#### Expeditions

- Fixed configured Origins being discarded when their cached `Ships` snapshot was empty.
- Refreshed an Origin live immediately before attempting to send from it, allowing fleets that returned while the worker was asleep to be used again.
- Kept the live refresh limited to the Origin being considered, avoiding a full ship refresh on every Origin during every cycle.
- Kept the round-robin and `MaxExpeditionsPerOrigin` planning logic intact while allowing genuinely empty Origins to be skipped for the rest of the cycle.

## v3.5.0-beta3

> **Beta warning:** This release contains further AutoFarm state-management and diagnostic changes. It is still experimental and edge-case bugs may remain. If you need the known functional version, use **v3.4.9**.

### Fixed

#### AutoFarm report handling

- Prevented the same non-actionable espionage report from being processed repeatedly when report deletion fails or OGame keeps the report visible.
- Added null-safe handling when a stored target has no previous report.
- Kept previously processed reports permanently excluded through their report ID.
- Improved handling of interrupted AutoFarm cycles so the worker does not restart from an invalid scan position.
- Persisted the scan cursor when AutoFarm stops because of slot exhaustion, unavailable probes, production constraints or another worker taking the last slot.
- Restored the persisted scan cursor when AutoFarm starts again.

### Added

#### AutoFarm diagnostic dashboard

- Added a read-only AutoFarm diagnostic page focused on explaining worker decisions.
- Added worker status, last activity, next execution and database availability.
- Added AutoFarm slot usage, available slots, probes observed and attacks observed.
- Added scan progress, current cursor, cached systems and last observed system.
- Added target decisions with detailed reasons, including:
  - `Cached empty`
  - `Cached no targets`
  - `Blacklisted until...`
  - `Report already processed`
  - `Attack pending`
  - `Attack sent`
  - `Blocked: ... required`
  - `No suitable origin`
  - `Attack dispatch failed`
- Added cached-system status, last scan date and next refresh date.
- Added recent AutoFarm errors and warnings.
- Added a collapsible recent-log view.
- Added `Copy logs` and `Copy errors` buttons for easier bug reports.
- Added automatic dashboard updates every five seconds without a full page refresh.
- Added the `/AutoFarm/Snapshot` JSON endpoint used by the live dashboard.

#### AutoFarm attack history

- Added the real origin coordinate to newly recorded attack history.
- Added the mission type to newly recorded attack history.
- Kept the history limited to fleets successfully dispatched and recorded by AutoFarm.

### To be determined

- Scan resumption after an abrupt TBot shutdown remains to be monitored in wider use.
- Every slot and probe interruption path remains to be monitored in wider use.
- Dashboard behavior with multiple active instances and continuous log rotation remains to be monitored.
- Historical attack records created before this version cannot recover their original origin; they will display `Unknown origin`.
- Further user testing is required before considering the AutoFarm rework stable.

## v3.5.0-beta2

> **Beta warning:** This release contains a substantial rework of the mine, research, Lifeforms and resource-planning workers. It has been tested with automated tests and live OGame sessions, but regressions or edge-case bugs may still remain.
>
> If you need the known functional stable version, use **v3.4.9**. It remains functional and is the recommended fallback while this beta is being validated.

### Brain planning rework

- Added a centralized account planner for AutoMine, AutoResearch, LifeformsAutoMine and LifeformsAutoResearch.
- Prioritized urgent infrastructure and energy work before ordinary mines, then ranked equivalent candidates by their calculated return or cost.
- Chained each celestial worker's first execution in the planned order, preventing concurrent workers from racing on shared resources while keeping randomized gaps.
- Reused the freshly returned celestial snapshot after every update, including lifeform buildings, lifeform research, facilities, buildings, resources, production and bonuses.
- Reset per-planet lifeform limits and applied the real lifeform cost reductions when comparing construction candidates.
- Added defensive planning guards for incomplete Fast snapshots so a missing optional payload cannot crash AutoMine.
- Fixed the AutoResearch plasma prerequisite check using a logical operator.
- Reworked multiple-origin resource planning to reserve resources and cargo capacity before sending fleets, merge demands for the same destination and reject incomplete batches instead of sending unusable partial transports.
- Fixed multi-origin cargo calculation to use the bonuses of the actual sending origin and stopped the planner from mutating destination snapshots.
- Added regression tests for deterministic construction ordering, resource reservation, multi-origin splitting, incomplete transport batches and sub-threshold allocation rollback.

#### Before vs What to expect now

| Situation | Before | What to expect now |
| --- | --- | --- |
| P1 has a Terraformer candidate while P2 has a profitable Crystal Mine | Each celestial worker could start independently, so the effective order depended on timing and semaphore acquisition. | P1 is selected first because the Terraformer has account-level priority; P2 follows after P1's first action. |
| A priority building competes with a profitable mine | The mine return could determine the order even when infrastructure or energy was the real bottleneck. | Terraformer, energy, storage/crawler and infrastructure needs are considered before ordinary mine ROI. |
| Mine, research and Lifeforms workers run in the same cycle | Workers could read stale data from the previous update or race while using shared resource state. | Each update is based on the latest returned snapshot, and celestial workers are chained so one first action completes before the next begins. |
| Lifeform limits are evaluated across planets | Limits calculated for one planet could leak into the next planet's decision. | Every planet gets a fresh set of lifeform limits and its own bonuses/cost reductions. |
| A Fast planet response lacks optional planning data | AutoMine could throw a null-reference exception while deciding whether to build crawlers or the next building. | The planet is skipped for that planning branch until the required data exists; the worker continues safely. |
| One destination needs 300k resources and two origins can provide 150k each | The same resources could be considered more than once, or a partial fleet could be sent even though the construction still could not start. | The planner reserves 150k from each origin and creates two complete planned shipments. |
| A target needs 100k metal plus 100k crystal, but an origin has only 100k metal | A partial allocation could be sent even though the construction still could not start. | The incomplete allocation is rejected; no known-incomplete batch is sent. |
| A source contributes less than the configured minimum shipment size | A sub-threshold partial allocation could consume simulated capacity and make the rest of the plan incorrect. | The partial allocation is rolled back and the planner tries another valid source or sends nothing. |
| Origin A has a +20% lifeform cargo bonus while Origin B has +0% | Cargo requirements could be calculated with the destination or current celestial's bonus instead of the real sender's bonus. | Each fleet uses the cargo bonus of its actual origin, so A and B are evaluated independently. |

### Artifact inventory

- Added the ogamed v13 artifact inventory endpoint and exposed collected artifacts and capacity to TBot.
- Validated the endpoint against a live Xanthippe account (`272 / 3600` returned successfully).

### AutoFarm report integrity and activity view

- Prevented AutoFarm from processing an espionage report after its report ID has already been used for an attack.
- Kept targets in `AttackSent` while an attack fleet is still in progress, including attacks sent from another configured origin.
- Persisted consumed report IDs and an idempotent attack history in the existing AutoFarm SQLite database.
- Added a read-only English AutoFarm page showing current reports, pending attacks, recent dispatches and the cached system count.

#### Before vs What to expect now

| Situation | Before | What to expect now |
| --- | --- | --- |
| OGame keeps an old espionage report after a delete request fails | The report could be read again in a later cycle and become attackable again. | The report ID is persisted as consumed and is skipped permanently for AutoFarm processing. |
| An attack is still travelling or returning when the next AutoFarm cycle starts | The target could be reset too early and compete with the old report again. | The target stays in `AttackSent` until no matching attack fleet remains. |
| TBot is restarted after an attack | The in-memory target state was not enough to prove that a report had already been used. | The consumed report ledger and attack history are reloaded from SQLite. |
| You need to understand what AutoFarm has done | The information was spread across logs and raw SQLite rows. | The WebUI has a simple read-only AutoFarm page with reports, statuses, coordinates and attack history. |

## v3.4.9

### AutoDiscovery

- Fixed the v13 availability check to use the supported planet-scoped ogamed endpoint.
- Removed the obsolete account-level endpoint so future callers cannot reintroduce the same failure.
- Confirmed with a live Xanthippe account test that AutoDiscovery queries the system view and sends a discovery fleet successfully.

## v3.4.8

### AutoFarm

- Replaced the six-hour JSON empty-system cache with a persistent SQLite state store.
- Added configurable `DaysToKeepOldSystemData` (default: 7) and `EmptySystemCooldownDays` (default: 30).
- Only systems with no planets or only vacation-mode planets receive the long empty-system cooldown.
- Systems with incomplete or failed responses are never cached as empty.
- Persisted viewed systems and farm targets across restarts, while reusing fresh cached system data.
- Counted espionage and attack fleets together against AutoFarm's `MaxSlots` budget.
- Stopped failed fleet submissions from invalidating targets and allowed the worker to continue using remaining slots.
- Continued scanning and scheduling eligible targets instead of sleeping until the longest attack returns.
- Preserved manual and mixed fleets from AutoFarm ownership and fixed circular range progression.

### AutoDiscovery

- Used the system-view discovery availability instead of probing every planet position blindly.
- Persisted a cursor per origin and advanced it after blacklisted, rejected, or failed positions, including the last position of a system.
- Kept account-wide discovery-slot accounting and stopped when no eligible discovery slot remains.

### Release

- Published as a patch release because v3.4.7 is already public.
- Release packages contain clean default settings and the OGame v13-compatible `ogamed` binary.

## v3.4.7

### OGame v13

- Updated the bundled `ogamed` submodule to `ronnie32/ogame.mod` v13-fix.4.
- Fixed expedition fleet composition and origin handling for OGame v13.
- Fixed AutoDiscovery cursor advancement after blacklisted or rejected positions.
- Fixed AutoDiscovery fleet and discovery-slot accounting.

### AutoFarm

- Added persistent cooldowns for systems confirmed to have no eligible targets.
- Avoided refreshing every origin's ship data on every scan cycle.
- Deduplicated configured farm origins and reused the cached fleet state.
- Kept incomplete galaxy responses out of the empty-system cache.
- Continued scanning when an individual system request fails.
- Preserved targets even when another attack to the same target is already in progress.

### Validation

- TBot solution builds successfully with .NET 9.
- TBot unit tests pass.
- ogame bridge tests pass.
- AutoFarm was exercised against a real OGame v13 account: the first empty-system scan created a persistent cache, and the next cycle skipped that system without rescanning or sending a fleet.

## v3.4.7 (Outgrow1827 fork details)

More detailed record of what this fork itself changed for the v3.4.7 release, ahead of and independent from `ronnie32`'s own v3.4.7 entry above.

### Fixed
- **Log-privacy leaks** — `TelegramMessenger.AddTbotInstance`/`RemoveTBotInstance` had their own unguarded player/server name interpolation bypassing `LogPrivacy`, same as `ExpeditionsWorker`'s `[EXP DEBUG]` lines (now routed through `Coordinate.ToString()`, which already masks). `GetCelestials()`'s `AddRange` calls null-guarded — was throwing on every cycle while logged out instead of logging one meaningful warning.
- **`AutoDiscovery` cursor stuck at position 15 when blacklisted** — `resumeNextPos` was only advanced on the successful-send path; the blacklisted-position `continue` left it unchanged, so a blacklisted position 15 made the cursor retry the same position forever instead of moving on.
- **Forced HTTPS redirect breaking the HTTP-only local WebUI** — `UseHttpsRedirection()`/`UseHsts()` were 307-redirecting every visit of a WebUI that's only ever served over plain HTTP, to a scheme nothing listens on.
- **`AutoDefence` production-based calculation restored** (lost in the 3.4.6 rebase) — port of Vesselin Bontchev's Optimal Defense calculator, gated by `Brain.AutoDefence.UseProductionBasedCalculation`. Also restores the `Facilities` fetch `CalcProductionTime` needs (missing = crash) and one-build-order-per-cycle behavior.
- **Self-contained single-file publish config restored**; AutoFarm poll spam fixed; `SlotPriorityLevel.Colonize` renamed back to `AutoColonize`.
- **Log/CSV files writing to exe root instead of `log/` folder** — root cause: `LoggerService<T>` is an open-generic singleton, so static/instance fields declared inside it (`_logPath`, `syncObject`, `_telegramLevelSwitch`, `_telegramAdded`) weren't shared the way they looked like they should be; `Program.cs`'s `logPath` now uses `AppContext.BaseDirectory` instead of the fragile `Directory.GetCurrentDirectory()`, matching the pattern already used for `settingsPath`.

### Added
- **`DefenderWorker` counter-espionage** — `NotifySpyWatch` sends a Telegram alert when someone spies us; `SpyBackAtOrigin` counter-spies the attacker's origin plus every other coordinate recorded for that player (`PlayersDatabase`), each cooldown-limited. Needed `InstanceSettingsPath` made public on `ITBotMain`/`TBotMain` to load the per-instance players DB, and `Coordinate.ToRawString()`/`TryParse()` for privacy-safe persistence.
- **AutoFarm combat-simulation target vetting and defense-probing rework** — new `TryGetAcceptableCombatFleet`/`EstimateDebrisField`/`GetAcceptableFleetLossPercentage` reject farm targets whose predicted loss ratio or loot/risk ratio is unacceptable (`AcceptableFleetLossPercentage`/`MinLootToRiskRatio`). Also reworks the defense-probing flow (send 1 probe as an attack to detect otherwise-undetectable defenses) into a tracked, cached `DefenseProbing` state.
- **Crash self-relaunch** (`TryRelaunchSelfAfterCrash`), replacing the removed `TBot.Watchdog.exe` process — `AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException` handlers log the crash, kill the orphaned `ogamed.exe` child, and relaunch a fresh instance of the same `TBot.exe` with the same args, capped at 5 restarts per 10 minutes to avoid a crash loop. New `--no-crash-restart` flag disables it. Runs entirely in-process, no separate executable involved.
- **`AutoColonize` empty-system targeting, exclude filters, and galaxy-scan cache** — new per-target `TargetEmptySystems`/`EmptySystemsBuffer`/`ExcludeSystems` fields, a top-level `AutoColonize.Exclude` global filter, retry+backoff on `GetGalaxyInfo`, and an in-memory 1h galaxy-scan cache so the same systems aren't re-scanned every cycle.
- **`BrainTransportCoordinator` lock-wait bound** — `ResourceDecisionLock` previously had no timeout, so a stuck network call inside one Brain worker's locked section could block the other 3 indefinitely (matches an observed console freeze). Also: new `BrainTransportCoordinator` shares `Brain.Transports.MaxSlots` across the 4 active Brain items instead of each reading it as its own full budget, and serializes their `Execute()` via a semaphore to stop concurrent double-spend of the same origin celestial's resources; new opt-in `Brain.Transports.StockpileForRoundTrip` sizes a transport to cover every construction that would sequentially run during the fleet's round trip; `AutoFleetJumpGate` pairing fix.
- **New settings**: `ManualModeTimeout`, `Watchdog`, config keys for AutoDefence/AutoFarm/AutoColonize/Defender (`SpyWatch` section, `SpyAttacker.CooldownMinutes`/`MaxKnownCoordinates`, etc. — the repo template was missing several keys the code already read).

### Infrastructure
- **Ported from `net9.0` to `net10.0`** — `TargetFramework` updated across all 4 projects (TBot, TBot.Common, TBot.Ogame.Infrastructure, TBot.WebUI). Removed our own `Extensions.Shuffle<T>` (`OrderBy(rnd.Next())`) — .NET 10 added a native `Enumerable.Shuffle` to LINQ with the same purpose, which made every call site ambiguous (`CS0121`); all callers now use the native, unbiased Fisher-Yates implementation.
- **Build documentation** for Windows and Linux — proper step-by-step for both OSes: installing the .NET 10 SDK (`winget` on Windows, `apt` on Linux), and building/placing `ogamed.exe` first (this fork's daemon must be built from the `ogame` repo's own source, not downloaded from upstream's release page — was previously undocumented, and the old text incorrectly said to grab it from an official release). Both build commands verified end-to-end, including the Linux-specific path.
