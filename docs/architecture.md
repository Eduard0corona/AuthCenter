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
- **Fail-closed application policies**: active rules are evaluated by priority using optional group
  and CIDR conditions. The first matching rule controls allow/deny and MFA behavior; if rules exist
  and none match, access is denied. Policy mutations revoke all sessions for the application and are
  audited atomically with the rule mutation.
- **Administrative workflows**: user creation, invitations, pending access approval, role/permission activation, and audit-log search are exposed through permission-protected controllers.
