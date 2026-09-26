# AuthCenter — Architecture

## Overview

AuthCenter is built with Clean Architecture. The solution is divided into five layers with strict dependency rules.

```
src/
├── AuthCenter.Domain          ← Core business entities and enums
├── AuthCenter.Contracts       ← Shared DTOs for API input/output
├── AuthCenter.Application     ← Interfaces, use-case logic, validators
├── AuthCenter.Infrastructure  ← EF Core, Identity, JWT, external services
└── AuthCenter.Api             ← Controllers, middleware, DI composition root
```

## Layer Responsibilities

### AuthCenter.Domain
- Contains all business entities: `ApplicationUser`, `ApplicationRole`, `ApplicationSystem`,
  `DirectoryGroup`, group assignments, `Permission`, `RefreshToken`, etc.
- Contains `ApplicationRegistrationMode` enum and `DomainConstants`.
- **Has no dependencies on other layers.**
- References `Microsoft.Extensions.Identity.Core` only to allow `ApplicationUser : IdentityUser<Guid>`.

### AuthCenter.Contracts
- Contains request/response DTOs shared between the API and external consumers.
- No business logic. No infrastructure dependencies.
- Depends on: **nothing**.

### AuthCenter.Application
- Defines all service interfaces (`IAuthService`, `ITokenService`, `IGoogleAuthService`, etc.).
- Contains FluentValidation validators for all incoming requests.
- Contains `OperationResult<T>` for typed, non-throwing service outcomes.
- Contains application exception types (`NotFoundException`, `ConflictException`, `ForbiddenException`).
- Depends on: **Domain**, **Contracts**.

### AuthCenter.Infrastructure
- Implements all interfaces defined in Application.
- Contains `AuthCenterDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>`.
- All entity configurations via `IEntityTypeConfiguration<T>`.
- JWT token generation with `System.IdentityModel.Tokens.Jwt`.
- Google ID Token validation with `Google.Apis.Auth`.
- Refresh token rotation with SHA-256 hashing.
- `AuthCenterSeeder` for initial data.
- Depends on: **Application**, **Domain**, **Contracts**.

### AuthCenter.Api
- ASP.NET Core Web API with Controllers (no Minimal APIs).
- `Program.cs` is the DI composition root.
- Contains `ExceptionHandlingMiddleware` mapping exceptions to HTTP status codes.
- `PermissionPolicyProvider` + `PermissionAuthorizationHandler` for claim-based permission policies.
- `CurrentUserService` extracts the authenticated user from `HttpContext`.
- Depends on: **Application**, **Infrastructure**, **Contracts**.

## Dependency Rules

```
Api ──────────────────────────→ Application
 │                                    │
 ├──────────────────────────→ Infrastructure
 │                                    │
 └──────────────────────────→ Contracts
                                      │
                                   Domain
```

No layer may reference a layer above it. Infrastructure must not reference Api.

## Key Design Decisions

- **No MediatR**: Services are injected and called directly. This keeps the call graph explicit and avoids ceremony for a service with focused scope.
- **OperationResult pattern**: Services return `OperationResult<T>` instead of throwing for expected failures. Only truly exceptional cases (entity not found after an assumption) throw exceptions caught by middleware.
- **Refresh token rotation**: Every use of a refresh token creates a new one and revokes the previous. Detected reuse (using a revoked token) triggers revocation of all tokens for the user.
- **Permission policies**: `PermissionPolicyProvider` intercepts any policy name and creates a `PermissionRequirement` from it, enabling `[Authorize(Policy = "AUTHCENTER_USERS_READ")]` without registering each permission manually.
- **Configuration validation**: non-development environments fail fast when critical connection, JWT, and CORS settings are missing or still use placeholder values.
- **Integration testing**: API integration tests run against EF Core InMemory and seed the same default `AUTHCENTER` application, roles, permissions, and admin flow used by the application seed.
- **Application-scoped tokens**: login and refresh issue JWT roles, permissions, and application claims scoped to the requested application, preventing cross-application permission leakage.
- **Effective group entitlements**: group membership grants access only through an explicit
  application assignment; group roles must belong to that application. Role and permission queries
  union direct and group-derived entitlements, and entitlement changes revoke affected sessions.
- **Universal directory profiles**: stable custom-attribute definitions own type and bounded
  validation constraints. Values are canonical JSON linked by definition/user, required attributes
  always have a valid default, and incompatible schema edits are rejected before existing data can
  be invalidated.
