[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,

    [string]$EvidencePath = "",

    [string]$ReleaseManifestPath = ""
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

function Assert-ReleaseMatch {
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
$archiveFullPath = [System.IO.Path]::GetFullPath($ArchivePath)
if(-not (Test-Path -LiteralPath $archiveFullPath -PathType Leaf))
{
    throw "Release candidate archive was not found at '$archiveFullPath'."
}
$currentRelease = Read-JsonFile -Path ([System.IO.Path]::GetFullPath($ReleaseManifestPath)) -Description "Current release manifest"
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("discard-advisor-package-verify-" + [Guid]::NewGuid().ToString("N"))
try
{
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    Expand-Archive -LiteralPath $archiveFullPath -DestinationPath $stagingRoot -Force
    $rootEntries = @(Get-ChildItem -LiteralPath $stagingRoot -Force)
    $packageDirectories = @($rootEntries | Where-Object { $_.PSIsContainer })
    if($packageDirectories.Count -ne 1 -or $rootEntries.Count -ne 1)
    {
        throw "Release candidate archive must contain exactly one package directory."
    }
    $packageRoot = $packageDirectories[0].FullName
    $manifest = Read-JsonFile -Path (Join-Path $packageRoot "package-manifest.json") -Description "Package manifest"
    if([int](Get-RequiredProperty -Object $manifest -Name "schemaVersion" -Description "Package manifest") -ne 1)
    {
        throw "Unsupported package manifest schemaVersion."
    }
    Assert-ReleaseMatch -Expected $currentRelease -Actual $manifest -Description "Package manifest release"
    if(-not [string]::Equals(
        (Get-RequiredString -Object $manifest -Name "targetFramework" -Description "Package manifest"),
        "net472",
        [System.StringComparison]::Ordinal))
    {
        throw "Package manifest targetFramework must be net472."
    }
    if(-not [string]::Equals(
        (Get-RequiredString -Object $manifest -Name "platform" -Description "Package manifest"),
        "x64",
        [System.StringComparison]::Ordinal))
    {
        throw "Package manifest platform must be x64."
    }
    $presentationMode = Get-RequiredString -Object $manifest -Name "presentationMode" -Description "Package manifest"
    if($presentationMode -notin @("shadow", "experimental"))
    {
        throw "Package manifest presentationMode '$presentationMode' is not supported."
    }

    $entries = @((Get-RequiredProperty -Object $manifest -Name "files" -Description "Package manifest"))
    if($entries.Count -eq 0)
    {
        throw "Package manifest contains no files."
    }
    $packagePrefix = $packageRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $seenPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach($entry in $entries)
    {
        $relativePath = Get-RequiredString -Object $entry -Name "path" -Description "Package manifest file"
        if($relativePath.Contains("\") -or [System.IO.Path]::IsPathRooted($relativePath))
        {
            throw "Package manifest file path '$relativePath' is not normalized."
        }
        $segments = $relativePath.Split("/")
        $invalidSegments = @($segments | Where-Object { $_ -eq "" -or $_ -eq "." -or $_ -eq ".." })
        if($segments.Count -eq 0 -or $invalidSegments.Count -gt 0)
        {
            throw "Package manifest file path '$relativePath' contains an invalid segment."
        }
        if(-not $seenPaths.Add($relativePath))
        {
            throw "Package manifest contains duplicate path '$relativePath'."
        }
        $filePath = [System.IO.Path]::GetFullPath((Join-Path $packageRoot ($relativePath -replace "/", [string][System.IO.Path]::DirectorySeparatorChar)))
        if(-not $filePath.StartsWith($packagePrefix, [System.StringComparison]::OrdinalIgnoreCase))
        {
            throw "Package manifest file path '$relativePath' escapes the package directory."
        }
        if(-not (Test-Path -LiteralPath $filePath -PathType Leaf))
        {
            throw "Packaged file '$relativePath' is missing."
        }
        $expectedLength = [long](Get-RequiredProperty -Object $entry -Name "length" -Description "Package manifest file '$relativePath'")
        $expectedHash = Get-RequiredString -Object $entry -Name "sha256" -Description "Package manifest file '$relativePath'"
        if($expectedHash -notmatch "^[0-9a-f]{64}$")
        {
            throw "Package manifest file '$relativePath' has an invalid SHA-256 value."
        }
        $file = Get-Item -LiteralPath $filePath
        if($file.Length -ne $expectedLength)
        {
            throw "Packaged file '$relativePath' has length $($file.Length), expected $expectedLength."
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if(-not [string]::Equals($expectedHash, $actualHash, [System.StringComparison]::Ordinal))
        {
            throw "Packaged file '$relativePath' does not match its SHA-256 value."
        }
    }

    foreach($actualFile in Get-ChildItem -LiteralPath $packageRoot -Recurse -File)
    {
        $actualRelativePath = $actualFile.FullName.Substring($packagePrefix.Length).Replace("\", "/")
        if([string]::Equals($actualRelativePath, "package-manifest.json", [System.StringComparison]::Ordinal))
        {
            continue
        }
        if(-not $seenPaths.Contains($actualRelativePath))
        {
            throw "Release candidate contains untracked file '$actualRelativePath'."
        }
    }

    foreach($assembly in @(
        "DiscardAdvisor.Plugin.dll",
        "DiscardAdvisor.Domain.dll",
        "DiscardAdvisor.Rules.dll",
        "DiscardAdvisor.Search.dll",
        "System.Buffers.dll",
        "System.Collections.Immutable.dll",
        "System.Memory.dll",
        "System.Numerics.Vectors.dll",
        "System.Runtime.CompilerServices.Unsafe.dll"))
    {
        if(-not $seenPaths.Contains("Plugins/DiscardAdvisor/$assembly"))
        {
            throw "Release candidate is missing required assembly '$assembly'."
        }
    }
    if((Get-ChildItem -LiteralPath $packageRoot -Recurse -Filter "*.pdb").Count -gt 0)
    {
        throw "Release candidate must not contain PDB files."
    }
    $settings = Read-JsonFile -Path (Join-Path $packageRoot "Data\DiscardAdvisor\settings.json") -Description "Packaged settings"
    if(-not [string]::Equals(
        (Get-RequiredString -Object $settings -Name "mode" -Description "Packaged settings"),
        $presentationMode,
        [System.StringComparison]::Ordinal))
    {
        throw "Packaged settings mode does not match package manifest presentationMode."
    }
    $archivedRelease = Read-JsonFile -Path (Join-Path $packageRoot "release.json") -Description "Packaged release manifest"
    Assert-ReleaseMatch -Expected $currentRelease -Actual $archivedRelease -Description "Packaged release manifest"

    if([string]::Equals($presentationMode, "experimental", [System.StringComparison]::Ordinal))
    {
        if(-not [string]::Equals(
            (Get-RequiredString -Object $currentRelease -Name "channel" -Description "Current release manifest"),
            "experimental",
            [System.StringComparison]::Ordinal))
        {
            throw "Current release manifest channel must be experimental for an experimental package."
        }
        if([string]::IsNullOrWhiteSpace($EvidencePath))
        {
            throw "EvidencePath is required to verify an experimental package."
        }
        & (Join-Path $PSScriptRoot "verify-validation-evidence.ps1") `
            -EvidencePath $EvidencePath `
            -ReleaseManifestPath $ReleaseManifestPath `
            -RequireVisiblePrerequisites
    }

    Write-Host "Release candidate verified: $archiveFullPath"
    Write-Host "Plugin/rules/HDT: $($manifest.pluginVersion) / $($manifest.ruleSetVersion) / $($manifest.hdtVersion)"
    Write-Host "Presentation mode: $presentationMode"
    Write-Host "Verified files: $($entries.Count)"
}
finally
{
    if(Test-Path -LiteralPath $stagingRoot)
    {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
