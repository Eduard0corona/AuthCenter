# TODO

Operational and deployment work. Code-level performance, security and scalability findings live in
[TODO-improvements.md](TODO-improvements.md).

## Deployment

- [x] Recreate the Azure Web App and deploy through GitHub Actions with OIDC. The workflow uses
      repository variables for the app and slot names and no longer stores a publish profile.
- [x] Store `ConnectionStrings:DefaultConnection` in Azure Key Vault and expose it to App Service
      through a resolved versionless reference. Azure SQL authentication is passwordless through
      the App Service managed identity and a least-privilege contained database principal.
- [x] Consolidate CI and deployment so that only a successful validation run on `main` publishes
      the artifact, deploys it with OIDC, and verifies `/health/live`.
- [ ] Configure the remaining deployed app settings. The API fails fast without them:
  - `Jwt:RsaPrivateKeyPem`
  - `Jwt:SigningKey` (min 64 chars)
  - `Mfa:EncryptionKey` (min 32 chars)
  - `Cors:AllowedOrigins`
  - `AllowedHosts`, `Jwt:Issuer`, `Oidc:PublicOrigin`, and `ActionLinks:DefaultBaseUrl`
  - Data Protection certificate/settings and the distributed rate-limiting backend
- [x] Apply all 12 EF Core migrations to the new Azure SQL database out of band with the Microsoft
      Entra administrator; the application identity retains no DDL permissions.
- [ ] Seed the deployed database once with `Database:SeedOnStartup=true` plus intentionally chosen
      production `Seed:*` values, then remove those values and switch seeding back off.
- [ ] Narrow `AllowedHosts` from `*` to the real public hostnames once they exist.

## Security

- [x] Retire the RSA signing key previously committed in `appsettings.Development.json`. It is not
      present in the current tree, does not match the ignored local key and is not configured on
      the Azure App Service. It must never be used again.
- [x] Generate RSA, HMAC, MFA and password fixtures at test runtime instead of committing them.
- [x] Scan every pull request and push to `main` with a checksum-verified Gitleaks binary. Native
      GitHub Secret Scanning is unavailable for the current private-repository plan.
- [ ] Purge the retired RSA key from historical commits. This requires a coordinated history
      rewrite and force-push; rotation and non-use are the security boundary until that operation
      is explicitly authorized.
- [ ] Perform the first production key rotation after the runtime secrets are configured, following
      the procedure in the README. `Jwt:AdditionalValidationKeysPem` keeps retired public keys
      valid and published while the new active private key signs.

## Operational setup

- [x] Docker Compose for the API and SQL Server. Verified end to end on a clean volume: all seven
      migrations apply, the seed creates `AUTHCENTER`, both roles and the admin user, and the auth
      smoke test (`login` -> `me` -> `users` -> `refresh-token`, including refresh reuse rejection)
      passes against the running stack.

## Test coverage

Every controller now has integration coverage: login and refresh rotation, permissions,
registration, invitations, TOTP MFA with trusted devices, email OTP, magic links, forced password
change, the full OAuth/OIDC surface, key rotation, account self-service (sessions, trusted
devices, linked providers, email change, deletion), roles and permissions, and GitHub login
against a stubbed provider API.

Remaining gaps are the ones that cannot be reached without a real provider:

- [ ] Microsoft and Apple login only have negative cases. Both validate a signed ID token, so a
      positive test needs either a stubbed JWKS endpoint or a fake token signed by a key the
      service is configured to trust.
