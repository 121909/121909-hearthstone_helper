[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateRange(1, 1000)]
    [int]$MatchCount = 5,

    [string]$AdvicePath = (Join-Path $env:APPDATA "HearthstoneDeckTracker\DiscardAdvisor\Automation\current-advice.json"),

    [string]$DiagnosticsPath = (Join-Path $env:APPDATA "HearthstoneDeckTracker\DiscardAdvisor\Diagnostics"),

    [string]$FixturePath = (Join-Path $env:APPDATA "HearthstoneDeckTracker\DiscardAdvisor\Fixtures"),

    [string]$ReplayPath = (Join-Path $env:APPDATA "HearthstoneDeckTracker\Replays"),

    [string]$LayoutPath = (Join-Path $PSScriptRoot "default-layout.json"),

    [string]$WindowTitle = "Hearthstone",

    [string]$OutputDirectory = ".\.artifacts\match-runner",

    [ValidateRange(1, 300)]
    [int]$AdviceMaximumAgeSeconds = 15,

    [ValidateRange(100, 10000)]
    [int]$PollMilliseconds = 500,

    [ValidateRange(5, 600)]
    [int]$GameStartTimeoutSeconds = 180,

    [ValidateRange(60, 7200)]
    [int]$GameTimeoutSeconds = 1800,

    [ValidateRange(1, 30)]
    [int]$ActionSettleSeconds = 2,

    [ValidateRange(5, 300)]
    [int]$BlockedAdviceTimeoutSeconds = 30,

    [ValidateRange(0, 60)]
    [int]$MulliganDelaySeconds = 8,

    [ValidateRange(0, 60)]
    [int]$ContinueDelaySeconds = 5,

    [string]$RepositoryPath = "",

    [string]$GitRemote = "origin",

    [string]$GitBranch = "",

    [switch]$UploadToGitHub,

    [switch]$SkipPlayButton,

    [switch]$SkipDeckSelection,

    [switch]$SkipMulligan,

    [switch]$SkipContinue,

    [switch]$ValidateOnly,

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$expectedPluginVersion = "0.4.13"
$expectedRuleSetVersion = "0.3.4"
$sessionId = [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$sessionDirectory = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) $sessionId
$sessionLogPath = Join-Path $sessionDirectory "runner-events.jsonl"
$summaryPath = Join-Path $sessionDirectory "session-summary.json"
$stopFile = Join-Path $sessionDirectory "STOP"
$handledAdviceKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$observedGameIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$completedGameIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$script:stopRequested = $false
$script:sessionStartUtc = [DateTimeOffset]::UtcNow
$script:activeGameId = $null

New-Item -ItemType Directory -Path $sessionDirectory -Force | Out-Null

function Write-RunnerEvent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Event,

        [hashtable]$Data = @{}
    )

    $entry = [ordered]@{
        timestamp = [DateTimeOffset]::UtcNow.ToString("o")
        event = $Event
        data = $Data
    }
    $line = $entry | ConvertTo-Json -Depth 12 -Compress
    Add-Content -LiteralPath $sessionLogPath -Value $line -Encoding UTF8
    Write-Host ("[{0:HH:mm:ss}] {1}" -f [DateTime]::Now, $Event)
}

function Assert-WindowsHost {
    if($env:OS -ne "Windows_NT")
    {
        throw "The match runner requires Windows."
    }
}

function Initialize-NativeMethods {
    if("DiscardAdvisor.MatchRunner.NativeMethods" -as [type])
    {
        return
    }
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace DiscardAdvisor.MatchRunner
{
    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr handle);
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr handle, int id);
        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG message, IntPtr handle, uint minimum, uint maximum, uint remove);
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }
        public const uint MouseLeftDown = 0x0002;
        public const uint MouseLeftUp = 0x0004;
        public const uint WmHotKey = 0x0312;
        public const uint PmRemove = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint VirtualKeyF12 = 0x7B;
    }
}
"@
}

