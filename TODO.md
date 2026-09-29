# TODO

Operational and deployment work. Code-level performance, security and scalability findings live in
[TODO-improvements.md](TODO-improvements.md). The open items below need the owner's accounts or
approval; each has a step-by-step procedure in
[docs/operations/OWNER-ACTIONS.md](docs/operations/OWNER-ACTIONS.md) (the `OPS-*` codes are those of
`REMEDIACION-INTEGRACION-FEDERACION.md`).

## Deployment

- [x] Recreate the Azure Web App and deploy through GitHub Actions with OIDC. The workflow uses
      repository variables for the app and slot names and no longer stores a publish profile.
- [x] Store `ConnectionStrings:DefaultConnection` in Azure Key Vault and expose it to App Service
      through a resolved versionless reference. Azure SQL authentication is passwordless through
      the App Service managed identity and a least-privilege contained database principal.
- [x] Consolidate CI and deployment so that only a successful validation run on `main` publishes
      the artifact, deploys it with OIDC, and verifies `/health/live`, `/health/ready` and branding.
- [x] Restore GitHub Actions execution (OPS-01): the pull request of the remediation passed the
      whole `CI/CD` workflow on 2026-09-26.
- [ ] Protect `main` (OPS-02).
- [x] Configure the remaining required production settings: new RSA/HMAC/MFA secrets, encrypted
      Data Protection with a generated PKCS#12 certificate, exact host/issuer/origins, and SQL-backed
      distributed rate limiting. All sensitive values are versionless Key Vault references.
- [x] Apply the 12 EF Core migrations that existed then to the new Azure SQL database out of band
      with the Microsoft Entra administrator; the application identity retains no DDL permissions.
- [x] Apply the migrations added since with the idempotent script of the `database-migrations` CI
      artifact before deploying (OPS-03): the 35 migrations were applied on 2026-09-26 and the
      remediation deployed with every post-deployment check green. `/health/ready` fails its
      `database-schema` check while any is missing.
- [ ] Seed the deployed database once with `Database:SeedOnStartup=true` plus intentionally chosen
      production `Seed:*` values, then remove those values and switch seeding back off (OPS-04).
- [x] Narrow `AllowedHosts` to the public hostnames: `authcenter.info` and the App Service host
      (OPS-15). The deployment verifies discovery through the issuer's URL.
- [ ] Add the real frontend hostname to `Cors:AllowedOrigins` and `ActionLinks` when it exists. The
      current configuration intentionally permits only the AuthCenter origin (OPS-07).
- [ ] Register Paquetenvia: its application, the confidential client `paquetenvia-web-prod` and the
      client secret in its Key Vault, with `scripts/ops/Register-Paquetenvia.ps1` (OPS-16).
- [ ] Configure outbound email (`Email:*` SMTP settings, sender domain with SPF, DKIM and DMARC).
      Until then confirmations, magic links and password resets are never delivered (OPS-17).

## Security

- [x] Retire the RSA signing key previously committed in `appsettings.Development.json`. It is not
      present in the current tree, does not match the ignored local key and is not configured on
      the Azure App Service. It must never be used again.
- [x] Generate RSA, HMAC, MFA and password fixtures at test runtime instead of committing them.
- [x] Scan every pull request and push to `main` with a checksum-verified Gitleaks binary. Native
      GitHub Secret Scanning is unavailable for the current private-repository plan.
- [ ] Purge the retired RSA key from historical commits. This requires a coordinated history
      rewrite and force-push; rotation and non-use are the security boundary until that operation
      is explicitly authorized (OPS-06).
- [ ] Perform the first production key rotation after the runtime secrets are configured, following
      the procedure in the README. `Jwt:AdditionalValidationKeysPem` keeps retired public keys
      valid and published while the new active private key signs (OPS-05).
- [x] Weekly Dependabot updates for NuGet, npm, Actions and Docker, grouped by ecosystem.

## Operational setup

- [x] Docker Compose for the API and SQL Server. Verified end to end on a clean volume when the
      schema had seven migrations: they applied, the seed created `AUTHCENTER`, both roles and the
      admin user, and the auth smoke test (`login` -> `me` -> `users` -> `refresh-token`, including
      refresh reuse rejection) passed against the running stack.
- [x] Capacity test of the administrative reads with a 100,000-user directory
      (`DirectoryScaleRelationalTests`, weekly `Directory scale` workflow, results in
      [docs/operations/CAPACITY.md](docs/operations/CAPACITY.md#directorio-grande)).

## Test coverage

CI enforces minimums: 80% of lines and 60% of branches for .NET (unit and integration merged), and
per-metric minimums for the console's logic modules (`src/AuthCenter.Admin/vite.config.ts`).

Every controller now has integration coverage: login and refresh rotation, permissions,
registration, invitations, TOTP MFA with trusted devices, email OTP, magic links, forced password
change, the full OAuth/OIDC surface, key rotation, account self-service (sessions, trusted
devices, linked providers, email change, deletion), roles and permissions, and GitHub login
against a stubbed provider API.

Provider validation no longer depends on live third parties in tests:

- [x] Microsoft single-tenant, Microsoft multi-tenant and Apple have positive RS256 cases against
      controlled OpenID configuration/JWKS data; Apple also retains an unverified-email regression.
