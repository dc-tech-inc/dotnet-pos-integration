#Requires -Version 5.1
<#
.SYNOPSIS
Health check for monitoring: prints each check and exits 0 when all pass, 1 otherwise.
.EXAMPLE
./scripts/Test-Health.ps1 -PosApiUrl http://localhost:5080 -ConnectionString $env:POS_SQL_CONNECTION
#>
[CmdletBinding()]
param(
    [string]$PosApiUrl = 'http://localhost:5080',
    [string]$ConnectionString = $env:POS_SQL_CONNECTION,
    [int]$MaxLagMinutes = 15
)
if (-not $ConnectionString) { throw 'Pass -ConnectionString or set POS_SQL_CONNECTION.' }
Import-Module (Join-Path $PSScriptRoot 'PosTools.psm1') -Force
$result = Test-PosHealth -PosApiUrl $PosApiUrl -ConnectionString $ConnectionString -MaxLagMinutes $MaxLagMinutes
foreach ($c in $result.Checks) { '{0,-9} {1,-9} {2}' -f $(if ($c.Ok) { 'OK' } else { 'FAIL' }), $c.Name, $c.Detail }
exit $(if ($result.Healthy) { 0 } else { 1 })