function Test-StopRequested {
    if($script:stopRequested -or (Test-Path -LiteralPath $stopFile -PathType Leaf))
    {
        return $true
    }
    $message = New-Object DiscardAdvisor.MatchRunner.NativeMethods+MSG
    while([DiscardAdvisor.MatchRunner.NativeMethods]::PeekMessage(
            [ref]$message,
            [IntPtr]::Zero,
            [DiscardAdvisor.MatchRunner.NativeMethods]::WmHotKey,
            [DiscardAdvisor.MatchRunner.NativeMethods]::WmHotKey,
            [DiscardAdvisor.MatchRunner.NativeMethods]::PmRemove))
    {
        if([int]$message.wParam -eq 1)
        {
            $script:stopRequested = $true
            Write-RunnerEvent -Event "emergency_stop_requested" -Data @{ source = "Ctrl+Shift+F12" }
        }
    }
    return $script:stopRequested
}

function Read-JsonWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    for($attempt = 1; $attempt -le 3; $attempt++)
    {
        try
        {
            return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        }
        catch
        {
            if($attempt -eq 3)
            {
                return $null
            }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Get-DiagnosticEvents {
    $events = New-Object 'System.Collections.Generic.List[object]'
    if(-not (Test-Path -LiteralPath $DiagnosticsPath -PathType Container))
    {
        return $events
    }
    foreach($file in Get-ChildItem -LiteralPath $DiagnosticsPath -Filter "discard-advisor.jsonl*" -File | Sort-Object Name)
    {
        foreach($line in Get-Content -LiteralPath $file.FullName)
        {
            if([string]::IsNullOrWhiteSpace($line))
            {
                continue
            }
            try
            {
                $events.Add(($line | ConvertFrom-Json))
            }
            catch
            {
            }
        }
    }
    return @($events | Sort-Object { [DateTimeOffset]$_.timestamp })
}

function Update-GameProgress {
    foreach($event in Get-DiagnosticEvents)
    {
        if($event.timestamp -and ([DateTimeOffset]$event.timestamp) -lt $script:sessionStartUtc)
        {
            continue
        }
        if($event.data.pluginVersion -ne $expectedPluginVersion -or $event.data.ruleSetVersion -ne $expectedRuleSetVersion)
        {
            continue
        }
        $gameId = [string]$event.data.gameId
        if([string]::IsNullOrWhiteSpace($gameId))
        {
            continue
        }
        if($event.event -eq "game_started")
        {
            [void]$observedGameIds.Add($gameId)
            $script:activeGameId = $gameId
        }
        elseif($event.event -eq "game_ended" -and [bool]$event.data.completed)
        {
            [void]$observedGameIds.Add($gameId)
            [void]$completedGameIds.Add($gameId)
            if([string]::Equals($script:activeGameId, $gameId, [System.StringComparison]::OrdinalIgnoreCase))
            {
                $script:activeGameId = $null
            }
        }
    }
}

function Get-HearthstoneWindow {
    $handle = [DiscardAdvisor.MatchRunner.NativeMethods]::FindWindow($null, $WindowTitle)
    if($handle -eq [IntPtr]::Zero)
    {
        throw "Could not find a window with the exact title '$WindowTitle'."
    }
    $rect = New-Object DiscardAdvisor.MatchRunner.NativeMethods+RECT
    if(-not [DiscardAdvisor.MatchRunner.NativeMethods]::GetWindowRect($handle, [ref]$rect))
    {
        throw "Could not read the Hearthstone window bounds."
    }
    if(($rect.Right - $rect.Left) -lt 640 -or ($rect.Bottom - $rect.Top) -lt 360)
    {
        throw "The Hearthstone window is minimized or too small."
    }
    return [pscustomobject]@{
        Handle = $handle
        Left = $rect.Left
        Top = $rect.Top
        Width = $rect.Right - $rect.Left
        Height = $rect.Bottom - $rect.Top
    }
}

function Get-ScaledPoint {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Point,

        [Parameter(Mandatory = $true)]
        [object]$Window
    )

    return [pscustomobject]@{
        X = $Window.Left + [int]([double]$Point.x * $Window.Width / [double]$layout.referenceWidth)
        Y = $Window.Top + [int]([double]$Point.y * $Window.Height / [double]$layout.referenceHeight)
    }
}

function Get-LinePoint {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Definition,

        [Parameter(Mandatory = $true)]
        [int]$Index,

        [Parameter(Mandatory = $true)]
        [int]$Count
    )

    if($Count -lt 1 -or $Index -lt 0 -or $Index -ge $Count)
    {
        throw "Invalid indexed zone location $Index/$Count."
    }
    $span = [Math]::Min([double]$Definition.maximumSpan, [Math]::Max(0, ($Count - 1) * [double]$Definition.spacing))
    $start = [double]$Definition.centerX - ($span / 2)
    $x = if($Count -eq 1) { [double]$Definition.centerX } else { $start + ($span * $Index / ($Count - 1)) }
    return [pscustomobject]@{ x = [int]$x; y = [int]$Definition.y }
}

