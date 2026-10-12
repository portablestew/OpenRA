<#
.SYNOPSIS
    Validate a mod's MiniYAML by running OpenRA.Utility --check-yaml, the same
    check the Makefile / make.ps1 "test" target runs.

.DESCRIPTION
    Sets ENGINE_DIR so the utility (which lives in bin/) can find the mods/
    folder at the repo root, then runs `OpenRA.Utility <mod> --check-yaml` for
    each requested mod. Exits non-zero if any mod fails, so it can gate CI-style
    checks.

    The game must be built first (bin/OpenRA.Utility.dll present).

.PARAMETER Mods
    One or more mod ids to check. Defaults to the three built-in RTS mods whose
    trait code we commonly touch: ra, cnc, d2k.

.EXAMPLE
    .kiro\scripts\build\check-yaml.ps1
.EXAMPLE
    .kiro\scripts\build\check-yaml.ps1 ra
.EXAMPLE
    .kiro\scripts\build\check-yaml.ps1 ra cnc d2k
#>

[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Mods = @('ra', 'cnc', 'd2k')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This file lives in <repo>/.kiro/scripts/build, so the repo root is three up.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$utility = Join-Path $repoRoot 'bin\OpenRA.Utility.exe'

if (-not (Test-Path $utility)) {
    Write-Host "OpenRA.Utility not found at $utility - build the solution first." -ForegroundColor Red
    exit 1
}

# The utility resolves mods relative to Platform.EngineDir, which reads ENGINE_DIR.
# It runs from bin/, so the engine dir is one level up (the repo root).
$env:ENGINE_DIR = '..'

$failed = @()
foreach ($mod in $Mods) {
    Write-Host "`n== Checking $mod MiniYAML ==" -ForegroundColor Cyan
    & $utility $mod --check-yaml
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  $mod --check-yaml FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        $failed += $mod
    }
    else {
        Write-Host "  $mod OK" -ForegroundColor Green
    }
}

if ($failed.Count -gt 0) {
    Write-Host "`ncheck-yaml FAILED for: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "`nAll mods passed --check-yaml." -ForegroundColor Green
exit 0
