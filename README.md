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
The product-level capability plan is tracked in
[OKTA-LEVEL-ROADMAP.md](OKTA-LEVEL-ROADMAP.md); the current API is a hardened foundation, not yet
feature parity with a full Identity-as-a-Service platform.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10)
- Node.js 24 (required to build the administrative SPA)
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

### Administrative frontend

The React/TypeScript console is developed independently and published together with the API:

```powershell
cd src/AuthCenter.Admin
npm ci
npm run dev
```

The development server exposes `/admin-v2/` and proxies `/ui-api` to the local API. Before a
change is submitted, run `npm run lint`, `npm run typecheck`, `npm test`, `npm run test:e2e`, and
`npm run build`. A normal `dotnet publish` performs a reproducible `npm ci` and frontend build;
CI may set `SkipAdminFrontendBuild=true` only after producing the same assets in its quality gate.
The existing `/admin` console remains available during the progressive route migration.

The directory module supports `/admin-v2/users/new` for local identities and
`/admin-v2/users/invite` for email invitations. Local creation generates the temporary password in
browser memory, sends it only in the create request, and marks it for mandatory replacement at the
first sign-in. Invitations return user metadata only; the invitation token is delivered by the
configured email/outbox path and is never exposed in the administrative response.
The user directory keeps its selected server-side ordering in the URL for reproducible links.

The OAuth client module is available at `/admin-v2/oauth-clients`. It supports filtered,
server-paginated administration of exact redirect URIs, grants, scopes and client state. A
confidential client secret is revealed only after create or rotation, remains in browser memory,
and must be copied before explicitly closing the dialog. Rotation, deactivation and reactivation
require a short-lived, purpose-bound, single-use reauthentication proof; the API audits successful
and rejected operations without recording the credential.

Provisioning credentials are managed at `/admin-v2/provisioning-tokens`. The module lists only
server-paginated metadata, filters by application and lifecycle state, and supports creation with
explicit SCIM scopes and an expiration of at most one year. The raw token is held in browser memory
only for the create or rotate response and is discarded after the operator explicitly closes the
reveal dialog. Rotation and revocation require separate purpose-bound single-use reauthentication
proofs; historical token values cannot be retrieved.

Lifecycle automation is managed at `/admin-v2/profile-mappings` and `/admin-v2/group-rules`. Profile
mappings bind a dot-separated SCIM path (URN extensions included) to an active universal-profile
attribute, can be validated before they are created, and expose a simulation panel that resolves the
path against an operator-supplied SCIM document without persisting anything. Group rules evaluate an
equality condition on a profile attribute; the expected value is converted to the attribute's JSON
type before it is sent, and the editor shows a server-paginated preview of the active users that
currently match. Both editors send the loaded version on update and surface a `409
CONCURRENCY_CONFLICT` with a reload action instead of overwriting newer changes.

Access policies are managed at `/admin-v2/access-policies`. Each application exposes immutable
published/archived history and at most one editable draft. Operators can build ordered rules for
user or group targets, CIDR ranges, UTC schedules, risk and assurance requirements; simulate a
specific user/context with per-rule explanations; and review the effective diff against the
published version. Publishing requires a purpose-bound single-use reauthentication proof, keeps
the `AUTHCENTER` unconditional allow fallback invariant, and revokes active application sessions.

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
| `Passkeys:RelyingPartyId` | Exact WebAuthn RP host, without scheme or path |
| `Passkeys:AllowedOrigins` | Exact HTTPS origins allowed to complete WebAuthn ceremonies |
| `Passkeys:CeremonyMinutes` | Single-use ceremony lifetime, from 1 to 10 minutes |
| `Passkeys:ReauthenticationMinutes` | Single-use sensitive-operation proof lifetime, from 1 to 15 minutes |
| `Passkeys:MaxCredentialsPerUser` | Per-user resource limit, from 2 to 20 |
| `AdaptiveAuth:SignalHashKey` | Key Vault secret used to HMAC minimized network/device signals |
| `Saml:EntityId` | Stable SAML service-provider entity identifier |
| `Saml:AssertionConsumerServiceUrl` | Exact public HTTPS SAML POST callback |
| `Saml:SigningCertificateBase64` | Key Vault PKCS#12 certificate used to sign AuthnRequests/metadata |
| `Saml:SigningCertificatePassword` | Optional Key Vault password for the PKCS#12 certificate |
| `Cors:AllowedOrigins` | Array of allowed CORS origins |
| `AllowedHosts` | Host header allow-list. `*` by default; narrow it to your public hostnames when deploying |
| `Database:MigrateOnStartup` | Apply pending EF Core migrations at startup (default: on only in Development) |
| `AzureMonitor:ConnectionString` | Versionless Key Vault reference for the Application Insights connection string; mandatory outside Development/Testing |
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

