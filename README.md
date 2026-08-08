# AuthCenter

Centralized authentication and identity service for multiple systems. Handles user registration, login (email/password and Google), JWT access tokens, refresh token rotation, roles, permissions, and multi-application access control.

## Architecture

Clean Architecture with five layers:

| Layer | Responsibility |
|-------|---------------|
| `AuthCenter.Domain` | Entities, enums, constants |
| `AuthCenter.Contracts` | Request/response DTOs |
| `AuthCenter.Application` | Interfaces, validators, use-case results |
| `AuthCenter.Infrastructure` | EF Core, Identity, JWT, Google auth, services |
| `AuthCenter.Api` | Controllers, middleware, composition root |

See [docs/architecture.md](docs/architecture.md) for full details.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10)
- SQL Server (LocalDB, Express, or full)
- (Optional) Google OAuth 2.0 Client ID for Google login

## Quick Start

### 1. Initialize user secrets and configure local settings

```powershell
cd src/AuthCenter.Api

dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=(localdb)\\mssqllocaldb;Database=AuthCenter;Trusted_Connection=True;"
dotnet user-secrets set "Jwt:SigningKey" "your-very-long-secret-key-min-32-chars"
$rsaPrivateKeyPem = Get-Content "C:\path\authcenter-private-key.pem" -Raw
dotnet user-secrets set "Jwt:RsaPrivateKeyPem" "$rsaPrivateKeyPem"
dotnet user-secrets set "Seed:AdminEmail" "admin@example.com"
dotnet user-secrets set "Seed:AdminPassword" "Admin@12345"
dotnet user-secrets set "Seed:AdminFullName" "System Administrator"
```

Google login is optional. Configure it only if you plan to use Google Sign-In:

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "your-google-client-id.apps.googleusercontent.com"
```

### 2. Run migrations

```bash
dotnet ef database update --project src/AuthCenter.Infrastructure --startup-project src/AuthCenter.Api
```

Or let the app auto-migrate in Development (enabled by default).

### 3. Run the API

```bash
dotnet run --project src/AuthCenter.Api
```

Swagger UI: `https://localhost:7001/swagger`

### Running with Docker Compose

Brings up SQL Server and the API without installing either locally. The API waits for SQL Server
to accept logins, then migrates and seeds on first start.

```bash
cp .env.example .env
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048   # paste into JWT_RSA_PRIVATE_KEY_PEM
docker compose up --build
```

API at `http://localhost:8080`, Swagger at `http://localhost:8080/swagger`, health at
`http://localhost:8080/health`. The stack runs in the `Development` environment; `.env` is
git-ignored and its values are for local use only.

## Configuration Reference

