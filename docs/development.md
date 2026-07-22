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
