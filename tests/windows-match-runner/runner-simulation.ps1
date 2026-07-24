[CmdletBinding()]
param(
    [string]$PowerShellPath = "pwsh"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$runnerPath = Join-Path $repositoryRoot "tools\windows-match-runner\start-match-runner.ps1"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("discard-advisor-runner-test-" + [Guid]::NewGuid().ToString("N"))
$hdtData = Join-Path $tempRoot "hdt-data"
$adviceDirectory = Join-Path $hdtData "Automation"
$diagnosticsDirectory = Join-Path $hdtData "Diagnostics"
$fixtureDirectory = Join-Path $hdtData "Fixtures"
$replayDirectory = Join-Path $hdtData "Replays"
$outputDirectory = Join-Path $tempRoot "runner-output"
$repositoryPath = Join-Path $tempRoot "repository"
$remotePath = Join-Path $tempRoot "remote.git"
$advicePath = Join-Path $adviceDirectory "current-advice.json"
$historyPath = Join-Path $adviceDirectory "advice-history.jsonl"
$diagnosticPath = Join-Path $diagnosticsDirectory "discard-advisor.jsonl"
$process = $null
$keepArtifacts = $false

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $result = & git -C $WorkingDirectory @Arguments 2>&1
    if($LASTEXITCODE -ne 0)
    {
        throw "git -C '$WorkingDirectory' $($Arguments -join ' ') failed with exit code ${LASTEXITCODE}: $($result -join ' ')"
    }
    return $result
}

function Append-JsonLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    $line = $Value | ConvertTo-Json -Depth 16 -Compress
    [System.IO.File]::AppendAllText(
        $Path,
        $line + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))
}

function Get-RunnerEvents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if(-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        return @()
    }
    $events = @()
    foreach($line in Get-Content -LiteralPath $Path)
    {
        if([string]::IsNullOrWhiteSpace($line))
        {
            continue
        }
        try
        {
            $events += ($line | ConvertFrom-Json)
        }
        catch
        {
            # The runner may be writing the last line while it is being read.
        }
    }
    return $events
}

function Wait-Until {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while([DateTimeOffset]::UtcNow -lt $deadline)
    {
        if(& $Condition)
        {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $Description."
}

function New-DiagnosticEvent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Event,

        [Parameter(Mandatory = $true)]
        [string]$GameId,

        [Parameter(Mandatory = $true)]
        [hashtable]$Data
    )

    return [ordered]@{
        timestamp = [DateTimeOffset]::UtcNow.ToString("o")
        event = $Event
        data = $Data
    }
}

function Publish-SimulatedAdvice {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GameId,

        [Parameter(Mandatory = $true)]
        [string]$StateId
    )

    $advice = [ordered]@{
        protocolVersion = "1.0.0"
        pluginVersion = "0.4.13"
        ruleSetVersion = "0.3.4"
        generatedAt = [DateTimeOffset]::UtcNow.ToString("o")
        gameId = $GameId
        stateId = $StateId
        status = "READY"
        routeId = "simulation-route"
        confidence = 0.95
        coverageProbability = 1.0
        lethalProbability = 0.0
        automationAllowed = $true
        blockers = @()
        layout = [ordered]@{
            friendlyHandCount = 1
            friendlyBoardCount = 0
            opponentBoardCount = 0
            choiceCount = 0
        }
        steps = @([ordered]@{
            index = 0
            type = "END_TURN"
        })
    }
    $json = $advice | ConvertTo-Json -Depth 16
    $temporaryPath = $advicePath + ".tmp"
    [System.IO.File]::WriteAllText($temporaryPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporaryPath -Destination $advicePath -Force
    Append-JsonLine -Path $historyPath -Value $advice
}

