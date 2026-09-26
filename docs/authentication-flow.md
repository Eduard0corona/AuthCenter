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

## OAuth 2.0 / OpenID Connect authorization code

Each OAuth client is attached to one `ApplicationSystem`. Register the application first, then
create the client with its `applicationSystemId`, exact redirect URIs, exact scopes and grant
types. Public and browser/mobile clients must never receive or persist a client secret.

```
Client -> GET /oauth/authorize
          ?response_type=code
          &client_id=<registered-client>
          &redirect_uri=<exact-registered-uri>
          &scope=openid email offline_access
          &state=<unpredictable-client-state>
          &nonce=<unpredictable-login-nonce>
          &code_challenge=<base64url-sha256-verifier>
          &code_challenge_method=S256

1. AuthCenter validates client, application, redirect URI, response type, exact scopes, state,
   nonce, PKCE, prompt, max_age, login_hint, id_token_hint, acr_values and response_mode.
2. Single sign-on: when the browser already holds a hosted-login session that satisfies the
   request (no prompt=login/select_account, within max_age, same subject as id_token_hint, consent
   already given), AuthCenter evaluates the client's application for that session (step 5) and, if
   it allows it, answers the client directly. prompt=none never shows a page: it returns
   login_required (no usable session or a step-up is needed), consent_required or access_denied.
3. Otherwise AuthCenter stores a 10-minute interaction bound to this browser and redirects to the
   client login URL (the hosted login, https://<authcenter>/login) with `interaction_id`. The page
   reads GET /oauth/interactions/{interactionId}/context and signs the user in to the client's
   application (its branding, password policy, access policy and MFA).
4. The consent UI calls authenticated GET /oauth/interactions/{interactionId} to render application
   name and requested scopes, then posts `{ interactionId, consent }` to /oauth/authorize/complete.
5. AuthCenter evaluates the client's application for the session: active application access, the
   published access policy (with the browser's address and risk), the application's MFA setting,
   the user's own MFA and the least demanding supported acr_values. A denial goes back to the
   client as access_denied. A session below the required assurance gets STEP_UP_REQUIRED without
   consuming the interaction: the hosted login calls POST /oauth/interactions/{id}/step-up, the user
   verifies a second factor (or a passkey) and the same single sign-on session, with the same sid,
   is strengthened before the request completes.
6. AuthCenter redirects to the exact callback with `code`, original `state` and `iss` (or renders
   an auto-submitted form for response_mode=form_post). Protocol errors use the same callback only
   after its URI is trusted; client/redirect errors are never redirected.
7. The client exchanges the single-use code at /oauth/token with the original verifier. A
   confidential client authenticates with HTTP Basic or form credentials; a public client sends
   only client_id.
8. The access token contains application-scoped roles and permissions. `email` and `name` appear
   only when their scopes permit them. The ID token carries sid, auth_time, amr and acr.
9. `offline_access` creates a rotating refresh-token family with a fixed absolute expiration.
   Reuse of an old member revokes the entire family. Every refresh checks application access and
   the published access policy again (network conditions use the address of the sign-in session),
   and a denial revokes the family.
```

The client must compare returned `state`, validate the ID token signature/issuer/audience/expiry,
and compare `nonce`. Never log authorization codes, client secrets, access tokens, refresh tokens,
PKCE verifiers or ID tokens.

## OpenID Connect logout

```
Client -> GET /oauth/logout
          ?id_token_hint=<id token from this client>
          &post_logout_redirect_uri=<exact registered post-logout URI>
          &state=<client state>

1. AuthCenter validates the ID token (issuer, signature, typ; it may be expired), takes the client
   from its audience (or from client_id) and checks the exact post_logout_redirect_uri.
2. If the ID token belongs to the signed-in user and, when it carries sid, to this session, the
   session ends immediately. Otherwise the browser goes to /logout?logout_id=... and the user
   confirms there (POST /oauth/logout/{logoutId}/confirm with the hosted CSRF token).
3. Ending the session revokes it, revokes every OAuth refresh token it authorized and queues a
   back-channel logout for each client that received tokens through it.
4. The browser returns to post_logout_redirect_uri?state=..., or to /login?signed_out=1.
```

Back-channel logout tokens are posted as `logout_token` to the client's `BackchannelLogoutUri`.
They are JWTs with `typ: logout+jwt`, `iss`, `aud` (client ID), `iat`, `exp` (2 minutes), `jti`,
`sub`, `sid` and `events: {"http://schemas.openid.net/event/backchannel-logout": {}}`, and never a
nonce. Clients must validate them like ID tokens, reject replays by `jti` and end every local
session created from that `sid`. AuthCenter.Client does this at `/auth/backchannel-logout`.

## OAuth client credentials

This grant is only for confidential service identities and does not represent a user. Register a
client with only `client_credentials` and machine scopes; `openid` and `offline_access` are not
valid here. Authenticate to `/oauth/token` with `Authorization: Basic base64(client_id:secret)`.
The returned token contains the linked application claim but no user, roles, permissions or
UserInfo subject.

## OAuth refresh and revocation

Send a refresh token only to `/oauth/token` with `grant_type=refresh_token`. Store it in a secure,
server-side or platform-protected location. Each successful use replaces it. To sign out or react
to suspected compromise, call `/oauth/revoke`; AuthCenter returns success even for an unknown token
and revokes the known token's entire family. Already-issued stateless access tokens remain valid
until their short expiration, so sensitive consumers should keep access-token lifetimes small.

## Migration and rollout

Migration `LinkOAuthClientsToApplicationsAndHardenRefreshFamilies` adds the required application
foreign key and refresh-family metadata. During rollout it maps existing clients by exact
application code or by a `<application-code>-`/`<application-code>_` client-id prefix, preferring
the longest matching application code. It aborts instead of guessing when any legacy client cannot
be mapped. Review client/application mappings before deployment and apply migrations using the
database deployment identity; the App Service runtime identity intentionally has no DDL rights.
