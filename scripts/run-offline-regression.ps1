param(
    [Parameter(Mandatory = $true)]
    [string[]]$InputPath,

    [string]$OutputPath = ".\.artifacts\offline-regression",

    [ValidateRange(1, 10000)]
    [int]$TimeBudgetMs = 250
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\DiscardAdvisor.Replay\DiscardAdvisor.Replay.csproj"
$arguments = @(
    "run",
    "--project", $project,
    "--configuration", "Release",
    "--",
    "--output", $OutputPath,
    "--time-budget-ms", $TimeBudgetMs
)
foreach($path in $InputPath)
{
    $arguments += @("--input", $path)
}

& dotnet @arguments
exit $LASTEXITCODE
