# Integración de proyectos con AuthCenter

Guía para conectar una aplicación a AuthCenter: elegir el flujo, registrar el cliente, usar los
SDK y habilitar SSO, logout global, APIs protegidas y federación empresarial. El procedimiento de
alta en producción (aprobaciones, despliegue y rollback) está en
[production-idp-integration.md](production-idp-integration.md). Los proyectos de `samples/` usan
hosts ilustrativos y no contienen credenciales.

## Elección del flujo

- Web con frontend propio: BFF confidencial con Authorization Code + PKCE (`AuthCenter.Client`
  para ASP.NET Core). Es el patrón recomendado: los tokens nunca llegan al navegador.
- API: bearer token RS256 (`typ: at+jwt`) validado mediante discovery/JWKS y la audiencia de la
  API registrada en el catálogo.
- Servicio a servicio: Client Credentials con `resource` y scopes mínimos; una API que llama a otra
  en nombre del usuario usa token exchange (RFC 8693).
- SPA pública sin backend: Authorization Code + PKCE sin secreto (`@authcenter/client`); úsala
  sólo cuando no sea viable un BFF y conserva los tokens únicamente en memoria.

## Paquetes

| Paquete | Plataforma | Instalación |
|---|---|---|
| `AuthCenter.Client` | ASP.NET Core .NET 8 LTS y .NET 10 | `dotnet add package AuthCenter.Client` |
| `@authcenter/client` | Navegador y Node.js ≥ 20, sin dependencias | `npm install @authcenter/client` |

Ambos se publican juntos con la etiqueta `sdk-vX.Y.Z` (workflow `SDK release`); la versión de los
dos paquetes siempre coincide. Referencia: [SDK .NET](../sdk/dotnet/AuthCenter.Client/README.md) y
[SDK TypeScript](../sdk/typescript/README.md).

## Registro del cliente

En la consola (`/admin-v2/oauth-clients`) o con `POST /api/oauth/clients`:

| Campo | BFF / web | SPA | Máquina |
|---|---|---|---|
| Tipo | Confidencial | Público | Confidencial |
| Grants | `authorization_code`, `refresh_token` | `authorization_code` (y `refresh_token` si lo necesita) | `client_credentials` |
| Redirect URIs | `https://app/signin-authcenter` | `https://spa/callback` | — |
| `LoginUrl` | `https://<host-de-AuthCenter>/login` (login hospedado) | igual | — |
| Post-logout redirect URIs | `https://app/signout-callback-authcenter` | `https://spa/` | — |
| Back-channel logout URI | `https://app/auth/backchannel-logout` | — | — |
| Orígenes CORS | — | `https://spa` | — |
| Scopes | `openid profile email offline_access` + scopes de API | `openid profile email` + scopes de API | scopes de API |

El `LoginUrl` debe ser el login hospedado de AuthCenter: `/oauth/authorize` lo usa cuando no hay
una sesión SSO válida y el login aplica la marca, las políticas y el MFA de la aplicación del
cliente. El login hospedado ofrece contraseña, passkeys, enlace de acceso por correo, recuperación
de contraseña, federación y segundo factor; si la aplicación exige un factor que el usuario no
tiene, lo inscribe en el mismo flujo (app de autenticación con QR y códigos de respaldo, o passkey)
y la solicitud continúa. Para que los correos de AuthCenter (restablecer contraseña, invitación,
confirmación y cambio de correo, enlace de acceso) abran las páginas hospedadas, configura
`ActionLinks:DefaultBaseUrl` (o `ActionLinks:ApplicationBaseUrls:<código>`) con el origen público
de AuthCenter. El portal de cuenta (`/portal`) permite al usuario gestionar contraseña, MFA,
passkeys, sesiones, aplicaciones y proveedores vinculados.

## BFF .NET

```json
{ "AuthCenter": { "Authority": "https://identity.example.com", "ClientId": "shop-web", "Scopes": [ "openid", "profile", "email", "offline_access" ] } }
```

```csharp
builder.Services.AddAuthCenterBff(builder.Configuration.GetRequiredSection("AuthCenter")); // ClientSecret desde el secret store
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthCenterBff(); // /auth/login, /auth/session, /auth/refresh, /auth/logout, /auth/backchannel-logout
```

El navegador sólo recibe una cookie opaca `__Host-`; el access token se agrega a las llamadas
server-to-server con `AuthCenterBffAccessTokenHandler`. Ejemplo completo: `samples/dotnet-web`.

## API protegida

