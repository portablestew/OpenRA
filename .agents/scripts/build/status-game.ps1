<#
.SYNOPSIS
    Report the state of the OpenRA session. Read-only, no side effects.

.DESCRIPTION
    Prints the tracked game process, whether a debugger currently holds the
    process (an openra_debug session in flight, or a stray netcoredbg), OpenRA's
    own logs, and any crash dumps. Shares its implementation (Show-GameStatus in
    _common.ps1) with `build-and-run.ps1 -Status`.

.EXAMPLE
    .kiro\scripts\build\status-game.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_common.ps1')

Show-GameStatus
exit 0
