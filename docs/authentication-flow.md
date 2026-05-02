# AuthCenter — Authentication Flows

## Register with Email/Password

```
Client → POST /api/auth/register
         { fullName, email, password, applicationCode }

1. Validate request (FluentValidation).
2. Load ApplicationSystem by code. Return 400 if not found or inactive.
3. Check ApplicationRegistrationSettings:
   - AllowPasswordLogin must be true.
   - RegistrationMode must not be Closed or InviteOnly.
4. Verify email is not already taken.
5. Create ApplicationUser via UserManager.CreateAsync.
6. Grant UserApplicationAccess:
   - Open → IsActive = true.
   - ApprovalRequired → IsActive = false.
7. Assign DefaultRole if configured.
8. Record AuditLog.
9. If ApprovalRequired → return 400 APPROVAL_REQUIRED (no tokens).
10. Build and return AuthResponse (access token + refresh token).
```

## Login with Email/Password

```
Client → POST /api/auth/login
         { email, password, applicationCode }

1. Validate request.
2. Find user by email. Return 401 if not found or IsActive=false.
3. Verify password via UserManager.CheckPasswordAsync.
   - On failure: increment AccessFailed, return 401.
4. Load ApplicationSystem. Return 401 if not found or inactive.
5. Verify UserApplicationAccess IsActive for that application. Return 401 if none.
6. Reset AccessFailedCount.
7. Update LastLoginAt.
8. Record AuditLog (success).
9. Return AuthResponse.
```

## Refresh Token

```
Client → POST /api/auth/refresh-token
         { refreshToken }

1. SHA256-hash the incoming token.
2. Query RefreshToken by TokenHash.
3. If not found → 401 INVALID_TOKEN.
4. If RevokedAt is set → revoke ALL tokens for this user (reuse attack), return 401.
5. If ExpiresAt < now → return 401 TOKEN_EXPIRED.
6. If User.IsActive = false → return 401.
7. Generate new refresh token (plain + hash).
8. Set old token: RevokedAt = now, ReplacedByTokenHash = new hash.
9. Create new RefreshToken in DB.
10. Build new access token with current roles/permissions/applications.
11. Return AuthResponse with new access token and new plain refresh token.
```

## Logout

```
Client → POST /api/auth/logout  (requires JWT)
         { refreshToken? }

1. Require authentication.
2. If refreshToken provided: hash it, find by hash, revoke if it belongs to this user.
3. Record AuditLog.
4. Return 200 OK. (Client must discard JWT client-side.)
```

## Authorization by Permission

```
1. User logs in → JWT issued with claims:
   - sub, email, name, jti
   - roles: ["SuperAdmin"]
   - permissions: ["AUTHCENTER_USERS_READ", "AUTHCENTER_USERS_WRITE", ...]
   - applications: ["AUTHCENTER"]

2. Client sends request:
   Authorization: Bearer <access_token>

3. PermissionPolicyProvider intercepts [Authorize(Policy = "AUTHCENTER_USERS_READ")].
   Creates PermissionRequirement("AUTHCENTER_USERS_READ").

4. PermissionAuthorizationHandler checks:
   user.Claims where type == "permissions" and value == "AUTHCENTER_USERS_READ"

5. If found → Succeed. If not → Forbid (403).
```

## Token Lifecycle

```
Access Token:  15 minutes (configurable via Jwt:AccessTokenMinutes)
Refresh Token: 30 days    (configurable via Jwt:RefreshTokenDays)

Refresh tokens are stored hashed (SHA-256) in the database.
Plain tokens are only returned in API responses — never stored in plain text.
```
