# 3. Dapper and hand-written SQL instead of an ORM

Status: accepted

**Context.** The interesting part of this project is the SQL: an insert-first idempotency guard, `UPDLOCK` reads, a `MERGE` upsert and an invariant query. An ORM would hide exactly those.

**Decision.** Microsoft.Data.SqlClient with [Dapper](https://github.com/DapperLib/Dapper) for parameter mapping. The schema is one idempotent `schema.sql` embedded in the assembly and applied at start (statements check before they create, `GO` separates batches).

**Consequences.** Every statement is visible and reviewable, and every one is covered by an integration test against a real SQL Server. There is no migration history: this is fine for a reference, but a long-lived system would add a migration tool (see the roadmap issue).
