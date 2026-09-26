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
   - ApprovalRequired -> IsActive = false, plus the AccessRequest behind it (source Registration,
     asking for the default role); the application's owners are emailed. An owner decides it from
     the portal, or an administrator from the console queue or the users page; nobody decides their
     own request and separation of duties is checked first. A rejected request leaves the access
     revoked, never pending.
7. Assign DefaultRole if configured.
8. If RequireEmailConfirmation is true, send the confirmation link (same transaction).
9. Commit. The first sign-in runs after the commit: its services (risk signals, sessions) write
   through their own connections and would otherwise wait on the uncommitted user row.
10. If RequireEmailConfirmation, return EMAIL_CONFIRMATION_REQUIRED; if ApprovalRequired, return
    APPROVAL_REQUIRED.
11. Run the application's gate (access policy, MFA). A factor to enroll (MFA_SETUP_REQUIRED) or a
    denial keeps the account; otherwise build and return AuthResponse.
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

## Emailed links and the hosted pages

With `ActionLinks:DefaultBaseUrl` (or `ActionLinks:ApplicationBaseUrls:{code}`) pointing at
AuthCenter, the links in its emails open hosted pages:

| Link | Page action |
|---|---|
| `/reset-password?token&email&application` | New password → `POST /api/auth/reset-password` (closes every session) |
| `/accept-invitation?token&email&application` | First password → `POST /api/auth/reset-password` |
| `/confirm-email?token&email&application` | Confirm → `POST /api/auth/confirm-email` |
| `/confirm-email-change?token&email&userId` | Confirm → `POST /api/auth/email-change/confirm` |
| `/magic-link?token&email&application` | Sign in → `POST /ui-api/session/magic-link {token}` |

The page removes the token from the address bar before anything else, is served with
`Referrer-Policy: no-referrer` and `Cache-Control: no-store`, and acts only on a click, so link
scanners that open it do not consume the token. A magic link requested from the hosted login
continues, in the same browser, the authorization request or return URL it was requested from.

## Hosted sign-in steps

```
POST /ui-api/session/login | passkey/complete | magic-link | federation/complete | forced-change
  -> { user, csrfToken }                                     signed in
  -> { requiresMfa, mfaPendingToken, mfaMethod }             POST /ui-api/session/mfa
                                                             (email factor: POST /ui-api/session/mfa/email-otp)
  -> { requiresMfaEnrollment, enrollmentToken }              POST /ui-api/session/mfa/enrollment/start -> totpUri, secret
                                                             POST /ui-api/session/mfa/enrollment/complete -> session + backup codes
  -> { requiresPasskeyEnrollment, enrollmentToken }          POST /ui-api/session/passkey/enrollment/options|complete,
                                                             then a passkey sign-in
  -> { requiresPasswordChange, passwordChangeToken }         POST /ui-api/session/forced-change
```

1. Enrollment tokens are single-use, bound to the user, the application and the factor, and expire
   with twice the MFA window. JSON API clients receive a fixed message instead of the token.
2. A pending second factor allows five attempts; it is spent by the first success or the fifth
   failure. Each authenticator code is accepted once (its time step is remembered).
3. The OAuth step-up (`POST /oauth/interactions/{id}/step-up`) answers the same steps, so a user
   who lacks the factor the client's application requires enrolls it in place and the request
   completes on the same single sign-on session.
4. Direct visits read `GET /ui-api/session/login-options?applicationCode=` (password, magic link,
   federation); authorization requests read the interaction context, whose `expiresAt` the page
   uses to tell the user when the request expired.

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

## APIs, token exchange and introspection

