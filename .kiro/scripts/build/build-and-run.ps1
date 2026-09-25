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

      agent : from openra_debug import session  (in a run_python snippet)
              (spawns its own netcoredbg, attaches, detaches when the block exits)
      human : VS Code -> Run and Debug -> "Attach (OpenRA)"
              (attaches vsdbg directly by PID)

.EXAMPLE
    .kiro\scripts\build\build-and-run.ps1 Game.Mod=ra
.EXAMPLE
    .kiro\scripts\build\build-and-run.ps1 -Build
    # Builds the whole solution (game + utility + all mod assemblies) and exits
    # without launching anything. Use this to verify the code still compiles.
    # Exit 0 means the build succeeded, non-zero means it failed.
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
.EXAMPLE
    .kiro\scripts\build\build-and-run.ps1 -Tests -Filter ActivityCloneTest
    # Runs only the tests whose fully-qualified name contains "ActivityCloneTest".
    # -Filter is passed to `dotnet test --filter`, so any NUnit filter expression
    # works, e.g. -Filter "FullyQualifiedName~Fork|FullyQualifiedName~Clone".
.EXAMPLE
    .kiro\scripts\build\build-and-run.ps1 -Tests -SkipBuild -Filter MersenneTwister -PerTest
    # Reuses the existing test build, runs just the matching tests, and prints
    # each test name and result instead of only the summary.
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

    # Build only, then exit - do not launch the game or run tests. Builds the whole
    # solution (game + utility + all mod assemblies) so this doubles as a "does it
    # still compile?" check without the cost/side effects of launching anything.
    [Parameter()]
    [switch]$Build,

    # Print the status of the current session and exit.
    [Parameter()]
    [switch]$Status,

    # Run the unit test suite instead of launching the game, then exit with the
    # test runner's exit code. Honours -SkipBuild and -Configuration.
    [Parameter()]
    [switch]$Tests,

    # Test-run only: restrict the run to tests matching this expression. Forwarded
    # verbatim to `dotnet test --filter`. NUnit accepts FullyQualifiedName~<substr>,
    # TestCategory=<cat>, and boolean combinations with | and &. A bare string like
    # "ActivityCloneTest" is treated as FullyQualifiedName~ActivityCloneTest for
    # convenience.
    [Parameter()]
    [string]$Filter,

    # Test-run only: print each test's name and result (console logger at normal
    # verbosity) instead of just the pass/fail summary. Handy when a run fails and
    # you want to see which tests without re-running. Named to avoid confusion with
    # `dotnet test --list-tests`, which lists tests without running them.
    [Parameter()]
    [switch]$PerTest,

    # Test-run only: escape hatch for any other `dotnet test` arguments. Everything
    # here is appended verbatim, so the script does not need editing to reach a flag
    # it does not model explicitly.
    [Parameter()]
    [string[]]$TestArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')

# Build the whole solution once. Produces the game, the utility, and all three mod
# assemblies (Cnc/Common/D2k) into bin\. This is the right unit of build for every
# path here: the game needs its mods, and the OpenRA.Test.Game integration suite
# loads the mod assemblies from bin\ at RUNTIME via ObjectCreator (it does not
# reference them at compile time), so building only the test projects would leave
# stale mod code in bin\ and produce misleading test results. Returns $true on success.
function Invoke-FullBuild {
    param([string]$Configuration)

    $rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    Write-Host "Building $(Split-Path $Solution -Leaf) ($Configuration, $rid)..." -ForegroundColor Cyan

    # Out-Host, not bare invocation: a native command's stdout goes to the success stream, so without
    # this the compiler diagnostics would be captured as part of this function's RETURN VALUE instead of
    # being shown. That made `if (-not (Invoke-FullBuild ...))` always false (a non-empty array is truthy),
    # so a failed build printed its error and then happily ran the tests against stale binaries in bin\.
    & dotnet build $Solution -c $Configuration --nologo -p:TargetPlatform=$rid | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host "ERROR: build failed (exit $LASTEXITCODE)." -ForegroundColor Red
        return $false
    }
    Write-Host 'Build succeeded.' -ForegroundColor Green
    return $true
}

# --- reject test-only flags on a non-test run ------------------------------
# These only affect the -Tests path. Silently ignoring them on a game launch
# would hide a caller mistake, so fail fast instead.
if (-not $Tests) {
    $misused = @()
    if ($Filter) { $misused += '-Filter' }
    if ($PerTest) { $misused += '-PerTest' }
    if ($TestArgs) { $misused += '-TestArgs' }
    if ($misused.Count -gt 0) {
        $verb = if ($misused.Count -eq 1) { 'applies' } else { 'apply' }
        Write-Host "ERROR: $($misused -join ', ') only $verb with -Tests." -ForegroundColor Red
        exit 1
    }
}

