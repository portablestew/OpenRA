<#
.SYNOPSIS
    Build OpenRA and launch it, tracking the process so the other scripts can find it.

.DESCRIPTION
    1. Refuses to start if a tracked session is already running (prints status instead).
    2. Builds OpenRA.slnx. Bails out on failure.
    3. Launches the game under the plain runtime and records its PID in
       .pyddock/tmp/run/game.json.
    4. Returns immediately; the game keeps running after this script exits.

    No debugger is started here. Nothing is attached to the game until you ask
    for it, which keeps the crash-dump path working (DOTNET_DbgEnableMiniDump is
    ignored while a debugger is attached) and leaves the single CoreCLR debugger
    slot free:

      agent : .kiro\scripts\debug\dbg.py probe --at <file:line>
              (spawns its own netcoredbg, attaches, detaches when done)
      human : VS Code -> Run and Debug -> "Attach (OpenRA)"
              (attaches vsdbg directly by PID)

.EXAMPLE
    .kiro\scripts\build\build-and-run.ps1 Game.Mod=ra
.EXAMPLE
    .kiro\scripts\build\build-and-run.ps1 -SkipBuild Game.Mod=ra
.EXAMPLE
    .kiro\scripts\build\build-and-run.ps1 -Configuration Release Game.Mod=cnc Game.Fullscreen=false
.EXAMPLE
    .kiro\scripts\build\build-and-run.ps1 -Tests
    # Builds OpenRA.Test and runs the unit test suite instead of launching the
    # game. Exits 0 if every test passed, non-zero otherwise. This mirrors what
    # CI runs on Windows (make.ps1 tests). Combine with -SkipBuild to reuse the
    # existing bin\OpenRA.Test.dll.
#>
[CmdletBinding()]
param(
    # Everything positional is forwarded verbatim to OpenRA, so that
    #   build-and-run.ps1 Game.Mod=ra
    # works the same way as launch-game.cmd Game.Mod=ra.
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$GameArgs,

    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # Skip the build and go straight to launching.
    [Parameter()]
    [switch]$SkipBuild,

    # Print the status of the current session and exit.
    [Parameter()]
    [switch]$Status,

    # Run the unit test suite instead of launching the game, then exit with the
    # test runner's exit code. Honours -SkipBuild and -Configuration.
    [Parameter()]
    [switch]$Tests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')

# --- -Status short-circuit -------------------------------------------------
if ($Status) {
    Show-GameStatus
    exit 0
}

# --- -Tests short-circuit --------------------------------------------------
# Build and run the unit test suite instead of the game. This is a separate
# path from the game launch: it never touches the tracked game session state,
# never sets the crash-dump env vars, and runs to completion synchronously
# (unlike the detached game process). Mirrors make.ps1 'tests' / CI on Windows.
if ($Tests) {
    $testProject = Join-Path $RepoRoot 'OpenRA.Test\OpenRA.Test.csproj'
    $testDll     = Join-Path $RepoRoot 'bin\OpenRA.Test.dll'

    if (-not $SkipBuild) {
        $rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
        Write-Host "Building OpenRA.Test ($Configuration, $rid)..." -ForegroundColor Cyan
        & dotnet build $testProject -c $Configuration --nologo -p:TargetPlatform=$rid
        if ($LASTEXITCODE -ne 0) {
            Write-Host ''
            Write-Host "ERROR: test build failed (exit $LASTEXITCODE). Not running tests." -ForegroundColor Red
            exit 1
        }
        Write-Host 'Test build succeeded.' -ForegroundColor Green
    }

    if (-not (Test-Path $testDll)) {
        Write-Host "ERROR: $testDll not found. Build the tests first (drop -SkipBuild)." -ForegroundColor Red
        exit 1
    }

    Write-Host ''
    Write-Host 'Running unit tests...' -ForegroundColor Cyan
    # --test-adapter-path:. matches make.ps1/Makefile: NUnit's adapter ships
    # alongside the test dll in bin, not in the default probing location.
    & dotnet test $testDll --test-adapter-path:.
    $testExit = $LASTEXITCODE

    Write-Host ''
    if ($testExit -eq 0) {
        Write-Host 'All unit tests passed.' -ForegroundColor Green
    }
    else {
        Write-Host "Unit tests FAILED (dotnet test exit $testExit)." -ForegroundColor Red
    }
    exit $testExit
}

# --- 1. refuse to double-launch -------------------------------------------
if (Test-GameRunning) {
    Write-Host 'OpenRA is already running.' -ForegroundColor Yellow
    Show-GameStatus
    Write-Host 'Not launching a second instance.' -ForegroundColor Yellow
    Write-Host 'To stop it:   .kiro\scripts\build\kill-game.ps1' -ForegroundColor Yellow
    Write-Host 'For status:   .kiro\scripts\build\status-game.ps1' -ForegroundColor Yellow
    exit 1
}

# A stale state file (process died) is just noise - drop it.
if (Get-GameState) {
    Write-Verbose 'Clearing stale session state.'
    Clear-GameState
}

# --- 2. build -------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host "Building $(Split-Path $Solution -Leaf) ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $Solution -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host "ERROR: build failed (exit $LASTEXITCODE). Not launching." -ForegroundColor Red
        exit 1
    }
    Write-Host 'Build succeeded.' -ForegroundColor Green
}

