param(
    [string]$HdtReferenceDir = "",

    [string]$PluginDirectory = (Join-Path $env:APPDATA "HearthstoneDeckTracker\Plugins\DiscardAdvisor"),

    [string]$HdtDataDirectory = (Join-Path $env:APPDATA "HearthstoneDeckTracker"),

    [string]$DotNetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if([string]::IsNullOrWhiteSpace($HdtReferenceDir))
{
    $HdtReferenceDir = Join-Path $repositoryRoot ".artifacts\hdt\1.53.11\Hearthstone Deck Tracker"
}
$HdtReferenceDir = [System.IO.Path]::GetFullPath($HdtReferenceDir)
$hdtExecutable = Join-Path $HdtReferenceDir "Hearthstone Deck Tracker.exe"
if(-not (Test-Path -LiteralPath $hdtExecutable -PathType Leaf))
{
    throw "HDT v1.53.11 was not found at '$HdtReferenceDir'. Run scripts\bootstrap-hdt-reference.ps1 first or pass -HdtReferenceDir."
}
if(Get-Process -Name "Hearthstone Deck Tracker" -ErrorAction SilentlyContinue)
{
    throw "Close Hearthstone Deck Tracker before installing the plugin."
}

$project = Join-Path $repositoryRoot "src\DiscardAdvisor.Plugin\DiscardAdvisor.Plugin.csproj"
& $DotNetPath build $project --configuration Release --framework net472 "-p:HdtReferenceDir=$HdtReferenceDir"
if($LASTEXITCODE -ne 0)
{
    throw "The Windows plugin build failed with exit code $LASTEXITCODE."
}

$buildDirectory = Join-Path $repositoryRoot "src\DiscardAdvisor.Plugin\bin\Release\net472"
$assemblies = @(
    "DiscardAdvisor.Plugin.dll",
    "DiscardAdvisor.Domain.dll",
    "DiscardAdvisor.Rules.dll",
    "DiscardAdvisor.Search.dll",
    "System.Buffers.dll",
    "System.Collections.Immutable.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll"
)

New-Item -ItemType Directory -Path $PluginDirectory -Force | Out-Null
foreach($assembly in $assemblies)
{
    $source = Join-Path $buildDirectory $assembly
    if(-not (Test-Path -LiteralPath $source -PathType Leaf))
    {
        throw "Required plugin assembly '$source' was not produced."
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $PluginDirectory $assembly) -Force
}

$advisorDataDirectory = Join-Path $HdtDataDirectory "DiscardAdvisor"
New-Item -ItemType Directory -Path $advisorDataDirectory -Force | Out-Null
$settingsPath = Join-Path $advisorDataDirectory "settings.json"
Set-Content -LiteralPath $settingsPath -Encoding UTF8 -NoNewline -Value "{`r`n  `"mode`": `"shadow`"`r`n}`r`n"

Write-Host "Installed Discard Advisor in shadow mode."
Write-Host "Plugin directory: $PluginDirectory"
Write-Host "Settings: $settingsPath"
Write-Host "Start HDT, enable Discard Advisor under Options > Tracker > Plugins, and keep settings.json set to shadow during the 5-game run."
