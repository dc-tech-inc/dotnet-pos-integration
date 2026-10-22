# Contributing

Small, focused changes are easiest to review.

1. Branch from `main`: `feat/<scope>` or `fix/<scope>`.
2. Use [conventional commits](https://www.conventionalcommits.org/) (`feat(core): …`, `fix(data): …`, `docs: …`, `test: …`).
3. Before pushing:
   ```sh
   dotnet format --verify-no-changes
   dotnet build -warnaserror
   dotnet test            # integration tests are skipped unless POS_TEST_SQL is set (see the README)
   ```
   and, for the scripts, `Invoke-Pester ./scripts/tests` (Pester 5). Run `gitleaks git .` too if you have it.
4. Open a pull request. CI must be green: format, build, tests against SQL Server (a skipped test fails the job), Pester and PSScriptAnalyzer, the `docker compose up` smoke test and gitleaks.
5. Merge with a **merge commit** (no squash) so the history stays as it happened.

If a change touches a rule (idempotency, shortfall, reconciliation), add a test that fails without it and, for a design change, a short ADR in `docs/adr`.

Keep example data synthetic. No real names, customers, credentials, POS vendor code or personal data.
