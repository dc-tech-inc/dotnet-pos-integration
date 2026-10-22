#Requires -Version 5.1
<#
.SYNOPSIS
Exports current stock (Sku, Opening, OnHand) to a CSV file.
.EXAMPLE
./scripts/Export-Stock.ps1 -Path ./stock-today.csv -ConnectionString $env:POS_SQL_CONNECTION
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$ConnectionString = $env:POS_SQL_CONNECTION
)
if (-not $ConnectionString) { throw 'Pass -ConnectionString or set POS_SQL_CONNECTION.' }
Import-Module (Join-Path $PSScriptRoot 'PosTools.psm1') -Force
Export-PosStock -Path $Path -ConnectionString $ConnectionString
