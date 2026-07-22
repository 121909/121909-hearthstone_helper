[CmdletBinding()]
param(
    [ValidateSet("shadow", "experimental")]
    [string]$PresentationMode = "shadow",

    [string]$EvidencePath = "",

    [string]$HdtReferenceDir = "",

    [string]$OutputDirectory = ".\.artifacts\release-candidate",

    [string]$DotNetPath = "dotnet",

    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Read-RequiredJson {
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

function Get-RequiredString {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $property = $Object.PSObject.Properties[$Name]
    if($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value))
    {
        throw "$Description is missing '$Name'."
    }
    return [string]$property.Value
}

function Read-SourceConstant {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $source = Get-Content -LiteralPath $Path -Raw
    $match = [regex]::Match($source, "\b" + [regex]::Escape($Name) + "\s*=\s*`"([^`"]+)`"")
    if(-not $match.Success)
    {
        throw "Could not read source constant '$Name' from '$Path'."
    }
    return $match.Groups[1].Value
}

function Assert-ExactVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$ManifestValue,

        [Parameter(Mandatory = $true)]
        [string]$SourceValue
    )

    if(-not [string]::Equals($ManifestValue, $SourceValue, [System.StringComparison]::Ordinal))
    {
        throw "$Name version mismatch: release manifest '$ManifestValue', source '$SourceValue'."
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$releaseManifestPath = Join-Path $repositoryRoot "profiles\release.json"
$release = Read-RequiredJson -Path $releaseManifestPath -Description "Release manifest"
if([int]$release.manifestVersion -ne 1)
{
    throw "Unsupported release manifest version '$($release.manifestVersion)'."
}
$pluginVersion = Get-RequiredString -Object $release -Name "pluginVersion" -Description "Release manifest"
$ruleSetVersion = Get-RequiredString -Object $release -Name "ruleSetVersion" -Description "Release manifest"
$hdtVersion = Get-RequiredString -Object $release -Name "hdtVersion" -Description "Release manifest"
$releaseChannel = Get-RequiredString -Object $release -Name "channel" -Description "Release manifest"

Assert-ExactVersion `
    -Name "Plugin" `
    -ManifestValue $pluginVersion `
    -SourceValue (Read-SourceConstant -Path (Join-Path $repositoryRoot "src\DiscardAdvisor.Plugin\DiscardAdvisorPlugin.cs") -Name "SemanticVersion")
Assert-ExactVersion `
    -Name "Rule set" `
    -ManifestValue $ruleSetVersion `
    -SourceValue (Read-SourceConstant -Path (Join-Path $repositoryRoot "src\DiscardAdvisor.Domain\TargetDeckProfile.cs") -Name "RuleSetVersion")
Assert-ExactVersion `
    -Name "HDT" `
    -ManifestValue $hdtVersion `
    -SourceValue (Read-SourceConstant -Path (Join-Path $repositoryRoot "src\DiscardAdvisor.Domain\RuntimeCompatibility.cs") -Name "HdtVersion")

if([string]::Equals($PresentationMode, "experimental", [System.StringComparison]::Ordinal))
{
    if(-not [string]::Equals($releaseChannel, "experimental", [System.StringComparison]::Ordinal))
    {
        throw "The release manifest channel must be experimental for a visible-test package."
    }
    if([string]::IsNullOrWhiteSpace($EvidencePath))
    {
        throw "EvidencePath is required for an experimental release candidate."
    }
    & (Join-Path $PSScriptRoot "verify-validation-evidence.ps1") `
        -EvidencePath $EvidencePath `
        -ReleaseManifestPath $releaseManifestPath `
        -RequireVisiblePrerequisites
}

if([string]::IsNullOrWhiteSpace($HdtReferenceDir))
{
    $HdtReferenceDir = Join-Path $repositoryRoot ".artifacts\hdt\$hdtVersion\Hearthstone Deck Tracker"
}
$HdtReferenceDir = [System.IO.Path]::GetFullPath($HdtReferenceDir)
$hdtExecutable = Join-Path $HdtReferenceDir "Hearthstone Deck Tracker.exe"
if(-not (Test-Path -LiteralPath $hdtExecutable -PathType Leaf))
{
    throw "HDT v$hdtVersion was not found at '$HdtReferenceDir'. Run scripts\bootstrap-hdt-reference.ps1 or pass -HdtReferenceDir."
}

$project = Join-Path $repositoryRoot "src\DiscardAdvisor.Plugin\DiscardAdvisor.Plugin.csproj"
& $DotNetPath build $project --configuration Release --framework net472 "-p:Platform=x64" "-p:HdtReferenceDir=$HdtReferenceDir"
if($LASTEXITCODE -ne 0)
{
    throw "The Windows release-candidate build failed with exit code $LASTEXITCODE."
}

$buildDirectory = Join-Path $repositoryRoot "src\DiscardAdvisor.Plugin\bin\x64\Release\net472"
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
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$packageBaseName = "DiscardAdvisor-$pluginVersion-rules-$ruleSetVersion-hdt-$hdtVersion-x64-$PresentationMode"
$archivePath = Join-Path $outputRoot ($packageBaseName + ".zip")
if(Test-Path -LiteralPath $archivePath -PathType Leaf)
{
    if(-not $Force)
    {
        throw "Release candidate '$archivePath' already exists. Pass -Force to replace it."
    }
}

$stagingRoot = Join-Path $outputRoot (".staging-" + [Guid]::NewGuid().ToString("N"))
$packageRoot = Join-Path $stagingRoot $packageBaseName
$pluginDirectory = Join-Path $packageRoot "Plugins\DiscardAdvisor"
$dataDirectory = Join-Path $packageRoot "Data\DiscardAdvisor"
try
{
    New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    foreach($assembly in $assemblies)
    {
        $source = Join-Path $buildDirectory $assembly
        if(-not (Test-Path -LiteralPath $source -PathType Leaf))
        {
            throw "Required release assembly '$source' was not produced."
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $pluginDirectory $assembly) -Force
    }

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        (Join-Path $dataDirectory "settings.json"),
        "{`r`n  `"mode`": `"$PresentationMode`"`r`n}`r`n",
        $utf8)
    Copy-Item -LiteralPath $releaseManifestPath -Destination (Join-Path $packageRoot "release.json") -Force
    [System.IO.File]::WriteAllText(
        (Join-Path $packageRoot "README.txt"),
        "Discard Advisor release candidate`r`n`r`n" +
        "Copy Plugins\DiscardAdvisor to %APPDATA%\HearthstoneDeckTracker\Plugins\DiscardAdvisor.`r`n" +
        "Copy Data\DiscardAdvisor to %APPDATA%\HearthstoneDeckTracker\DiscardAdvisor.`r`n" +
        "Target: HDT $hdtVersion, net472, x64, mode $PresentationMode.`r`n",
        $utf8)

    $packagePrefix = $packageRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $packageFiles = @(
        Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
            Sort-Object FullName |
            ForEach-Object {
                $relativePath = $_.FullName.Substring($packagePrefix.Length).Replace("\", "/")
                [ordered]@{
                    path = $relativePath
                    length = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    $packageManifest = [ordered]@{
        schemaVersion = 1
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            [System.Globalization.CultureInfo]::InvariantCulture)
        pluginVersion = $pluginVersion
        ruleSetVersion = $ruleSetVersion
        hdtVersion = $hdtVersion
        targetFramework = "net472"
        platform = "x64"
        presentationMode = $PresentationMode
        files = $packageFiles
    }
    $packageManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageRoot "package-manifest.json") -Encoding UTF8

    $temporaryArchivePath = Join-Path $stagingRoot ($packageBaseName + ".zip")
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $temporaryArchivePath -CompressionLevel Optimal
    if(Test-Path -LiteralPath $archivePath -PathType Leaf)
    {
        Remove-Item -LiteralPath $archivePath -Force
    }
    [System.IO.File]::Move($temporaryArchivePath, $archivePath)
}
finally
{
    if(Test-Path -LiteralPath $stagingRoot)
    {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Release candidate: $archivePath"
Write-Host "Plugin/rules/HDT: $pluginVersion / $ruleSetVersion / $hdtVersion"
Write-Host "Presentation mode: $PresentationMode"
