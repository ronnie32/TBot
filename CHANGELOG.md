# Changelog

## v3.6.0-beta3

> **Beta warning:** Safe and Balanced remain experimental and can lose ships. User feedback is needed.

### Added

#### Defender

- Routine attack checks no longer force a switch away from the player's selected celestial.
- Added optional delayed fleetsave with immediate alerts and a configurable pre-impact safety margin. Disabled by default.

### Fixed

#### AutoFarm

- Distinguish cargo minimum and reserve settings from an actual ship shortage in logs and the dashboard.

Build version: **3.6.0.3**
Release channel: **Beta**

## v3.6.0-beta2

> **Beta warning:** Safe and Balanced are experimental and can lose ships. Start in observation-only mode. User feedback is needed.

### Fixed

#### AutoFarm

- Fixed ship inventory requests losing the origin ID when the planet or moon is missing from the cache.
- Read stock counters by technology ID; reject malformed inventories and responses for another origin instead of using incorrect ship counts.

Build version: **3.6.0.2**
Release channel: **Beta**

## v3.6.0-beta1

> **Beta warning:** Safe and Balanced are experimental and can lose ships. Start in observation-only mode. User feedback is needed: share your profile, settings, prediction and combat result, without private account data.

### Added

#### AutoFarm

- Added optional simulation-based fleet selection with Safe (no sampled losses) and Balanced (limited sampled losses) profiles, ranked by profit or profit per hour.
- Added observation-only mode to review predictions without sending attacks or probes.

### Changed

#### AutoFarm

- Rechecked reports, bonuses, ships, fuel, cargo capacity and fleet slots before dispatch. Missing or unsupported combat data blocks the attack.

### Fixed

#### AutoFarm

- Read defender combat bonuses from espionage report HTML, preserving all reported decimals.
- Corrected Bomber, Deathstar and Reaper rapid-fire values.
- Kept deferred targets pending and prevented automatic fleet-dispatch retries.

Enable `AutoFarm.CombatPlanner.Active`; start with `ObservationOnly: true`. Update TBot and OGameD together. See [configuration](docs/autofarm-combat-planner.md).

Build version: **3.6.0.1**
Release channel: **Beta**

## v3.5.1-beta3

> **Beta release:** This release fixes AutoFarm pending attacks, escort sizing, point limits and defense report retention.

### Fixed

#### AutoFarm

- Fixed pending attacks being delayed by a new galaxy scan after fleet slots run out. Pending targets now resume before scanning, including after a worker restart, and `StopAfterFullScan` waits until they have been dispatched.
- Fixed bomber and reaper escort sizing using OGame score instead of resource value (metal + crystal + deuterium). A fleet worth 37,000,000 resources now requests 740 reapers with `ReapersPer100kFleet: 2`.
- Fixed `MaxDefensePoints` and `MaxFleetPoints` to use the same resource values as escort sizing. Pending targets are checked against the current limits again before dispatch.
- Fixed `KeepReportsWithDefense` preserving low-resource reports. Reports are kept only when their loot exceeds `MinimumResources`, respecting `PreferedResource`, including previously cached retention decisions.

Build version: **3.5.1.3**
Release channel: **Beta**

## v3.5.1-beta2

> **Beta release:** This is a focused fix for AutoFarm cargo calculation.

### Fixed

#### AutoFarm

- Fixed valid targets being skipped with an “Insufficient cargo space after adjustment” message when espionage probes contributed cargo capacity.

Build version: **3.5.1.2**
Release channel: **Beta**

## v3.5.1-beta1

> **Beta warning:** This release changes the AutoDiscovery dispatch workflow and adds Discord notifications and Auction improvements. It is experimental; edge-case bugs may still remain. Use **v3.4.9** if you need the known functional fallback.

### Added

#### AutoDiscovery

- Added system-first dispatch: AutoDiscovery selects the nearest eligible Origin/system pair and fills that system before moving to the next one. Round-robin is used only to break distance ties.
- Added persistent per-galaxy cursors and a system availability cache so discovery progress survives restarts without repeatedly rescanning the same systems.
- Added optional artifact-storage thresholds (`Active`, `StopAt`, `ResumeBelow`) to pause and resume discoveries automatically.

#### AutoFarm

- Added optional handling of targets with defenses or fleets, including configurable defense/fleet point limits, bomber/reaper escort ratios and missile ignoring.

#### Discord notifications

- Added optional Discord webhook notifications configured per instance.
- Added global and feature-level switches for Discord notifications, including Defender and SleepMode.
- Added Discord embeds with event-specific colors, timestamps and a TBot footer.

### Changed

#### AutoDiscovery

- Discovery positions, fleet availability and fleet slots are revalidated immediately before dispatch. Dispatches are serialized through FleetScheduler, and rejected or partial results invalidate only the affected cached state.
- A system-level dispatch is used only when the complete set of currently eligible positions can be sent safely; otherwise positions are sent individually while keeping the system-first order.

#### AutoFarm

- Report processing now tracks report ownership and consumption explicitly, preserves reports intended for manual review, and continues processing eligible reports after a scan completes.
- Transient galaxy-scan failures no longer restart OGameD from inside the AutoFarm worker.

### Fixed

#### Auction

- Fixed the Telegram minimum-bid flow so it uses the current auction resources and multipliers, selects a celestial that can actually cover the bid, and rejects incomplete or invalid auction data cleanly.

Build version: **3.5.1.1**
Release channel: **Beta**

## v3.5.0-beta9

> **Beta warning:** This release contains further OGame v13 discovery and CAPTCHA-flow changes. It is experimental; use **v3.4.9** if you need the known functional fallback.

