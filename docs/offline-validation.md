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

For an incremental Windows checkpoint (safe to run while collecting more
games), use:

```powershell
.\scripts\check-shadow-progress.ps1 `
  -InputPath "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics", `
             "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures" `
  -OutputPath .\.artifacts\shadow-checkpoint
```

Add `-RequireAcceptance` only for the final gate. The checkpoint reports
completed games, request/terminal-analysis closure, true concurrent or
post-publish duplicate requests, failures, visible suggestions, p95 latency,
and unsupported-interaction occurrences. Superseded work followed by a retry
is not counted as a duplicate request.

The shadow section counts only sessions recorded with `mode=shadow`. Only a real HDT `OnGameEnd` increments completed-game progress; plugin unloads and interrupted sessions do not. The five-game threshold counts only completed games that contain at least one `Published` Shadow analysis, so completed games that never produce an accepted analysis do not inflate the evidence. The report covers end-to-end latency, analyses superseded by a newer state, cancellations, failures, duplicate requests for the same `game_id + state_id`, and any suggestion incorrectly marked visible. Its automated threshold is met at five completed games with published analyses, p95 below 300 ms, and zero failures, duplicate requests, or visible suggestions; manual review is still required for obvious HDT lag and gameplay quality.
Every request and terminal analysis also carries a per-process `runId`, plugin
version, and rule-set version. The final automated threshold requires complete
metadata and exactly one plugin/rule version cohort; multiple HDT runs using
that same version may be combined. When target versions are supplied, telemetry
from other plugin/rule cohorts and fixtures from other rule sets are reported
as ignored instead of contaminating the active regression.

## Expert annotations

An optional `*.annotation.json` identifies one to three acceptable expert routes for a `state_id`. Actions use the stable protocol kinds `PLAY_CARD`, `ATTACK`, `USE_HERO_POWER`, `USE_LOCATION`, `SELECT_CHOICE`, and `END_TURN`, plus the relevant entity IDs, target, board position, or choice ID. A Snapshot is Top-3 consistent when at least one of the advisor's first three complete action sequences exactly matches one of the expert routes. Protocol `1.1.0` also records an anonymous `reviewerId` and UTC `reviewedAtUtc`. Annotations are reported as optional metrics and do not affect the active release gate.

`expert-review-pack.json` is a blind route-ranking pack retained for optional analysis. It contains deterministically shuffled `option-*` routes and their display CardIds, but no candidate IDs, scores, confidence, or original Advisor rank. Reviewers may rank one to three complete routes when additional quality research is desired.

On Windows, write a ranked selection from the blind pack without manually
copying entity IDs:

```powershell
.\scripts\create-expert-annotation.ps1 `
  -ReviewPack .\.artifacts\offline-regression\expert-review-pack.json `
  -StateId "turn-8:<hash>" `
  -ReviewerId "expert-a" `
  -RankedOption option-4, option-1
```

The first option is the expert primary route. The command validates every
action, writes the anonymous reviewer ID and UTC review time atomically under
an `annotations` directory, and refuses to replace an existing review unless
`-Force` is supplied. Use the pack's
`customRouteTemplate` when no offered route is correct, then rerun the offline
regression with the annotation directory as an input.

Offline reports include legal-route rate, p50/p95/maximum latency, deadline expiration, Top-3 consistency, and unsupported-interaction counts. Deadline expiration compares compute time with the Snapshot's remaining turn time. State supersession and whether an expired result became visible require shadow-run telemetry and are not inferred from Power.log.

## Visible-test gate

The report exposes `meetsVisibleSuggestionPrerequisites`. It remains false until the offline routes are fully evaluated and legal, p95 latency is below 300 ms with no deadline expirations or unsupported interactions, and one complete five-game shadow cohort passes the automated thresholds. Expert annotations are not required. The automated fields make the evidence auditable but do not independently prove that an HDT session produced every input; review the collected source data before enabling a visible cohort.

Keep `profiles\release.json` aligned with the plugin and rule versions used for the shadow run. Once the final report is ready, enable the small visible-test cohort with:

```powershell
.\scripts\enable-visible-test.ps1 `
  -RegressionReportPath .\.artifacts\offline-regression\offline-regression.json
```

The script verifies the prerequisite flag and exact version cohort before atomically writing `mode: experimental`; a failed check never changes the settings file.

## Evidence archive

Before clearing diagnostics or moving a completed Windows collection aside, archive
the exact report and white-listed evidence files with hashes:

```powershell
.\scripts\archive-validation-evidence.ps1 `
  -RegressionReportPath .\.artifacts\offline-regression\offline-regression.json `
  -ReplayPath "$env:APPDATA\HearthstoneDeckTracker\Replays" `
  -FixturePath "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures" `
  -DiagnosticsPath "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics"
```

The archive contains only `*.hdtreplay`, `*.snapshot.json`,
`*.annotation.json`, `discard-advisor.jsonl*`, the generated regression files,
and `profiles\release.json`. Its `validation-evidence.json` records SHA-256,
byte length, category, and an anonymous source index for each copied file; it
does not record absolute local source paths. Verify source authenticity and
reviewer identity outside this automated archive before treating it as real
evidence.

Verify a transferred archive before using it for review or release gating:

```powershell
.\scripts\verify-validation-evidence.ps1 `
  -EvidencePath .\.artifacts\validation-evidence\<archive-directory>
```

It rejects incomplete archives, malformed or duplicate relative paths, missing
or untracked files, byte-length changes, SHA-256 mismatches, report/manifest
timestamp mismatches, and release-manifest drift. Add `-RequireVisiblePrerequisites` only
for the final archive; this also requires the final report and its sole Shadow
version cohort to match the current release manifest.
