# Defender options

```json
{
  "Defender": {
    "UseCurrentCelestial": true,
    "Autofleet": {
      "Active": true,
      "DelayedFleetSave": {
        "Active": true,
        "DelaySeconds": 120,
        "SafetySeconds": 120
      }
    }
  }
}
```

- `UseCurrentCelestial` defaults to `true`: routine checks read the empire event list without a `cp` parameter. They do not visit Home or a random celestial. Actual defence actions and other workers can still change the selected celestial.
- Delayed fleetsave defaults to **disabled**. Enable it explicitly. The initial attack notification uses your existing Telegram/Discord settings; this option does not enable a notification channel.
- Departure is scheduled at the earlier of detection plus `DelaySeconds` and impact minus `SafetySeconds`. With a 120-second delay and safety margin, an attack arriving in 180 seconds triggers departure after 60 seconds. Attacks already inside the margin trigger an immediate attempt.
- Every attempt requires a current attack list and refreshed ships. A recalled attack or a fleet already moved by the player cancels the pending save. Failed dispatches remain pending for another attempt; they are not logged as successful saves.
- Grace periods do not restart on each check. Pending deadlines shorten the Defender polling interval. Pending plans are in memory: a process restart resets the grace period, still capped by the observed impact deadline.
- The existing fleetsave destination, mission and speed selection is unchanged. A delay does not guarantee a successful escape: the bot must remain running with working API access, fuel and a free fleet slot. The safety margin includes dispatch latency; this is not a last-second evasion feature.
- Valid ranges: `DelaySeconds` 0–86400, `SafetySeconds` 1–3600. Invalid values log an explicit error and do not schedule a save.

AutoFarm now distinguishes a target below `MinCargosToSend`, an empty stock, a reserved stock and an insufficient remaining stock. Dashboard cargo reasons require the matching instance, report ID and an `AttackPending` state; old global log messages are not treated as proof of a shortage.