### Fixed

#### AutoDiscovery

- Stopped reporting the normal in-game redirect returned after a successful OGame v13 discovery dispatch as a false `302` error.
- Stopped blindly retrying state-changing discovery requests; pending, rejected, unavailable-position and session-expired responses are now handled explicitly.
- Serialized discovery dispatches with the other fleet workers and revalidated the available fleet slot immediately before sending.
- Used the discovery page token and accepted only positions that expose an actual lifeform-discovery mission.
- Kept the origin cursor moving after rejected or unavailable positions without permanently hiding a valid origin.

#### CAPTCHA flow

- Preserved the solved CAPTCHA challenge ID through the next Gameforge login instead of losing it during session establishment.
- Reused an already loaded challenge instead of starting a second login flow unnecessarily.
- Made the local image solver deterministic: invalid or unknown challenges now fail clearly instead of selecting a random answer.
- Propagated CAPTCHA and bridge errors with their actual HTTP status and message.

Build version: **3.5.0.9**
Release channel: **Beta**

## v3.5.0-beta8

> **Beta warning:** This release contains a targeted OGame v13 discovery fix. It is still experimental; use **v3.4.9** if you need the known functional fallback.

### Fixed

#### AutoDiscovery

- Fixed intermittent `400 BadRequest` responses when another worker refreshed OGame's shared AJAX token during a discovery dispatch.
- Discovery dispatches now use the token returned with the system page being sent, for both individual and system-wide discoveries.
- Missing page tokens now stop the dispatch cleanly instead of sending a request that OGame will reject.

Build version: **3.5.0.8**
Release channel: **Beta**

## v3.5.0-beta7

> **Beta warning:** This release adds a change to AutoFarm report handling. It is still experimental; use **v3.4.9** if you need the known functional fallback.

### Fixed

#### AutoFarm

- AutoFarm no longer deletes espionage reports that contain known planetary defenses, so they remain available for manual attacks.
- Kept reports with defenses are recorded as `ManualReviewDefense` and skipped on later cycles instead of being repeatedly processed.
- Reports with no actual defensive structures, fleet-only reports, and reports without defense information keep their normal AutoFarm behavior.

### Added

- Added `AutoFarm.KeepReportsWithDefense`, enabled by default. Set it to `false` to restore automatic deletion of defense reports.
- Added an explicit dashboard reason, `Manual review: defense detected`, for reports kept for manual handling.

### OGamed v13 bridge

- Fixed OGame v13 discovery dispatches returning a normal in-game `302` as an error.
- Confirmed the fleet after this redirect without replaying the state-changing request, preventing duplicate discovery fleets.

Build version: **3.5.0.7**
Release channel: **Beta**

## v3.5.0-beta6

> **Beta warning:** This release contains further AutoFarm and Expeditions workflow changes. It is experimental and edge-case bugs may remain. If you need the known functional fallback, use **v3.4.9**.

### Fixed

#### AutoFarm

- AutoFarm now reads espionage reports page by page until the first known AutoFarm boundary, instead of loading the complete mailbox or blindly restarting at page one.
- Prevented report cleanup failures from causing the worker to repeatedly traverse the entire mailbox.
- Kept report IDs protected in the AutoFarm state store when deletion fails, so the same report cannot be attacked twice.
- Preserved reports outside the configured AutoFarm scope without treating them as AutoFarm reports.
- Kept the scan checkpoint pending until the report phase has completed successfully.
- Reopened stale `NotSuitable` targets only after the configured `KeepReportFor` cooldown, so cached resource values are never reused as a fresh attack decision.
- Kept the long empty-system cooldown limited to genuinely empty systems or systems containing only vacation-mode planets.
- Preserved AutoFarm slot ownership across espionage and attack fleets while excluding unrelated manual or mixed fleets.
- Calculated the required cargo fleet from the actual sending Origin's lifeform cargo bonus instead of using the first planet in the account.
- Revalidated the final cargo count and capacity after the Origin refresh, preventing a stale or partially available fleet from being sent with insufficient payload capacity.
- Preferred moons only as a tie-break after distance, keeping the nearest valid farming Origin as the primary choice.

#### Expeditions

- Serialized fleet snapshot, origin selection and dispatch decisions through the FleetScheduler coordinator.
- Selected Origins dynamically using current active expedition counts, per-Origin capacity and a bounded round-robin tie-breaker.
- Made `RandomizeOrder` affect only equivalent tie-breaks; it can no longer make valid Origins disappear from a cycle.
- Refreshed the selected Origin immediately before dispatch and skipped it only for the current execution when its ships are unavailable.
- Revalidated slots, ships, payload and dispatch results immediately before sending, without blind retries after an ambiguous response.
- Preserved local successful dispatches when a server fleet snapshot is temporarily behind.
- Returned each fleet snapshot together with the freshness result that produced it, preventing concurrent workers from pairing a valid list with another refresh's status.

#### OGamed v13 bridge

- Added a page-scoped espionage report endpoint for AutoFarm.
- Propagated HTTP status details in bridge errors instead of returning only a generic error message.
- Kept message pagination ordered and bounded for callers that need the complete mailbox.
- Restored the OGame v13 lifeform-bonus parser, including ship cargo bonuses used by fleet planning.

### Added

- Added regression coverage for fixed and randomized Origin order, thirteen-Origin round-robin distribution, unavailable Origins, stale snapshots, serialized dispatches, atomic snapshot freshness, per-Origin cargo bonuses, report deletion failures, report ordering and single-page report processing.

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
