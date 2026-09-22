<#
.SYNOPSIS
    Stop the OpenRA game (and any debugger left attached to it).

.DESCRIPTION
    Kills the tracked game process and clears the session state file. Any
    netcoredbg left over from a crashed dbg.py probe is also cleaned up, since a
    stray debugger holds the single CoreCLR debugger slot and blocks both VS Code
    and new probes.

    With -All it additionally sweeps up untracked OpenRA / netcoredbg processes.

.EXAMPLE
    .kiro\scripts\build\kill-game.ps1
.EXAMPLE
    .kiro\scripts\build\kill-game.ps1 -All
#>
[CmdletBinding()]
param(
    # Also kill untracked netcoredbg and OpenRA processes.
    [switch]$All
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')

$killed = @()

function Stop-Tracked($ProcessId, $NameLike, $Label) {
    $p = Get-LiveProcess $ProcessId $NameLike
    if (-not $p) { return $false }
    try {
        Stop-Process -Id $p.Id -Force -ErrorAction Stop
        Write-Host "  killed $Label (PID $($p.Id))" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "  failed to kill $Label (PID $($p.Id)): $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# --- a stray probe debugger, first ----------------------------------------
# Order matters: kill the debugger before the game. If the game is halted at a
# breakpoint, killing the debugger is what lets it run (or die) freely.
$dbgState = Get-DebugState
if ($dbgState) {
    Write-Host 'Cleaning up debug session...' -ForegroundColor Cyan
    if (Stop-Tracked (Get-Prop $dbgState 'DebuggerPid') 'netcoredbg*' 'netcoredbg') {
        $killed += 'netcoredbg'
    }
    Clear-DebugState
    Write-Host '  cleared debug state (.pyddock\tmp\run\debug.json)' -ForegroundColor DarkGray
}

# --- the tracked game -----------------------------------------------------
$state = Get-GameState
if ($state) {
    Write-Host 'Stopping tracked session...' -ForegroundColor Cyan
    if (Stop-Tracked (Get-Prop $state 'GamePid') 'dotnet*' 'game') { $killed += 'game' }

    Clear-GameState
    Write-Host '  cleared session state (.pyddock\tmp\run\game.json)' -ForegroundColor DarkGray
}
else {
    Write-Host 'No tracked session.' -ForegroundColor DarkGray
}

if ($All) {
    Write-Host 'Sweeping untracked processes...' -ForegroundColor Cyan
    $strays = @()
    $strays += Get-Process netcoredbg -ErrorAction SilentlyContinue
    $strays += Get-Process OpenRA -ErrorAction SilentlyContinue
    # dotnet processes whose command line is our game dll
    $strays += Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like '*OpenRA.dll*' } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }

    $strays = $strays | Where-Object { $_ } | Sort-Object Id -Unique
    if (-not $strays) {
        Write-Host '  nothing stray found' -ForegroundColor DarkGray
    }
    foreach ($s in $strays) {
        try {
            Stop-Process -Id $s.Id -Force -ErrorAction Stop
            Write-Host "  killed $($s.Name) (PID $($s.Id))" -ForegroundColor Green
            $killed += $s.Name
        }
        catch {
            Write-Host "  failed to kill $($s.Name) (PID $($s.Id))" -ForegroundColor Red
        }
    }
    Clear-DebugState
}

Write-Host ''
if ($killed.Count -gt 0) {
    Write-Host "Stopped $($killed.Count) process(es)." -ForegroundColor Green
}
else {
    Write-Host 'Nothing was running.' -ForegroundColor DarkGray
    if (-not $All) {
        Write-Host 'If you suspect a stray instance, try: .kiro\scripts\build\kill-game.ps1 -All' -ForegroundColor DarkGray
    }
}
exit 0