# --- reject contradictory mode combinations --------------------------------
# -Build (build then stop) is incompatible with the modes that need to DO something
# after building, and with -SkipBuild (which would leave -Build with nothing to do).
if ($Build) {
    $conflict = @()
    if ($Tests)     { $conflict += '-Tests' }
    if ($SkipBuild) { $conflict += '-SkipBuild' }
    if ($GameArgs -and $GameArgs.Count -gt 0) { $conflict += 'game args' }
    if ($conflict.Count -gt 0) {
        Write-Host "ERROR: -Build cannot be combined with $($conflict -join ', '). It only builds and exits." -ForegroundColor Red
        exit 1
    }
}

# --- -Build short-circuit --------------------------------------------------
# Build the whole solution and stop. No game launch, no tests, no session state.
if ($Build) {
    if (Invoke-FullBuild -Configuration $Configuration) { exit 0 } else { exit 1 }
}

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
    # Two test assemblies, run as one suite:
    #   OpenRA.Test       - fast, isolated unit tests (no mod, no World).
    #   OpenRA.Test.Game  - integration tests that boot a real mod and construct a live World.
    #                       Kept separate because it must NOT reference the mod assemblies (see
    #                       its csproj): ObjectCreator has to be the sole loader of Common/Cnc, or
    #                       cross-context type identity breaks trait validation.
    $testSuites = @(
        @{ Dll = Join-Path $RepoRoot 'bin\OpenRA.Test.dll' }
        @{ Dll = Join-Path $RepoRoot 'bin\OpenRA.Test.Game.dll' }
    )

    # Build the WHOLE solution, not just the two test projects. OpenRA.Test.Game boots
    # a real mod and loads the mod assemblies from bin\ at runtime; building only the
    # test projects would leave stale (or missing) mod code in bin\ and make the
    # integration suite test the wrong thing. A full build keeps bin\ consistent.
    if (-not $SkipBuild) {
        if (-not (Invoke-FullBuild -Configuration $Configuration)) {
            Write-Host 'Not running tests.' -ForegroundColor Red
            exit 1
        }
    }

    $filterExpr = $null
    if ($Filter) {
        # A bare identifier (no filter operators) is a common case; expand it to the
        # usual substring-match form so callers can pass just a fixture name.
        $filterExpr = if ($Filter -match '[~=&|!()]') { $Filter } else { "FullyQualifiedName~$Filter" }
    }

    $overallExit = 0
    foreach ($suite in $testSuites) {
        $testDll = $suite.Dll
        $name = [System.IO.Path]::GetFileNameWithoutExtension($testDll)

        if (-not (Test-Path $testDll)) {
            Write-Host "ERROR: $testDll not found. Build the tests first (drop -SkipBuild)." -ForegroundColor Red
            exit 1
        }

        Write-Host ''
        Write-Host "Running tests: $name ..." -ForegroundColor Cyan

        # --test-adapter-path:. matches make.ps1/Makefile: NUnit's adapter ships
        # alongside the test dll in bin, not in the default probing location.
        $testCmd = @($testDll, '--test-adapter-path:.')

        if ($filterExpr) {
            Write-Host "  filter: $filterExpr" -ForegroundColor DarkGray
            $testCmd += @('--filter', $filterExpr)
        }

        if ($PerTest) {
            $testCmd += @('--logger', 'console;verbosity=normal')
        }

        if ($TestArgs) {
            $testCmd += $TestArgs
        }

        & dotnet test @testCmd
        if ($LASTEXITCODE -ne 0) {
            $overallExit = $LASTEXITCODE
        }
    }

    Write-Host ''
    if ($overallExit -eq 0) {
        Write-Host 'All unit tests passed.' -ForegroundColor Green
    }
    else {
        Write-Host "Unit tests FAILED (dotnet test exit $overallExit)." -ForegroundColor Red
    }
    exit $overallExit
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
    if (-not (Invoke-FullBuild -Configuration $Configuration)) {
        Write-Host 'Not launching.' -ForegroundColor Red
        exit 1
    }
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
# the runtime ignores them while a debugger is attached, so a crash during an
# openra_debug session window produces no dump - the session reports the stop instead.
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

# Record the OS process creation time, not just "now". The debug tooling compares
# this against the live process's creation time to detect PID reuse (Windows
# recycles PIDs freely), so it must be the process's own start time - which can
# differ from now by the launch delay. Fall back to now if the process is too
# short-lived to query (it would fail the liveness check anyway).
$startTime = $null
try {
    $proc = Get-Process -Id $gamePid -ErrorAction Stop
    $startTime = $proc.StartTime.ToUniversalTime().ToString('o')
}
catch {
    $startTime = (Get-Date).ToUniversalTime().ToString('o')
}

Save-GameState ([pscustomobject]@{
    GamePid       = $gamePid
    StartedAt     = (Get-Date).ToString('o')
    StartTime     = $startTime
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
Write-Host 'Debug (agent) : run_python -> from openra_debug import session' -ForegroundColor DarkGray
Write-Host 'Debug (human) : VS Code -> Run and Debug -> "Attach (OpenRA)"' -ForegroundColor DarkGray
Write-Host 'Status        : .kiro\scripts\build\status-game.ps1' -ForegroundColor DarkGray
Write-Host 'Stop          : .kiro\scripts\build\kill-game.ps1' -ForegroundColor DarkGray
exit 0