The Azure deployment uses a passwordless database connection:

- App Service uses its system-assigned managed identity.
- `ConnectionStrings__DefaultConnection` is an App Service Key Vault reference to a versionless
  secret, so secret rotations do not require a code change.
- The matching contained Azure SQL principal has only `db_datareader`, `db_datawriter`, and
  `EXECUTE`; it is not a database owner and cannot change the schema.
- RSA, internal-token HMAC, MFA, and Data Protection certificate material are separate versionless
  Key Vault references generated for production; local `.env` values are never promoted.
- The application identity receives one secret-scoped `Key Vault Secrets User` assignment per
  value. Operators do not retain a Key Vault data-plane role after provisioning.
- Windows App Service loads the PKCS#12 private key with `WEBSITE_LOAD_USER_PROFILE=1`. The
  certificate itself and its password remain in Key Vault.
- The initial Azure host is the exact `AllowedHosts`, issuer, OIDC/action-link origin, and only CORS
  origin. Add the real frontend origin explicitly when it exists. Google login remains disabled
  until a real client ID is configured.

The single `CI/CD` workflow validates every pull request. On a push to `main` (or a manual run on
`main`), it deploys only after the secret scan, build, tests, and dependency audit pass. Azure
authentication uses GitHub OIDC; no publish profile or Azure client secret is stored in GitHub.
The repository must provide `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, and
`CI_MSSQL_SA_PASSWORD` as secrets, plus `AZURE_WEBAPP_NAME` and `AZURE_WEBAPP_SLOT` as variables.
After deployment, `/health/live` must return HTTP 200 or the workflow is marked failed.

OpenTelemetry exports traces, metrics and bounded custom SLI dimensions to Azure Monitor. System
Log entries contain only the W3C `traceId` correlation key, not telemetry payloads. SLOs and burn-
rate rules live in `ops/slo/` and `ops/alerts/`; load/DR tooling and incident procedures are in
`ops/load/`, `scripts/ops/` and [docs/operations](docs/operations/).

## Key Endpoints

### First-party experience

| Route | Purpose |
|---|---|
| `/login` | Hosted password, MFA, passkey and OAuth consent flow with application branding |
| `/portal` | Self-service sessions, trusted devices, passkeys, linked identities and consent grants |
| `/admin` | Permission-aware users, applications, branding, System Log and hook operations console |
| `/admin-v2/` | React administrative console under progressive migration |

These pages use a server-issued encrypted cookie; bearer tokens and refresh tokens are never
written to browser storage. Cookie-authenticated writes require the `X-AuthCenter-CSRF` double-
submit token and every API repeats authorization server-side. Branding is public but accepts only
bounded colors and absolute HTTPS links.

SDKs and executable integration examples live under `sdk/` and `samples/`. See
[docs/integration-quickstarts.md](docs/integration-quickstarts.md), follow the
[production integration runbook](docs/production-idp-integration.md), and run
`./scripts/Invoke-Conformance.ps1` for the automated OIDC/SCIM profile.

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

### Passkeys / WebAuthn

Passkeys use ASP.NET Core Identity schema v3 and require user verification. Production startup
fails unless the relying-party host and exact HTTPS origins are configured. Registration and
assertion state is protected by Data Protection, while application context is stored as short-lived,
single-use distributed state. The server stores only public credential material; private keys remain
in the authenticator.

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/auth/passkeys/registration/options` | Create registration options for the authenticated user |
| POST | `/api/auth/passkeys/registration/complete` | Verify attestation and store a named passkey |
| GET | `/api/auth/passkeys` | List the current user's passkeys |
| PUT | `/api/auth/passkeys/{credentialId}` | Rename a passkey |
| DELETE | `/api/auth/passkeys/{credentialId}` | Revoke a passkey |
| POST | `/api/auth/passkeys/login/options` | Start username or discoverable passwordless login |
| POST | `/api/auth/passkeys/login/complete` | Verify the assertion and issue application-scoped tokens |
| POST | `/api/auth/passkeys/step-up/options` | Start passkey reauthentication for a closed purpose |
| POST | `/api/auth/passkeys/step-up/complete` | Return a short-lived, single-use reauthentication proof |
| POST | `/api/auth/reauth/password` | Return a password-backed proof when local password is available |

