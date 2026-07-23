param(
    [Parameter(Mandatory = $true)]
    [string[]]$InputPath,

    [string]$OutputPath = ".\.artifacts\offline-regression",

    [ValidateRange(1, 10000)]
    [int]$TimeBudgetMs = 250,

    [string]$DotNetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repositoryRoot "src\DiscardAdvisor.Replay\DiscardAdvisor.Replay.csproj"
$release = Get-Content -LiteralPath (Join-Path $repositoryRoot "profiles\release.json") -Raw | ConvertFrom-Json
$arguments = @(
    "run",
    "--project", $project,
    "--configuration", "Release",
    "--",
    "--output", $OutputPath,
    "--time-budget-ms", $TimeBudgetMs,
    "--plugin-version", [string]$release.pluginVersion,
    "--rule-set-version", [string]$release.ruleSetVersion
)
foreach($path in $InputPath)
{
    $arguments += @("--input", $path)
}

& $DotNetPath @arguments
exit $LASTEXITCODE
