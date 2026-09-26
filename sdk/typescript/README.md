# @authcenter/client

Cliente OAuth 2.0 / OpenID Connect sin dependencias de ejecución para AuthCenter: inicio de sesión
con Authorization Code + PKCE, procesamiento del callback con validación del ID token (firma RS256
contra el JWKS, `iss`, `aud`/`azp`, `exp`/`iat`, `nonce` y `max_age`), refresh, revocación,
UserInfo y logout iniciado por la aplicación. Los endpoints se leen del documento de discovery,
por lo que funciona también con un `authority` que incluye path base (`https://host/identity`).

No persiste tokens: una SPA debe mantener el access token en memoria. Para refresh tokens de larga
vida se recomienda el BFF (`AuthCenter.Client` para .NET) o un backend propio con cookie
`HttpOnly`.

## Instalación

```sh
npm install @authcenter/client
```

## Uso en una SPA

```ts
import { AuthCenterClient, AuthCenterError } from "@authcenter/client";

const client = new AuthCenterClient({
  authority: "https://identity.example.com",
  clientId: "my-spa",
  redirectUri: `${location.origin}/callback`,
  postLogoutRedirectUri: `${location.origin}/`,
  scopes: ["openid", "profile", "email"]
});

// Iniciar sesión (state, nonce y el verificador PKCE quedan en sessionStorage hasta el callback).
await client.signInRedirect({ returnTo: "/pedidos" });

// En /callback
try {
  const { tokens, claims, returnTo } = await client.handleCallback();
  // claims: sub, email, name, sid, auth_time, amr, acr… ya validados.
} catch (error) {
  if (error instanceof AuthCenterError && error.code === "login_required") { /* prompt=none sin sesión */ }
}

// UserInfo, refresh (si el cliente tiene refresh tokens) y logout global
const profile = await client.userInfo(tokens.access_token);
const refreshed = await client.refresh(tokens.refresh_token!, { resource: "https://orders.example.com/api" });
await client.signOutRedirect({ idTokenHint: tokens.id_token });
```

Opciones de `signInRedirect` / `createSignInUrl`:

| Opción | Parámetro | Uso |
|---|---|---|
| `prompt` | `prompt` | `none` (SSO silencioso: sin sesión el callback lanza `login_required`), `login`, `consent`, `select_account`. |
| `maxAge` | `max_age` | Exige una autenticación reciente; se verifica `auth_time`. |
| `loginHint` | `login_hint` | Prellena el correo del login hospedado. |
| `acrValues` | `acr_values` | Nivel de autenticación, por ejemplo `urn:authcenter:acr:mfa`. |
| `identityProvider` | `idp` | ID del proveedor de federación: el login hospedado va directo a ese IdP. |
| `domainHint` | `domain_hint` | Dominio para descubrir el IdP de la organización. |
| `resources` | `resource` | APIs (RFC 8707) cuyos scopes se piden; el access token tendrá esa audiencia. |
| `returnTo` | — | Ruta local a la que volver; se descarta cualquier otro origen. |

Errores: todo fallo es un `AuthCenterError` con `code` estable (`invalid_state`, `invalid_token`,
`invalid_discovery`, `network_error` o el `error` OAuth de AuthCenter) y `status` HTTP cuando aplica.

## Registro del cliente en AuthCenter

Cliente público con PKCE: redirect URI exacto `https://app.example.com/callback`, post-logout
redirect URI `https://app.example.com/` y el origen `https://app.example.com` en
`AllowedCorsOrigins` (el navegador llama a `/oauth/token`, `/oauth/revoke` y `/oauth/userinfo`).

## Fuera del navegador

En Node.js pasa `storage` (cualquier objeto con `getItem`, `setItem` y `removeItem`) y
`fetch` si lo necesitas; el resto de la API es la misma.