| Key | Description |
|-----|-------------|
| `ConnectionStrings:DefaultConnection` | SQL Server connection string |
| `Jwt:Issuer` | JWT issuer claim |
| `Jwt:Audience` | JWT audience claim |
| `Jwt:SigningKey` | HMAC-SHA256 key for internal pending MFA, forced-password-change, and magic-link tokens (min 32 chars) |
| `Jwt:RsaPrivateKeyPem` | Active RSA private key (at least 2048 bits) in PEM format used to sign access and ID tokens with RS256 (required in every environment) |
| `Jwt:AdditionalValidationKeysPem` | Array of PEM keys still accepted on validation and published in the JWKS, but no longer used for signing. See [Rotating the signing key](#rotating-the-signing-key) |
| `Jwt:AccessTokenMinutes` | Access token lifetime (default 15) |
| `Jwt:RefreshTokenDays` | Refresh token lifetime (default 30) |
| `Mfa:EncryptionKey` | Key used to encrypt TOTP secrets at rest (min 32 chars, required outside Development) |
| `Authentication:Google:ClientId` | Google OAuth Client ID |
| `Cors:AllowedOrigins` | Array of allowed CORS origins |
| `AllowedHosts` | Host header allow-list. `*` by default; narrow it to your public hostnames when deploying |
| `Database:MigrateOnStartup` | Apply pending EF Core migrations at startup (default: on only in Development) |
| `Database:SeedOnStartup` | Seed the `AUTHCENTER` application, roles, permissions, and admin user at startup (default: on only in Development) |
| `Seed:AdminEmail` | Initial admin user email |
| `Seed:AdminPassword` | Initial admin user password |
| `Seed:AdminFullName` | Initial admin user full name |

The API fails fast in every environment when `Jwt:RsaPrivateKeyPem` is missing, invalid, or still uses a placeholder. Outside `Development` and `Testing`, it also fails when:

- `ConnectionStrings:DefaultConnection` is missing.
- `Jwt:SigningKey` is empty, shorter than 64 characters, or still uses the placeholder.
- `Mfa:EncryptionKey` is empty, shorter than 32 characters, or still uses the placeholder.
- `Authentication:Google:ClientId` still uses the placeholder value.
- `Cors:AllowedOrigins` is empty.

For a fully conformant OIDC discovery document, set `Jwt:Issuer` to the public HTTPS URL of the
service. The endpoint URLs published at `/.well-known/openid-configuration` are derived from the
request, but the `issuer` value must match the `iss` claim of the tokens, so changing it
invalidates tokens already in circulation.

### Rotating the signing key

Every key is identified in the JWKS by a `kid` derived from the key itself (an RFC 7638
thumbprint), so a rotation is a three-step move that never invalidates tokens already issued:

1. **Publish** the new key by adding it to `Jwt:AdditionalValidationKeysPem`. Relying parties that
   refresh the JWKS pick it up while the old key keeps signing.
2. **Promote** it: move the new key to `Jwt:RsaPrivateKeyPem` and put the old key's public PEM in
   `Jwt:AdditionalValidationKeysPem`. New tokens are signed with the new key; tokens signed with
   the old one are still accepted.
3. **Retire** the old key by removing it from `Jwt:AdditionalValidationKeysPem`, once the longest
   token lifetime you issue (`Jwt:AccessTokenMinutes`) has elapsed since step 2.

Only the public half of an additional key is ever exposed, so step 2 can use the public PEM alone.
Steps must be one deploy apart — collapsing them means clients holding a stale JWKS will reject
tokens signed with a key they never saw.

### Deploying

The database is not created or seeded automatically outside `Development`. To initialize a
deployed environment, run it once with `Database:MigrateOnStartup` and `Database:SeedOnStartup`
set to `true` along with the `Seed:*` values, then turn both back off so that later restarts do
not re-run the bootstrap. Alternatively, apply the migrations out of band with
`dotnet ef database update`.

## Key Endpoints

### Authentication

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/auth/register` | Register with email/password |
| POST | `/api/auth/login` | Login with email/password |
| POST | `/api/auth/google` | Login with Google ID Token |
| POST | `/api/auth/refresh-token` | Rotate refresh token |
| POST | `/api/auth/logout` | Logout (revoke refresh token) |
| POST | `/api/auth/revoke-token` | Revoke a specific refresh token |
| GET | `/api/auth/me` | Get current authenticated user |
| POST | `/api/auth/confirm-email` | Confirm email with token |
| POST | `/api/auth/resend-email-confirmation` | Resend email confirmation |
| POST | `/api/auth/forgot-password` | Send password reset token |
| POST | `/api/auth/reset-password` | Reset password or accept invitation |
| POST | `/api/auth/change-password` | Change the current user's password |
| POST | `/api/auth/forced-change-password` | Complete a forced password change |

### Social and passwordless login

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/auth/microsoft` | Login with Microsoft ID Token |
| POST | `/api/auth/github` | Login with GitHub OAuth code |
| POST | `/api/auth/apple` | Login with Apple ID Token |
| POST | `/api/auth/magic-link/request` | Send a magic login link by email |
| POST | `/api/auth/magic-link/verify` | Complete a magic-link login |
| GET | `/api/auth/external-providers` | List linked external providers |
| DELETE | `/api/auth/external-providers/{providerId}` | Unlink an external provider |

### Multi-factor authentication

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/auth/mfa/status` | Current MFA status |
| POST | `/api/auth/mfa/setup` | Start TOTP enrollment (returns QR payload) |
| POST | `/api/auth/mfa/enable` | Confirm and enable TOTP |
| DELETE | `/api/auth/mfa` | Disable MFA |
| POST | `/api/auth/mfa/verify` | Complete a login pending MFA |
| POST | `/api/auth/mfa/backup-codes` | Regenerate backup codes |
| POST | `/api/auth/mfa/email-otp/setup` | Start email OTP enrollment |
| POST | `/api/auth/mfa/email-otp/enable` | Confirm and enable email OTP |
| POST | `/api/auth/mfa/email-otp/send` | Send an email OTP for a pending login |

### Sessions, devices, and account

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/auth/sessions` | List active sessions |
| DELETE | `/api/auth/sessions/{tokenId}` | Revoke one session |
| DELETE | `/api/auth/sessions` | Revoke every other session |
| GET | `/api/auth/trusted-devices` | List trusted devices |
| DELETE | `/api/auth/trusted-devices/{deviceId}` | Remove one trusted device |
| DELETE | `/api/auth/trusted-devices` | Remove every trusted device |
| POST | `/api/auth/email-change/request` | Request an email change |
| POST | `/api/auth/email-change/confirm` | Confirm an email change |
| DELETE | `/api/auth/account` | Delete the current user's account |

### OAuth 2.0 / OpenID Connect provider

AuthCenter acts as an authorization server: authorization code with PKCE, client credentials, and
refresh token grants. Tokens are signed with RS256 and verifiable through the published JWKS.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/.well-known/openid-configuration` | Discovery document |
| GET | `/.well-known/jwks.json` | Public signing keys |
| GET | `/oauth/authorize` | Start an authorization request |
| POST | `/oauth/authorize/complete` | Grant consent and issue the code |
| POST | `/oauth/token` | Exchange code / refresh token / client credentials |
| GET | `/oauth/userinfo` | OIDC claims for the access token's subject |
| GET | `/api/oauth/clients` | List registered clients |
| POST | `/api/oauth/clients` | Register a client |
| PUT | `/api/oauth/clients/{clientId}` | Update a client |
| POST | `/api/oauth/clients/{clientId}/rotate-secret` | Rotate the client secret |
| DELETE | `/api/oauth/clients/{clientId}` | Deactivate a client |

`/oauth/userinfo` accepts only access tokens issued by `/oauth/token`; first-party login tokens are
rejected because they are not scoped to an OAuth client.

### Applications

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/applications` | List all applications |
| POST | `/api/applications` | Create application |
| PUT | `/api/applications/{id}` | Update application |
| PATCH | `/api/applications/{id}/activate` | Activate |
| PATCH | `/api/applications/{id}/deactivate` | Deactivate |

### Users

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/users` | List all users |
| POST | `/api/users` | Create user administratively |
| PUT | `/api/users/{id}` | Update user profile fields |
| POST | `/api/users/invitations` | Invite a user to an application |
| POST | `/api/users/{id}/applications/{appId}` | Grant application access |
| PATCH | `/api/users/{id}/applications/{appId}/approve` | Approve pending application access |
| DELETE | `/api/users/{id}/applications/{appId}` | Revoke application access |
| POST | `/api/users/{id}/roles/{roleId}` | Assign role |
| DELETE | `/api/users/{id}/roles/{roleId}` | Remove role |
| PATCH | `/api/users/{id}/activate` | Activate user |
| PATCH | `/api/users/{id}/deactivate` | Deactivate user |

### Other

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/roles` | List roles |
| GET | `/api/permissions` | List permissions |
| GET | `/api/audit-logs` | Search audit logs |
| GET | `/health` | Health check |

## Testing Login with Password

```bash
curl -X POST https://localhost:7001/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@example.com","password":"Admin@12345","applicationCode":"AUTHCENTER"}'
```

Use the returned `accessToken` as `Authorization: Bearer <token>` for subsequent requests.

## Testing Google Login

1. Obtain a Google ID Token from your frontend app (using Google Identity Services SDK).
2. Send it to AuthCenter:

```bash
curl -X POST https://localhost:7001/api/auth/google \
  -H "Content-Type: application/json" \
  -d '{"idToken":"<google_id_token>","applicationCode":"AUTHCENTER"}'
```

## Roles and Permissions

- **Roles** are assigned to users via `/api/users/{id}/roles/{roleId}`.
- **Permissions** are assigned to roles via `/api/roles/{roleId}/permissions/{permissionId}`.
- The JWT includes `roles`, `permissions`, and `applications` claims scoped to the application used for login.
- Endpoints are protected with `[Authorize(Policy = "PERMISSION_CODE")]`.
- Refresh tokens are bound to the application they were issued for.

### Default Seed Data

The `AUTHCENTER` application is seeded automatically with:
- **SuperAdmin** role (all permissions)
- **Admin** role (read + write users, read apps/roles/permissions)
- All 8 default permissions under `AUTHCENTER_*`

## Running Tests

```bash
dotnet test
```

## Repository Notes

The repository ignores generated `bin/` and `obj/` directories, logs, local appsettings files, and local assistant/tooling state. Keep runtime secrets in user-secrets locally and environment variables or a secret store in deployed environments.
