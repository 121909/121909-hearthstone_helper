# Development

## Prerequisites

- Windows 10 or later
- Visual Studio 2022 or .NET 8 SDK
- PowerShell 5.1 or later

The plugin targets HDT `v1.53.11` (`net472`, x64). Prepare the pinned host reference before building:

```powershell
.\scripts\bootstrap-hdt-reference.ps1
dotnet build .\DiscardAdvisor.sln -c Release -p:Platform=x64
```

To use an existing HDT installation, set `HDT_INSTALL_DIR` to the directory containing `Hearthstone Deck Tracker.exe`.

Unit tests target .NET 8 so domain and lifecycle behavior can also be verified outside the HDT process:

```powershell
dotnet test .\DiscardAdvisor.sln -c Release -p:Platform=x64
```

## Local diagnostics

While the plugin is enabled it writes redacted JSONL events and privacy-filtered Snapshot fixtures under HDT's data directory:

```text
DiscardAdvisor\Diagnostics\discard-advisor.jsonl
DiscardAdvisor\Fixtures\<state_id>.snapshot.json
```

Diagnostics contain state identifiers, counts, gate status, and exception type only. They do not contain exception messages, account identifiers, server data, or local paths. Logs rotate at 5 MiB and retain three previous files.
Production diagnostics are queued in event order and serialized on a background
worker; Snapshot fixture I/O does not run on HDT's update thread.

## Presentation mode

The plugin defaults to `shadow`, including when `DiscardAdvisor\settings.json` does not exist. Shadow mode is the safe starting point and does not attach the recommendation Overlay. Explicitly opt into shadow mode by creating `DiscardAdvisor\settings.json` under HDT's data directory:

```json
{
  "mode": "shadow"
}
```

Shadow mode still captures privacy-filtered fixtures and runs the local advisor, but it never attaches the Overlay. Diagnostics record game boundaries and one terminal disposition for each analysis (`Published`, `Superseded`, `Cancelled`, or `Failed`). Invalid settings and a missing settings file both fail closed to shadow mode. The `experimental` value is only for the small visible-test cohort after the final evidence gate passes.

To build and install the complete dependency set for a shadow run, close HDT and run:

```powershell
.\scripts\install-shadow-plugin.ps1
```

The script builds `net472` against pinned HDT `v1.53.11`, installs into `%APPDATA%\HearthstoneDeckTracker\Plugins\DiscardAdvisor`, and writes shadow settings under `%APPDATA%\HearthstoneDeckTracker\DiscardAdvisor`. Pass `-HdtReferenceDir`, `-PluginDirectory`, or `-HdtDataDirectory` when using an existing or portable HDT layout. Start HDT afterward and enable Discard Advisor under `Options > Tracker > Plugins`.

After the run, aggregate all evidence in one report:

```powershell
.\scripts\run-offline-regression.ps1 `
  -InputPath `
    "$env:APPDATA\HearthstoneDeckTracker\Replays", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics"
```

The release cohort is recorded in `profiles\release.json`. Do not switch a live settings file to `experimental` manually. After the final report contains at least 5 completed real shadow games with a published analysis and all automated thresholds, run the Windows gate. Expert annotations are optional in the active validation profile:

```powershell
.\scripts\enable-visible-test.ps1 `
  -RegressionReportPath .\.artifacts\offline-regression\offline-regression.json
```

The gate requires the report's single plugin/rule version cohort to match the release manifest and the active no-annotation validation policy, then atomically writes `mode: experimental`. Any missing evidence, mixed cohort, malformed report, or write failure leaves the existing settings unchanged.

Build the version-checked Windows release candidate with
`.\scripts\build-release-candidate.ps1`. It defaults to Shadow mode; see
`docs\release-candidate.md` for the package contents and the evidence-gated
experimental command.

For the complete Windows collection, annotation, validation, and visible-test
workflow, see [Windows Shadow 运行操作手册](windows-operation-guide.md).

The plugin also writes the latest machine-readable route to
`%APPDATA%\HearthstoneDeckTracker\DiscardAdvisor\Automation\current-advice.json`.
Its contract is `schemas\v1\automation-advice.schema.json`. The separate
`tools\windows-match-runner\start-match-runner.ps1` Windows experiment consumes
only the first step of a fresh matching `gameId + stateId`; the operation guide
documents coordinate calibration, emergency stop, match limits, and evidence
upload.

Run the repeatable two-game state-machine and Git-upload simulation with the
same PowerShell executable that launched the test:

```powershell
$shell = (Get-Process -Id $PID).Path
& $shell -NoProfile -File .\tests\windows-match-runner\runner-simulation.ps1 `
  -PowerShellPath $shell
```

The simulation drives the production runner without calling `user32.dll`. It
uses the same `stateId` in two different games and verifies two executions,
action acknowledgement, session-only diagnostics/advice/replay/fixture
archival, a Git commit, and a push to a temporary bare remote.
