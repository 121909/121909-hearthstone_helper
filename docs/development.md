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

## Presentation mode

The plugin defaults to `experimental`, which attaches the recommendation Overlay. For a no-display shadow run, create `DiscardAdvisor\settings.json` under HDT's data directory before loading the plugin:

```json
{
  "mode": "shadow"
}
```

Shadow mode still captures privacy-filtered fixtures and runs the local advisor, but it never attaches the Overlay. Diagnostics record game boundaries and one terminal disposition for each analysis (`Published`, `Superseded`, `Cancelled`, or `Failed`). Invalid settings fail closed to shadow mode.

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
