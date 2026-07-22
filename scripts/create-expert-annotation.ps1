param(
    [Parameter(Mandatory = $true)]
    [string]$ReviewPack,

    [Parameter(Mandatory = $true)]
    [string]$StateId,

    [Parameter(Mandatory = $true)]
    [string]$ReviewerId,

    [Parameter(Mandatory = $true)]
    [ValidateCount(1, 3)]
    [string[]]$RankedOption,

    [string]$OutputPath = "",

    [switch]$Force,

    [string]$DotNetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repositoryRoot "src\DiscardAdvisor.Replay\DiscardAdvisor.Replay.csproj"
if([string]::IsNullOrWhiteSpace($OutputPath))
{
    $OutputPath = Join-Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($ReviewPack))) "annotations"
}

$arguments = @(
    "run",
    "--project", $project,
    "--configuration", "Release",
    "--",
    "annotate",
    "--review-pack", $ReviewPack,
    "--state-id", $StateId,
    "--reviewer-id", $ReviewerId,
    "--output", $OutputPath
)
foreach($option in $RankedOption)
{
    $arguments += @("--rank", $option)
}
if($Force)
{
    $arguments += "--force"
}

& $DotNetPath @arguments
exit $LASTEXITCODE