Present a proof once in `X-AuthCenter-Reauthentication`. Clients must not persist it or reuse it for
another purpose.

Cross-origin browser calls must use credentials mode so the protected ceremony cookie is returned.
CORS credentials are enabled only for explicitly configured origins. A completed assertion is
treated as phishing-resistant MFA by application access policies, and its signature counter is
persisted before tokens are issued.
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
| GET | `/oauth/interactions/{interactionId}` | Read safe application/scope metadata for the authenticated consent UI |
| POST | `/oauth/authorize/complete` | Grant consent and issue the code |
| POST | `/oauth/token` | Exchange code / refresh token / client credentials |
| POST | `/oauth/revoke` | Revoke a refresh token and its complete rotation family |
| GET | `/oauth/userinfo` | OIDC claims for the access token's subject |
| GET | `/api/oauth/clients` | List registered clients |
| POST | `/api/oauth/clients` | Register a client |
| PUT | `/api/oauth/clients/{clientId}` | Update a client |
| POST | `/api/oauth/clients/{clientId}/rotate-secret` | Rotate the client secret |
| DELETE | `/api/oauth/clients/{clientId}` | Deactivate a client |

`/oauth/userinfo` accepts only access tokens issued by `/oauth/token`; first-party login tokens are
rejected because they are not scoped to an OAuth client.

