# Improvements backlog

Findings from a review of performance, security and scalability. Operational and deployment work
lives in [TODO.md](TODO.md); this file is only about the code.

Ordered by impact. The three marked **top** are independent of each other and are the ones worth
doing first.

## Blocking a second instance

- [x] Moved single-use security state out of `IMemoryCache` into a `TransientStates` table behind
      `ITransientStateStore`, so it is shared by every instance and survives a restart. A unique
      index on `(Purpose, Key)` is what makes redemption atomic; the pre-check only keeps the
      common path cheap. Covers the MFA pending token, the forced password change token, magic
      links, both email OTP codes and OAuth interaction sessions. Expired rows are swept
      opportunistically, at most once every five minutes per instance.

- [x] Added `UseForwardedHeaders` ahead of the rest of the pipeline, so rate limiting and the
      audit log see the caller rather than the balancer. Note `UseHttpsRedirection` is inert in the
      container (no HTTPS port configured, verified against the Compose stack); with forwarded
      headers now honoured, configuring one no longer risks a redirect loop.

- [ ] Rate limiters are in-memory, so limits multiply by instance count. Needs a distributed
      limiter once there is more than one instance.

## Security

- [x] Rate limited `/oauth/token`, deliberately looser than the interactive endpoints because a
      machine client legitimately exchanges tokens in bursts.
- [x] `client_secret` is now compared with `CryptographicOperations.FixedTimeEquals`.
- [ ] Access tokens cannot be revoked. Revoking a session only kills the refresh token; the access
      token stays valid for up to 15 minutes. This is inherent to stateless JWTs and is probably
      the right trade-off, but `DELETE /api/auth/sessions` implies an immediacy it does not
      deliver — decide and document it, or add a `jti` revocation list checked in middleware.
- [ ] No global query filter for `DeletedAt`. It works because every query remembers to filter
      (`UserAccessService.cs:40`), but it only has to be forgotten once to expose deleted accounts.
- [ ] `/health` is anonymous and reports database status. Minor disclosure; move it to an internal
      port or authenticate it.

## Performance and resilience

- [x] Enabled `EnableRetryOnFailure` with a 30s command timeout on both the scoped context and the
      context factory.
- [ ] Nothing prunes the append-only tables. There is no `BackgroundService` in the project, so
      `RefreshTokens`, `OAuthAuthorizationCodes`, `UserTrustedDevices` and `AuditLogs` grow without
      bound; revoked tokens and expired codes are never deleted. `RefreshTokens` is the one that
      degrades first under real traffic. (`TransientStates` is exempt: it sweeps itself.)
- [ ] User search causes a table scan. `UserAccessService.cs:48` uses `Contains`, which becomes
      `LIKE '%x%'` — not sargable, so the `IX_AspNetUsers_FullName` index cannot be used. Fine at
      small scale. `StartsWith` would use the index if prefix search is acceptable; otherwise this
      needs full-text search.
- [ ] Serilog writes to a file in production (`appsettings.json:46-53`). In a container those logs
      are ephemeral and add disk I/O per request. Console only is the usual container setup.
- [x] Split the health endpoint into `/health/live`, which carries no dependencies, and
      `/health/ready`, which reports SQL Server. `/health` is unchanged.

## Correctness

- [x] `Jwt:RefreshTokenDays` is now honoured; `RefreshTokenService` used to hardcode 30 days.

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