function Get-LocatorPoint {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Locator,

        [Parameter(Mandatory = $true)]
        [object]$Advice,

        [Parameter(Mandatory = $true)]
        [object]$Window
    )

    $point = switch([string]$Locator.zone)
    {
        "FRIENDLY_HERO" { $layout.friendlyHero; break }
        "OPPONENT_HERO" { $layout.opponentHero; break }
        "FRIENDLY_HERO_POWER" { $layout.friendlyHeroPower; break }
        "OPPONENT_HERO_POWER" { $layout.opponentHeroPower; break }
        "FRIENDLY_WEAPON" { $layout.friendlyWeapon; break }
        "OPPONENT_WEAPON" { $layout.opponentWeapon; break }
        "FRIENDLY_HAND" { Get-LinePoint $layout.hand ([int]$Locator.index) ([int]$Locator.count); break }
        "FRIENDLY_BOARD" { Get-LinePoint $layout.friendlyBoard ([int]$Locator.index) ([int]$Locator.count); break }
        "OPPONENT_BOARD" { Get-LinePoint $layout.opponentBoard ([int]$Locator.index) ([int]$Locator.count); break }
        "CHOICE" { Get-LinePoint $layout.choice ([int]$Locator.index) ([int]$Locator.count); break }
        default { throw "The advice references unsupported zone '$($Locator.zone)'." }
    }
    return Get-ScaledPoint $point $Window
}

