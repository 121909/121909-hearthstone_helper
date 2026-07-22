[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,

    [string]$ReleaseManifestPath = "",

    [switch]$RequireVisiblePrerequisites
)

$ErrorActionPreference = "Stop"

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if(-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "$Description was not found at '$Path'."
    }
    try
    {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch
    {
        throw "$Description at '$Path' is not valid JSON: $($_.Exception.Message)"
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

function Get-RequiredBoolean {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $value = Get-RequiredProperty -Object $Object -Name $Name -Description $Description
    if($value -isnot [bool])
    {
        throw "$Description field '$Name' must be Boolean."
    }
    return [bool]$value
}

function Get-RequiredDateTimeOffset {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $value = Get-RequiredProperty -Object $Object -Name $Name -Description $Description
    try
    {
        if($value -is [DateTimeOffset])
        {
            return [DateTimeOffset]$value
        }
        if($value -is [DateTime])
        {
            return [DateTimeOffset]([DateTime]$value)
        }
        return [DateTimeOffset]::Parse(
            [string]$value,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind)
    }
    catch
    {
        throw "$Description field '$Name' must be an ISO-8601 timestamp."
    }
}

function Assert-VersionMatch {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Expected,

        [Parameter(Mandatory = $true)]
        [object]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    foreach($field in @("pluginVersion", "ruleSetVersion", "hdtVersion"))
    {
        $expectedValue = Get-RequiredString -Object $Expected -Name $field -Description "$Description expected release"
        $actualValue = Get-RequiredString -Object $Actual -Name $field -Description "$Description actual release"
        if(-not [string]::Equals($expectedValue, $actualValue, [System.StringComparison]::Ordinal))
        {
            throw "$Description differs for ${field}: expected '$expectedValue', observed '$actualValue'."
        }
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if([string]::IsNullOrWhiteSpace($ReleaseManifestPath))
{
    $ReleaseManifestPath = Join-Path $repositoryRoot "profiles\release.json"
}

$archiveRoot = [System.IO.Path]::GetFullPath($EvidencePath)
if(-not (Test-Path -LiteralPath $archiveRoot -PathType Container))
{
    throw "Evidence archive directory was not found at '$archiveRoot'."
}
if(Test-Path -LiteralPath (Join-Path $archiveRoot ".incomplete") -PathType Leaf)
{
    throw "Evidence archive '$archiveRoot' is incomplete and cannot be verified."
}

$manifest = Read-JsonFile -Path (Join-Path $archiveRoot "validation-evidence.json") -Description "Evidence manifest"
if([int](Get-RequiredProperty -Object $manifest -Name "schemaVersion" -Description "Evidence manifest") -ne 1)
{
    throw "Unsupported evidence manifest schemaVersion."
}
$archivedRelease = Get-RequiredProperty -Object $manifest -Name "release" -Description "Evidence manifest"
$currentRelease = Read-JsonFile -Path ([System.IO.Path]::GetFullPath($ReleaseManifestPath)) -Description "Current release manifest"
Assert-VersionMatch -Expected $currentRelease -Actual $archivedRelease -Description "Evidence archive release"

$entries = @((Get-RequiredProperty -Object $manifest -Name "files" -Description "Evidence manifest"))
if($entries.Count -eq 0)
{
    throw "Evidence manifest contains no files."
}
$rootPrefix = $archiveRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
$seenPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach($entry in $entries)
{
    $relativePath = Get-RequiredString -Object $entry -Name "archivePath" -Description "Evidence manifest file"
    if($relativePath.Contains("\") -or [System.IO.Path]::IsPathRooted($relativePath))
    {
        throw "Evidence manifest file path '$relativePath' is not a normalized relative path."
    }
    $segments = $relativePath.Split("/")
    $invalidSegments = @($segments | Where-Object { $_ -eq "" -or $_ -eq "." -or $_ -eq ".." })
    if($segments.Count -eq 0 -or $invalidSegments.Count -gt 0)
    {
        throw "Evidence manifest file path '$relativePath' contains an invalid segment."
    }
    if(-not $seenPaths.Add($relativePath))
    {
        throw "Evidence manifest contains duplicate archive path '$relativePath'."
    }
    $filePath = [System.IO.Path]::GetFullPath((Join-Path $archiveRoot ($relativePath -replace "/", [string][System.IO.Path]::DirectorySeparatorChar)))
    if(-not $filePath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Evidence manifest file path '$relativePath' escapes the archive directory."
    }
    if(-not (Test-Path -LiteralPath $filePath -PathType Leaf))
    {
        throw "Archived file '$relativePath' is missing."
    }
    $expectedLength = [long](Get-RequiredProperty -Object $entry -Name "length" -Description "Evidence manifest file '$relativePath'")
    $expectedHash = Get-RequiredString -Object $entry -Name "sha256" -Description "Evidence manifest file '$relativePath'"
    if($expectedHash -notmatch "^[0-9a-f]{64}$")
    {
        throw "Evidence manifest file '$relativePath' has an invalid SHA-256 value."
    }
    $file = Get-Item -LiteralPath $filePath
    if($file.Length -ne $expectedLength)
    {
        throw "Archived file '$relativePath' has length $($file.Length), expected $expectedLength."
    }
    $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if(-not [string]::Equals($expectedHash, $actualHash, [System.StringComparison]::Ordinal))
    {
        throw "Archived file '$relativePath' does not match its SHA-256 value."
    }
}

foreach($actualFile in Get-ChildItem -LiteralPath $archiveRoot -Recurse -File)
{
    $actualRelativePath = $actualFile.FullName.Substring($rootPrefix.Length).Replace("\", "/")
    if([string]::Equals($actualRelativePath, "validation-evidence.json", [System.StringComparison]::Ordinal))
    {
        continue
    }
    if(-not $seenPaths.Contains($actualRelativePath))
    {
        throw "Evidence archive contains untracked file '$actualRelativePath'."
    }
}

$archivedReleaseFile = Read-JsonFile -Path (Join-Path $archiveRoot "release\release.json") -Description "Archived release manifest"
Assert-VersionMatch -Expected $archivedRelease -Actual $archivedReleaseFile -Description "Archived release manifest"
$report = Read-JsonFile -Path (Join-Path $archiveRoot "report\offline-regression.json") -Description "Archived regression report"
$manifestRegression = Get-RequiredProperty -Object $manifest -Name "regression" -Description "Evidence manifest"
$manifestGeneratedAt = Get-RequiredDateTimeOffset -Object $manifestRegression -Name "generatedAtUtc" -Description "Evidence manifest regression"
$reportGeneratedAt = Get-RequiredDateTimeOffset -Object $report -Name "generatedAtUtc" -Description "Archived regression report"
if($manifestGeneratedAt -ne $reportGeneratedAt)
{
    throw "Archived regression report generatedAtUtc does not match the evidence manifest."
}
if((Get-RequiredBoolean -Object $manifestRegression -Name "meetsVisibleSuggestionPrerequisites" -Description "Evidence manifest regression") -ne
   (Get-RequiredBoolean -Object $report -Name "meetsVisibleSuggestionPrerequisites" -Description "Archived regression report"))
{
    throw "Archived regression report visible-test prerequisite state does not match the evidence manifest."
}

if($RequireVisiblePrerequisites)
{
    if(-not (Get-RequiredBoolean -Object $report -Name "meetsVisibleSuggestionPrerequisites" -Description "Archived regression report"))
    {
        throw "Archived regression report does not meet visible-test prerequisites."
    }
    $shadow = Get-RequiredProperty -Object $report -Name "shadowRun" -Description "Archived regression report"
    if(-not (Get-RequiredBoolean -Object $shadow -Name "meetsAutomatedAcceptanceThresholds" -Description "Archived regression shadowRun"))
    {
        throw "Archived regression report does not meet Shadow acceptance thresholds."
    }
    if([int](Get-RequiredProperty -Object $shadow -Name "versionCohortCount" -Description "Archived regression shadowRun") -ne 1)
    {
        throw "Archived regression report must contain exactly one version cohort."
    }
    $cohorts = @((Get-RequiredProperty -Object $shadow -Name "versionCohorts" -Description "Archived regression shadowRun"))
    if($cohorts.Count -ne 1)
    {
        throw "Archived regression report must contain exactly one version cohort entry."
    }
    $cohort = $cohorts[0]
    $expectedPlugin = Get-RequiredString -Object $currentRelease -Name "pluginVersion" -Description "Current release manifest"
    $expectedRules = Get-RequiredString -Object $currentRelease -Name "ruleSetVersion" -Description "Current release manifest"
    $actualPlugin = Get-RequiredString -Object $cohort -Name "pluginVersion" -Description "Archived regression version cohort"
    $actualRules = Get-RequiredString -Object $cohort -Name "ruleSetVersion" -Description "Archived regression version cohort"
    if(-not [string]::Equals($expectedPlugin, $actualPlugin, [System.StringComparison]::Ordinal) -or
       -not [string]::Equals($expectedRules, $actualRules, [System.StringComparison]::Ordinal))
    {
        throw "Archived regression cohort ($actualPlugin/$actualRules) does not match current release ($expectedPlugin/$expectedRules)."
    }
}

Write-Host "Evidence archive verified: $archiveRoot"
Write-Host "Verified files: $($entries.Count)"
Write-Host "Visible-test prerequisites: $((Get-RequiredBoolean -Object $report -Name 'meetsVisibleSuggestionPrerequisites' -Description 'Archived regression report'))"