1. Registra la API en el catálogo (`/api/api-resources`): identificador URI absoluto (por
   ejemplo `https://orders.example.com/api`), sus scopes (`orders.read`) y la aplicación dueña de
   los permisos (`ORDERS_READ`).
2. Permite los scopes de la API en los clientes que la llaman; el access token tendrá como
   audiencia la API y los roles/permisos de su aplicación.
3. Valida los tokens:

```csharp
// "AuthCenter": { "Authority": "https://identity.example.com", "Audience": "https://orders.example.com/api" }
builder.Services.AddAuthentication().AddAuthCenterJwtBearer(builder.Configuration.GetRequiredSection("AuthCenter"));
builder.Services.AddAuthorization(options => options.AddPolicy("Orders.Read", policy => policy
    .RequireAuthCenterScope("orders.read").RequireAuthCenterPermission("ORDERS_READ")));
```

Ejemplo completo: `samples/dotnet-api`.

## SPA

```ts
const client = new AuthCenterClient({ authority, clientId: "shop-spa", redirectUri: `${location.origin}/callback`, postLogoutRedirectUri: `${location.origin}/` });
await client.signInRedirect();                   // en el botón de login
const { tokens, claims } = await client.handleCallback(); // en /callback: valida state, PKCE e ID token
await client.signOutRedirect({ idTokenHint: tokens.id_token });
```

Ejemplo completo con SSO silencioso, UserInfo y llamada a una API: `samples/spa`.

## SSO y parámetros de inicio de sesión

Mientras la sesión SSO de AuthCenter es válida, `/oauth/authorize` responde a cualquier cliente sin
mostrar el login (si la política y el MFA de su aplicación lo permiten). Parámetros soportados por
el BFF (`/auth/login?…`), el SDK TypeScript (`createSignInUrl`) y `/oauth/authorize`:

| Parámetro | Uso |
|---|---|
| `prompt=none` | SSO silencioso; sin sesión la aplicación recibe `login_required`. |
| `prompt=login`, `max_age=N` | Exige reautenticación o una autenticación reciente. |
| `login_hint` | Prellena el correo. |
| `acr_values=urn:authcenter:acr:mfa` | Pide MFA (el ID token informa `acr` y `amr`). |
| `idp=<id del proveedor>` | Envía al usuario directamente al IdP de su organización. |
| `domain_hint=empresa.com` | Descubre el IdP por dominio. |

## Logout

- Logout de la aplicación: el BFF revoca su sesión (`POST /auth/logout`).
- Logout global: `GET /auth/logout?sid=…` (BFF) o `signOutRedirect` (SPA) llaman al
  `end_session_endpoint` con `id_token_hint`; AuthCenter cierra la sesión SSO y vuelve al
  post-logout redirect URI registrado.
- Back-channel: cuando el usuario sale desde otra aplicación, AuthCenter envía un `logout_token` a
  la back-channel logout URI y el BFF invalida las sesiones de ese `sid` en todas sus instancias.

## Federación empresarial

Los IdP OIDC o SAML se configuran por aplicación en `/admin-v2/federation`. Registra en el IdP los
valores de `GET /api/federation/service-provider` (callback OIDC
`https://<host>/api/federation/oidc/callback`, entity ID y ACS SAML) y comprueba la configuración
con "Probar conexión". Los usuarios llegan al IdP por su dominio de correo, por `domain_hint` o por
`idp`; al volver, AuthCenter aplica el acceso, la política y el MFA de la aplicación (o confía en
el MFA del IdP si así se configuró) y puede sincronizar grupos desde un claim. Detalle en el
[README](../README.md#enterprise-federation) y en
[authentication-flow.md](authentication-flow.md#enterprise-federation-from-the-hosted-login).

## Varias instancias

- `IDistributedCache` compartido (Redis o SQL) para los tickets del BFF.
- Key ring de Data Protection común, protegido con Key Vault.
- `UseDistributedRefreshCoordination: true`: AuthCenter revoca la sesión si un refresh token rotado
  se reutiliza, y el coordinador evita que dos instancias renueven la misma sesión a la vez.

## Comprobación rápida

```powershell
dotnet build samples/dotnet-web/AuthCenter.SampleWeb.csproj -c Release
dotnet build samples/dotnet-api/AuthCenter.SampleApi.csproj -c Release
./scripts/Invoke-Conformance.ps1
```

El perfil de conformidad cubre discovery/JWKS, Authorization Code, PKCE, nonce/state,
refresh/revocación, SCIM Users/Groups, filtros, paginación, scopes y aislamiento por aplicación;
también compila y prueba el SDK TypeScript.
