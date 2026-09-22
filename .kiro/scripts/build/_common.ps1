# Shared helpers for the .kiro/scripts/build game scripts.
# Dot-source this file; it defines paths and state/status helpers.

Set-StrictMode -Version Latest

# This file lives in <repo>/.kiro/scripts/build, so the repo root is three up.
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
# Runtime artifacts live under .pyddock/tmp (already git-ignored) so nothing
# extra needs to be excluded from source control.
$script:TmpRoot  = Join-Path $RepoRoot '.pyddock\tmp'
$script:RunDir   = Join-Path $TmpRoot 'run'
$script:LogDir   = Join-Path $TmpRoot 'logs'
$script:DumpDir  = Join-Path $TmpRoot 'dumps'
# Written by build-and-run.ps1, read by the debug tooling (.kiro/scripts/debug).
$script:StateFile = Join-Path $RunDir 'game.json'
# Written by .kiro/scripts/debug/dbg.py while a probe holds the debugger.
$script:DebugStateFile = Join-Path $RunDir 'debug.json'
$script:Solution  = Join-Path $RepoRoot 'OpenRA.slnx'
$script:GameDll   = Join-Path $RepoRoot 'bin\OpenRA.dll'

function Initialize-KiroDirs {
    foreach ($d in @($RunDir, $LogDir, $DumpDir)) {
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    }
}