if (-not (Test-Path $GameDll)) {
    Write-Host "ERROR: $GameDll not found. Build did not produce the game." -ForegroundColor Red
    exit 1
}

# --- 3. launch ------------------------------------------------------------
Initialize-KiroDirs

if (-not $GameArgs -or $GameArgs.Count -eq 0) {
    $GameArgs = @('Game.Mod=ra')
    Write-Host 'No game args given; defaulting to Game.Mod=ra' -ForegroundColor DarkGray
}

# Crash dumps for the no-debugger case. %p expands to the pid. These must be set
# in THIS process's environment so the child game process inherits them. Note
# the runtime ignores them while a debugger is attached, so a crash during a
# dbg.py probe window produces no dump - the probe reports the stop instead.
$env:DOTNET_DbgEnableMiniDump = '1'
$env:DOTNET_DbgMiniDumpType   = '4'   # 4 = full dump
$env:DOTNET_DbgMiniDumpName   = Join-Path $DumpDir 'openra.%p.dmp'

# Mirror launch-game.cmd: "dotnet bin\OpenRA.dll -- Engine.EngineDir=.. <args>"
# run from the repo root.
$gameProcArgs = @($GameDll, '--', 'Engine.EngineDir=..') + $GameArgs

Write-Host 'Launching game...' -ForegroundColor Cyan
Write-Host "  game args: $($GameArgs -join ' ')" -ForegroundColor DarkGray

$gameLaunch = Start-Detached -FilePath 'dotnet' `
    -Arguments $gameProcArgs `
    -WorkingDirectory $RepoRoot

if (-not $gameLaunch) {
    Write-Host 'ERROR: game process failed to start.' -ForegroundColor Red
    Write-Host 'Check OpenRA logs via .kiro\scripts\build\status-game.ps1' -ForegroundColor Yellow
    exit 1
}
$gamePid = $gameLaunch.TargetPid

# Confirm it is still alive a moment later (catches immediate crashes).
Start-Sleep -Milliseconds 400
if (-not (Get-LiveProcess $gamePid 'dotnet*')) {
    Write-Host 'ERROR: game exited immediately after launch.' -ForegroundColor Red
    Write-Host 'Check OpenRA logs via .kiro\scripts\build\status-game.ps1' -ForegroundColor Yellow
    exit 1
}

Save-GameState ([pscustomobject]@{
    GamePid       = $gamePid
    StartedAt     = (Get-Date).ToString('o')
    GameArgs      = $GameArgs
    Configuration = $Configuration
    DumpDir       = $DumpDir
})

Write-Host ''
Write-Host 'Launched.' -ForegroundColor Green
Write-Host "  game PID       : $gamePid"
Write-Host "  main game logs : $(Get-OpenRALogDir)"
Write-Host "  dumps          : .pyddock\tmp\dumps\"
Write-Host ''
Write-Host 'Debug (agent) : .kiro\scripts\debug\dbg.py probe --at <file:line>' -ForegroundColor DarkGray
Write-Host 'Debug (human) : VS Code -> Run and Debug -> "Attach (OpenRA)"' -ForegroundColor DarkGray
Write-Host 'Status        : .kiro\scripts\build\status-game.ps1' -ForegroundColor DarkGray
Write-Host 'Stop          : .kiro\scripts\build\kill-game.ps1' -ForegroundColor DarkGray
exit 0
