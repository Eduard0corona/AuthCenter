# Improvements backlog

Findings from a review of performance, security and scalability. Operational and deployment work
lives in [TODO.md](TODO.md); this file is only about the code.

Ordered by impact. The three marked **top** are independent of each other and are the ones worth
doing first.

## Blocking a second instance

- [ ] **top** — Move single-use security state out of `IMemoryCache`. Four anti-replay mechanisms
      keep their state in process memory, so with more than one instance the protection disappears
      silently (a consumed magic link or MFA pending token replayed against another instance is
      accepted), and a restart clears it too:
  - MFA pending token single-use — `AuthService.cs:474`, `AuthService.cs:535`
  - Magic link single-use — `AuthService.cs:804`, `AuthService.cs:841`
  - Email OTP codes — `TotpService.cs:253`, `TotpService.cs:319`
  - OAuth interaction sessions — `OAuthAuthorizationService.cs:90`

  OAuth authorization codes already do this correctly: stored hashed in the database. These four
  should follow the same pattern, or move to `IDistributedCache` backed by Redis.

- [ ] **top** — Add `UseForwardedHeaders`. Every rate limit partitions on
      `httpContext.Connection.RemoteIpAddress`, which behind App Service or any reverse proxy is
      the balancer's address, not the client's. Two consequences: every user shares one rate limit
      partition, so five requests can lock out everyone's login; and the `IpAddress` recorded in
      the audit log is useless for investigating an incident. Note `UseHttpsRedirection` is
      currently inert in the container (no HTTPS port configured, verified against the Compose
      stack); configuring one before fixing forwarded headers would cause a redirect loop.

- [ ] Rate limiters are in-memory, so limits multiply by instance count. Needs a distributed
      limiter once there is more than one instance.

## Security

- [ ] No rate limiting on `/oauth/token` (`OAuthController.cs:59`). There is no global limiter
      either — only named policies on specific `AuthController` endpoints. This is where
      `client_secret` and refresh tokens are validated, so it is the natural target for brute
      force. User login is protected and machine login is not.
- [ ] `client_secret` comparison is not constant-time — `OAuthAuthorizationService.cs:164`, `:258`,
      `:296` use `string.Equals`. Should be `CryptographicOperations.FixedTimeEquals`. Low severity
      in practice since hashes are compared, but free to fix.
- [ ] Access tokens cannot be revoked. Revoking a session only kills the refresh token; the access
      token stays valid for up to 15 minutes. This is inherent to stateless JWTs and is probably
      the right trade-off, but `DELETE /api/auth/sessions` implies an immediacy it does not
      deliver — decide and document it, or add a `jti` revocation list checked in middleware.
- [ ] No global query filter for `DeletedAt`. It works because every query remembers to filter
      (`UserAccessService.cs:40`), but it only has to be forgotten once to expose deleted accounts.
- [ ] `/health` is anonymous and reports database status. Minor disclosure; move it to an internal
      port or authenticate it.

## Performance and resilience

- [ ] No `EnableRetryOnFailure` on the SQL Server provider
      (`InfrastructureServiceExtensions.cs:17-26`). Azure SQL drops connections routinely
      (throttling, failover) and without a retry strategy that surfaces as sporadic 500s. Best
      value-per-line fix in this list.
- [ ] Nothing prunes the append-only tables. There is no `BackgroundService` in the project, so
      `RefreshTokens`, `OAuthAuthorizationCodes`, `UserTrustedDevices` and `AuditLogs` grow without
      bound; revoked tokens and expired codes are never deleted. `RefreshTokens` is the one that
      degrades first under real traffic.
- [ ] User search causes a table scan. `UserAccessService.cs:48` uses `Contains`, which becomes
      `LIKE '%x%'` — not sargable, so the `IX_AspNetUsers_FullName` index cannot be used. Fine at
      small scale. `StartsWith` would use the index if prefix search is acceptable; otherwise this
      needs full-text search.
- [ ] Serilog writes to a file in production (`appsettings.json:46-53`). In a container those logs
      are ephemeral and add disk I/O per request. Console only is the usual container setup.
- [ ] `/health` mixes liveness and readiness — it includes the SQL Server check, so a database
      blip restarts the application if the platform uses it as a liveness probe. Split into
      `/health/live` (no dependencies) and `/health/ready` (with SQL).

## Correctness

- [ ] `Jwt:RefreshTokenDays` is never read. It exists in `JwtSettings`, in both appsettings files
      and in the README, but `RefreshTokenService.cs:28` hardcodes `AddDays(30)`. Changing the
      setting does nothing, which is worse than not having it.

## Structure

- [ ] `AuthService` is a 977-line god class with 18 dependencies, covering registration, password
      login, four social providers, MFA orchestration, magic links, password reset and forced
      password change. The next largest file is 543 lines. Splittable by use case without changing
      the public API.
- [ ] Application logic lives in Infrastructure. `AuthCenter.Application` holds only interfaces,
      validators and models, so business rules cannot be tested without EF Core and Identity —
      which is why all coverage of these flows is integration rather than unit. Worth addressing
      only if test speed starts to hurt.

## Verified as already sound

Recorded so a future review does not re-flag them: the user listing avoids N+1 with a grouped role
query (`UserAccessService.cs:71-81`); `AuditService` uses its own `DbContextFactory` so it does not
flush the caller's unit of work; OAuth authorization codes, refresh tokens and trusted device
tokens are all stored hashed with covering indexes; PKCE is enforced per client.
