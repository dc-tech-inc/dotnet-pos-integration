# 1. Idempotency lives in the database, in the same transaction as the stock change

Status: accepted

**Context.** The POS delivers at least once: a page can be retried, the cursor can be lost, two workers can race. Booking the same sale twice would take stock out twice.

**Decision.** `dbo.ProcessedSale` has the sale id as its primary key. `ApplySaleAsync` inserts that row first, inside the transaction that also updates stock and writes the movements. A second delivery fails the insert with error 2627, the transaction is rolled back and the call returns `Duplicate`. There is no "have I seen this?" read in application code, so there is no check-then-act race.

**Consequences.** Safe under concurrent duplicates: a test sends one sale ten times in parallel and expects exactly one `Applied`. The cursor becomes an optimisation (skip what was already read), not a correctness mechanism, so it may be written after the page without a distributed transaction.
