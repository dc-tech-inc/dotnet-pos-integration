# 4. Pull sales with a cursor; retry transient failures with backoff

Status: accepted

**Context.** The fictional POS exposes an HTTP API with a monotonic sequence per sale. Real POS APIs are slow, rate limited and occasionally return 5xx.

**Decision.** The worker reads pages after a stored cursor and moves the cursor after each page. Transient errors (HTTP 5xx, 408, 429, no response; SQL deadlock, timeout and connection-level errors) are retried up to five times with exponential backoff and jitter, via a small `RetryPolicy` (no library: the behaviour fits in forty lines and the delay function is injectable, so tests do not sleep). Permanent errors (HTTP 4xx, bad data) fail fast. Invalid sales are rejected and logged rather than retried.

**Consequences.** After a crash the worker re-reads at most one page, which idempotency (ADR 1) makes harmless. A POS that is down for longer than the retry window makes that run fail loudly and the next poll tries again. There is no dead-letter table for rejected sales; reconciliation is what exposes them.
