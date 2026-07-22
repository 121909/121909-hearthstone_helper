param(
    [string]$Version = "1.53.11"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root ".artifacts\hdt\$Version"
$executable = Join-Path $artifactRoot "Hearthstone Deck Tracker\Hearthstone Deck Tracker.exe"

if (-not (Test-Path $executable)) {
    $archive = Join-Path ([System.IO.Path]::GetTempPath()) "Hearthstone.Deck.Tracker-v$Version.zip"
    $url = "https://github.com/HearthSim/Hearthstone-Deck-Tracker/releases/download/v$Version/Hearthstone.Deck.Tracker-v$Version.zip"
    Invoke-WebRequest -Uri $url -OutFile $archive
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    Expand-Archive -Path $archive -DestinationPath $artifactRoot -Force
}

Write-Output "HDT reference ready: $executable"

