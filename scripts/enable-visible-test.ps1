[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RegressionReportPath,

    [string]$ReleaseManifestPath = "",

    [string]$SettingsPath = ""
)

$ErrorActionPreference = "Stop"

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if(-not (Test-Path -LiteralPath $fullPath -PathType Leaf))
    {
        throw "$Description was not found at '$fullPath'."
    }
    try
    {
        return (Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json)
    }
    catch
    {
        throw "$Description at '$fullPath' is not valid JSON: $($_.Exception.Message)"
    }
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $property = $Object.PSObject.Properties[$Name]
    if($null -eq $property -or $null -eq $property.Value)
    {
        throw "$Description is missing '$Name'."
    }
    return $property.Value
}

function Get-RequiredString {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $value = Get-RequiredProperty -Object $Object -Name $Name -Description $Description
    if([string]::IsNullOrWhiteSpace([string]$value))
    {
        throw "$Description has an empty '$Name'."
    }
    return [string]$value
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if([string]::IsNullOrWhiteSpace($ReleaseManifestPath))
{
    $ReleaseManifestPath = Join-Path $repositoryRoot "profiles\release.json"
}
if([string]::IsNullOrWhiteSpace($SettingsPath))
{
    if([string]::IsNullOrWhiteSpace($env:APPDATA))
    {
        throw "SettingsPath was not supplied and APPDATA is not set."
    }
    $SettingsPath = Join-Path $env:APPDATA "HearthstoneDeckTracker\DiscardAdvisor\settings.json"
}

$report = Read-JsonFile -Path $RegressionReportPath -Description "Regression report"
$prerequisites = Get-RequiredProperty -Object $report -Name "meetsVisibleSuggestionPrerequisites" -Description "Regression report"
if(-not ($prerequisites -is [bool]) -or -not $prerequisites)
{
    throw "Visible-test prerequisites are not met. Keep the plugin in shadow mode and collect the required real evidence."
}

$shadow = Get-RequiredProperty -Object $report -Name "shadowRun" -Description "Regression report"
$shadowThresholds = Get-RequiredProperty -Object $shadow -Name "meetsAutomatedAcceptanceThresholds" -Description "Regression report shadowRun"
if(-not ($shadowThresholds -is [bool]) -or -not $shadowThresholds)
{
    throw "Shadow automated acceptance thresholds are not met."
}
$cohortCount = [int](Get-RequiredProperty -Object $shadow -Name "versionCohortCount" -Description "Regression report shadowRun")
if($cohortCount -ne 1)
{
    throw "The regression report must contain exactly one version cohort; observed $cohortCount."
}
$cohorts = @((Get-RequiredProperty -Object $shadow -Name "versionCohorts" -Description "Regression report shadowRun"))
if($cohorts.Count -ne 1)
{
    throw "The regression report must contain exactly one version cohort entry; observed $($cohorts.Count)."
}

$manifest = Read-JsonFile -Path $ReleaseManifestPath -Description "Release manifest"
$manifestVersion = [int](Get-RequiredProperty -Object $manifest -Name "manifestVersion" -Description "Release manifest")
if($manifestVersion -ne 1)
{
    throw "Release manifest version '$manifestVersion' is not supported."
}
$manifestPluginVersion = Get-RequiredString -Object $manifest -Name "pluginVersion" -Description "Release manifest"
$manifestRuleSetVersion = Get-RequiredString -Object $manifest -Name "ruleSetVersion" -Description "Release manifest"
$releaseChannel = Get-RequiredString -Object $manifest -Name "channel" -Description "Release manifest"
if(-not [string]::Equals($releaseChannel, "experimental", [System.StringComparison]::Ordinal))
{
    throw "Release manifest channel must be 'experimental'; observed '$releaseChannel'."
}
$cohort = $cohorts[0]
$cohortPluginVersion = Get-RequiredString -Object $cohort -Name "pluginVersion" -Description "Regression version cohort"
$cohortRuleSetVersion = Get-RequiredString -Object $cohort -Name "ruleSetVersion" -Description "Regression version cohort"
if(-not [string]::Equals($manifestPluginVersion, $cohortPluginVersion, [System.StringComparison]::Ordinal) -or
   -not [string]::Equals($manifestRuleSetVersion, $cohortRuleSetVersion, [System.StringComparison]::Ordinal))
{
    throw "Regression version cohort ($cohortPluginVersion/$cohortRuleSetVersion) does not match release manifest ($manifestPluginVersion/$manifestRuleSetVersion)."
}

$settingsFullPath = [System.IO.Path]::GetFullPath($SettingsPath)
$settingsDirectory = Split-Path -Parent $settingsFullPath
if([string]::IsNullOrWhiteSpace($settingsDirectory))
{
    throw "SettingsPath must include a directory."
}
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
$temporaryPath = Join-Path $settingsDirectory ("." + [System.IO.Path]::GetFileName($settingsFullPath) + "." + [Guid]::NewGuid().ToString("N") + ".tmp")
$backupPath = Join-Path $settingsDirectory ("." + [System.IO.Path]::GetFileName($settingsFullPath) + "." + [Guid]::NewGuid().ToString("N") + ".bak")
$settingsJson = "{`r`n  `"mode`": `"experimental`"`r`n}`r`n"
$replacementCompleted = $false
try
{
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($temporaryPath, $settingsJson, $utf8)
    if(Test-Path -LiteralPath $settingsFullPath -PathType Leaf)
    {
        [System.IO.File]::Replace($temporaryPath, $settingsFullPath, $backupPath)
        $replacementCompleted = $true
    }
    else
    {
        [System.IO.File]::Move($temporaryPath, $settingsFullPath)
        $replacementCompleted = $true
    }
}
catch
{
    if(-not (Test-Path -LiteralPath $settingsFullPath -PathType Leaf) -and
       (Test-Path -LiteralPath $backupPath -PathType Leaf))
    {
        try
        {
            [System.IO.File]::Move($backupPath, $settingsFullPath)
        }
        catch
        {
            Write-Warning "Could not restore the previous settings file; recover it from '$backupPath'."
        }
    }
    throw
}
finally
{
    if(Test-Path -LiteralPath $temporaryPath -PathType Leaf)
    {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
    if($replacementCompleted -and (Test-Path -LiteralPath $backupPath -PathType Leaf))
    {
        Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Visible-test gate passed for plugin $manifestPluginVersion / rules $manifestRuleSetVersion."
Write-Host "Experimental settings written atomically to '$settingsFullPath'."
