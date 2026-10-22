# 5. PowerShell tooling around one mockable SQL function

Status: accepted

**Context.** Operators want to load and export stock and check health without opening the .NET solution. The tools must be testable in CI, where a PowerShell test that needs a database is slow and fragile.

**Decision.** The logic lives in the `PosTools` module. The only function that touches SQL is `Invoke-PosSql`, a thin wrapper over `Invoke-Sqlcmd` from the SqlServer module; [Pester](https://pester.dev) mocks it. CSV validation is strict and runs before any SQL is built, so the generated `MERGE` only ever contains SKUs matching `[A-Z0-9-]` and integers. The `scripts/*.ps1` files are thin entry points.

**Consequences.** Validation, SQL generation, `-WhatIf` and the health logic are unit tested on every push. The real SQL path of the `MERGE` is the same statement as `SqlStockStore.UpsertOpeningStockAsync`, which has its own integration test; the PowerShell wrapper itself is not run against a live database in CI.
