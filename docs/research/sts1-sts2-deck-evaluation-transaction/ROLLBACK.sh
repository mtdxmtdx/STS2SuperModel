#!/usr/bin/env pwsh
param([string]$Target = ".")
$ErrorActionPreference = "Stop"
$baseline = Join-Path $Target "BASELINE_FILE"
$modified = Join-Path $Target "MODIFIED_FILE"
if (-not (Test-Path -LiteralPath $baseline)) {
    Write-Error "missing baseline: $baseline"
    exit 2
}
Copy-Item -LiteralPath $baseline -Destination $modified -Force
Write-Output "restored $modified"
