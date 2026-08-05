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

- [x] Docker Compose for the API and SQL Server. Verified end to end on a clean volume: all seven
      migrations apply, the seed creates `AUTHCENTER`, both roles and the admin user, and the auth
      smoke test (`login` → `me` → `users` → `refresh-token`, including refresh reuse rejection)
      passes against the running stack.

## Test coverage gaps

Login, refresh rotation, permissions, registration, invitations, TOTP MFA with trusted devices,
email OTP, magic links, forced password change, the full OAuth/OIDC surface and key rotation are
covered. Still untested:

- [ ] Session endpoints (`/api/auth/sessions`) and trusted-device listing/removal.
- [ ] External provider listing and unlinking.
- [ ] Email change and account deletion.
- [ ] GitHub login; Microsoft and Apple only have negative cases.
- [ ] Roles and permissions controllers.
