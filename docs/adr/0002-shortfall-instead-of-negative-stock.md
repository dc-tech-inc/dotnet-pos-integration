# 2. Stock never goes negative; the difference is recorded as shortfall

Status: accepted

**Context.** The POS already sold the goods, so rejecting the sale would make the two systems disagree. But a negative on-hand quantity is meaningless and hides the real problem (stock was wrong before the sale).

**Decision.** Each line books `min(OnHand, quantity)`. The rest, and any SKU that is not in `dbo.Stock`, is stored as `Shortfall` on the movement and returned to the caller, which logs it. `CHECK (OnHand >= 0)` backs this up in the database. Stock rows are read with `UPDLOCK` and lines are processed in SKU order, so concurrent sales serialise per SKU and cannot deadlock on each other.

**Consequences.** Reconciliation compares what the POS sold with what was *requested* (`Quantity`), so oversold units do not look like missing sales. Shortfall is visible per movement for someone to investigate. There is no back-order or automatic correction.
