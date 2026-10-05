# AuthCenter

Centralized authentication and identity service for multiple systems: an OAuth 2.0/OpenID Connect provider with single sign-on and global logout, a SAML 2.0 identity provider, hosted login (passwords, passkeys, MFA, emailed links, enterprise federation with OIDC and SAML providers), a user portal, SCIM 2.0 provisioning, a universal directory with groups and rules, per-application access policies, access governance (requests, reviews, separation of duties), System Log and Event Hooks, an administrative console, and .NET/TypeScript SDKs.

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
[OKTA-LEVEL-ROADMAP.md](OKTA-LEVEL-ROADMAP.md), which lists what is still outside the product (PAR,
device authorization, custom domains, bulk imports); the remediation of the 2026-09-26 analysis is
tracked in [REMEDIACION-INTEGRACION-FEDERACION.md](REMEDIACION-INTEGRACION-FEDERACION.md).

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
The first console (`/admin`) was retired and its URL redirects to `/admin-v2/`.

The console shows the environment it is connected to (`AdminConsole:EnvironmentName`, else the
host environment) and the server version; errors quote the request's `traceId` as a reference for
support. Its overview reads `GET /api/admin-dashboard` (operators with `AUTHCENTER_AUDIT_LOGS_READ`)
and each indicator opens the matching filtered page. Selectors load every page of applications,
roles, groups and permissions, and users are searched by name or email.

The System Log (`/admin-v2/system-log`) filters by action, application, entity, actor, trace and
local dates (kept in the URL), names the actor of each event, links to the affected entity, shows
the event's IP, user agent and metadata, and exports the matching events as CSV. Entity pages link
to their own history.

Event Hooks are managed at `/admin-v2/event-hooks`: scope (platform or one application), endpoint,
event types picked from the catalog by area (or `*` for every event), endpoint verification, secret
rotation with step-up and a 24-hour dual-signature window, activation, and a deliveries view with
filters, the signed payload and dead-letter replay.

The universal profile schema is edited at `/admin-v2/profile-schema` (types, defaults, ranges,
patterns and allowed values; a change that would invalidate stored values is refused), and the API
catalog at `/admin-v2/api-resources` (resource indicator, owning application and scopes). The OAuth
client editor picks API scopes from that catalog.

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
proofs; historical token values cannot be retrieved. Each token's page shows the SCIM base URL to
configure in the identity provider and a SCIM diagnostics panel: request counts for the last day and
week, the kinds of failure with their last detail, and every request with its outcome and trace id.

Lifecycle automation is managed at `/admin-v2/profile-mappings` and `/admin-v2/group-rules`. Profile
mappings bind a SCIM attribute path (sub-attributes, value filters and extension URNs) to an active universal-profile
attribute, can be validated before they are created, and expose a simulation panel that resolves the
path against an operator-supplied SCIM document without persisting anything. Group rules compare a
profile attribute with an expected value (`eq`, `ne`, `in`, `contains`, `startsWith`, `gt`, `gte`,
`lt`, `lte`, `exists`), offering only the operators of the attribute's type; the expected value is
converted to that type before it is sent, and the editor shows a server-paginated preview of the
active users that currently match. A group with active rules is managed by them: a user belongs when
any rule matches, the membership is recomputed when a rule or a profile changes, and manual member
changes are refused with `GROUP_MANAGED_BY_RULES`. Both editors send the loaded version on update and surface a `409
CONCURRENCY_CONFLICT` with a reload action instead of overwriting newer changes.

