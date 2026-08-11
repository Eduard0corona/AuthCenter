# Integración de proyectos con AuthCenter

## Elección del flujo

- Web con frontend propio: BFF confidencial con Authorization Code + PKCE. Es el patrón recomendado.
- API: bearer token RS256 validado mediante discovery/JWKS y audience propio.
- Servicio a servicio: Client Credentials y scopes mínimos.
- SPA pública sin backend: Authorization Code + PKCE, sin secreto; úsala sólo cuando no sea viable
  un BFF y conserva tokens únicamente en memoria.

El procedimiento completo de alta, configuración, despliegue y rollback está en
[production-idp-integration.md](production-idp-integration.md). Los proyectos de `samples/` usan
hosts ilustrativos y no contienen credenciales.

## Comprobación rápida

```powershell
dotnet build samples/dotnet-web/AuthCenter.SampleWeb.csproj -c Release
dotnet build samples/dotnet-api/AuthCenter.SampleApi.csproj -c Release
./scripts/Invoke-Conformance.ps1
```

El perfil de conformidad cubre discovery/JWKS, Authorization Code, PKCE, nonce/state,
refresh/revocación, SCIM Users/Groups, filtros, paginación, scopes y aislamiento por aplicación;
también compila y prueba el SDK TypeScript.
