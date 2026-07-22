[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RegressionReportPath,

    [string[]]$ReplayPath = @(),

    [string[]]$FixturePath = @(),

    [string[]]$AnnotationPath = @(),

    [string[]]$DiagnosticsPath = @(),

    [string]$OutputDirectory = ".\.artifacts\validation-evidence",

    [string]$ReleaseManifestPath = ""
)

$ErrorActionPreference = "Stop"
$utcTimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ"
$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture

function Resolve-RequiredFile {
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
    return $fullPath
}

function Get-RelativeInputPath {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File,

        [Parameter(Mandatory = $true)]
        [string]$Root,

        [bool]$IsFileInput
    )

    if($IsFileInput)
    {
        return $File.Name
    }
    $rootPath = $Root.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    return $File.FullName.Substring($rootPath.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}

function Copy-WithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    $lastError = $null
    for($attempt = 1; $attempt -le 3; $attempt++)
    {
        try
        {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
            return
        }
        catch
        {
            $lastError = $_
            if($attempt -lt 3)
            {
                Start-Sleep -Milliseconds 200
            }
        }
    }
    throw "Could not copy '$Source' after 3 attempts: $($lastError.Exception.Message)"
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if([string]::IsNullOrWhiteSpace($ReleaseManifestPath))
{
    $ReleaseManifestPath = Join-Path $repositoryRoot "profiles\release.json"
}
$reportFullPath = Resolve-RequiredFile -Path $RegressionReportPath -Description "Offline regression report"
$manifestFullPath = Resolve-RequiredFile -Path $ReleaseManifestPath -Description "Release manifest"
try
{
    $report = Get-Content -LiteralPath $reportFullPath -Raw | ConvertFrom-Json
    $release = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
}
catch
{
    throw "The regression report and release manifest must be valid JSON: $($_.Exception.Message)"
}
if($null -eq $report.shadowRun -or
   $null -eq $report.generatedAtUtc -or
   $null -eq $report.PSObject.Properties["meetsVisibleSuggestionPrerequisites"] -or
   $null -eq $release.pluginVersion -or
   $null -eq $release.ruleSetVersion -or
   $null -eq $release.hdtVersion)
{
    throw "The regression report or release manifest is missing required validation metadata."
}

$archiveName = "validation-evidence-" + [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssZ") +
    "-plugin-" + $release.pluginVersion + "-rules-" + $release.ruleSetVersion +
    "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$archiveRoot = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) $archiveName
if(Test-Path -LiteralPath $archiveRoot)
{
    throw "Evidence archive '$archiveRoot' already exists."
}
New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
$incompleteMarker = Join-Path $archiveRoot ".incomplete"
Set-Content -LiteralPath $incompleteMarker -Encoding ASCII -NoNewline -Value "Archive copy in progress."

$files = New-Object 'System.Collections.Generic.List[object]'

function Add-ArchiveFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$ArchiveRelativePath,

        [Parameter(Mandatory = $true)]
        [string]$Category,

        [Parameter(Mandatory = $true)]
        [string]$SourceId
    )

    $destination = Join-Path $archiveRoot $ArchiveRelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-WithRetry -Source $Source -Destination $destination
    $copied = Get-Item -LiteralPath $destination
    $files.Add([ordered]@{
        archivePath = $ArchiveRelativePath.Replace("\", "/")
        category = $Category
        sourceId = $SourceId
        length = $copied.Length
        sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

function Add-Category {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Category,

        [string[]]$Paths,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Matches
    )

    $sourceIndex = 0
    foreach($inputPath in @($Paths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }))
    {
        $sourceIndex++
        $fullPath = [System.IO.Path]::GetFullPath($inputPath)
        if(-not (Test-Path -LiteralPath $fullPath))
        {
            throw "$Category input '$fullPath' does not exist."
        }
        $isFileInput = Test-Path -LiteralPath $fullPath -PathType Leaf
        $inputRoot = if($isFileInput) { Split-Path -Parent $fullPath } else { $fullPath }
        $candidates = if($isFileInput)
        {
            @(Get-Item -LiteralPath $fullPath)
        }
        else
        {
            @(Get-ChildItem -LiteralPath $fullPath -Recurse -File)
        }
        $fileIndex = 0
        foreach($candidate in $candidates | Sort-Object FullName)
        {
            if(-not (& $Matches $candidate))
            {
                continue
            }
            $fileIndex++
            $relativePath = Get-RelativeInputPath -File $candidate -Root $inputRoot -IsFileInput $isFileInput
            Add-ArchiveFile `
                -Source $candidate.FullName `
                -ArchiveRelativePath (Join-Path (Join-Path $Category ("source-{0:D3}" -f $sourceIndex)) $relativePath) `
                -Category $Category `
                -SourceId ("{0}-{1:D3}" -f $Category, $sourceIndex)
        }
        if($fileIndex -eq 0)
        {
            Write-Warning "No recognized $Category files were found in input $sourceIndex."
        }
    }
}

$reportDirectory = Split-Path -Parent $reportFullPath
Add-ArchiveFile -Source $reportFullPath -ArchiveRelativePath "report\offline-regression.json" -Category "report" -SourceId "report-001"
foreach($name in @("offline-regression.md", "expert-review-pack.json"))
{
    $path = Join-Path $reportDirectory $name
    if(Test-Path -LiteralPath $path -PathType Leaf)
    {
        Add-ArchiveFile -Source $path -ArchiveRelativePath (Join-Path "report" $name) -Category "report" -SourceId "report-001"
    }
}
Add-ArchiveFile -Source $manifestFullPath -ArchiveRelativePath "release\release.json" -Category "release" -SourceId "release-001"

Add-Category -Category "replays" -Paths $ReplayPath -Matches {
    param($file)
    $file.Name.EndsWith(".hdtreplay", [System.StringComparison]::OrdinalIgnoreCase)
}
Add-Category -Category "fixtures" -Paths $FixturePath -Matches {
    param($file)
    $file.Name.EndsWith(".snapshot.json", [System.StringComparison]::OrdinalIgnoreCase)
}
Add-Category -Category "annotations" -Paths $AnnotationPath -Matches {
    param($file)
    $file.Name.EndsWith(".annotation.json", [System.StringComparison]::OrdinalIgnoreCase)
}
Add-Category -Category "diagnostics" -Paths $DiagnosticsPath -Matches {
    param($file)
    $file.Name.EndsWith(".jsonl", [System.StringComparison]::OrdinalIgnoreCase) -or
        $file.Name.IndexOf(".jsonl.", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

$evidenceManifest = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString($utcTimestampFormat, $invariantCulture)
    release = [ordered]@{
        pluginVersion = [string]$release.pluginVersion
        ruleSetVersion = [string]$release.ruleSetVersion
        hdtVersion = [string]$release.hdtVersion
    }
    regression = [ordered]@{
        generatedAtUtc = ([DateTimeOffset]$report.generatedAtUtc).ToUniversalTime().ToString($utcTimestampFormat, $invariantCulture)
        meetsVisibleSuggestionPrerequisites = [bool]$report.meetsVisibleSuggestionPrerequisites
    }
    files = @($files | Sort-Object archivePath)
}
$evidenceManifestPath = Join-Path $archiveRoot "validation-evidence.json"
$evidenceManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidenceManifestPath -Encoding UTF8
Remove-Item -LiteralPath $incompleteMarker -Force

Write-Host "Validation evidence archive: $archiveRoot"
Write-Host "Archived files: $($files.Count)"
Write-Host "Manifest: $evidenceManifestPath"
