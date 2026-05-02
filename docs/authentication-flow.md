# AuthCenter - Authentication Flows

## Register with Email/Password

```
Client -> POST /api/auth/register
         { fullName, email, password, applicationCode }

1. Validate request.
2. Load ApplicationSystem by code. Return 400 if not found or inactive.
3. Check ApplicationRegistrationSettings:
   - AllowPasswordLogin must be true.
   - RegistrationMode must not be Closed or InviteOnly.
   - AllowedEmailDomains must allow the email domain when configured.
4. Verify email is not already taken.
5. Create ApplicationUser via UserManager.CreateAsync.
6. Grant UserApplicationAccess:
   - Open -> IsActive = true.
   - ApprovalRequired -> IsActive = false.
7. Assign DefaultRole if configured.
8. If RequireEmailConfirmation is true, send confirmation token and return EMAIL_CONFIRMATION_REQUIRED.
9. If ApprovalRequired, return APPROVAL_REQUIRED.
10. Build and return AuthResponse.
```

## Email Confirmation

```
Client -> POST /api/auth/confirm-email
         { email, token }

1. Load user by email.
2. Confirm email with UserManager.ConfirmEmailAsync.
3. Record audit log.
4. User can now sign in when application access is active.
```

## Invitation

```
Admin -> POST /api/users/invitations
         { fullName, email, applicationSystemId, roleIds?, callbackBaseUrl? }

1. Require AUTHCENTER_USERS_WRITE.
2. Validate target application and allowed email domains.
3. Create user if needed.
4. Grant application access.
5. Assign requested roles when compatible with the application.
6. Send invitation token. The same reset-password endpoint accepts the token and sets the first local password.
```

## Login with Email/Password

```
Client -> POST /api/auth/login
         { email, password, applicationCode }

1. Validate request.
2. Find user by email. Return 401 if not found or IsActive=false.
3. Verify password.
4. Load requested ApplicationSystem with settings.
5. If RequireEmailConfirmation and EmailConfirmed=false, return 401.
6. Verify active UserApplicationAccess for that application.
7. Update LastLoginAt.
8. Return AuthResponse scoped to the requested application.
```

## Refresh Token

```
Client -> POST /api/auth/refresh-token
         { refreshToken, applicationCode? }

1. SHA256-hash the incoming token.
2. Query RefreshToken by TokenHash.
3. Reject missing, revoked, expired, or reused tokens.
4. Resolve the application from the stored ApplicationCode.
5. Reject if caller supplies a different applicationCode.
6. Verify user still has active access to the application.
7. Rotate refresh token and issue a new access token scoped to that application.
```

## Authorization by Permission

```
1. User logs in for one application.
2. JWT includes only roles and permissions valid for that application.
3. PermissionPolicyProvider creates a PermissionRequirement from the policy name.
4. PermissionAuthorizationHandler checks the permissions claim.
```