try
{
    New-Item -ItemType Directory -Path $adviceDirectory, $diagnosticsDirectory, $fixtureDirectory, $replayDirectory, $outputDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $repositoryPath, $remotePath -Force | Out-Null

    & git init --bare --quiet $remotePath
    if($LASTEXITCODE -ne 0) { throw "Could not initialize the simulation remote repository." }
    & git init --quiet -b master $repositoryPath
    if($LASTEXITCODE -ne 0) { throw "Could not initialize the simulation repository." }
    Invoke-Git $repositoryPath @("config", "user.email", "runner-test@example.invalid") | Out-Null
    Invoke-Git $repositoryPath @("config", "user.name", "Runner Test") | Out-Null
    Set-Content -LiteralPath (Join-Path $repositoryPath "README.md") -Value "runner simulation" -Encoding UTF8
    Invoke-Git $repositoryPath @("add", "README.md") | Out-Null
    Invoke-Git $repositoryPath @("commit", "-m", "test: initialize runner repository") | Out-Null
    & git -C $repositoryPath remote add origin $remotePath
    if($LASTEXITCODE -ne 0) { throw "Could not configure the simulation remote." }

    $runnerEventsPath = $null
    $arguments = @(
        "-NoProfile",
        "-File", $runnerPath,
        "-SimulationMode",
        "-DryRun",
        "-MatchCount", "2",
        "-AdvicePath", $advicePath,
        "-DiagnosticsPath", $diagnosticsDirectory,
        "-FixturePath", $fixtureDirectory,
        "-ReplayPath", $replayDirectory,
        "-LayoutPath", (Join-Path $repositoryRoot "tools\windows-match-runner\default-layout.json"),
        "-OutputDirectory", $outputDirectory,
        "-SkipDeckSelection",
        "-MulliganDelaySeconds", "0",
        "-ContinueDelaySeconds", "0",
        "-ActionSettleSeconds", "1",
        "-ActionAcknowledgeTimeoutSeconds", "10",
        "-PollMilliseconds", "100",
        "-GameStartTimeoutSeconds", "10",
        "-GameTimeoutSeconds", "60",
        "-UploadToGitHub",
        "-RepositoryPath", $repositoryPath,
        "-GitRemote", "origin",
        "-GitBranch", "master"
    )
    $stdoutPath = Join-Path $tempRoot "runner.stdout.log"
    $stderrPath = Join-Path $tempRoot "runner.stderr.log"
    $process = Start-Process `
        -FilePath $PowerShellPath `
        -ArgumentList $arguments `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath

    Wait-Until -Description "runner session directory" -Condition {
        $directories = @(Get-ChildItem -LiteralPath $outputDirectory -Directory -ErrorAction SilentlyContinue)
        if($directories.Count -eq 1)
        {
            $script:sessionDirectory = $directories[0].FullName
            $script:runnerEventsPath = Join-Path $script:sessionDirectory "runner-events.jsonl"
            return $true
        }
        return $false
    }

    $stateId = "same-state-in-two-games"
    for($matchIndex = 1; $matchIndex -le 2; $matchIndex++)
    {
        Wait-Until -Description "match $matchIndex request" -Condition {
            @((Get-RunnerEvents $script:runnerEventsPath) |
                Where-Object { $_.event -eq "match_requested" -and [int]$_.data.matchIndex -eq $matchIndex }).Count -gt 0
        }
        $gameId = ("{0:x32}" -f $matchIndex)
        Append-JsonLine -Path $diagnosticPath -Value (New-DiagnosticEvent -Event "game_started" -GameId $gameId -Data @{
            gameId = $gameId
            mode = "shadow"
            pluginVersion = "0.4.13"
            ruleSetVersion = "0.3.4"
        })
        Wait-Until -Description "match $matchIndex mulligan click" -Condition {
            @((Get-RunnerEvents $script:runnerEventsPath) |
                Where-Object { $_.event -eq "mouse_click" -and $_.data.purpose -eq "Keep opening hand" }).Count -ge $matchIndex
        }
        Publish-SimulatedAdvice -GameId $gameId -StateId $stateId
        Wait-Until -Description "match $matchIndex advice execution" -Condition {
            @((Get-RunnerEvents $script:runnerEventsPath) |
                Where-Object { $_.event -eq "advice_step_executed" -and $_.data.gameId -eq $gameId }).Count -gt 0
        }
        Set-Content -LiteralPath (Join-Path $fixtureDirectory ("simulation-{0}.snapshot.json" -f $matchIndex)) -Value "{}" -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $replayDirectory ("simulation-{0}.hdtreplay" -f $matchIndex)) -Value "simulation" -Encoding UTF8
        Append-JsonLine -Path $diagnosticPath -Value (New-DiagnosticEvent -Event "game_ended" -GameId $gameId -Data @{
            gameId = $gameId
            mode = "shadow"
            pluginVersion = "0.4.13"
            ruleSetVersion = "0.3.4"
            completed = $true
        })
        Wait-Until -Description "match $matchIndex completion" -Condition {
            @((Get-RunnerEvents $script:runnerEventsPath) |
                Where-Object { $_.event -eq "match_completed" -and [int]$_.data.matchIndex -eq $matchIndex }).Count -gt 0
        }
    }

    Wait-Until -Description "runner process exit" -TimeoutSeconds 45 -Condition { $process.HasExited }
    if($process.ExitCode -ne 0)
    {
        throw "Runner simulation exited with code $($process.ExitCode): $(Get-Content -LiteralPath $stderrPath -Raw)"
    }
    $summary = Get-Content -LiteralPath (Join-Path $script:sessionDirectory "session-summary.json") -Raw | ConvertFrom-Json
    if([int]$summary.completedMatches -ne 2 -or [bool]$summary.failed -or [int]$summary.handledStateCount -ne 2)
    {
        throw "Unexpected runner summary: $($summary | ConvertTo-Json -Compress)"
    }
    $events = Get-RunnerEvents $script:runnerEventsPath
    if(@($events | Where-Object event -eq "advice_step_acknowledged").Count -ne 2)
    {
        throw "The runner did not acknowledge both simulated actions."
    }
    if(@(Get-Content -LiteralPath (Join-Path $script:sessionDirectory "diagnostics\discard-advisor-session.jsonl")).Count -ne 4)
    {
        throw "Session diagnostics were not scoped to the two simulated games."
    }
    if(@(Get-Content -LiteralPath (Join-Path $script:sessionDirectory "automation\advice-history-session.jsonl")).Count -ne 2)
    {
        throw "Session advice history was not scoped to the two simulated games."
    }
    if(@(Get-ChildItem -LiteralPath (Join-Path $script:sessionDirectory "fixtures") -File).Count -ne 2 -or
        @(Get-ChildItem -LiteralPath (Join-Path $script:sessionDirectory "replays") -File).Count -ne 2)
    {
        throw "Session fixture/replay evidence count was incorrect."
    }

    $archivePath = Join-Path $repositoryPath (Join-Path "validation-runs" $script:sessionDirectory.Split([System.IO.Path]::DirectorySeparatorChar)[-1])
    if(-not (Test-Path -LiteralPath (Join-Path $archivePath "session-summary.json") -PathType Leaf))
    {
        throw "The runner did not archive its session into the repository."
    }
    $tree = & git --git-dir $remotePath ls-tree -r --name-only master
    if($LASTEXITCODE -ne 0 -or -not ($tree -contains (Join-Path ("validation-runs" + [System.IO.Path]::DirectorySeparatorChar + $script:sessionDirectory.Split([System.IO.Path]::DirectorySeparatorChar)[-1]) "session-summary.json").Replace("\", "/")))
    {
        throw "The runner did not push the session archive to the configured remote."
    }
    if(@(Invoke-Git $repositoryPath @("status", "--porcelain")).Count -ne 0)
    {
        throw "The runner left the upload repository dirty."
    }
    Write-Host "Runner simulation passed: 2/2 games, 2 acknowledged actions, scoped evidence, and Git push."
}
catch
{
    $keepArtifacts = $true
    throw
}
finally
{
    if($null -ne $process -and -not $process.HasExited)
    {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if($keepArtifacts)
    {
        Write-Warning "Simulation artifacts retained at '$tempRoot'."
    }
    elseif(Test-Path -LiteralPath $tempRoot)
    {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
