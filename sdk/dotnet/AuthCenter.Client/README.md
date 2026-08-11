# AuthCenter.Client

Cliente OAuth/OIDC tipado y middleware para APIs ASP.NET Core. No almacena secretos ni tokens;
el consumidor decide el almacenamiento seguro. Usa Authorization Code + PKCE para usuarios,
Client Credentials para servicios y validación RS256 mediante discovery/JWKS.

```csharp
builder.Services.AddAuthCenterClient(new AuthCenterClientOptions
{
    Authority = new Uri(builder.Configuration["AuthCenter:Authority"]!),
    ClientId = builder.Configuration["AuthCenter:ClientId"]!,
    ClientSecret = builder.Configuration["AuthCenter:ClientSecret"]
});

builder.Services.AddAuthentication()
    .AddAuthCenterJwtBearer(new Uri(builder.Configuration["AuthCenter:Authority"]!), "my-api");
```