function Invoke-MouseClick {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Point,

        [Parameter(Mandatory = $true)]
        [string]$Purpose
    )

    Write-RunnerEvent -Event "mouse_click" -Data @{ purpose = $Purpose; x = $Point.X; y = $Point.Y; dryRun = [bool]$DryRun }
    if($DryRun)
    {
        return
    }
    if($PSCmdlet.ShouldProcess("Hearthstone at $($Point.X),$($Point.Y)", $Purpose))
    {
        [void][DiscardAdvisor.MatchRunner.NativeMethods]::SetCursorPos($Point.X, $Point.Y)
        Start-Sleep -Milliseconds 100
        [DiscardAdvisor.MatchRunner.NativeMethods]::mouse_event([DiscardAdvisor.MatchRunner.NativeMethods]::MouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 75
        [DiscardAdvisor.MatchRunner.NativeMethods]::mouse_event([DiscardAdvisor.MatchRunner.NativeMethods]::MouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
    }
}

function Invoke-Drag {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Source,

        [Parameter(Mandatory = $true)]
        [object]$Target,

        [Parameter(Mandatory = $true)]
        [string]$Purpose
    )

    Write-RunnerEvent -Event "mouse_drag" -Data @{ purpose = $Purpose; sourceX = $Source.X; sourceY = $Source.Y; targetX = $Target.X; targetY = $Target.Y; dryRun = [bool]$DryRun }
    if($DryRun)
    {
        return
    }
    if($PSCmdlet.ShouldProcess("Hearthstone", $Purpose))
    {
        [void][DiscardAdvisor.MatchRunner.NativeMethods]::SetCursorPos($Source.X, $Source.Y)
        Start-Sleep -Milliseconds 120
        [DiscardAdvisor.MatchRunner.NativeMethods]::mouse_event([DiscardAdvisor.MatchRunner.NativeMethods]::MouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        [void][DiscardAdvisor.MatchRunner.NativeMethods]::SetCursorPos($Target.X, $Target.Y)
        Start-Sleep -Milliseconds 160
        [DiscardAdvisor.MatchRunner.NativeMethods]::mouse_event([DiscardAdvisor.MatchRunner.NativeMethods]::MouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
    }
}

function Invoke-AdviceStep {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Advice
    )

    $step = @($Advice.steps)[0]
    if($null -eq $step)
    {
        throw "The advice contains no executable step."
    }
    $window = Get-HearthstoneWindow
    if(-not $DryRun)
    {
        [void][DiscardAdvisor.MatchRunner.NativeMethods]::SetForegroundWindow($window.Handle)
        Start-Sleep -Milliseconds 250
    }
    switch([string]$step.type)
    {
        "END_TURN"
        {
            Invoke-MouseClick (Get-ScaledPoint $layout.endTurn $window) "End turn"
        }
        "SELECT_CHOICE"
        {
            Invoke-MouseClick (Get-LocatorPoint $step.target $Advice $window) "Select choice"
        }
        "ATTACK"
        {
            Invoke-Drag (Get-LocatorPoint $step.source $Advice $window) (Get-LocatorPoint $step.target $Advice $window) "Attack"
        }
        "USE_LOCATION"
        {
            Invoke-MouseClick (Get-LocatorPoint $step.source $Advice $window) "Activate location"
            Start-Sleep -Milliseconds 250
            Invoke-MouseClick (Get-LocatorPoint $step.target $Advice $window) "Select location target"
        }
        "USE_HERO_POWER"
        {
            $source = Get-LocatorPoint $step.source $Advice $window
            if($null -ne $step.target)
            {
                Invoke-Drag $source (Get-LocatorPoint $step.target $Advice $window) "Use hero power"
            }
            else
            {
                Invoke-MouseClick $source "Use hero power"
            }
        }
        "PLAY_CARD"
        {
            $source = Get-LocatorPoint $step.source $Advice $window
            if($null -ne $step.target)
            {
                Invoke-Drag $source (Get-LocatorPoint $step.target $Advice $window) "Play targeted card"
            }
            elseif($null -ne $step.boardPosition)
            {
                $boardCount = [int]$Advice.layout.friendlyBoardCount + 1
                $position = [int]$step.boardPosition
                Invoke-Drag $source (Get-ScaledPoint (Get-LinePoint $layout.friendlyBoard ($position - 1) $boardCount) $window) "Play board card"
            }
            else
            {
                Invoke-Drag $source (Get-ScaledPoint $layout.playTarget $window) "Play card"
            }
        }
        default
        {
            throw "Unsupported advice action '$($step.type)'."
        }
    }
    Write-RunnerEvent -Event "advice_step_executed" -Data @{
        gameId = [string]$Advice.gameId
        stateId = [string]$Advice.stateId
        routeId = [string]$Advice.routeId
        actionType = [string]$step.type
    }
}

function Get-EligibleAdvice {
    if(-not (Test-Path -LiteralPath $AdvicePath -PathType Leaf))
    {
        return $null
    }
    $advice = Read-JsonWithRetry $AdvicePath
    if($null -eq $advice -or $advice.protocolVersion -ne "1.0.0")
    {
        return $null
    }
    if($advice.pluginVersion -ne $expectedPluginVersion -or $advice.ruleSetVersion -ne $expectedRuleSetVersion)
    {
        return $null
    }
    if([string]::IsNullOrWhiteSpace($script:activeGameId) -or
        -not [string]::Equals([string]$advice.gameId, $script:activeGameId, [System.StringComparison]::OrdinalIgnoreCase))
    {
        return $null
    }
    if($advice.status -ne "READY" -or -not [bool]$advice.automationAllowed)
    {
        return $null
    }
    $adviceKey = ([string]$advice.gameId) + ":" + ([string]$advice.stateId)
    if([string]::IsNullOrWhiteSpace([string]$advice.stateId) -or $handledAdviceKeys.Contains($adviceKey))
    {
        return $null
    }
    $age = [DateTimeOffset]::UtcNow - [DateTimeOffset]$advice.generatedAt
    if($age.TotalSeconds -lt -2 -or $age.TotalSeconds -gt $AdviceMaximumAgeSeconds)
    {
        return $null
    }
    if(@($advice.steps).Count -eq 0)
    {
        return $null
    }
    return $advice
}

function Get-BlockingAdvice {
    if(-not (Test-Path -LiteralPath $AdvicePath -PathType Leaf))
    {
        return $null
    }
    $advice = Read-JsonWithRetry $AdvicePath
    if($null -eq $advice -or $advice.protocolVersion -ne "1.0.0" -or
        $advice.pluginVersion -ne $expectedPluginVersion -or $advice.ruleSetVersion -ne $expectedRuleSetVersion)
    {
        return $null
    }
    $blockingStatus = $advice.status -in @("READY", "NO_LEGAL_ROUTE", "UNSUPPORTED_INTERACTION", "UNSUPPORTED_PATCH")
    if(-not $blockingStatus -or ($advice.status -eq "READY" -and [bool]$advice.automationAllowed))
    {
        return $null
    }
    if([string]::IsNullOrWhiteSpace($script:activeGameId) -or
        -not [string]::Equals([string]$advice.gameId, $script:activeGameId, [System.StringComparison]::OrdinalIgnoreCase))
    {
        return $null
    }
    $age = [DateTimeOffset]::UtcNow - [DateTimeOffset]$advice.generatedAt
    if($age.TotalSeconds -lt -2 -or $age.TotalSeconds -gt $AdviceMaximumAgeSeconds)
    {
        return $null
    }
    return $advice
}

function Start-NextGame {
    if($SkipPlayButton)
    {
        Write-RunnerEvent -Event "play_button_skipped" -Data @{}
        return
    }
    $window = Get-HearthstoneWindow
    if(-not $DryRun)
    {
        [void][DiscardAdvisor.MatchRunner.NativeMethods]::SetForegroundWindow($window.Handle)
        Start-Sleep -Milliseconds 250
    }
    if(-not $SkipDeckSelection)
    {
        Invoke-MouseClick (Get-ScaledPoint $layout.deckSlot $window) "Select target deck"
        Start-Sleep -Seconds 1
    }
    Invoke-MouseClick (Get-ScaledPoint $layout.playButton $window) "Start traditional match"
}

function Confirm-Mulligan {
    if($SkipMulligan)
    {
        Write-RunnerEvent -Event "mulligan_confirmation_skipped" -Data @{}
        return
    }
    Start-Sleep -Seconds $MulliganDelaySeconds
    $window = Get-HearthstoneWindow
    if(-not $DryRun)
    {
        [void][DiscardAdvisor.MatchRunner.NativeMethods]::SetForegroundWindow($window.Handle)
        Start-Sleep -Milliseconds 250
    }
    Invoke-MouseClick (Get-ScaledPoint $layout.mulliganConfirm $window) "Keep opening hand"
}

function Dismiss-CompletedGame {
    if($SkipContinue)
    {
        Write-RunnerEvent -Event "continue_button_skipped" -Data @{}
        return
    }
    Start-Sleep -Seconds $ContinueDelaySeconds
    $window = Get-HearthstoneWindow
    if(-not $DryRun)
    {
        [void][DiscardAdvisor.MatchRunner.NativeMethods]::SetForegroundWindow($window.Handle)
        Start-Sleep -Milliseconds 250
    }
    Invoke-MouseClick (Get-ScaledPoint $layout.continueButton $window) "Dismiss completed match"
    Start-Sleep -Seconds 2
}

function Copy-JsonLinesSinceSessionStart {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$Filter,

        [Parameter(Mandatory = $true)]
        [string]$TimestampProperty,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    if(-not (Test-Path -LiteralPath $SourceDirectory -PathType Container))
    {
        return
    }
    $lines = New-Object 'System.Collections.Generic.List[string]'
    foreach($file in Get-ChildItem -LiteralPath $SourceDirectory -Filter $Filter -File | Sort-Object Name)
    {
        foreach($line in Get-Content -LiteralPath $file.FullName)
        {
            if([string]::IsNullOrWhiteSpace($line))
            {
                continue
            }
            try
            {
                $entry = $line | ConvertFrom-Json
                $property = $entry.PSObject.Properties[$TimestampProperty]
                if($null -ne $property -and ([DateTimeOffset]$property.Value) -ge $script:sessionStartUtc)
                {
                    $lines.Add($line)
                }
            }
            catch
            {
            }
        }
    }
    if($lines.Count -gt 0)
    {
        $parent = Split-Path -Parent $Destination
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        [System.IO.File]::WriteAllLines($Destination, $lines, (New-Object System.Text.UTF8Encoding($false)))
    }
}

function Copy-SessionEvidence {
    Copy-JsonLinesSinceSessionStart `
        -SourceDirectory $DiagnosticsPath `
        -Filter "discard-advisor.jsonl*" `
        -TimestampProperty "timestamp" `
        -Destination (Join-Path $sessionDirectory "diagnostics\discard-advisor-session.jsonl")
    $automationDirectory = Split-Path -Parent $AdvicePath
    Copy-JsonLinesSinceSessionStart `
        -SourceDirectory $automationDirectory `
        -Filter "advice-history.jsonl" `
        -TimestampProperty "generatedAt" `
        -Destination (Join-Path $sessionDirectory "automation\advice-history-session.jsonl")
    if(Test-Path -LiteralPath $AdvicePath -PathType Leaf)
    {
        $destination = Join-Path $sessionDirectory "automation"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath $AdvicePath -Destination (Join-Path $destination "current-advice.json") -Force
    }
    foreach($item in @(
        @{ Name = "fixtures"; Path = $FixturePath; Filter = "*.snapshot.json" },
        @{ Name = "replays"; Path = $ReplayPath; Filter = "*.hdtreplay" }))
    {
        if(-not (Test-Path -LiteralPath $item.Path -PathType Container))
        {
            continue
        }
        $destination = Join-Path $sessionDirectory $item.Name
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach($file in Get-ChildItem -LiteralPath $item.Path -Filter $item.Filter -File |
            Where-Object { $_.LastWriteTimeUtc -ge $script:sessionStartUtc.UtcDateTime })
        {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force
        }
    }
}

function Publish-SessionToGitHub {
    if(-not $UploadToGitHub)
    {
        return
    }
    if([string]::IsNullOrWhiteSpace($RepositoryPath))
    {
        throw "-RepositoryPath is required with -UploadToGitHub."
    }
    $repo = [System.IO.Path]::GetFullPath($RepositoryPath)
    if(-not (Test-Path -LiteralPath (Join-Path $repo ".git") -PathType Container))
    {
        throw "RepositoryPath '$repo' is not a Git worktree."
    }
    & git -C $repo diff --cached --quiet --exit-code
    if($LASTEXITCODE -eq 1)
    {
        throw "The Git index already contains staged changes. Commit or unstage them before using -UploadToGitHub."
    }
    if($LASTEXITCODE -ne 0)
    {
        throw "Could not inspect the Git index; git diff exited with code $LASTEXITCODE."
    }
    $archiveRelative = Join-Path "validation-runs" $sessionId
    $archiveTarget = Join-Path $repo $archiveRelative
    if(Test-Path -LiteralPath $archiveTarget)
    {
        throw "GitHub archive target '$archiveTarget' already exists."
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $archiveTarget) -Force | Out-Null
    Copy-Item -LiteralPath $sessionDirectory -Destination $archiveTarget -Recurse
    & git -C $repo add -f -- $archiveRelative
    if($LASTEXITCODE -ne 0) { throw "git add failed with exit code $LASTEXITCODE." }
    & git -C $repo commit -m "data: add automated match run $sessionId"
    if($LASTEXITCODE -ne 0) { throw "git commit failed with exit code $LASTEXITCODE." }
    $branch = if([string]::IsNullOrWhiteSpace($GitBranch)) { (& git -C $repo branch --show-current).Trim() } else { $GitBranch }
    if([string]::IsNullOrWhiteSpace($branch)) { throw "Could not determine the Git branch to push." }
    & git -C $repo push $GitRemote "HEAD:$branch"
    if($LASTEXITCODE -ne 0) { throw "git push failed with exit code $LASTEXITCODE." }
    Write-RunnerEvent -Event "github_upload_completed" -Data @{ remote = $GitRemote; branch = $branch; path = $archiveRelative }
}

function Write-SessionSummary {
    param(
        [bool]$Failed = $false,

        [string]$ErrorMessage = ""
    )

    $summary = [ordered]@{
        schemaVersion = 1
        sessionId = $sessionId
        startedAtUtc = $script:sessionStartUtc.ToString("o")
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        requestedMatches = $MatchCount
        completedMatches = $completedGameIds.Count
        stopped = [bool](Test-StopRequested)
        failed = $Failed
        dryRun = [bool]$DryRun
        pluginVersion = $expectedPluginVersion
        ruleSetVersion = $expectedRuleSetVersion
        handledStateCount = $handledAdviceKeys.Count
        gameIds = @($completedGameIds | Sort-Object)
    }
    if(-not [string]::IsNullOrWhiteSpace($ErrorMessage))
    {
        $summary.error = $ErrorMessage
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Assert-WindowsHost
Initialize-NativeMethods
if(-not (Test-Path -LiteralPath $LayoutPath -PathType Leaf))
{
    throw "Layout file was not found at '$LayoutPath'."
}
$layout = Get-Content -LiteralPath $LayoutPath -Raw | ConvertFrom-Json
if($ValidateOnly)
{
    $window = Get-HearthstoneWindow
    foreach($name in @("deckSlot", "playButton", "mulliganConfirm", "continueButton", "endTurn", "friendlyHero", "opponentHero", "friendlyHeroPower", "opponentHeroPower", "friendlyWeapon", "opponentWeapon", "playTarget"))
    {
        if($null -eq $layout.PSObject.Properties[$name])
        {
            throw "Layout is missing required point '$name'."
        }
        $point = Get-ScaledPoint $layout.$name $window
        Write-Host ("{0}: {1},{2}" -f $name, $point.X, $point.Y)
    }
    foreach($name in @("hand", "friendlyBoard", "opponentBoard", "choice"))
    {
        if($null -eq $layout.PSObject.Properties[$name])
        {
            throw "Layout is missing required indexed zone '$name'."
        }
    }
    Write-Host ("Layout validated for Hearthstone window {0}x{1}." -f $window.Width, $window.Height)
    exit 0
}
if(-not [DiscardAdvisor.MatchRunner.NativeMethods]::RegisterHotKey(
        [IntPtr]::Zero,
        1,
        [DiscardAdvisor.MatchRunner.NativeMethods]::ModControl -bor [DiscardAdvisor.MatchRunner.NativeMethods]::ModShift,
        [DiscardAdvisor.MatchRunner.NativeMethods]::VirtualKeyF12))
{
    throw "Could not register the Ctrl+Shift+F12 emergency stop hotkey."
}

try
{
    Write-RunnerEvent -Event "session_started" -Data @{
        sessionId = $sessionId
        requestedMatches = $MatchCount
        pluginVersion = $expectedPluginVersion
        ruleSetVersion = $expectedRuleSetVersion
        advicePath = [System.IO.Path]::GetFullPath($AdvicePath)
        dryRun = [bool]$DryRun
        emergencyStop = "Ctrl+Shift+F12"
    }

    for($matchIndex = 1; $matchIndex -le $MatchCount -and -not (Test-StopRequested); $matchIndex++)
    {
        Update-GameProgress
        $completedBefore = $completedGameIds.Count
        $observedBefore = $observedGameIds.Count
        Write-RunnerEvent -Event "match_requested" -Data @{ matchIndex = $matchIndex; completedBefore = $completedBefore }
        Start-NextGame
        $startDeadline = [DateTimeOffset]::UtcNow.AddSeconds($GameStartTimeoutSeconds)
        while([DateTimeOffset]::UtcNow -lt $startDeadline -and -not (Test-StopRequested))
        {
            Update-GameProgress
            if($observedGameIds.Count -gt $observedBefore)
            {
                break
            }
            Start-Sleep -Milliseconds $PollMilliseconds
        }
        if($observedGameIds.Count -le $observedBefore)
        {
            Write-RunnerEvent -Event "match_start_timeout" -Data @{ matchIndex = $matchIndex }
            throw "Match $matchIndex did not start within $GameStartTimeoutSeconds seconds."
        }
        Confirm-Mulligan

        $gameDeadline = [DateTimeOffset]::UtcNow.AddSeconds($GameTimeoutSeconds)
        $blockingAdviceKey = $null
        $blockingAdviceSince = $null
        while([DateTimeOffset]::UtcNow -lt $gameDeadline -and -not (Test-StopRequested))
        {
            Update-GameProgress
            if($completedGameIds.Count -gt $completedBefore)
            {
                Write-RunnerEvent -Event "match_completed" -Data @{ matchIndex = $matchIndex; completedMatches = $completedGameIds.Count }
                break
            }
            $advice = Get-EligibleAdvice
            if($null -ne $advice)
            {
                $adviceKey = ([string]$advice.gameId) + ":" + ([string]$advice.stateId)
                [void]$handledAdviceKeys.Add($adviceKey)
                $blockingAdviceKey = $null
                $blockingAdviceSince = $null
                try
                {
                    Invoke-AdviceStep $advice
                }
                catch
                {
                    Write-RunnerEvent -Event "advice_step_failed" -Data @{ stateId = [string]$advice.stateId; message = $_.Exception.Message }
                    throw
                }
                Start-Sleep -Seconds $ActionSettleSeconds
            }
            else
            {
                $blocked = Get-BlockingAdvice
                if($null -ne $blocked)
                {
                    $blockedKey = ([string]$blocked.gameId) + ":" + ([string]$blocked.stateId)
                    if(-not [string]::Equals($blockingAdviceKey, $blockedKey, [System.StringComparison]::Ordinal))
                    {
                        $blockingAdviceKey = $blockedKey
                        $blockingAdviceSince = [DateTimeOffset]::UtcNow
                        Write-RunnerEvent -Event "advice_blocked" -Data @{
                            gameId = [string]$blocked.gameId
                            stateId = [string]$blocked.stateId
                            blockers = @($blocked.blockers)
                        }
                    }
                    elseif(([DateTimeOffset]::UtcNow - $blockingAdviceSince).TotalSeconds -ge $BlockedAdviceTimeoutSeconds)
                    {
                        throw "Advice for state '$($blocked.stateId)' remained blocked for $BlockedAdviceTimeoutSeconds seconds: $(@($blocked.blockers) -join ', ')."
                    }
                }
                else
                {
                    $blockingAdviceKey = $null
                    $blockingAdviceSince = $null
                }
                Start-Sleep -Milliseconds $PollMilliseconds
            }
        }
        if($completedGameIds.Count -le $completedBefore -and -not (Test-StopRequested))
        {
            Write-RunnerEvent -Event "match_timeout" -Data @{ matchIndex = $matchIndex }
            throw "Match $matchIndex did not complete within $GameTimeoutSeconds seconds."
        }
        if($completedGameIds.Count -gt $completedBefore)
        {
            Dismiss-CompletedGame
        }
    }

    Copy-SessionEvidence
    Write-SessionSummary
    Write-RunnerEvent -Event "session_completed" -Data @{ completedMatches = $completedGameIds.Count; sessionDirectory = $sessionDirectory }
    Publish-SessionToGitHub
}
catch
{
    $failureMessage = $_.Exception.Message
    try
    {
        Write-RunnerEvent -Event "session_failed" -Data @{ message = $failureMessage; completedMatches = $completedGameIds.Count }
        Copy-SessionEvidence
        Write-SessionSummary -Failed $true -ErrorMessage $failureMessage
    }
    catch
    {
        Write-Warning ("Could not archive the failed session: " + $_.Exception.Message)
    }
    throw
}
finally
{
    [void][DiscardAdvisor.MatchRunner.NativeMethods]::UnregisterHotKey([IntPtr]::Zero, 1)
}

Write-Host "Session directory: $sessionDirectory"
Write-Host "Completed matches: $($completedGameIds.Count)/$MatchCount"
