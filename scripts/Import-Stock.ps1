#Requires -Version 5.1
<#
.SYNOPSIS
Loads opening stock from a CSV (columns: Sku,Quantity). Use -WhatIf to validate and preview without touching the database.
.EXAMPLE
./scripts/Import-Stock.ps1 -Path ./scripts/sample-stock.csv -ConnectionString $env:POS_SQL_CONNECTION -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$ConnectionString = $env:POS_SQL_CONNECTION
)
if (-not $ConnectionString) { throw 'Pass -ConnectionString or set POS_SQL_CONNECTION.' }
Import-Module (Join-Path $PSScriptRoot 'PosTools.psm1') -Force
Import-PosStock -Path $Path -ConnectionString $ConnectionString -WhatIf:$WhatIfPreference