# Safe property read off a ConvertFrom-Json object under StrictMode.
function Get-Prop($Object, [string]$Name) {
    if (-not $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if (-not $p) { return $null }
    return $p.Value
}

function Get-GameState {
    if (-not (Test-Path $StateFile)) { return $null }
    try { return Get-Content $StateFile -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Save-GameState($State) {
    Initialize-KiroDirs
    $State | ConvertTo-Json -Depth 5 | Set-Content -Path $StateFile -Encoding UTF8
}

function Clear-GameState {
    if (Test-Path $StateFile) { Remove-Item $StateFile -Force }
}

# Debug-session state, owned by dbg.py. We only ever read it here.
function Get-DebugState {
    if (-not (Test-Path $DebugStateFile)) { return $null }
    try { return Get-Content $DebugStateFile -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Clear-DebugState {
    if (Test-Path $DebugStateFile) { Remove-Item $DebugStateFile -Force }
}

# Returns the process if alive, else $null. Guards against PID reuse by name.
# NOTE: parameter is deliberately not named $Pid - that collides with PowerShell's
# read-only automatic $PID variable.
function Get-LiveProcess($ProcessId, $NameLike) {
    if (-not $ProcessId) { return $null }
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    if ($NameLike -and $p.Name -notlike $NameLike) { return $null }
    return $p
}

function Test-GameRunning {
    $state = Get-GameState
    if (-not $state) { return $false }
    return [bool](Get-LiveProcess (Get-Prop $state 'GamePid') 'dotnet*')
}

# Any netcoredbg process, tracked or not. A debugger attached to the game blocks
# both VS Code and a second probe (CoreCLR permits one debugger per process),
# so callers use this to produce a clear error instead of a mystery failure.
function Get-AttachedDebuggers {
    return @(Get-Process netcoredbg -ErrorAction SilentlyContinue)
}

# Launch a process fully detached from this script.
#
# Key detail: we deliberately do NOT use -RedirectStandardOutput/-RedirectStandardError.
# Those keep the PARENT attached to the child's stdio pipes, so the launching
# script cannot exit until the child does - that was the "script never returns"
# bug. Plain "Start-Process -PassThru" (no redirection) returns immediately and
# the child survives the parent exiting, which is exactly what we want.
#
# Consequence: we don't capture the child's raw console stdout here. That's fine
# because the game writes to OpenRA's Support/Logs (surfaced by Show-GameStatus).
#
# -PassThru gives us the real PID directly, so there is no grandchild hunting.
# Returns @{ TargetPid = <pid> } or $null if the process exited instantly.
function Start-Detached {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)] [string]$WorkingDirectory,
        [switch]$Hidden
    )

    $splat = @{
        FilePath         = $FilePath
        ArgumentList     = $Arguments
        WorkingDirectory = $WorkingDirectory
        PassThru         = $true
    }
    if ($Hidden) { $splat.WindowStyle = 'Hidden' }

    $p = Start-Process @splat
    if (-not $p) { return $null }

    # A truly failed launch either throws above or exits with a code instantly.
    Start-Sleep -Milliseconds 200
    if ($p.HasExited -and $p.ExitCode -ne 0) { return $null }

    return @{ TargetPid = [int]$p.Id }
}

function Get-OpenRALogDir {
    $candidates = @(
        (Join-Path $RepoRoot 'Support\Logs'),
        (Join-Path $env:APPDATA 'OpenRA\Logs'),
        (Join-Path $env:USERPROFILE 'Documents\OpenRA\Logs')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

function Write-Section($Title) {
    Write-Host ''
    Write-Host "== $Title " -ForegroundColor Cyan -NoNewline
    Write-Host ('=' * [Math]::Max(0, 60 - $Title.Length)) -ForegroundColor DarkCyan
}

function Show-Tail($Path, $Lines = 20, $Label = $null) {
    if (-not $Label) { $Label = $Path }
    if (-not (Test-Path $Path)) {
        Write-Host "  (no $Label)" -ForegroundColor DarkGray
        return
    }
    $item = Get-Item $Path
    Write-Host "  $Label  [$([Math]::Round($item.Length / 1KB, 1)) KB, modified $($item.LastWriteTime.ToString('HH:mm:ss'))]" -ForegroundColor DarkGray
    if ($item.Length -eq 0) {
        Write-Host '    (empty)' -ForegroundColor DarkGray
        return
    }
    Get-Content $Path -Tail $Lines | ForEach-Object { Write-Host "    $_" }
}

function Show-GameStatus {
    $state = Get-GameState

    Write-Section 'Process'
    if (-not $state) {
        Write-Host '  No tracked session (no .pyddock\tmp\run\game.json).' -ForegroundColor DarkGray
    }
    else {
        $gamePid = Get-Prop $state 'GamePid'
        $game    = Get-LiveProcess $gamePid 'dotnet*'
        $started = [DateTime]::Parse((Get-Prop $state 'StartedAt'))
        $uptime  = (Get-Date) - $started

        Write-Host "  Started    : $($started.ToString('yyyy-MM-dd HH:mm:ss'))  (uptime $([int]$uptime.TotalMinutes)m $($uptime.Seconds)s)"
        Write-Host "  Mod / args : $((Get-Prop $state 'GameArgs') -join ' ')"
        Write-Host "  Config     : $(Get-Prop $state 'Configuration')"
        if ($game) { Write-Host "  game       : PID $gamePid RUNNING" -ForegroundColor Green }
        elseif ($gamePid) { Write-Host "  game       : PID $gamePid not running (stale state)" -ForegroundColor Yellow }
        else { Write-Host '  game       : PID was never resolved' -ForegroundColor DarkGray }
    }

    Write-Section 'Debugger'
    $dbgState = Get-DebugState
    $live = Get-AttachedDebuggers
    if ($dbgState) {
        $dbgPid = Get-Prop $dbgState 'DebuggerPid'
        $alive = Get-LiveProcess $dbgPid 'netcoredbg*'
        if ($alive) {
            Write-Host "  Probe in flight: netcoredbg PID $dbgPid" -ForegroundColor Yellow
            Write-Host "    started : $(Get-Prop $dbgState 'StartedAt')" -ForegroundColor DarkGray
            Write-Host "    target  : $((Get-Prop $dbgState 'Breakpoints') -join ', ')" -ForegroundColor DarkGray
            Write-Host '    The game may be halted at a breakpoint.' -ForegroundColor DarkGray
        }
        else {
            Write-Host "  Stale debug state (netcoredbg PID $dbgPid is gone)." -ForegroundColor Yellow
            Write-Host '    A probe died without cleaning up. Safe to remove:' -ForegroundColor DarkGray
            Write-Host '    .kiro\scripts\build\kill-game.ps1 -All' -ForegroundColor DarkGray
        }
    }
    elseif ($live) {
        Write-Host "  $($live.Count) untracked netcoredbg process(es): $(($live | ForEach-Object { $_.Id }) -join ', ')" -ForegroundColor Yellow
        Write-Host '    Not started by dbg.py. This will block both VS Code and new probes.' -ForegroundColor DarkGray
    }
    else {
        Write-Host '  No debugger attached. VS Code and dbg.py are both free to attach.' -ForegroundColor DarkGray
    }

    Write-Section 'OpenRA logs'
    $oraLogs = Get-OpenRALogDir
    if (-not $oraLogs) {
        Write-Host '  (no OpenRA log directory found yet)' -ForegroundColor DarkGray
    }
    else {
        Write-Host "  dir: $oraLogs" -ForegroundColor DarkGray
        $since = if ($state) { [DateTime]::Parse((Get-Prop $state 'StartedAt')).AddSeconds(-5) } else { (Get-Date).AddHours(-1) }
        $recent = Get-ChildItem $oraLogs -Filter *.log -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $since } | Sort-Object LastWriteTime -Descending
        if (-not $recent) { Write-Host '  (no logs touched this session)' -ForegroundColor DarkGray }
        foreach ($f in $recent) {
            $lines = if ($f.Name -match 'exception') { 40 } else { 8 }
            $colour = if ($f.Name -match 'exception') { 'Red' } else { $null }
            if ($colour) { Write-Host "  !! $($f.Name) contains an exception trace" -ForegroundColor Red }
            Show-Tail $f.FullName $lines $f.Name
        }
    }

    Write-Section 'Crash dumps'
    $since = if ($state) { [DateTime]::Parse((Get-Prop $state 'StartedAt')).AddSeconds(-5) } else { [DateTime]::MinValue }
    $dumps = Get-ChildItem $DumpDir -Filter *.dmp -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    if (-not $dumps) {
        Write-Host '  (none)' -ForegroundColor DarkGray
    }
    foreach ($d in $dumps) {
        $new = $d.LastWriteTime -ge $since
        $tag = if ($new) { 'NEW ' } else { '    ' }
        $col = if ($new) { 'Red' } else { 'DarkGray' }
        Write-Host ("  $tag{0}  {1:N1} MB  {2}" -f $d.Name, ($d.Length / 1MB), $d.LastWriteTime) -ForegroundColor $col
    }
    if ($dumps) {
        Write-Host '  Analyse with: .kiro\scripts\debug\dump.py analyze' -ForegroundColor DarkGray
    }
    Write-Host ''
}
