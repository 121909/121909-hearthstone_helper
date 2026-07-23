param(
    [Parameter(Mandatory = $true)]
    [string[]]$InputPath,

    [string]$OutputPath = ".\.artifacts\shadow-checkpoint",

    [ValidateRange(1, 10000)]
    [int]$TimeBudgetMs = 250,

    [switch]$RequireAcceptance,

    [string]$DotNetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repositoryRoot "src\DiscardAdvisor.Replay\DiscardAdvisor.Replay.csproj"
$releasePath = Join-Path $repositoryRoot "profiles\release.json"
$release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
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
$existingInputCount = 0
foreach($path in $InputPath)
{
    if(Test-Path -LiteralPath $path)
    {
        $arguments += @("--input", $path)
        $existingInputCount++
    }
    else
    {
        Write-Warning "Input path does not exist yet and was skipped: $path"
    }
}
if($existingInputCount -eq 0)
{
    throw "None of the supplied input paths exist."
}

& $DotNetPath @arguments
$regressionExitCode = $LASTEXITCODE
if($regressionExitCode -ge 2)
{
    throw "The offline regression command failed with exit code $regressionExitCode."
}

$reportPath = Join-Path $OutputPath "offline-regression.json"
if(-not (Test-Path -LiteralPath $reportPath -PathType Leaf))
{
    throw "The regression report was not written to '$reportPath'."
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$shadow = $report.shadowRun
Write-Host ("Shadow checkpoint: {0}/5 completed games with a published analysis ({1} completed total)" -f `
    $shadow.completedGameWithPublishedAnalysisCount, $shadow.completedGameCount)
Write-Host ("Runs/version cohorts/missing metadata: {0}/{1}/{2}" -f `
    $shadow.runCount, $shadow.versionCohortCount, $shadow.missingVersionMetadataGameCount)
Write-Host ("Target cohort: {0}/{1}; historical games ignored: {2}" -f `
    $shadow.targetPluginVersion, $shadow.targetRuleSetVersion, $shadow.ignoredVersionGameCount)
Write-Host ("Requests/analyses: {0}/{1}; missing starts: {2}; unfinished: {3}" -f `
    $shadow.requestCount, $shadow.analysisCount, $shadow.missingRequestCount, $shadow.unfinishedRequestCount)
Write-Host ("p95: {0:N2} ms; duplicates: {1}; failures: {2}; visible: {3}; unsupported occurrences: {4}" -f `
    $shadow.latencyP95Ms, $shadow.duplicateRequestCount, $shadow.failedCount, $shadow.visibleSuggestionCount, $shadow.unsupportedInteractionOccurrenceCount)

$enoughGames = $shadow.completedGameWithPublishedAnalysisCount -ge 5
$hardThresholds = [bool]$shadow.meetsAutomatedAcceptanceThresholds
if($RequireAcceptance -and (-not $enoughGames -or -not $hardThresholds))
{
    [Console]::Error.WriteLine("Shadow acceptance is not met. Keep settings.json in shadow mode and collect more real completed games with published analyses.")
    exit 3
}
exit 0
