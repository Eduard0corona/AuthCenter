# TODO

## Deployment (blocked on infrastructure)

- [ ] Recreate the Azure Web App. `authcentral.azurewebsites.net` and its SCM endpoint no longer
      resolve, so every run of the deploy workflow fails with `ENOTFOUND`. The publish profile in
      `AZUREAPPSERVICE_PUBLISHPROFILE_...` has to be regenerated for the new resource — or the
      workflow removed if the target is dropped.
- [ ] Configure the deployed app settings before the first successful deploy. The API now fails
      fast without them:
  - `ConnectionStrings:DefaultConnection`
  - `Jwt:RsaPrivateKeyPem` (new RSA key — see below)
  - `Jwt:SigningKey` (min 64 chars)
  - `Mfa:EncryptionKey` (min 32 chars)
  - `Cors:AllowedOrigins`
- [ ] Bootstrap the deployed database: run once with `Database:MigrateOnStartup` and
      `Database:SeedOnStartup` set to `true` plus the `Seed:*` values, then switch both off.
- [ ] Narrow `AllowedHosts` from `*` to the real public hostnames once they exist.

## Security

- [ ] Rotate the RSA signing key. The key previously committed in
      `appsettings.Development.json` is still readable in the git history and must not be used
      anywhere. Tests now use their own key (`tests/AuthCenter.IntegrationTests/TestRsaKey.cs`),
      which is public by design and equally must never be reused.
- [ ] Perform the first rotation once the deployment exists, following the procedure in the
      README. Key rotation itself is implemented: `Jwt:AdditionalValidationKeysPem` keeps retired
      keys valid and published while the active key signs.

## Operational setup

- [ ] Add Docker Compose for the API and SQL Server.
- [ ] Run a real auth smoke test against LocalDB:
  - `POST /api/auth/login`
  - `GET /api/auth/me`
  - `GET /api/users`
  - `POST /api/auth/refresh-token`

## Test coverage gaps

- [ ] MFA (TOTP enrollment, email OTP, backup codes, trusted devices).
- [ ] Magic-link login.
- [ ] Social login (Microsoft, GitHub, Apple).
- [ ] Sessions and account management (email change, account deletion).
- [ ] Roles, permissions, and audit log controllers.
