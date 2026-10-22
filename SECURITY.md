# Security policy

This repository is a reference implementation with synthetic data. It is not deployed anywhere.

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private vulnerability reporting: **Security → Report a vulnerability** on this repository. If that is unavailable, contact DCtech. through https://dctech.live.

Include what you found, how to reproduce it, and the affected commit. You can expect an acknowledgement within a few business days.

## Scope notes

- The SQL Server `sa` password in `.env.example` and in the CI workflow is a throwaway value for a local or ephemeral container. Never reuse it, and never point this code at a database that holds real data.
- The fictional POS API has no authentication and the worker connects to SQL Server as `sa` with `TrustServerCertificate=True` in the demo. Both are documented limitations of the demo, not vulnerabilities; a real deployment would use an authenticated POS API, a least-privilege SQL login and a trusted certificate.
- SQL built by the PowerShell import contains only values that passed strict validation (`[A-Z0-9-]` SKUs, integer quantities). A bypass of that validation would be a valid report.
- The repository is scanned with gitleaks on every push and pull request; Dependabot keeps NuGet, Docker and GitHub Actions dependencies current.
