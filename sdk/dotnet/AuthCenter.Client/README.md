# AuthCenter.Client

SDK para integrar aplicaciones ASP.NET Core (.NET 8 LTS y .NET 10) con AuthCenter como proveedor
OAuth 2.0/OpenID Connect. Se instala con `dotnet add package AuthCenter.Client`. Incluye dos
perfiles:

- BFF web confidencial: Authorization Code + PKCE, sesión opaca y tokens cifrados sólo en el
  servidor.
- API de recursos: validación RS256 por discovery/JWKS, issuer, audience, expiración, scopes y
  permisos.

## Aplicación web/BFF

Registra en AuthCenter un cliente confidencial con grants `authorization_code` y `refresh_token`,
scopes `openid profile email offline_access` y la URI exacta
`https://app.example.com/signin-authcenter`. Guarda el secreto en Key Vault o en el secret store
del entorno; nunca en `appsettings.json`.

En AuthCenter, el `LoginUrl` del cliente es el login hospedado (`https://<host-de-AuthCenter>/login`);
registra también la URI de post-logout `https://app.example.com/signout-callback-authcenter` y la
URI de back-channel logout `https://app.example.com/auth/backchannel-logout` (ver "Logout").

```json
{
  "AuthCenter": {
    "Authority": "https://identity.example.com",
    "ClientId": "shop-web",
    "Scopes": [ "openid", "profile", "email", "offline_access" ]
  }
}
```

```csharp
using AuthCenter.Client;

// Authority, ClientId y ClientSecret (desde el secret store) son obligatorios; el resto de
// propiedades de AuthCenterBffOptions (Scopes, Resource, rutas, SessionLifetime,
// UseDistributedRefreshCoordination…) son opcionales y conservan su valor por defecto.
builder.Services.AddAuthCenterBff(builder.Configuration.GetRequiredSection("AuthCenter"));
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Orders.Read", policy => policy
        .RequireAuthenticatedUser()
        .RequireAuthCenterPermission("ORDERS_READ"));
});

var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthCenterBff();
```

El SDK publica:

| Método | Ruta | Uso |
|---|---|---|
| GET | `/auth/login?return_url=/ruta-local` | Inicia OIDC; rechaza redirecciones externas. Acepta `prompt`, `max_age`, `login_hint`, `acr_values`, `idp` y `domain_hint` |
| GET | `/auth/session` | Devuelve usuario autorizado y token CSRF, nunca tokens OAuth |
| POST | `/auth/refresh` | Rota el refresh token; requiere `X-AuthCenter-CSRF` |
| POST | `/auth/logout` | Cierre local: revoca la familia de refresh y elimina la sesión de la aplicación; requiere CSRF |
| GET | `/auth/logout?sid=…` | Cierre global: además termina la sesión SSO en AuthCenter y vuelve por `/signout-callback-authcenter`. Usa el `logoutUrl` de `/auth/session` |
| POST | `/auth/backchannel-logout` | Recibe el logout token de AuthCenter y cierra las sesiones locales de ese `sid` |
| GET | `/auth/error` | Error genérico sin datos del proveedor; incluye `error` sólo para códigos OIDC accionables |

### Cierre de sesión

Registra en el cliente OAuth de AuthCenter:

- **Post-logout redirect URI:** `https://tu-app/signout-callback-authcenter` (`SignedOutCallbackPath`).
- **Back-channel logout URI:** `https://tu-app/auth/backchannel-logout` (`BackchannelLogoutPath`).

Para cerrar sesión en todas partes navega (GET) al `logoutUrl` que entrega `/auth/session`; incluye
el `sid` de la sesión, por lo que otro sitio no puede forzar el cierre con un enlace. Cuando la
sesión de AuthCenter termina por cualquier motivo, el logout token back-channel marca ese `sid` en
`IDistributedCache` y cada instancia rechaza la cookie en la siguiente petición. Con más de una
instancia registra una caché distribuida compartida (por ejemplo Redis) en lugar de la caché en
memoria que el SDK agrega por defecto.