Every OAuth client belongs to exactly one active `ApplicationSystem`. Authorization-code clients
must use an exact registered redirect URI, `state`, PKCE `S256` and `nonce` when requesting
`openid`. AuthCenter checks the user's active application access again at consent, code exchange,
refresh and UserInfo time. OAuth access tokens carry only that application's roles and permissions.
Confidential clients may authenticate with either `client_secret_basic` (preferred) or
`client_secret_post`; public clients never have a secret. New clients use 15-minute access tokens
by default and registration rejects lifetimes above one hour. See
[docs/authentication-flow.md](docs/authentication-flow.md#oauth-20--openid-connect-authorization-code).

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
| GET | `/api/users/{id}` | Get identity, direct/inherited applications and roles, and group sources |
| POST | `/api/users` | Create user administratively |
| PUT | `/api/users/{id}` | Update user profile fields |
| PUT | `/api/users/{id}/access` | Atomically replace direct applications and roles and revoke sessions |
| POST | `/api/users/invitations` | Invite a user to an application |
| POST | `/api/users/{id}/applications/{appId}` | Grant application access |
| PATCH | `/api/users/{id}/applications/{appId}/approve` | Approve pending application access |
| DELETE | `/api/users/{id}/applications/{appId}` | Revoke application access |
| POST | `/api/users/{id}/roles/{roleId}` | Assign role |
| DELETE | `/api/users/{id}/roles/{roleId}` | Remove role |
| PATCH | `/api/users/{id}/activate` | Activate user |
| PATCH | `/api/users/{id}/deactivate` | Deactivate user |
| POST | `/api/users/{id}/force-password-change` | Require a password change and revoke sessions |
| DELETE | `/api/users/{id}/mfa` | Reset MFA with a single-use `admin.mfa.reset` reauthentication proof |
| DELETE | `/api/users/{id}` | Anonymize a user with a single-use `admin.user.delete` reauthentication proof |

System-role changes require an effective direct `SuperAdmin`. The final effective `SuperAdmin`
cannot be deactivated, anonymized, or stripped of the direct AuthCenter application/role pair.

### Directory groups

Groups provide effective application access and roles without copying direct assignments to every
user. Membership, application and role changes revoke affected sessions so new tokens cannot keep
stale entitlements.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/groups` | Search and paginate groups |
| POST | `/api/groups` | Create a group |
| PUT | `/api/groups/{id}` | Update name and description |
| PATCH | `/api/groups/{id}/activate` | Activate a group |
| PATCH | `/api/groups/{id}/deactivate` | Deactivate a group and revoke member sessions |
| GET | `/api/groups/{id}/members` | List members |
| POST/DELETE | `/api/groups/{id}/members/{userId}` | Add or remove a member |
| POST/DELETE | `/api/groups/{id}/applications/{applicationId}` | Grant or remove effective application access |
| POST/DELETE | `/api/groups/{id}/roles/{roleId}` | Grant or remove an application role |
| PUT | `/api/groups/{id}/access` | Atomically replace group applications and roles, then revoke affected member sessions |

`SuperAdmin` cannot be inherited through a directory group. Assign that role directly to a named user
so privileged access remains attributable and auditable.

A group role is valid only after the role's application is assigned explicitly to the same group.
Removing an application also removes every group-role assignment for that application.

### Universal directory profile schema

Custom profile attributes are global, stable-key schema definitions. Supported types are `String`,
`Integer`, `Decimal`, `Boolean`, `Date`, and `DateTime`; definitions can enforce required/default
values, string length and a bounded non-backtracking regular expression, numeric ranges, and an
allow-list. Required attributes must have a valid default so publishing a schema change cannot make
every existing user invalid. Values are stored as canonical JSON and audit events contain changed
keys, never profile values.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/profile-schema` | List active definitions (`includeInactive=true` includes retired definitions) |
| POST | `/api/profile-schema` | Create a typed attribute definition |
| PUT | `/api/profile-schema/{definitionId}` | Update constraints after validating every existing value |
| DELETE | `/api/profile-schema/{definitionId}` | Retire a definition without destroying stored values |
| GET | `/api/users/{userId}/profile` | Read effective explicit/default profile values |
| PUT | `/api/users/{userId}/profile` | Validate and replace the submitted custom values atomically |

Schema administration requires `AUTHCENTER_PROFILE_SCHEMAS_READ` or
`AUTHCENTER_PROFILE_SCHEMAS_WRITE`; user profile values retain the existing
`AUTHCENTER_USERS_READ`/`AUTHCENTER_USERS_WRITE` boundary.

### Application access policies

Access-policy rules are evaluated in ascending priority inside an immutable published version. A
rule can target a user, active group, IPv4/IPv6 ranges, UTC validity dates/days/daily windows and a
risk range; it can allow or deny sign-in and require password or MFA assurance. Applications without
a published policy (or with no active published rules) preserve allow-by-default. Once active rules
exist, a request that matches none is denied.

Edits happen only in one draft per application and do not affect sign-in or active sessions. A new
draft clones the published version. Publishing atomically archives the old version and revokes every
session for that application; published/archived rules are immutable. Administrators can simulate a
draft at an explicit user, IP, UTC timestamp, risk and assurance level and receive a reason for every
rule before publishing. Audit data contains policy metadata, never credentials or raw tokens.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/access-policies/applications/{applicationId}` | List draft rules, or published rules when no draft exists |
| GET | `/api/access-policies/applications/{applicationId}/versions` | List immutable version history |
| POST | `/api/access-policies/applications/{applicationId}/drafts` | Get or create the single editable draft |
| POST | `/api/access-policies` | Add a rule to the requested draft version |
| PUT | `/api/access-policies/{ruleId}` | Replace a draft rule |
| DELETE | `/api/access-policies/{ruleId}` | Delete a draft rule |
| POST | `/api/access-policies/applications/{applicationId}/versions/{versionId}/publish` | Publish a draft and revoke stale sessions |
| POST | `/api/access-policies/simulate` | Explain a draft or published decision without changing state |

Administrative access requires `AUTHCENTER_ACCESS_POLICIES_READ` or
`AUTHCENTER_ACCESS_POLICIES_WRITE`. Priorities are unique within a policy version, range from 1 to
10000, and lower numbers are evaluated first. Each included/excluded network condition accepts up
to 50 CIDR ranges. To prevent locking every administrator out of the identity control plane,
`AUTHCENTER` must always retain an active unconditional `Allow` fallback whenever it has active
rules; give that fallback the lowest precedence (the largest priority number).

### Enterprise federation

`/api/federation/providers` and `/api/federation/routing-rules` configure OIDC/SAML providers per
application. OIDC callbacks are exact registered HTTPS values and upstream client secrets are
protected at rest. SAML publishes metadata at `/api/federation/saml/{providerId}/metadata` and the
POST ACS is `/api/federation/saml/acs`. Keep the SAML PKCS#12 certificate and password in Key Vault.
Administrative provider and routing mutations require a short-lived, purpose-bound, single-use
`admin.federation.change` proof. Providers and rules expose an explicit `version`; stale updates or
reorders return `CONCURRENCY_CONFLICT`. Routing rules can be listed by application, updated,
activated/deactivated, reordered atomically and deleted.

### SCIM and lifecycle automation

Provisioning tokens are created at `/api/provisioning-tokens`; the raw value is returned once.
Send it as `Authorization: Bearer acp_...` to `/scim/v2/Users` or `/scim/v2/Groups`. Tokens are
application-bound and use separate read/write scopes. Filters support bounded `userName`,
`externalId`, or `displayName eq`; pagination accepts `startIndex` and `count` up to 200. DELETE
deprovisions rather than erasing identity history.

`GET /api/provisioning-tokens` and `GET /api/provisioning-tokens/{id}` return only metadata,
including application, scopes, lifecycle status, expiration and last use. Rotation and revocation
require `admin.provisioning-token.rotate` or `admin.provisioning-token.revoke`; neither endpoint can
recover an existing raw token.

Profile mappings and dynamic group rules are managed under `/api/lifecycle`. The administrative
contract supports paginated list/detail, validation, versioned update, JSON-path simulation and a
paginated preview of users affected by a group rule. An authoritative
mapping prevents other sources from overwriting its target attribute. Group membership immediately
feeds existing application/role assignments and invalidates stale entitlement sessions.

Event hooks are managed at `/api/event-hooks`. The endpoint must be public HTTPS and echo the
verification challenge before delivery is enabled. Deliveries include `X-AuthCenter-Event-Id`,
`X-AuthCenter-Idempotency-Key`, `X-AuthCenter-Timestamp`, and
`X-AuthCenter-Signature: v1=<hex-hmac-sha256>`. Consumers should verify the signature over
`<timestamp>.<raw-body>`, reject stale timestamps, and deduplicate by event ID. Failed deliveries
retry and appear in `/api/event-hooks/deliveries?deadLettersOnly=true` for controlled replay.
The full administrative API also supports paginated hook list/detail/update and delivery filters by
hook, status, event, type and UTC range. Replay accepts `Idempotency-Key`; repeating the same key is
safe. The legacy `deadLettersOnly` shape remains available while `/admin` is being migrated.

### Administrative operations and System Log

`GET /api/admin-dashboard` aggregates directory, integration, delivery and security-posture
indicators without returning user-level data. `GET /api/audit-logs/export` exports one bounded,
filtered CSV page and records `SYSTEM_LOG_EXPORTED`. Administrative successes and rejected writes
record actor, target, application, result and trace without request bodies or secrets.

Every supported JSON API response exposes `traceId`. `GET /api/admin-metadata` publishes the stable
error catalog, step-up purposes, maximum page size and required permission for each new operation.
`GET /api/version` is a public, `no-store` deployment manifest containing only the assembly version,
source commit when available, admin base path and contract version.

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
- Effective application access and claims are the union of active direct assignments and active
  directory-group assignments; duplicates are removed.

### Default Seed Data

The `AUTHCENTER` application is seeded automatically with:
- **SuperAdmin** role (all permissions)
- **Admin** role (read + write users, read apps/roles/permissions)
- All permissions enumerated under `AUTHCENTER_*`

## Running Tests

```bash
dotnet test
```

## Repository Notes

The repository ignores generated `bin/` and `obj/` directories, logs, local appsettings files,
private-key/certificate formats, publish profiles, and local assistant/tooling state. Keep runtime
secrets in user-secrets locally and in a managed secret store in deployed environments. GitHub
Actions receives Azure identity values and its CI database password through repository secrets;
the App Service name and slot are repository variables. CI scans the tracked tree with Gitleaks.
