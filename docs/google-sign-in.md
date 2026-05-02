# AuthCenter — Google Sign-In Flow

## Overview

AuthCenter does not implement OAuth redirects. Instead, it validates a **Google ID Token** sent by the frontend after the user has already signed in on the client side using Google's SDK.

## Prerequisites

1. A Google OAuth 2.0 Client ID configured in Google Cloud Console.
2. The Client ID configured in `Authentication:Google:ClientId`.

## Flow

```
1. Frontend integrates Google Identity Services SDK (or firebase/auth).
2. User clicks "Sign in with Google" → SDK returns a Google ID Token (JWT).
3. Frontend sends ID Token to AuthCenter:

   POST /api/auth/google
   Content-Type: application/json
   {
     "idToken": "<google_id_token>",
     "applicationCode": "AUTHCENTER"
   }

4. AuthCenter validates the ID Token:
   - Uses GoogleJsonWebSignature.ValidateAsync().
   - Validates audience matches Authentication:Google:ClientId.
   - Validates email_verified = true.
   - On failure → 401.

5. AuthCenter resolves or creates the user:
   a. Search ExternalIdentityProviders where Provider=Google AND ProviderUserId=sub.
   b. If found → use existing user.
   c. If not found → search ApplicationUser by email.
      - If email match found → link Google to existing user.
      - If no user exists → create new user (IsExternalUser=true, HasLocalPassword=false).
        Apply RegistrationMode rules of the application.

6. Validate application access:
   - Application must exist and be active.
   - AllowGoogleLogin must be true.
   - User must have active UserApplicationAccess (or just got granted one).

7. AuthCenter issues its own JWT access token and refresh token.
   Google tokens are NEVER forwarded or stored. AuthCenter issues independent tokens.

8. Response:
   {
     "success": true,
     "data": {
       "accessToken": "<authcenter_jwt>",
       "refreshToken": "<plain_refresh_token>",
       "expiresIn": 900,
       "user": { ... }
     }
   }
```

## Security Notes

- AuthCenter only trusts the Google ID Token to verify identity. It does **not** use Google's access tokens.
- `email_verified=false` tokens are rejected.
- The Google ID Token is never stored.
- After validation, AuthCenter issues its own short-lived JWT (15 min) and a rotatable refresh token (30 days).
- The Google ClientId is configured via `Authentication:Google:ClientId` — set via user-secrets in development, environment variable in production.

## Configuration

```json
{
  "Authentication": {
    "Google": {
      "ClientId": "your-google-client-id.apps.googleusercontent.com"
    }
  }
}
```
