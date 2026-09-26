# Integración de una aplicación a producción

Este documento es el contrato operativo para incorporar una aplicación a AuthCenter. El patrón
preferido para una web es BFF: el navegador recibe una cookie opaca y el backend conserva los
tokens OAuth cifrados en almacenamiento distribuido.

## 1. Datos de alta

Define antes de crear el cliente:

- código y nombre de la aplicación;
- dueño técnico y contacto operativo;
- ambientes y URLs públicas;
- redirect URI exacta por ambiente (`/signin-authcenter` por defecto);
- scopes, roles y permisos mínimos;
- duración de access token;
- grants requeridos;
- URLs de logout/retorno que controlará la propia aplicación.

Para una web BFF registra un cliente **confidencial** con `authorization_code` y `refresh_token`,
PKCE S256, `openid profile email offline_access`, y una URI exacta por ambiente. Entrega el secreto
una sola vez al secret store del ambiente. Para una API registra su audience/client ID y no le
entregues un secreto sólo para validar JWT.

## 2. Configuración segura

La aplicación consumidora usa estas claves:

```text
AuthCenter__Authority=https://<host-publico-authcenter>
AuthCenter__ClientId=<client-id-publico>
AuthCenter__ClientSecret=@Microsoft.KeyVault(...)
```

En Azure App Service, usa identidad administrada y una referencia versionless de Key Vault para
el secreto. Otorga `Key Vault Secrets User` sólo sobre ese secreto. No copies el valor a GitHub,
variables de pipeline, archivos JSON, logs ni tickets.

Configura además:

- un `IDistributedCache` compartido (Redis/SQL) para tickets;
- un key ring de Data Protection compartido y protegido con Key Vault;
- un `IAuthCenterRefreshCoordinator` distribuido que deduplique cada token consumido y comparta
  brevemente el resultado de la rotación;
- forwarded headers restringidos al proxy de confianza;
- `AllowedHosts`, HTTPS, HSTS, CSP y rate limiting.

Una sola instancia puede usar los defaults en memoria sólo para desarrollo o una prueba inicial.

## 3. Integración del BFF

Instala `AuthCenter.Client`, registra `AddAuthCenterBff`, coloca `UseAuthentication` antes de
`UseAuthorization` y mapea `MapAuthCenterBff`. El ejemplo ejecutable está en
`samples/dotnet-web`.

El frontend usa esta secuencia:

1. Navega a `/auth/login?return_url=/ruta-local`.
2. Después del callback consulta `GET /auth/session` con cookies incluidas.
3. Conserva el `csrfToken` sólo en memoria y lo envía como `X-AuthCenter-CSRF` en writes del BFF.
4. Llama exclusivamente a su BFF; nunca recibe access o refresh tokens.
5. Ejecuta `POST /auth/logout`; el BFF revoca el refresh token y elimina su sesión local.

La autorización es server-side. Ocultar botones no sustituye las políticas. Usa
`RequireAuthCenterPermission` para permisos de negocio y `RequireAuthCenterScope` en los límites
de APIs.

## 4. Integración de APIs

Cada API valida discovery/JWKS, issuer HTTPS, expiración, algoritmo RS256 y su audience exacto con
`AddAuthCenterJwtBearer`. Debe exigir scopes y permisos en cada operación sensible. No acepte un
token pensado para otro cliente y no confíe en claims enviados directamente por el frontend.

Los servicios sin usuario usan Client Credentials con un cliente independiente por servicio y
ambiente. La rotación de ese secreto no debe afectar a clientes interactivos.

## 5. Gates antes de producción

- Discovery y JWKS responden por HTTPS y el issuer coincide exactamente con `iss`.
- Login, callback, refresh rotado, logout y revocación pasan en el dominio público real.
- Una redirect URI alterada, `state` incorrecto y PKCE inválido son rechazados.
- Un usuario sin acceso, scope o permiso recibe 403; un token de audience incorrecto recibe 401.
- El browser no contiene tokens en HTML, JavaScript, Local Storage, Session Storage ni logs.
- Las cookies tienen `Secure`, `HttpOnly`, prefijo `__Host-` y el CSRF es requerido en writes.
- Reiniciar o mover la sesión entre instancias no la invalida; dos refresh concurrentes no causan
  reuse detection.
- Secret scanning, dependencias, pruebas, health checks y rollback pasan en CI.
- Alertas cubren tasa de errores de callback/token, latencia, 401/403 anómalos y fallos de cache.

## 6. Despliegue y rollback

Despliega primero el registro y secretos, después el backend/API y al final el frontend. Realiza
un smoke test con un usuario de mínimo privilegio y otro sin acceso. Para rollback, revierte la
aplicación consumidora sin eliminar inmediatamente el cliente ni la clave anterior; conserva una
ventana controlada de solapamiento, revoca sesiones si hubo exposición y rota el secreto cuando el
rollback quede estable.

El logout global usa el `end_session_endpoint` de discovery: la aplicación lo llama con
`id_token_hint` y AuthCenter cierra la sesión SSO hospedada, revoca los grants OAuth de esa sesión,
envía un `logout_token` a la back-channel logout URI de cada cliente que la registró y vuelve al
post-logout redirect URI registrado (detalle en `docs/integration-quickstarts.md`, sección Logout).
Pruébalo en equipos compartidos: tras salir de una aplicación, otra que comparte la sesión debe
pedir credenciales de nuevo.
