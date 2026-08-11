# AuthCenter.Client

SDK para integrar aplicaciones ASP.NET Core con AuthCenter como proveedor OAuth 2.0/OpenID
Connect. Incluye dos perfiles:

- BFF web confidencial: Authorization Code + PKCE, sesión opaca y tokens cifrados sólo en el
  servidor.
- API de recursos: validación RS256 por discovery/JWKS, issuer, audience, expiración, scopes y
  permisos.

## Aplicación web/BFF

Registra en AuthCenter un cliente confidencial con grants `authorization_code` y `refresh_token`,
scopes `openid profile email offline_access` y la URI exacta
`https://app.example.com/signin-authcenter`. Guarda el secreto en Key Vault o en el secret store
del entorno; nunca en `appsettings.json`.

```csharp
using AuthCenter.Client;

var authCenter = builder.Configuration.GetRequiredSection("AuthCenter");
builder.Services.AddAuthCenterBff(new AuthCenterBffOptions
{
    Authority = new Uri(authCenter["Authority"]!),
    ClientId = authCenter["ClientId"]!,
    ClientSecret = authCenter["ClientSecret"]!
});
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
| GET | `/auth/login?return_url=/ruta-local` | Inicia OIDC; rechaza redirecciones externas |
| GET | `/auth/session` | Devuelve usuario autorizado y token CSRF, nunca tokens OAuth |
| POST | `/auth/refresh` | Rota el refresh token; requiere `X-AuthCenter-CSRF` |
| POST | `/auth/logout` | Revoca la familia de refresh y elimina la sesión; requiere CSRF |
| GET | `/auth/error` | Error genérico sin datos del proveedor |

El navegador debe llamar `/auth/session` con credenciales incluidas y conservar el `csrfToken`
sólo en memoria. En cada POST anterior debe enviarlo en `X-AuthCenter-CSRF`. La cookie tiene
prefijo `__Host-`, `Secure`, `HttpOnly` y `SameSite=Lax`; contiene únicamente un identificador
aleatorio. Access, refresh e ID tokens permanecen en el ticket protegido del servidor.

Para llamar una API desde el BFF sin entregar el token al browser:

```csharp
builder.Services.AddHttpClient("orders", client =>
    client.BaseAddress = new Uri(builder.Configuration["Services:Orders"]!))
    .AddHttpMessageHandler<AuthCenterBffAccessTokenHandler>();
```

El handler renueva el token próximo a expirar y agrega `Authorization: Bearer` sólo a la llamada
server-to-server.

## API de recursos

```csharp
var authority = new Uri(builder.Configuration["AuthCenter:Authority"]!);
builder.Services.AddAuthentication()
    .AddAuthCenterJwtBearer(authority, "orders-api");
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

## Requisitos de producción

- Reemplaza `IDistributedCache` en memoria por Redis o SQL distribuido antes de escalar a más de
  una instancia.
- Persiste las claves de ASP.NET Core Data Protection en almacenamiento compartido y protégelas
  con Key Vault. Sin un key ring común, otra instancia no podrá abrir el ticket.
- Sustituye `IAuthCenterRefreshCoordinator` por un coordinador distribuido si hay múltiples
  instancias. Debe deduplicar la rotación y compartir brevemente el resultado; un lock sin replay
  no basta porque una request que ya cargó el ticket anterior intentaría reutilizar el token.
- Configura correctamente los forwarded headers del proxy de confianza para que el callback se
  genere con el host y esquema HTTPS públicos. No confíes proxies arbitrarios.
- Usa HTTPS extremo a extremo, allow-list de hosts, CSP y límites de tasa en el BFF.
- Mantén `ClientSecret` en Key Vault; `Authority` y `ClientId` no son secretos.
- El logout del SDK revoca la sesión de esta aplicación. AuthCenter todavía no publica
  `end_session_endpoint`, por lo que no cierra automáticamente la sesión SSO hospedada.

El paquete no registra logging de tokens y las respuestas de sesión/refresh/logout se marcan
`no-store`.
