# Offline validation

`DiscardAdvisor.Replay` reruns the local advisor against privacy-filtered Snapshot fixtures. It accepts individual `*.snapshot.json` files, fixture directories, and HDT `*.hdtreplay` archives.

HDT `v1.53.11` stores `output_log.txt` in a ZIP archive. That log is counted and validated, but it is not converted directly into advisor state because doing so would bypass the plugin's visibility filter. Associate exported fixtures with a replay by using either layout:

```text
game.hdtreplay
game.snapshots/
  turn-3.snapshot.json
  turn-3.annotation.json
```

```text
game.hdtreplay
  output_log.txt
  discard-advisor/snapshots/turn-3.snapshot.json
  discard-advisor/annotations/turn-3.annotation.json
```

Run the regression on Windows with PowerShell:

```powershell
.\scripts\run-offline-regression.ps1 `
  -InputPath .\tests\Fixtures\schema\minimal-snapshot.json, .\tests\Fixtures\schema\minimal-snapshot.annotation.json
```

The command writes `.artifacts\offline-regression\offline-regression.json` and `.md`. It returns a non-zero exit code when an input cannot be loaded, a Snapshot cannot be mapped, no route is generated, or any generated route fails independent legality replay.

## Expert annotations

An optional `*.annotation.json` identifies one to three acceptable expert routes for a `state_id`. Actions use the stable protocol kinds `PLAY_CARD`, `ATTACK`, `USE_HERO_POWER`, `USE_LOCATION`, `SELECT_CHOICE`, and `END_TURN`, plus the relevant entity IDs, target, board position, or choice ID. A Snapshot is Top-3 consistent when at least one of the advisor's first three complete action sequences exactly matches one of the expert routes.

Offline reports include legal-route rate, p50/p95/maximum latency, deadline expiration, Top-3 consistency, and unsupported-interaction counts. Deadline expiration compares compute time with the Snapshot's remaining turn time. State supersession and whether an expired result became visible require shadow-run telemetry and are not inferred from Power.log.