### Inicio de sesión único (SSO)

AuthCenter mantiene una sesión SSO en su propio dominio: si el usuario ya inició sesión en otra
aplicación, `/auth/login` vuelve con la sesión creada sin mostrar el login. Parámetros opcionales
(los valores mal formados se descartan y AuthCenter vuelve a validarlos):

| Parámetro | Uso |
|---|---|
| `prompt=none` | SSO silencioso. Sin sesión (o sin consentimiento) AuthCenter responde `login_required` / `consent_required` y el BFF redirige a `/auth/error?error=login_required` para que la aplicación muestre su propio botón de ingreso. |
| `prompt=login`, `max_age=N` | Exige reautenticación (o una autenticación de hace menos de `N` segundos). La sesión SSO y su `sid` se conservan. |
| `login_hint=correo` | Prellena el correo en el login hospedado. |
| `acr_values=urn:authcenter:acr:mfa` | Solicita el nivel de autenticación; el ID token informa `acr`, `amr`, `auth_time` y `sid`. |
| `idp=<id del proveedor>` | Federación empresarial: el login hospedado envía al usuario directamente a ese IdP (OIDC o SAML) de la aplicación. |
| `domain_hint=empresa.com` | El login hospedado busca el IdP de ese dominio (descubrimiento de dominio) y, si existe, redirige a él. |

Tras volver del IdP, AuthCenter aplica la política de acceso y el MFA de la aplicación antes de
emitir la sesión; el ID token informa `amr` `fed` (y `mfa` si el MFA del IdP es de confianza o el
usuario completó el segundo factor). `AuthCenterClient.BuildAuthorizationUri` acepta los mismos
valores con `identityProvider` y `domainHint`.

El navegador debe llamar `/auth/session` con credenciales incluidas y conservar el `csrfToken`
sólo en memoria. En cada POST anterior debe enviarlo en `X-AuthCenter-CSRF`. La cookie tiene
prefijo `__Host-`, `Secure`, `HttpOnly` y `SameSite=Lax`; contiene únicamente un identificador
aleatorio. Access, refresh e ID tokens permanecen en el ticket protegido del servidor.

Para llamar una API desde el BFF sin entregar el token al browser, registra la API en el catálogo
de AuthCenter (`/api/api-resources`: identificador `https://orders.example.com/api`, scope
`orders.read`), permite ese scope al cliente del BFF y configura el recurso:

```csharp
builder.Services.AddAuthCenterBff(new AuthCenterBffOptions
{
    Authority = authority,
    ClientId = "sample-web",
    ClientSecret = secret,
    Resource = "https://orders.example.com/api",
    Scopes = ["openid", "profile", "email", "offline_access", "orders.read"]
});
builder.Services.AddHttpClient("orders", client =>
    client.BaseAddress = new Uri(builder.Configuration["Services:Orders"]!))
    .AddHttpMessageHandler<AuthCenterBffAccessTokenHandler>();
```

El access token tiene entonces como audiencia la API (RFC 8707) y lleva los roles y permisos de
la aplicación dueña de la API. El handler renueva el token próximo a expirar y agrega
`Authorization: Bearer` sólo a la llamada server-to-server.

## API de recursos

La audiencia es el identificador de la API en el catálogo de AuthCenter; los permisos se definen
en la aplicación dueña de la API:

```csharp
// appsettings: "AuthCenter": { "Authority": "https://identity.example.com", "Audience": "https://orders.example.com/api" }
builder.Services.AddAuthentication()
    .AddAuthCenterJwtBearer(builder.Configuration.GetRequiredSection("AuthCenter"));
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Orders.Read", policy => policy
        .RequireAuthenticatedUser()
        .RequireAuthCenterScope("orders.read")
        .RequireAuthCenterPermission("ORDERS_READ"));
});
```

