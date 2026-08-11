# Integración de proyectos con AuthCenter

## Selección del flujo

- SPA o web con usuario: Authorization Code + PKCE S256 y `state`/`nonce` aleatorios.
- Backend confidencial: Authorization Code + PKCE y secreto únicamente en Key Vault/secret store.
- Servicio a servicio: Client Credentials con scope mínimo.
- API de recursos: discovery y JWKS RS256; valida issuer, audience, expiración y algoritmo.

Los proyectos en `samples/` usan hosts de ejemplo y valores públicos. Ningún ejemplo contiene
credenciales. Los secretos se proporcionan mediante variables de entorno o el secret store del
entorno de ejecución.

## Conformidad local

Ejecuta `./scripts/Invoke-Conformance.ps1`. El perfil cubre discovery/JWKS, Authorization Code,
PKCE, nonce/state, refresh/revocación, SCIM Users/Groups, filtros, paginación, scopes y aislamiento
por aplicación; también compila y prueba el SDK TypeScript.