```
Client -> GET /oauth/authorize?...&scope=openid orders.read&resource=https://orders.example.com/api
Client -> POST /oauth/token  grant_type=authorization_code ... [resource=https://orders.example.com/api]

1. orders.read is an API scope of the catalog; resource, when sent, must name exactly the APIs of
   the requested API scopes (invalid_target otherwise).
2. The user needs access to the application that owns the API.
3. The access token audience is the API (plus urn:authcenter:userinfo with openid) and its roles
   and permissions are those of the API's application. A grant covering several APIs requires
   resource at the token endpoint, including on refresh; each token has one API audience.

API -> POST /oauth/token  grant_type=urn:ietf:params:oauth:grant-type:token-exchange
          subject_token=<user token the API received>
          subject_token_type=urn:ietf:params:oauth:token-type:access_token
          resource=https://shipping.example.com/api

4. Only a confidential client with that grant, of the application owning the subject token's API
   (or the client the token was issued to), can exchange it. The new token keeps the user as sub,
   names the caller in act, is limited to the caller's scopes for the target API and never
   outlives the subject token.

API -> POST /oauth/introspect token=<token>   (client_secret_basic or client_secret_post)

5. active is true only for the token's client or the APIs of the caller's application, and only
   while the user and the single sign-on session (sid) behind the token are active.
```

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

## Enterprise federation from the hosted login

```
Client -> GET /oauth/authorize?...&idp=<provider id>        (or &domain_hint=contoso.com)
AuthCenter -> 302 /login?interaction_id=...
Hosted login:
  GET  /oauth/interactions/{id}/context        -> identityProvider / domainHint / federationAvailable
  POST /ui-api/session/federation/discover     -> { federated, provider }   (domain_hint or typed email)
  POST /ui-api/session/federation/start        -> { redirectUrl }           (bound to this browser)
Browser -> upstream IdP (OIDC code + PKCE + nonce, or signed SAML AuthnRequest)
IdP -> GET  /api/federation/oidc/callback?code&state      (OIDC)
    -> POST /api/federation/saml/acs  SAMLResponse+RelayState (SAML, HTTP-POST binding)
AuthCenter validates the upstream response, links or provisions the user, syncs mapped groups,
stores a single-use result for the starting browser and answers 303 /login?...&federation_result=H
Hosted login:
  POST /ui-api/session/federation/complete {handle: H}
     -> access policy + MFA gate of the application -> session cookie, requiresMfa, or an error
  POST /oauth/authorize/complete -> code for the client (as after a password sign-in)
```

1. `idp` must be an active provider of the client's application; `domain_hint` must be a domain.
   Both are optional hints; an existing single sign-on session still answers the client directly.
2. Home realm discovery for anonymous callers only matches email-domain rules. Group and profile
   conditions apply only when the browser is already signed in as that email's user.
3. The callback/ACS never create a session. The result can only be redeemed once, within five
   minutes, from the browser whose `__Host-AuthCenter.Browser` cookie started the sign-in, so an
   attacker cannot plant their own upstream sign-in in a victim's browser.
4. The redeemed sign-in goes through the same gate as a password: active access, the published
   access policy, the application's `RequireMfa` and the user's MFA. With `TrustUpstreamMfa`, an
   upstream MFA satisfies it (`amr` `fed mfa`, `acr` `urn:authcenter:acr:mfa`); otherwise the
   hosted login asks for AuthCenter's second factor (`amr` `fed otp mfa`).
5. Requests that need a fresh sign-in (`prompt=login`, `select_account`, `max_age`) ask the IdP to
   authenticate again (`prompt=login` / `ForceAuthn="true"`).
6. A direct sign-in (no authorization request) sends `applicationCode` and a local `returnUrl`;
   any other return URL is dropped.

### Linking a provider from the portal

```
Portal (signed in) -> POST /api/auth/reauth/password {purpose: account.link-provider}
                   -> POST /ui-api/session/federation/start {providerId, applicationCode, returnUrl: "/portal#providers", link: true}
                      (X-AuthCenter-Reauthentication: proof)
Browser -> upstream IdP -> callback/ACS -> 303 /portal?federation_link=H#providers
Portal -> POST /ui-api/session/federation/link {handle: H} -> linked provider
```

The upstream identity is linked to the account that started the link: never by email, only from
the same browser, only while that account is still signed in there (`FEDERATION_LINK_USER_MISMATCH`)
and only if another account does not already use it (`FEDERATION_IDENTITY_IN_USE`). A link result
cannot be redeemed as a sign-in, nor a sign-in result as a link.

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