También se acepta una colección explícita de audiences durante una migración controlada. No uses
esa opción para compartir indiscriminadamente tokens entre APIs.

La validación exige el tipo RFC 9068 `typ: at+jwt`, de modo que un ID token (mismo emisor,
audiencia y algoritmo) no puede usarse como bearer. Los roles llegan en el claim `role` y funcionan
con `[Authorize(Roles = "...")]`, `RequireRole` e `IsInRole`; los tokens anteriores con el claim URI
de .NET se normalizan automáticamente. Sólo mientras un AuthCenter anterior a este contrato siga
emitiendo tokens, pasa `requireAccessTokenType: false`.

### Llamar a otra API en nombre del usuario, máquina a máquina e introspección

Con un cliente confidencial propio de la API (misma aplicación, grant
`urn:ietf:params:oauth:grant-type:token-exchange` y los scopes de la API destino):

```csharp
var client = new AuthCenterClient(httpClient, new AuthCenterClientOptions
{
    Authority = authority, ClientId = "orders-api", ClientSecret = secret
});
// RFC 8693: token para Shipping con el mismo usuario y claim act = orders-api.
var shipping = await client.ExchangeTokenAsync(userAccessToken, "https://shipping.example.com/api");
// Token de máquina para una API concreta.
var machine = await client.ClientCredentialsForResourceAsync("https://shipping.example.com/api", ["shipping.read"]);
// RFC 7662: el token deja de estar activo si el usuario cierra sesión o pierde el acceso.
var state = await client.IntrospectAsync(userAccessToken);
```

`AuthCenterClient` lee sus endpoints del documento de discovery (`GetDiscoveryDocumentAsync`,
en caché una hora; el `issuer` debe ser el `Authority` y todos los endpoints HTTPS), por lo que
funciona con un `Authority` con path base. También ofrece `BuildAuthorizationUriAsync`,
`GetUserInfoAsync` y `BuildEndSessionUriAsync` para aplicaciones que no usan el BFF. La sobrecarga
`AddAuthCenterClient(IConfiguration)` lee `Authority`, `ClientId`, `ClientSecret` y `Scopes`.

## Requisitos de producción

- Reemplaza `IDistributedCache` en memoria por Redis o SQL distribuido antes de escalar a más de
  una instancia.
- Persiste las claves de ASP.NET Core Data Protection en almacenamiento compartido y protégelas
  con Key Vault. Sin un key ring común, otra instancia no podrá abrir el ticket.
- Con varias instancias activa `UseDistributedRefreshCoordination` (o llama
  `services.AddAuthCenterDistributedRefreshCoordination()`): AuthCenter rota los refresh tokens y
  trata un segundo uso como robo (revoca la sesión), así que dos instancias no deben renovar la
  misma sesión a la vez. El coordinador usa el mismo `IDistributedCache` que los tickets: una
  instancia renueva y las demás esperan su resultado, que se comparte 30 s cifrado con Data
  Protection (el key ring debe ser común). `IDistributedCache` no tiene un "agregar si no existe"
  atómico: el lock se confirma releyéndolo tras una espera corta; si necesitas garantía estricta,
  implementa `IAuthCenterRefreshCoordinator` sobre un almacén con locks atómicos.
- Configura correctamente los forwarded headers del proxy de confianza para que el callback se
  genere con el host y esquema HTTPS públicos. No confíes proxies arbitrarios.
- Usa HTTPS extremo a extremo, allow-list de hosts, CSP y límites de tasa en el BFF.
- Mantén `ClientSecret` en Key Vault; `Authority` y `ClientId` no son secretos.
- `/auth/logout` revoca la sesión de esta aplicación; `GET /auth/logout?sid=` cierra también la
  sesión SSO de AuthCenter (`end_session_endpoint`), y el back-channel logout cierra esta sesión
  cuando el usuario sale desde otra aplicación.

El paquete no registra logging de tokens y las respuestas de sesión/refresh/logout se marcan
`no-store`.