Federation is managed at `/admin-v2/federation`, scoped to one application at a time. Operators
register OIDC or SAML 2.0 identity providers through protocol-specific forms (HTTPS issuer, exact
callback URL, SP metadata URL); client secrets and signing certificates are write-only and the UI
only shows their presence and thumbprint, and leaving them blank on update keeps the stored values.
Routing rules combine email domain, group and profile-attribute conditions, are reordered in place
with priorities re-issued on save, and a simulation panel resolves any email against the saved
rules. Every write requires a fresh single-use `admin.federation.change` reauthentication proof.

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
| `Oidc:PublicOrigin` | Public HTTPS origin of AuthCenter; outside Development it must identify the same URL as `Jwt:Issuer`. The hosted OIDC federation callback is `{PublicOrigin}/api/federation/oidc/callback` |
| `Jwt:Audience` | JWT audience claim |
| `Jwt:SigningKey` | HMAC-SHA256 key for internal pending MFA, forced-password-change, and magic-link tokens (min 32 chars) |
| `Jwt:RsaPrivateKeyPem` | Active RSA private key (at least 2048 bits) in PEM format used to sign access and ID tokens with RS256 (required in every environment) |
| `Jwt:AdditionalValidationKeysPem` | Array of PEM keys still accepted on validation and published in the JWKS, but no longer used for signing. See [Rotating the signing key](#rotating-the-signing-key) |
| `Jwt:AccessTokenMinutes` | Access token lifetime (default 15) |
| `Jwt:RefreshTokenDays` | Refresh token lifetime (default 30) |
| `Sso:SessionLifetimeMinutes` | Lifetime of the hosted-login single sign-on session shared by every application (default 480, from 5 minutes to 7 days). The session record is still checked on every request, so sign-out and entitlement changes end it immediately |
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
| `Saml:SigningCertificateBase64` | Key Vault PKCS#12 certificate with its RSA private key. As a service provider, AuthCenter signs AuthnRequests/metadata and decrypts encrypted assertions with it; as an identity provider, it signs the assertions and responses it issues to SAML applications |
| `Saml:IdentityProviderEntityId` | Entity ID of AuthCenter as a SAML identity provider (default: its metadata URL, `{Jwt:Issuer}/saml/idp/metadata`). Every SAML application registers it, so changing it breaks their sign-in until they are updated |
| `Saml:SigningCertificatePassword` | Optional Key Vault password for the PKCS#12 certificate |
| `Cors:AllowedOrigins` | Origins of first-party frontends allowed to call `/api/*` with credentials. OAuth clients register their own browser origins (`AllowedCorsOrigins`) for the token, revocation and UserInfo endpoints; discovery and JWKS are public; the hosted UI (`/ui-api/*`, `/oauth/authorize`, pages) never answers cross-origin |
| `RateLimiting:Enabled` | Turns rate limiting on or off (default on, except in the `Testing` environment) |
| `RateLimiting:DistributedEnabled` | Shares the counters of every instance in SQL Server instead of memory |
| `RateLimiting:Rules:{policy}` | Replaces a policy's rules: an array of `{ Dimension, PermitLimit, WindowSeconds }`, with `Dimension` `Ip`, `Account` (email in the body), `Client` (OAuth client) or `AnonymousIp` (address, only when no client is named). Defaults: login 5/min per address and 20/15 min per account; password reset and magic links also per account; `/oauth/token`, `/oauth/revoke` and `/oauth/introspect` 1200/min per client and 60/min per address without a client; federation discovery 30/min per address and 10/min per email, federation start and completion 20/min per address; the SAML identity provider endpoints (`saml-idp`) 60/min per address |
| `AllowedHosts` | Host header allow-list. `*` by default; narrow it to your public hostnames when deploying |
| `Database:MigrateOnStartup` | Apply pending EF Core migrations at startup, creating the database if it does not exist (default: on only in Development). Instances starting together serialize on a SQL Server application lock |
| `AzureMonitor:ConnectionString` | Versionless Key Vault reference for the Application Insights connection string; mandatory outside Development/Testing |
| `Database:SeedOnStartup` | Seed the `AUTHCENTER` application, roles, permissions, and admin user at startup (default: on only in Development) |
| `Seed:AdminEmail` | Initial admin user email |
| `Seed:AdminPassword` | Initial admin user password |
| `Seed:AdminFullName` | Initial admin user full name |
| `ActionLinks:DefaultBaseUrl` | Origin the links in AuthCenter's emails point to (password reset, invitation, email confirmation and change, magic link). Point it at AuthCenter's public origin to use the hosted pages, or at an application that hosts its own |
| `ActionLinks:ApplicationBaseUrls:{code}` | Per-application origin for those links. The paths are `ActionLinks:*Path` (defaults `/reset-password`, `/accept-invitation`, `/confirm-email`, `/confirm-email-change`, `/magic-link`, the hosted pages) |
| `Governance:AccessRequestLifetimeDays` | Days a portal access request waits for a decision before it expires (default 30, from 1 to 365) |
| `Governance:MaxPendingRequestsPerUser` | Portal requests a user may have waiting at once (default 10) |
| `Governance:MaintenanceIntervalMinutes` | Minutes between the runs that expire requests and complete or repeat access reviews (default 15); `Governance:MaintenanceEnabled` turns them off |
| `Email:DevelopmentPickupDirectory` | Development and tests only: write each email as a JSON file in this directory instead of sending it. Startup fails if it is set in any other environment |

The API fails fast in every environment when `Jwt:RsaPrivateKeyPem` is missing, invalid, or still uses a placeholder. Outside `Development` and `Testing`, it also fails when:

- `ConnectionStrings:DefaultConnection` is missing.
- `Jwt:SigningKey` is empty, shorter than 64 characters, or still uses the placeholder.
- `Mfa:EncryptionKey` is empty, shorter than 32 characters, or still uses the placeholder.
- `Authentication:Google:ClientId` still uses the placeholder value.
- `Cors:AllowedOrigins` is empty.

Set `Jwt:Issuer` to the public HTTPS URL of the service and `Oidc:PublicOrigin` to the same URL.
The discovery document at `/.well-known/openid-configuration` publishes the first as `issuer` and
builds every endpoint URL from the second, never from the request's host. Tokens carry the issuer
in `iss`, so changing it invalidates the tokens already in circulation.

### Custom domain

AuthCenter answers at `https://authcenter.info`. To serve it from a custom domain:

1. Bind the domain and its certificate to the App Service.
2. Set `AllowedHosts` to the domain and the App Service host, separated by a semicolon
   (`authcenter.info;<app>.azurewebsites.net`). The deployment checks reach the app through the
   App Service host, and a host missing from the list gets
   `HTTP Error 400. The request hostname is invalid.` before AuthCenter sees the request.
3. Set `Jwt__Issuer`, `Oidc__PublicOrigin` and `ActionLinks__DefaultBaseUrl` to `https://<domain>`,
   `Passkeys__RelyingPartyId` to the domain and `Passkeys__AllowedOrigins__0` to `https://<domain>`.

After a move to a new domain, users sign in again there, and the passkeys registered for the
previous host stop working. The default SAML entity ID and the federation callback URL change too,
so SAML applications and upstream providers already registered must be updated. Move before
relying parties are registered.

### Rotating the signing key

Every key is identified in the JWKS by a `kid` derived from the key itself (an RFC 7638
thumbprint), so a rotation is a three-step move that never invalidates tokens already issued:

1. **Publish** the new key by adding it to `Jwt:AdditionalValidationKeysPem`. Relying parties that
   refresh the JWKS pick it up while the old key keeps signing.
2. **Promote** it: move the new key to `Jwt:RsaPrivateKeyPem` and put the old key's public PEM in
   `Jwt:AdditionalValidationKeysPem`. New tokens are signed with the new key; tokens signed with
   the old one are still accepted.
3. **Retire** the old key by removing it from `Jwt:AdditionalValidationKeysPem`, no sooner than
   24 hours after step 2. ID tokens set that wait: clients send them back as `id_token_hint` to
   `/oauth/logout` and `/oauth/authorize`, and AuthCenter accepts an expired hint but not one
   signed with a key it no longer has. A BFF built on `AuthCenter.Client` sends the ID token of
   its session when the user signs out, for as long as the session lasts (`SessionLifetime`:
   8 hours by default, 24 at most). If a single-page application keeps ID tokens longer, wait that
   long. Access tokens (`Jwt:AccessTokenMinutes`, 60 minutes at most) and the one-time sign-in
   tokens (`Jwt:MagicLinkTokenMinutes`, `Mfa:MfaTokenExpirySeconds`) expire well within the wait.

A hint signed with a retired key makes `/oauth/logout` answer with an error and leave the user
signed in to AuthCenter, and `/oauth/authorize` answer `invalid_request`. If the old key was
compromised, retire it at once and accept those failures.

Only the public half of an additional key is ever exposed, so step 2 can use the public PEM alone.
Steps must be one deploy apart — collapsing them means clients holding a stale JWKS will reject
tokens signed with a key they never saw.

### Deploying

The database is not created or seeded automatically outside `Development`. To initialize a
deployed environment, run it once with `Database:MigrateOnStartup` and `Database:SeedOnStartup`
set to `true` along with the `Seed:*` values, then turn both back off so that later restarts do
not re-run the bootstrap. Alternatively, apply the migrations out of band with
`dotnet ef database update`.

In Azure the application identity cannot change the schema, so every release applies its
migrations **before** the code that needs them: the `CI/CD` workflow publishes the idempotent
script of each commit as the `database-migrations` artifact (`dotnet ef migrations script
--idempotent`, with the tools pinned in `.config/dotnet-tools.json`), and a database administrator
runs it. Readiness guards the order: the `database-schema` check of `/health/ready` answers 503
while this build has migrations the database lacks, and reports `Degraded` (still 200) when the
database is ahead of the build, as after rolling the code back. The response names each check and
its status. Step by step: [docs/operations/OWNER-ACTIONS.md](docs/operations/OWNER-ACTIONS.md#ops-03--migraciones-en-azure-sql).

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
- `https://authcenter.info` is the issuer, the OIDC and action-link origin and the passkey relying
  party, and `AllowedHosts` lists it together with the App Service host
  ([Custom domain](#custom-domain)). CORS lists only the App Service host: add the real frontend
  origin explicitly when it exists. Google login remains disabled until a real client ID is
  configured.

The single `CI/CD` workflow validates every pull request. On a push to `main` (or a manual run on
`main`), it deploys only after the secret scan, build, tests, and dependency audit pass. Azure
authentication uses GitHub OIDC; no publish profile or Azure client secret is stored in GitHub.
The repository must provide `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, and
`CI_MSSQL_SA_PASSWORD` as secrets, plus `AZURE_WEBAPP_NAME` and `AZURE_WEBAPP_SLOT` as variables.
`CI_MSSQL_SA_PASSWORD` is the password of the throwaway SQL Server the tests run against; pull
requests that get no secrets (Dependabot's, or a fork's) use one derived from the run instead.
After deployment, `/health/live`, `/health/ready` and the branding endpoint must return HTTP 200 or
the workflow is marked failed; on failure it prints the readiness report. The workflow then fetches
the discovery document through the issuer's own URL: it must answer with that same issuer and a
key set with at least one key, so a domain missing from `AllowedHosts` or not bound to the App
Service fails the deployment.

Besides the tests, CI fails when an EF Core model change has no migration, when .NET coverage
(unit and integration, merged) drops under 80% of lines or 60% of branches, when the console's
logic modules drop under their minimums (`vite.config.ts`), or when the console's JavaScript
outgrows its gzip budget (`src/AuthCenter.Admin/scripts/check-bundle-budget.mjs`). The coverage
summary goes to the run page and the reports to the `coverage-report` artifact. Dependabot
(`.github/dependabot.yml`) proposes weekly updates for NuGet, npm, Actions and Docker: minor and
patch versions grouped per ecosystem, vitest with its plugins, and every Actions update in one pull
request. TypeScript 7 waits until typescript-eslint supports it. The
`Directory scale` workflow runs the large-directory capacity test weekly
([docs/operations/CAPACITY.md](docs/operations/CAPACITY.md#directorio-grande)).

OpenTelemetry exports traces, metrics and bounded custom SLI dimensions to Azure Monitor. System
Log entries contain only the W3C `traceId` correlation key, not telemetry payloads. SLOs and burn-
rate rules live in `ops/slo/` and `ops/alerts/`; load/DR tooling and incident procedures are in
`ops/load/`, `scripts/ops/` and [docs/operations](docs/operations/): support procedures with the
console in [CONSOLE-RUNBOOKS.md](docs/operations/CONSOLE-RUNBOOKS.md), and the steps that need the
owner's accounts or approval (GitHub Actions billing, branch protection, production migrations,
seed and key rotation, SDK publishing, outbound email, registering Paquetenvia with
`scripts/ops/Register-Paquetenvia.ps1`) in [OWNER-ACTIONS.md](docs/operations/OWNER-ACTIONS.md).

## Key Endpoints

### First-party experience

| Route | Purpose |
|---|---|
| `/login` | Hosted sign-in with application branding, one step per card (each with its own heading and document title, worded for the application's audience): password, passkeys (with or without the email), emailed sign-in links, forgotten password, enterprise federation, the second factor (authenticator, emailed code or backup code) and, when the application requires a factor the user lacks, its guided enrollment (authenticator with QR code and backup codes, or a passkey); an unconfirmed email sends its confirmation again from here; OAuth consent (what the client may do, in words, the signed-in account and "¿No eres tú?") and step-up; a request that can no longer continue offers to go back to the application (the origin of its redirect URI or assertion consumer service). Administrators who sign in without a destination land in the console |
| `/portal` | Account portal: password, two-step verification (authenticator or email, backup codes), passkeys, sessions and trusted devices, applications (**Abrir** signs in to a SAML application that accepts sign-ins started by AuthCenter; access requests with their status), approvals for application owners (access requests and access reviews), linked identity providers (link an organization's provider, unlink), consent grants, email change and account deletion. Sensitive changes ask for the password or a passkey again |
| `/reset-password`, `/accept-invitation`, `/confirm-email`, `/confirm-email-change`, `/magic-link` | Pages the links in AuthCenter's emails open when `ActionLinks` point at AuthCenter. The single-use token leaves the address bar at once, the pages send no referrer and each action needs a click |
| `/logout` | Confirms a sign-out an application asked for but could not prove (RP-Initiated Logout), with that application's branding; opened directly, it signs out this browser's session. Signing out lands on the login, which says so |
| `/admin-v2/` | Administrative console: directory and profile schema, applications (with their owners), OAuth clients and API catalog, SAML applications, lifecycle, federation, roles, permissions and access policies, access governance (requests, reviews, separation of duties), System Log and Event Hooks. `/admin` redirects here |

**White label.** End users only know the applications they sign in to, so the hosted pages and the
emails never name AuthCenter. Both are in Spanish: the pages carry the application's name, logo,
colors and legal links, and the emails its name and help link. The system application (`AUTHCENTER`, which the migrations brand as "AuthCenter")
has no name on them until an administrator renames it in its branding. Two settings still name
something on their own: `Email:FromName`, the sender of emails with no application (an
application's emails are sent in its name), and `Mfa:TotpIssuer`, the account name authenticator
apps show; set both to your organization. The public host stays visible in the address bar:
serve AuthCenter from a domain of your own for a full white label.

**Audience.** Each application's registration settings carry `Audience`: `Consumers` (neutral
wording, "Continúa en {app}") or `Employees` ("Usa tu cuenta de la empresa…"). Left out when an
application is created, it is `Consumers` for `Open` registration and `Employees` otherwise; an
update that leaves it out keeps it.

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
| POST | `/api/auth/change-password` | Change the current user's password (the owner is told by email) |
| GET | `/api/auth/applications` | Applications the current user can use, directly or through groups |
| POST | `/api/auth/forced-change-password` | Complete a forced password change |

**Creating an account in the hosted login.** An application whose `RegistrationMode` is `Open` or
`ApprovalRequired` and that allows passwords shows "Crear cuenta" on `/login`. That includes a login
opened by an OAuth or SAML request. The form posts to `POST /ui-api/session/register`. The outcome
depends on the application:
- **Without required confirmation:** the new account signs in at once, and the request continues.
  It still receives the confirmation link, and its ID tokens carry `email_verified=false` until the
  user follows it.
- **With `RequireEmailConfirmation`:** the page asks the user to check their email. It remembers the
  request in the browser. The confirmation page, opened in that browser while the request lasts
  (10 minutes), continues the request after the user signs in.
- **With `ApprovalRequired`:** the page says an administrator must approve the access.

An email that already has an account gets the answer a new account would get ("check your email",
or the pending approval), so the form reveals no accounts. The owner of that account receives a
notice about the attempt, and the account itself is not changed. Without required confirmation or
approval this does not fully hold: a new account enters at once, but an existing one gets "check
your email". The endpoint shares the `/api/auth/register` rate limits.

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
| DELETE | `/api/auth/mfa` | Disable MFA with an authenticator, backup or (email factor) emailed code |
| POST | `/api/auth/mfa/verify` | Complete a login pending MFA (up to 5 attempts per pending sign-in) |
| POST | `/api/auth/mfa/backup-codes` | Regenerate backup codes |
| POST | `/api/auth/mfa/email-otp/setup` | Start email OTP enrollment |
| POST | `/api/auth/mfa/email-otp/enable` | Confirm and enable email OTP |
| POST | `/api/auth/mfa/email-otp/send` | Send an email OTP for a pending login |
| POST | `/api/auth/mfa/email-otp/verification` | Email a code that confirms managing an enabled email factor (disabling it) |

Each authenticator code is accepted once: the time step it matched cannot be used again, so an
observed code cannot be replayed. When an application requires a factor the user has not set up,
sign-in answers `MFA_SETUP_REQUIRED` (or `PASSKEY_ENROLLMENT_REQUIRED`): the hosted login receives
a single-use enrollment token to set it up in place, JSON clients a fixed message.

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

### Sessions, devices, and account

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/auth/sessions` | List active sessions |
| DELETE | `/api/auth/sessions/{tokenId}` | Revoke one session |
| DELETE | `/api/auth/sessions` | Revoke every other session |
| GET | `/api/auth/trusted-devices` | List trusted devices |
| DELETE | `/api/auth/trusted-devices/{deviceId}` | Remove one trusted device |
| DELETE | `/api/auth/trusted-devices` | Remove every trusted device |
| POST | `/api/auth/email-change/request` | Request an email change (the current address is warned; the link names the account) |
| POST | `/api/auth/email-change/confirm` | Confirm an email change (the previous address is told) |
| DELETE | `/api/auth/account` | Delete the current user's account |

### OAuth 2.0 / OpenID Connect provider

AuthCenter acts as an authorization server: authorization code with PKCE, client credentials, and
refresh token grants. Tokens are signed with RS256 and verifiable through the published JWKS.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/.well-known/openid-configuration` | Discovery document |
| GET | `/.well-known/jwks.json` | Public signing keys |
| GET/POST | `/oauth/authorize` | Start an authorization request (answered directly when a single sign-on session applies) |
| GET | `/oauth/interactions/{interactionId}/context` | Application, login hint and freshness for the hosted login; only from the browser that started the request |
| GET | `/oauth/interactions/{interactionId}` | Read safe application/scope metadata for the authenticated consent UI |
| POST | `/oauth/authorize/complete` | Grant consent and issue the code |
| GET | `/oauth/authorize/response/{responseId}` | One-time `form_post` delivery page for the browser that completed the interaction |
| POST | `/oauth/interactions/{interactionId}/step-up` | Hosted login only: start the MFA or passkey step-up the client's application requires |
| POST | `/oauth/introspect` | RFC 7662 introspection for confidential clients and the APIs a token was issued for |
| GET/POST | `/oauth/logout` | OpenID Connect RP-initiated logout (`end_session_endpoint`) |
| GET | `/oauth/logout/{logoutId}` | Hosted logout page context, only for the browser that started the request |
| POST | `/oauth/logout/{logoutId}/confirm` | The user confirms a sign-out AuthCenter could not verify on its own |
| POST | `/oauth/token` | Exchange code / refresh token / client credentials |
| POST | `/oauth/revoke` | Revoke a refresh token and its complete rotation family |
| GET | `/oauth/userinfo` | OIDC claims for the access token's subject |
| GET | `/api/oauth/clients` | List registered clients |
| POST | `/api/oauth/clients` | Register a client |
| PUT | `/api/oauth/clients/{clientId}` | Update a client |
| POST | `/api/oauth/clients/{clientId}/rotate-secret` | Rotate the client secret |
| DELETE | `/api/oauth/clients/{clientId}` | Deactivate a client |
| GET/POST | `/api/api-resources` | List or register APIs (RFC 8707 resources) and their scopes |
| GET/PUT | `/api/api-resources/{id}` | Read or update an API, its status and complete scope list |

`/oauth/userinfo` accepts only access tokens issued by `/oauth/token`; first-party login tokens are
rejected because they are not scoped to an OAuth client.

**Single sign-on.** Register each client's `LoginUrl` as the hosted login,
`https://<authcenter-host>/login`. `/oauth/authorize` appends `interaction_id`, and the page reads
`/oauth/interactions/{id}/context` to sign the user in to the client's own application (its
branding, password policy, access policies and MFA). The hosted-login cookie
(`__Host-AuthCenter.Ui`, `SameSite=Lax`) is the single sign-on session: while it is valid,
`/oauth/authorize` answers other clients directly without showing a page. Supported request
parameters: `prompt` (`none`, `login`, `consent`, `select_account`), `max_age`, `login_hint`,
`idp` (a federation provider ID of the client's application: the hosted login goes straight to
it), `domain_hint` (an email domain for home realm discovery), `id_token_hint`, `acr_values` and
`response_mode` (`query` or `form_post`); `request` and
`request_uri` are rejected with their standard errors. Interactions are bound to the browser that
started them (`__Host-AuthCenter.Browser`), so a link cannot be completed from another browser.
ID tokens carry `sid`, the real `auth_time`, `amr` (RFC 8176) and `acr`
(`urn:authcenter:acr:1fa`, `urn:authcenter:acr:mfa`, `urn:authcenter:acr:phr`); user access tokens
carry `auth_time`, `amr` and `acr` as well, so an API can require `mfa` in `amr`. `amr` is always a
JSON array built from the recorded authentication (`["pwd"]`, `["pwd","otp","mfa"]`, `["fed"]`,
`["pop","mfa"]`), is kept on refresh and token exchange, and no request parameter changes it. Re-authenticating
with the same account keeps the session and its `sid`; signing in with another account ends the
previous account's session.

A session is only reused under the rules of the client's own application: active access, its
published access policy (evaluated with the browser's address and risk), its `RequireMfa`
setting, the user's own MFA and the least demanding supported `acr_values`. A denial is returned
to the client as `access_denied`; a weaker session is stepped up in the hosted login (second
factor or passkey) without signing in again, and `prompt=none` reports `login_required` instead.
OAuth refresh re-checks access and the published policy and revokes the grant family on denial.

**APIs (resources).** Register each API in the catalog with an absolute URI identifier (for
example `https://orders.example.com/api`), its scopes (unique across APIs) and the application
that owns it. Clients are allowed API scopes like any other scope. An authorization or token
request with API scopes (and optionally `resource`, RFC 8707) yields an access token whose
audience is that API, plus `urn:authcenter:userinfo` when `openid` was granted, carrying the roles
and permissions of the API's application; the user needs access to that application. One token
has one API audience: a grant for several APIs names one with `resource` at the token endpoint
(also on refresh). `client_credentials` accepts `resource` the same way. Token exchange
(`urn:ietf:params:oauth:grant-type:token-exchange`, RFC 8693) lets a confidential client of the
API's application trade the user token it received for a token to another API, with an `act`
claim naming it and never outliving the original. `/oauth/introspect` (RFC 7662) reports a token
active only to its client or the APIs of the caller's application, and only while the user and the
single sign-on session behind it are still active.

**Logout.** Register each client's exact `PostLogoutRedirectUris` and, optionally, a
`BackchannelLogoutUri`. `/oauth/logout` accepts `id_token_hint`, `client_id`,
`post_logout_redirect_uri` and `state`: an ID token of the browser's current session signs out at
once; any other request is confirmed by the user on the hosted `/logout` page (a cross-site form
post continues as a top-level GET so the session cookie is available). Ending a single sign-on
session, from any path (RP-initiated logout, the hosted logout, revoking a session or signing out
everywhere, deactivation, access removal), also revokes the OAuth grants it authorized and queues
an OpenID Connect back-channel logout token (`typ: logout+jwt`, with `sid` and `sub`) for every
client that received tokens through it. The outbox mints a fresh token on each delivery attempt
and retries up to 10 times. Access tokens already issued remain valid until they expire.

Every OAuth client belongs to exactly one active `ApplicationSystem`. Authorization-code clients
must use an exact registered redirect URI, `state`, PKCE `S256` and `nonce` when requesting
`openid`. AuthCenter checks the user's active application access again at consent, code exchange,
refresh and UserInfo time. OAuth access tokens carry only that application's roles and permissions.
Confidential clients may authenticate with either `client_secret_basic` (preferred) or
`client_secret_post`; public clients never have a secret. New clients use 15-minute access tokens
by default and registration rejects lifetimes above one hour. See
[docs/authentication-flow.md](docs/authentication-flow.md#oauth-20--openid-connect-authorization-code).

`email_verified` in ID tokens and UserInfo is `true` only for a verified address. Either its owner
proved control of the inbox, or a trusted source asserted it:
- **The owner proved it:** by following a confirmation, magic-link, password-reset or email-change
  link, or by entering an emailed code.
- **A trusted source asserted it:**
  - an administrator who created or invited the user;
  - SCIM provisioning;
  - an enterprise federation provider;
  - Google, GitHub or Apple;
  - a personal Microsoft account;
  - a Microsoft work account in the one tenant of a single-tenant configuration.

In a multi-tenant (`common`) configuration, a Microsoft work account's email is whatever its tenant's
administrators set. It counts as verified only when the ID token carries `xms_edov=true`: add that
optional claim in the app registration. Password self-registration always sends the confirmation
link. The application's `RequireEmailConfirmation` only decides whether the account may sign in
before the link is followed.

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
| PATCH | `/api/users/{id}/applications/{appId}/approve` | Approve pending application access and the request behind it (same checks as the access requests queue; a revoked access is granted again with the POST above, never approved) |
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

### Access governance

Application owners, access requests with approval, periodic access reviews and separation of duties
(`AUTHCENTER_GOVERNANCE_READ`/`_WRITE`; on upgrade, roles holding `AUTHCENTER_USERS_READ`/`_WRITE`
received them). The console groups them under **Gobierno**; owners act from their portal.

- **Owners.** `GET`/`PUT /api/governance/applications/{id}` sets up to 20 active users as owners of an
  application and whether users may request it from the portal (`version` protects concurrent edits).
  Owners decide the application's access requests and review its access; nobody decides their own
  request or reviews their own access, administrators included. Without owners, governance
  administrators decide.
- **Access requests.** A user asks from the portal (`POST /api/auth/access-requests` with a
  justification, optionally for one of the application's non-system roles); owners are emailed and
  decide in the portal (`/api/auth/approvals`), administrators in the queue
  (`/api/governance/access-requests`, `POST .../{id}/approve|reject`; a rejection needs a reason the
  user reads). Registrations of applications that require approval and users an administrator left
  with pending access create a request too, so the pending-access approval of the users page approves
  that request. Approving grants the access (and the role) after the separation of duties check;
  portal requests expire after `Governance:AccessRequestLifetimeDays` and the requester can cancel them.
- **Access reviews.** `POST /api/governance/access-reviews` starts a campaign for one application: its
  items are the active users that can use it now, directly or through groups. Owners keep or revoke
  each access (portal or console); a revocation removes the direct access at once, and access that
  comes from a group is flagged for someone to remove the membership. At the due date the campaign
  completes and what nobody reviewed is kept or revoked, as configured; a recurring campaign starts
  again `RecurrenceMonths` after the previous one started. The maintenance runs every
  `Governance:MaintenanceIntervalMinutes`, one instance per campaign.
- **Separation of duties.** `/api/governance/sod-rules` keeps two roles (of any applications) apart.
  Administrative changes that would give someone both roles, directly or through groups, are refused
  with `SOD_CONFLICT` (direct roles, direct access replacement, group memberships, a group's roles,
  reactivating a group, approving a request) and recorded as `SOD_CONFLICT_BLOCKED`. Combinations that
  arrive from SCIM, dynamic group rules or federation are not blocked (the identity provider is the
  source of truth) and are listed with how each role is held at `GET /api/governance/sod-violations`
  and counted on the dashboard.
- Audit events (and event hooks): `APPLICATION_GOVERNANCE_UPDATED`, `ACCESS_REQUEST_*`, `ACCESS_REVIEW_*`,
  `SOD_RULE_*` and `SOD_CONFLICT_BLOCKED`.

### Enterprise federation

`/api/federation/providers` and `/api/federation/routing-rules` configure OIDC/SAML providers per
application. Upstream client secrets are protected at rest. Administrative provider and routing
mutations require a short-lived, purpose-bound, single-use `admin.federation.change` proof.
Providers and rules expose an explicit `version`; stale updates or reorders return
`CONCURRENCY_CONFLICT`. Routing rules can be listed by application, updated,
activated/deactivated, reordered atomically and deleted.

**Registering AuthCenter at the identity provider.** `GET /api/federation/service-provider`
returns the values to register: the hosted OIDC callback
`https://<authcenter-host>/api/federation/oidc/callback` (derived from `Oidc:PublicOrigin`; a
provider saved without a callback URL uses it), and the SAML entity ID and ACS
(`/api/federation/saml/acs`). SAML metadata, with signing and encryption keys, is published at
`/api/federation/saml/{providerId}/metadata`. Keep the SAML PKCS#12 certificate and password in Key
Vault. `POST /api/federation/providers/{id}/test` checks a saved provider without changing it:
OIDC discovery, issuer, HTTPS endpoints, signing keys, PKCE and the callback; SAML certificates
(validity, key size), SSO URL and AuthCenter's own certificate.

**Sign-in through the hosted login.** The hosted login offers federation to the application of
the authorization request (or of a direct sign-in): `idp` sends the user straight to a provider,
`domain_hint` or the email the user types selects one by home realm discovery
(`POST /ui-api/session/federation/discover`), and `POST /ui-api/session/federation/start` returns
the upstream URL (OIDC authorization code with PKCE, nonce and `prompt=login` when the request
needs a fresh sign-in; SAML signed AuthnRequest with `ForceAuthn`). The upstream answers at the
server-side OIDC callback or the ACS, which validate the response and map the identity (existing
link, account linking by verified email, or just-in-time provisioning), but never sign the browser
in by themselves: they leave a single-use result bound to the browser that started the sign-in
and redirect (303) to `/login`, which redeems it with `POST /ui-api/session/federation/complete`.
Only then does the application's access policy and MFA gate run, exactly as after a password: the
user may be asked for a second factor, or denied. A callback delivered to another browser (login
CSRF) signs nobody in. Access an administrator revoked is never granted again by federation.

**Discovery and directory data.** Anonymous home realm discovery only uses email-domain
conditions. Rules with group or profile-attribute conditions are evaluated only for the user the
browser is already signed in as, so the login page cannot be used to probe directory membership.
`POST /api/federation/route` (the administrative simulation that evaluates every condition) now
requires `AUTHCENTER_FEDERATION_READ`.

**Interoperability.** OIDC issuers are compared ignoring a trailing slash; ID tokens must be
signed with RSA/ECDSA algorithms; `client_secret_basic` is used when the provider only supports
it. `RequireVerifiedEmail` (default on) requires `email_verified=true` before an email links or
creates an account; with it off, only emails of the provider's routing-rule domains are trusted.
SAML entity IDs may be any absolute URI (https, http or urn). The Response, the assertion or both
may be signed (RSA with SHA-256 or stronger; every signature present must verify) and assertions
may be encrypted to AuthCenter's certificate (RSA-OAEP with AES-CBC or AES-GCM; RSA 1.5 is
refused). Responses and assertions are single-use.

**MFA and groups from the provider.** With `TrustUpstreamMfa`, an upstream multi-factor sign-in
(OIDC `amr` containing `mfa`; SAML authentication context such as
`http://schemas.microsoft.com/claims/multipleauthn` or the REFEDS MFA profile) counts as
AuthCenter MFA (`amr` `fed mfa`, `acr` `urn:authcenter:acr:mfa`); otherwise a user who must use MFA
completes AuthCenter's own second factor. `GroupsClaim` names the upstream claim or attribute with
group values and `GroupMappings` map values to directory groups: the provider is authoritative for
its mapped groups, so each sign-in adds and removes those memberships (other groups are untouched)
and a change closes the user's existing sessions. When Entra ID moves the groups out of the token
(overage), memberships are left as they are.

**Linking from the portal.** `GET /ui-api/session/federation/linkable` lists the active providers
of the user's applications. `POST /ui-api/session/federation/start` with `link: true` (from a
signed-in browser, with a single-use `account.link-provider` reauthentication proof) signs in at the
provider and returns to the portal with a single-use result that
`POST /ui-api/session/federation/link` redeems: the upstream identity is linked to that account
(never matched by email), only from the same browser and while the same account is signed in, and
only if it is not linked to another account. The owner is told by email. An identity unlinked
earlier is linked again on its row, since the pair (provider, subject) is unique.

The JSON API (`/api/federation/oidc/begin|complete`, `/api/federation/saml/begin`) remains for
integrations that host their own callback; it runs the same access policy and MFA gate and answers
`mfaPendingToken` when a second factor is needed.

### SAML applications (AuthCenter as identity provider)

Applications that only speak SAML 2.0 sign in with AuthCenter as their identity provider. Each one
is registered under `/api/saml/service-providers` (`AUTHCENTER_SAML_APPS_READ`/`_WRITE`; roles that
held `AUTHCENTER_OAUTH_CLIENTS_*` received them on upgrade) or in the console (**Aplicaciones
SAML**), usually from its metadata: `POST /api/saml/service-providers/parse-metadata` reads the
entity ID, the HTTP-POST assertion consumer services, the single logout URL, the NameID format and
the certificates. A SAML application belongs to an AuthCenter application, and that application's
access, access policy, risk evaluation and MFA apply to every assertion through the same gate as an
OAuth authorization. Registrations carry a `version` (a stale update answers `CONCURRENCY_CONFLICT`)
and their creation accepts `Idempotency-Key`.

What each SAML application registers about AuthCenter (`GET /api/saml/identity-provider`, also
shown in the console):

| Item | Value |
|---|---|
| Metadata | `{Jwt:Issuer}/saml/idp/metadata` |
| Entity ID | `Saml:IdentityProviderEntityId`, by default the metadata URL |
| Single sign-on | `{Jwt:Issuer}/saml/idp/sso` (HTTP-Redirect and HTTP-POST bindings) |
| Single logout | `{Jwt:Issuer}/saml/idp/slo` (HTTP-Redirect and HTTP-POST bindings) |
| Signing certificate | The `Saml:SigningCertificateBase64` certificate; without it the endpoints answer 503 and the console warns |

- **Requests.** An AuthnRequest must come from a registered, active SAML application (by its
  `Issuer`), be recent (5 minutes plus `Saml:ClockSkewSeconds`), name AuthCenter's SSO URL as
  `Destination`, ask for a registered assertion consumer service with the HTTP-POST binding, and it
  is single-use. When the application requires signed requests, or signs one anyway, the signature
  must verify with its certificate (RSA with SHA-256 or SHA-512: the query signature of
  HTTP-Redirect, an enveloped XML signature of the whole request for HTTP-POST). A request that
  fails these checks is answered on an AuthCenter error page and never posted to the address it
  names. Once the application is trusted, a refusal goes back to it as a SAML status
  (`InvalidNameIDPolicy`, `NoPassive`, `RequestDenied`).
- **Sign-in.** Without a single sign-on session, the browser continues at
  `/login?saml_interaction=...`: the same hosted login (password, passkeys, emailed links,
  federation, second factor) with the application's branding and without a consent screen. A
  `RequestedAuthnContext` with a multi-factor class (the REFEDS MFA profile,
  `http://schemas.microsoft.com/claims/multipleauthn`, ...) asks for step-up, `ForceAuthn` requires a
  sign-in made after the request, and `IsPassive` never shows the login (`NoPassive` instead).
- **Assertions.** Always signed (RSA-SHA256, exclusive canonicalization), and the Response too unless
  `SignResponse` is off. With `EncryptAssertions`, encrypted to the application's certificate
  (AES-256-CBC, key wrapped with RSA-OAEP). The NameID is the email, a persistent pseudonym (an HMAC
  of the user id with a per-application salt: stable for the application, not correlatable across
  applications) or the user id (unspecified). Attributes map a SAML name to `email`, `name`,
  `userId`, `roles`, `permissions`, `groups` or `profile:<key>` of the universal directory profile.
  When the session used a second factor, the authentication context is the multi-factor class the
  application asked for (by default the REFEDS MFA profile); otherwise it is
  `PasswordProtectedTransport`. Assertions live `AssertionLifetimeMinutes` (1 to 60,
  default 5).
- **Started by AuthCenter.** With `AllowIdpInitiated`, `GET /saml/idp/sso/initiate/{id}` posts an
  unsolicited response carrying `DefaultRelayState` (after the hosted login when needed); the portal
  offers it as **Abrir** on the application.
- **Logout.** A LogoutRequest from the application (signed when the application requires signed
  requests) ends the AuthCenter session named by its `SessionIndex`, or the browser's, when that
  session belongs to the NameID, and is answered with a signed LogoutResponse at the application's
  single logout URL. Logout is not propagated to the other SAML applications signed in with the same
  session, so each one should also end its own session; OAuth clients of the session still receive
  their back-channel logout.
- Audit events (and event hooks): `SAML_ASSERTION_ISSUED`, `SAML_SSO_DENIED`, `SAML_REQUEST_REJECTED`,
  `SAML_LOGOUT_COMPLETED` and `SAML_SERVICE_PROVIDER_CREATED`/`_UPDATED`/`_DELETED`.

### SCIM and lifecycle automation

Provisioning tokens are created at `/api/provisioning-tokens`; the raw value is returned once.
Send it as `Authorization: Bearer acp_...` to the SCIM 2.0 endpoints under `/scim/v2`. Tokens are
application-bound and use separate read/write scopes: a revoked or expired token answers 401, a
token without the operation's scope 403 (`WWW-Authenticate: Bearer error="insufficient_scope"`).

- Discovery: `ServiceProviderConfig`, `ResourceTypes` and `Schemas` (core User and Group, and the
  enterprise User extension) are public.
- Users and Groups support GET, POST, PUT (replace), PATCH and DELETE. PATCH accepts the shapes
  identity providers send: operations with a path (`name.givenName`, `emails[type eq "work"].value`,
  `urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department`, `members[value eq "id"]`)
  and without one (an object of attributes, extension URNs flattened or nested); `op` is
  case-insensitive and `active` may come as text. Removing members with a value list, replacing the
  whole list and adding to it all work. PUT replaces the resource (an omitted `externalId` or mapped
  attribute is cleared; an omitted `active` is kept). DELETE deprovisions rather than erasing identity
  history.
- Lists filter with `userName`, `emails`, `externalId`, `displayName` or `id` `eq`, sort with
  `sortBy`/`sortOrder` (`userName`, `displayName`, `externalId`, `id`, `meta.created`,
  `meta.lastModified`) and page with `startIndex`/`count` (at most 200; `count=0` returns only the
  total). `attributes` and `excludedAttributes` project any response.
- Every resource carries `meta.version`, also sent as a weak `ETag`: `If-None-Match` answers 304 to a
  GET of an unchanged resource, and `If-Match` on PUT, PATCH or DELETE answers 412 when the resource
  changed since. Creations answer 201 with `Location`.
- Attributes AuthCenter does not keep are ignored unless a profile mapping of the application reads
  them; mapped attributes are validated against the profile schema (`invalidValue` otherwise), returned
  at their SCIM path and part of the resource version. User writes are atomic.

Each request made with a known token is recorded for the console (`GET
/api/provisioning-tokens/{id}/diagnostics` summarizes the last 24 hours and 7 days and the kinds of
failure; `GET /api/provisioning-tokens/{id}/requests?outcome=failed` lists them) with method, path,
status, SCIM error type and detail, duration and trace id, never the payload. Records are kept for
`Retention:TokenHistoryDays`.

`GET /api/provisioning-tokens` and `GET /api/provisioning-tokens/{id}` return only metadata,
including application, scopes, lifecycle status, expiration and last use. Rotation and revocation
require `admin.provisioning-token.rotate` or `admin.provisioning-token.revoke`; neither endpoint can
recover an existing raw token.

Profile mappings and dynamic group rules are managed under `/api/lifecycle`. The administrative
contract supports paginated list/detail, validation, versioned update, JSON-path simulation and a
paginated preview of users affected by a group rule. Rules store their operator and the expected
value in the attribute's canonical JSON; creating, editing, deactivating or deleting one recomputes the
group for the whole directory, and every profile change (SCIM or administrator) recomputes the user's
rule-managed groups. Rules read the stored profile value: a schema default does not count. An authoritative
mapping prevents other sources from overwriting its target attribute. Group membership immediately
feeds existing application/role assignments and invalidates stale entitlement sessions.

Event hooks are managed at `/api/event-hooks` (`AUTHCENTER_EVENT_HOOKS_READ`/`_WRITE`). A hook
subscribes to event types from `GET /api/event-hooks/event-types` (the audited actions, grouped by
area) or to `*`; an unknown type is rejected with `UNKNOWN_EVENT_TYPE`. Every audit record, whichever
service writes it, queues a delivery for the active, verified hooks subscribed to it, in the same
transaction. The endpoint must be public HTTPS and echo the verification challenge before delivery
is enabled. Deliveries include `X-AuthCenter-Event-Id`, `X-AuthCenter-Idempotency-Key`,
`X-AuthCenter-Timestamp`, and `X-AuthCenter-Signature: v1=<hex-hmac-sha256>`. Consumers should
verify the signature over `<timestamp>.<raw-body>`, reject stale timestamps, and deduplicate by
event ID. `POST /api/event-hooks/{id}/rotate-secret` (step-up `admin.event-hook.rotate-secret`)
returns the new secret once; for 24 hours each delivery carries both signatures
(`v1=<new>,v1=<previous>`) so receivers can switch without dropping events. Failed deliveries retry
and end in dead letter (`GET /api/event-hooks/deliveries?status=dead-letter`), filterable by hook,
status, event, type and UTC range; `GET /api/event-hooks/deliveries/{id}` adds the signed payload.
Replay accepts `Idempotency-Key`; repeating the same key is safe.

### Administrative operations and System Log

`GET /api/admin-dashboard` aggregates directory, integration, delivery and security-posture
indicators without returning user-level data: pending access requests, rejected sign-ins of the
last 24 hours (passwords, lockouts, MFA codes, passkeys and federation) and high-risk sign-ins among
them. `GET /api/audit-logs` filters by user, application, action, trace, entity
(`entityName`/`entityId`) and UTC range and names each event's actor. `GET /api/audit-logs/export`
exports the newest 10,000 matching events as CSV (paging is ignored; `X-Total-Count` gives the
number of matches and `X-AuthCenter-Export-Truncated: true` marks a capped export), neutralizes
spreadsheet formulas and records `SYSTEM_LOG_EXPORTED`. Administrative successes and rejected writes
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
- Access tokens include `role`, `permissions`, and `applications` claims scoped to the application
  used for login, and carry the RFC 9068 header `typ: at+jwt` so resource servers can reject ID
  tokens presented as bearer tokens. Tokens issued before this contract used the .NET URI
  `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`; `AuthCenter.Client` accepts both.
- Endpoints are protected with `[Authorize(Policy = "PERMISSION_CODE")]`. AuthCenter's own
  administration permissions only count in a session issued for the `AUTHCENTER` application, and
  the `AUTHCENTER_` code prefix is reserved to it: permission codes are unique per application, so
  another application's look-alike permission must never open the administration API.
- Refresh tokens are bound to the application they were issued for.
- Effective application access and claims are the union of active direct assignments and active
  directory-group assignments; duplicates are removed.

### Default Seed Data

The `AUTHCENTER` application is seeded automatically with:
- **SuperAdmin** role (all permissions)
- **Admin** role: manages users, groups, access policies, the profile schema and access governance;
  reads applications, roles, permissions, the System Log, event hooks, federation and provisioning tokens
- All permissions enumerated under `AUTHCENTER_*`. Event hooks, federation and provisioning have
  their own `AUTHCENTER_EVENT_HOOKS_*`, `AUTHCENTER_FEDERATION_*` and `AUTHCENTER_PROVISIONING_*`
  permissions; roles that held `AUTHCENTER_APPLICATIONS_*` before them received them on upgrade. SAML applications have
  `AUTHCENTER_SAML_APPS_*`, received on upgrade by the roles that held `AUTHCENTER_OAUTH_CLIENTS_*`;
  access governance has `AUTHCENTER_GOVERNANCE_*`, received by the roles that held `AUTHCENTER_USERS_*`

## Running Tests

```bash
dotnet test
# Hosted pages: unit tests, then the end-to-end suite against the Release API, SQL Server and Chromium
cd tests/AuthCenter.HostedUi.Tests && npm ci && npm test
AUTHCENTER_RELATIONAL_TEST_CONNECTION="Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=true" npx playwright test
```

The end-to-end suite builds nothing: run `dotnet build -c Release` and, for the console specs,
`npm run build` in `src/AuthCenter.Admin` first. It starts the API in Development on
`http://localhost:5071` with a new database, an RSA key and a SAML certificate (made with `openssl`)
generated for the run, rate limiting off
and emails written to a pickup directory, and drives the login, the portal, the emailed-link pages
(with a virtual WebAuthn authenticator), SAML sign-in and the administration console against the real API, with
axe checks (`HOSTED_UI_CHROMIUM` can point at a local Chromium). The same variable runs the SQL
Server integration tests of `dotnet test`. The console's own Playwright suite (`npm run test:e2e`)
uses mocked responses and checks every route with axe (including the WCAG 2.2 `target-size` rule)
on desktop and mobile, and its reflow at 320 pixels. The hosted suite's `mobile` project (a phone
at 390 pixels) checks that the sign-in, the emailed-link pages, the sign-out and every portal panel
reflow down to 320 pixels without horizontal scrolling.

Coverage and capacity, as CI runs them:

```bash
dotnet tool restore
dotnet test --settings coverlet.runsettings --collect:"XPlat Code Coverage" --results-directory ./coverage
dotnet reportgenerator -reports:"coverage/**/coverage.cobertura.xml" -targetdir:coverage/report -reporttypes:"Cobertura;HtmlSummary"
python3 scripts/ci/coverage-gate.py coverage/report/Cobertura.xml 80 60
cd src/AuthCenter.Admin && npm run test:coverage && npm run build && npm run budget
# The large-directory test (SQL Server): 20,000 users by default
AUTHCENTER_SCALE_TESTS=1 AUTHCENTER_SCALE_USERS=100000 dotnet test tests/AuthCenter.IntegrationTests --filter Category=Scale
```

## Repository Notes

The repository ignores generated `bin/` and `obj/` directories, logs, local appsettings files,
private-key/certificate formats, publish profiles, and local assistant/tooling state. Keep runtime
secrets in user-secrets locally and in a managed secret store in deployed environments. GitHub
Actions receives Azure identity values and its CI database password through repository secrets;
the App Service name and slot are repository variables. CI scans the tracked tree with Gitleaks.