- **Versioned fail-closed application policies**: one editable draft per application is isolated
  from runtime. Published versions are immutable and rules evaluate user, group, CIDR, UTC schedule,
  risk and assurance in priority order. Publishing atomically archives the previous version, audits
  the transition and revokes sessions. The same evaluator powers an administrative dry-run with
  per-rule explanations, so simulation and production cannot drift.
- **Passkeys**: ASP.NET Core Identity WebAuthn validates exact RP/origin, challenge, signature,
  user-verification flags and replay counter. Ceremony application context is single-use and
  distributed; verified assertions issue the same application-scoped JWT/refresh sessions as other
  authentication methods and satisfy an MFA policy without weakening its assurance.
- **Step-up and adaptive risk**: sensitive operations use opaque, random, short-lived, single-use
  proofs bound to a user and closed purpose; only the proof hash is retained. A user-verified
  passkey raises assurance to `PhishingResistant`. Adaptive observations retain HMAC-protected
  network prefixes and device families instead of raw values, detect new contexts, failure bursts
  and impossible travel, and expire automatically.
- **Enterprise federation**: providers are data scoped to an application. OIDC uses server-side
  authorization-code redemption, PKCE, nonce/state, discovery and cached JWKS with forced refresh
  after signing-key failure; client secrets are protected with shared Data Protection keys. SAML
  emits signed Redirect-binding AuthnRequests and metadata, accepts only a signed correlated POST
  response with one assertion, exact issuer/audience/destination, bounded clock skew and SQL-backed
  replay protection. Routing evaluates application, email domain, directory group and canonical
  profile value before audited JIT provisioning or explicitly configured verified-email linking.
- **Lifecycle automation**: SCIM provisioning tokens are random, stored only as SHA-256 hashes,
  scoped per application/resource/action, expiring and rotatable. SCIM Users/Groups enforce
  application boundaries, bounded `eq` filters and pagination, PATCH allowlists, deprovisioning and
  entitlement-session invalidation. Authoritative profile mappings reject writes from other
  sources; dynamic group rules feed the existing group application/role model.
- **Event hooks**: verified public HTTPS destinations receive a minimized event envelope. The body
  is signed with a per-hook HMAC secret protected by Data Protection, and stable event/idempotency
  headers allow consumer deduplication. Audit creation and delivery enqueue share one database
  save; workers claim rows across instances, retry exponentially and expose terminal dead letters
  for explicit replay.
- **First-party web experience**: the hosted login, self-service portal and administrative console
  use an encrypted `HttpOnly`, `Secure`, `SameSite=Strict` cookie selected independently from API
  bearer authentication. Unsafe cookie-authenticated requests require a same-origin CSRF header;
  every administrative API still authorizes effective permissions server-side. Per-application
  branding accepts only bounded colors and HTTPS links, while CSP disallows inline scripts and
  custom CSS. OAuth consent is persisted per user/client/scope and revocation also invalidates the
  affected refresh sessions.
- **Developer platform**: the .NET SDK generates PKCE and configures strict RS256 resource-server
  validation through discovery/JWKS. The dependency-free TypeScript SDK keeps token storage as an
  explicit consumer decision. Executable SPA/web/API quickstarts contain only public example
  values, and a CI conformance profile exercises OIDC and SCIM contracts.
- **Observability and reliability**: OpenTelemetry emits ASP.NET Core, outbound HTTP, runtime and
  bounded platform metrics to Azure Monitor when its Key Vault connection reference is present.
  Custom dimensions are a fixed low-cardinality allowlist and never contain identity, credentials,
  raw URLs or network values. The DbContext attaches the current W3C trace ID to every new audit
  entry, including event-hook envelopes, so operators can move between a request trace and System
  Log without widening the log payload. SLOs, multi-window burn alerts, k6 profiles and guarded
  restart/restore/failover scripts are versioned under `ops/` and `scripts/ops/`.
- **Administrative workflows**: user creation, invitations, pending access approval, role/permission activation, and audit-log search are exposed through permission-protected controllers.
- **Access governance**: application owners, access requests (portal, registrations that require approval, pending access), periodic access reviews with recurrence and separation of duties rules live in `Services/Governance`. Owners decide from the portal and administrators from the console; nobody decides on themselves. Separation of duties is preventive for administrative changes and detective for SCIM, dynamic rules and federation. `GovernanceMaintenanceService` expires requests and completes and repeats reviews, one instance per campaign.
