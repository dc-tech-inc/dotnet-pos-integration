#Requires -Version 5.1
Set-StrictMode -Version Latest

# Helpers for operating the stock database next to the .NET worker: CSV import/export and a health check.
# SQL goes through Invoke-PosSql so the tests can replace it. Real use needs the SqlServer module (Invoke-Sqlcmd).

$script:SkuPattern = '^[A-Z0-9][A-Z0-9-]{0,31}$'

function Invoke-PosSql {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][string]$Query
    )
    if (-not (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)) {
        throw 'Invoke-Sqlcmd not found. Install the SqlServer module: Install-Module SqlServer -Scope CurrentUser'
    }
    Invoke-Sqlcmd -ConnectionString $ConnectionString -Query $Query -ErrorAction Stop
}

function Read-StockCsv {
    <#
    .SYNOPSIS
    Reads and validates a stock CSV with the columns Sku,Quantity. Throws one error that lists every bad row.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "File not found: $Path" }
    $rows = @(Import-Csv -LiteralPath $Path)
    if ($rows.Count -eq 0) { throw "No rows in $Path" }

    $problems = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    $items = New-Object System.Collections.Generic.List[object]
    $line = 1
    foreach ($row in $rows) {
        $line++
        $sku = if ($row.PSObject.Properties['Sku']) { "$($row.Sku)".Trim().ToUpperInvariant() } else { '' }
        $qtyText = if ($row.PSObject.Properties['Quantity']) { "$($row.Quantity)".Trim() } else { '' }
        $qty = 0
        if ($sku -notmatch $script:SkuPattern) { $problems.Add("line ${line}: invalid SKU '$sku'"); continue }
        if (-not [int]::TryParse($qtyText, [ref]$qty) -or $qty -lt 0) { $problems.Add("line ${line}: quantity '$qtyText' is not a whole number >= 0"); continue }
        if ($seen.ContainsKey($sku)) { $problems.Add("line ${line}: duplicate SKU $sku (first on line $($seen[$sku]))"); continue }
        $seen[$sku] = $line
        $items.Add([pscustomobject]@{ Sku = $sku; Quantity = $qty })
    }
    if ($problems.Count -gt 0) { throw ("Invalid stock file {0}:`n  {1}" -f $Path, ($problems -join "`n  ")) }
    $items.ToArray()
}

function Import-PosStock {
    <#
    .SYNOPSIS
    Loads opening stock from a CSV. Existing SKUs keep the sales already booked (OnHand moves by the same delta as Opening).
    .NOTES
    Values reach the query only after Read-StockCsv has restricted SKUs to [A-Z0-9-] and quantities to integers.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ConnectionString
    )
    $items = @(Read-StockCsv -Path $Path)
    $values = ($items | ForEach-Object { "('{0}',{1})" -f $_.Sku, $_.Quantity }) -join ','
    $query = @"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
MERGE dbo.Stock WITH (HOLDLOCK) AS t USING (VALUES $values) AS s (Sku, Quantity) ON t.Sku = s.Sku
WHEN MATCHED THEN UPDATE SET t.OnHand = t.OnHand + (s.Quantity - t.Opening), t.Opening = s.Quantity
WHEN NOT MATCHED THEN INSERT (Sku, Opening, OnHand) VALUES (s.Sku, s.Quantity, s.Quantity);
COMMIT;
"@
    if ($PSCmdlet.ShouldProcess("dbo.Stock", "upsert $($items.Count) SKU(s) from $Path")) {
        Invoke-PosSql -ConnectionString $ConnectionString -Query $query | Out-Null
    }
    [pscustomobject]@{ Imported = $items.Count; Path = $Path }
}

function Export-PosStock {
    <#
    .SYNOPSIS
    Writes current stock (Sku, Opening, OnHand) to a CSV and returns the row count.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ConnectionString
    )
    $rows = @(Invoke-PosSql -ConnectionString $ConnectionString -Query 'SELECT Sku, Opening, OnHand FROM dbo.Stock ORDER BY Sku')
    $rows | Select-Object Sku, Opening, OnHand | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8
    [pscustomobject]@{ Exported = $rows.Count; Path = $Path }
}

function Test-PosHealth {
    <#
    .SYNOPSIS
    Checks the POS API, the database, and how far behind the sync is. Returns one object; Healthy is false if any check failed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PosApiUrl,
        [Parameter(Mandatory)][string]$ConnectionString,
        [int]$MaxLagMinutes = 15
    )
    $checks = New-Object System.Collections.Generic.List[object]
    $add = { param($name, $ok, $detail) $checks.Add([pscustomobject]@{ Name = $name; Ok = [bool]$ok; Detail = $detail }) }

    try {
        $pos = Invoke-RestMethod -Uri ($PosApiUrl.TrimEnd('/') + '/health') -TimeoutSec 5
        & $add 'pos-api' ($pos.status -eq 'ok') "status=$($pos.status)"
    } catch { & $add 'pos-api' $false $_.Exception.Message }

    try {
        $r = @(Invoke-PosSql -ConnectionString $ConnectionString -Query @'
SELECT (SELECT COUNT(*) FROM dbo.Stock) AS Skus,
       (SELECT LastSeq FROM dbo.SyncCursor WHERE Id = 1) AS Cursor_,
       (SELECT MAX(ProcessedAt) FROM dbo.ProcessedSale) AS LastProcessed,
       (SELECT COUNT(*) FROM dbo.Stock WHERE OnHand = 0) AS OutOfStock
'@)[0]
        & $add 'database' $true "skus=$($r.Skus), cursor=$($r.Cursor_), out-of-stock=$($r.OutOfStock)"

        $last = $r.LastProcessed
        if ($last -is [DBNull] -or $null -eq $last) {
            & $add 'sync-lag' $false 'nothing has been processed yet'
        } else {
            $lag = ([datetime]::UtcNow - [datetime]$last).TotalMinutes
            & $add 'sync-lag' ($lag -le $MaxLagMinutes) ("last sale booked {0:N0} min ago (limit {1})" -f $lag, $MaxLagMinutes)
        }
    } catch { & $add 'database' $false $_.Exception.Message }

    [pscustomobject]@{ Healthy = -not ($checks | Where-Object { -not $_.Ok }); Checks = $checks.ToArray() }
}

Export-ModuleMember -Function Read-StockCsv, Import-PosStock, Export-PosStock, Test-PosHealth, Invoke-PosSql
