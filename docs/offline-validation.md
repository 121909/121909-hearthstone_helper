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

The command writes `.artifacts\offline-regression\offline-regression.json`, `.md`, and `expert-review-pack.json`. It returns a non-zero exit code when an input cannot be loaded, a Snapshot cannot be mapped, no route is generated, or any generated route fails independent legality replay.

Pass HDT's `DiscardAdvisor\Diagnostics` directory as another input to aggregate shadow-run JSONL telemetry:

```powershell
.\scripts\run-offline-regression.ps1 `
  -InputPath $ReplayDirectory, $FixtureDirectory, $DiagnosticDirectory
```

The shadow section counts only sessions recorded with `mode=shadow`. Only a real HDT `OnGameEnd` increments completed-game progress; plugin unloads and interrupted sessions do not. The report covers end-to-end latency, analyses superseded by a newer state, cancellations, failures, duplicate requests for the same `game_id + state_id`, and any suggestion incorrectly marked visible. Its automated threshold is met at 50 completed games with p95 below 300 ms and zero failures, duplicate requests, or visible suggestions; manual review is still required for obvious HDT lag and gameplay quality.

## Expert annotations

An optional `*.annotation.json` identifies one to three acceptable expert routes for a `state_id`. Actions use the stable protocol kinds `PLAY_CARD`, `ATTACK`, `USE_HERO_POWER`, `USE_LOCATION`, `SELECT_CHOICE`, and `END_TURN`, plus the relevant entity IDs, target, board position, or choice ID. A Snapshot is Top-3 consistent when at least one of the advisor's first three complete action sequences exactly matches one of the expert routes.

`expert-review-pack.json` is a blind route-ranking pack. It contains deterministically shuffled `option-*` routes and their display CardIds, but no candidate IDs, scores, confidence, or original Advisor rank. Reviewers rank one to three complete routes, putting the strongest route first, may author a custom route when none is correct, copy the action objects into `expertTop3`, add independent labels/reasons, and save the result as `<state_id>.annotation.json`. The report's Top-3 metric checks whether the expert's primary route (`expertTop3[0]`) appears in the Advisor's first three; alternatives do not make an otherwise missed primary route count. The report tracks progress toward 200 annotations and the 80% target.

On Windows, write a ranked selection from the blind pack without manually
copying entity IDs:

```powershell
.\scripts\create-expert-annotation.ps1 `
  -ReviewPack .\.artifacts\offline-regression\expert-review-pack.json `
  -StateId "turn-8:<hash>" `
  -RankedOption option-4, option-1
```

The first option is the expert primary route. The command validates every
action, writes atomically under an `annotations` directory, and refuses to
replace an existing review unless `-Force` is supplied. Use the pack's
`customRouteTemplate` when no offered route is correct, then rerun the offline
regression with the annotation directory as an input.

Offline reports include legal-route rate, p50/p95/maximum latency, deadline expiration, Top-3 consistency, and unsupported-interaction counts. Deadline expiration compares compute time with the Snapshot's remaining turn time. State supersession and whether an expired result became visible require shadow-run telemetry and are not inferred from Power.log.
