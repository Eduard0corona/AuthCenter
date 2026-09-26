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

- [x] Production rate limiting uses serializable SQL buckets shared by every instance; the
      in-memory limiter is restricted to development and testing.

## Security

- [x] Rate limited `/oauth/token`, deliberately looser than the interactive endpoints because a
      machine client legitimately exchanges tokens in bursts.
- [x] `client_secret` is now compared with `CryptographicOperations.FixedTimeEquals`.
- [x] First-party access tokens include `sid` and validate their active session on every request,
      so session revocation is immediate. OAuth access tokens remain deliberately stateless, use a
      15-minute default (one-hour maximum), and UserInfo rechecks client, user and application
      access; this behavior is explicitly documented.
- [x] `ApplicationUser` has a global `DeletedAt` query filter.
- [x] `/health/live` exposes no dependencies, while SQL readiness is registered only for an
      explicitly configured administration host.

## Performance and resilience

- [x] Enabled `EnableRetryOnFailure` with a 30s command timeout on both the scoped context and the
      context factory.
- [x] `RetentionCleanupService` prunes refresh tokens, authorization codes, trusted devices,
      audit logs, outbox records, transient state and distributed rate-limit buckets in batches.
- [x] User search uses indexable prefixes over `FullName` and `NormalizedEmail`.
- [x] Serilog writes to console only; there is no production file sink.
- [x] Split the health endpoint into `/health/live`, which carries no dependencies, and
      `/health/ready`, which reports SQL Server and, since the remediation's F15, whether the
      database has every migration of the build (`database-schema`). `/health` is unchanged.

## Correctness

- [x] `Jwt:RefreshTokenDays` is now honoured; `RefreshTokenService` used to hardcode 30 days.

## Structure

- [x] `AuthService` remains the compatibility coordinator, while session issuance, tokens, MFA,
      application access, roles, providers, external links, action links, account management and
      durable effects are delegated to focused services. Further mechanical file splitting would
      redistribute the coordinator without reducing its remaining coupling.
- [x] EF Core and Identity use-case implementations deliberately remain in Infrastructure behind
      Application interfaces. Pure validation stays in Application, where its unit tests run in
      seconds; moving framework adapters inward would violate the dependency rule without improving
      test feedback.

## Verified as already sound

Recorded so a future review does not re-flag them: the user listing avoids N+1 with a grouped role
query (`UserAccessService.cs:71-81`); `AuditService` uses its own `DbContextFactory` so it does not
flush the caller's unit of work; OAuth authorization codes, refresh tokens and trusted device
tokens are all stored hashed with covering indexes; PKCE is enforced per client.
