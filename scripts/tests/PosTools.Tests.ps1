BeforeAll {
    Import-Module (Join-Path (Split-Path $PSScriptRoot -Parent) 'PosTools.psm1') -Force
    function Get-TestCsvPath([string]$Body) {
        $p = Join-Path $TestDrive ([guid]::NewGuid().ToString('N') + '.csv')
        Set-Content -LiteralPath $p -Value $Body -Encoding UTF8
        $p
    }
}

Describe 'Read-StockCsv' {
    It 'reads valid rows and normalises SKUs to upper case' {
        $rows = Read-StockCsv -Path (Get-TestCsvPath "Sku,Quantity`nsku-0001,5`nSKU-0002, 7 ")
        $rows.Count | Should -Be 2
        $rows[0].Sku | Should -Be 'SKU-0001'
        $rows[1].Quantity | Should -Be 7
    }

    It 'lists every bad row in one error' {
        $path = Get-TestCsvPath "Sku,Quantity`nSKU-1,abc`nBAD SKU,1`nSKU-2,-4`nSKU-3,2`nSKU-3,9"
        $err = { Read-StockCsv -Path $path } | Should -Throw -PassThru
        $err.Exception.Message | Should -Match "line 2: quantity 'abc'"
        $err.Exception.Message | Should -Match "line 3: invalid SKU 'BAD SKU'"
        $err.Exception.Message | Should -Match 'line 4: quantity'
        $err.Exception.Message | Should -Match 'line 6: duplicate SKU SKU-3 \(first on line 5\)'
    }

    It 'rejects SQL metacharacters in a SKU' {
        { Read-StockCsv -Path (Get-TestCsvPath "Sku,Quantity`n`"X'');DROP TABLE dbo.Stock;--`",1") } | Should -Throw '*invalid SKU*'
    }

    It 'fails on a missing file and on an empty file' {
        { Read-StockCsv -Path (Join-Path $TestDrive 'nope.csv') } | Should -Throw '*File not found*'
        { Read-StockCsv -Path (Get-TestCsvPath "Sku,Quantity") } | Should -Throw '*No rows*'
    }

    It 'fails when a column is missing' {
        { Read-StockCsv -Path (Get-TestCsvPath "Sku`nSKU-1") } | Should -Throw '*quantity*'
    }
}

Describe 'Import-PosStock' {
    BeforeEach { Mock Invoke-PosSql { } -ModuleName PosTools }

    It 'sends one transactional MERGE with every row' {
        $r = Import-PosStock -Path (Get-TestCsvPath "Sku,Quantity`nA-1,3`nB-2,4") -ConnectionString 'x'
        $r.Imported | Should -Be 2
        Should -Invoke Invoke-PosSql -ModuleName PosTools -Exactly 1 -ParameterFilter {
            $Query -match "MERGE dbo.Stock" -and $Query -match "\('A-1',3\),\('B-2',4\)" -and $Query -match 'BEGIN TRANSACTION'
        }
    }

    It 'does not touch the database with -WhatIf' {
        Import-PosStock -Path (Get-TestCsvPath "Sku,Quantity`nA-1,3") -ConnectionString 'x' -WhatIf | Out-Null
        Should -Invoke Invoke-PosSql -ModuleName PosTools -Exactly 0
    }

    It 'does not touch the database when the file is invalid' {
        { Import-PosStock -Path (Get-TestCsvPath "Sku,Quantity`nA-1,oops") -ConnectionString 'x' } | Should -Throw
        Should -Invoke Invoke-PosSql -ModuleName PosTools -Exactly 0
    }
}

Describe 'Export-PosStock' {
    It 'writes the rows it reads' {
        Mock Invoke-PosSql { @([pscustomobject]@{ Sku = 'A-1'; Opening = 10; OnHand = 7 }) } -ModuleName PosTools
        $out = Join-Path $TestDrive 'out.csv'
        (Export-PosStock -Path $out -ConnectionString 'x').Exported | Should -Be 1
        $back = Import-Csv $out
        $back[0].Sku | Should -Be 'A-1'
        $back[0].OnHand | Should -Be '7'
    }
}

Describe 'Test-PosHealth' {
    It 'is healthy when the API answers, the database answers and the sync is recent' {
        Mock Invoke-RestMethod { [pscustomobject]@{ status = 'ok' } } -ModuleName PosTools
        Mock Invoke-PosSql { [pscustomobject]@{ Skus = 10; Cursor_ = 40; LastProcessed = [datetime]::UtcNow.AddMinutes(-2); OutOfStock = 0 } } -ModuleName PosTools
        $r = Test-PosHealth -PosApiUrl 'http://pos' -ConnectionString 'x'
        $r.Healthy | Should -BeTrue
        $r.Checks.Count | Should -Be 3
    }

    It 'fails when the sync is behind' {
        Mock Invoke-RestMethod { [pscustomobject]@{ status = 'ok' } } -ModuleName PosTools
        Mock Invoke-PosSql { [pscustomobject]@{ Skus = 10; Cursor_ = 40; LastProcessed = [datetime]::UtcNow.AddMinutes(-90); OutOfStock = 0 } } -ModuleName PosTools
        $r = Test-PosHealth -PosApiUrl 'http://pos' -ConnectionString 'x' -MaxLagMinutes 15
        $r.Healthy | Should -BeFalse
        ($r.Checks | Where-Object Name -eq 'sync-lag').Ok | Should -BeFalse
    }

    It 'reports the API and the database separately when both are down' {
        Mock Invoke-RestMethod { throw 'connection refused' } -ModuleName PosTools
        Mock Invoke-PosSql { throw 'login failed' } -ModuleName PosTools
        $r = Test-PosHealth -PosApiUrl 'http://pos' -ConnectionString 'x'
        $r.Healthy | Should -BeFalse
        ($r.Checks | Where-Object Name -eq 'pos-api').Detail | Should -Match 'connection refused'
        ($r.Checks | Where-Object Name -eq 'database').Detail | Should -Match 'login failed'
    }

    It 'fails when nothing has been processed yet' {
        Mock Invoke-RestMethod { [pscustomobject]@{ status = 'ok' } } -ModuleName PosTools
        Mock Invoke-PosSql { [pscustomobject]@{ Skus = 10; Cursor_ = 0; LastProcessed = [DBNull]::Value; OutOfStock = 0 } } -ModuleName PosTools
        (Test-PosHealth -PosApiUrl 'http://pos' -ConnectionString 'x').Healthy | Should -BeFalse
    }
}
