# Remediación: integración de sistemas, federación y SSO

Registro vivo de la remediación derivada del análisis completo del repositorio realizado el
2026-09-26 sobre `main` @ `994a4aa`. Cada punto tiene un identificador estable, la evidencia que lo
originó y su estado. Una casilla sólo se marca cuando el cambio tiene código, pruebas automatizadas
y documentación. Los puntos que dependen del propietario (Azure, GitHub, datos reales) se indican
como **acción del propietario** con el procedimiento preparado.

## Línea base verificada (antes de los cambios)

- Build .NET 10 Release: 0 errores, 0 advertencias.
- Pruebas .NET: 65 unitarias, 146 de integración (con SQL Server 2022 real), 29 de conformidad.
- Consola admin: lint, typecheck, 38 pruebas unitarias, build y 75/75 escenarios Playwright.
- SDK TypeScript 3/3; quickstart SPA compila; 0 paquetes NuGet vulnerables; npm: 2 moderadas.
- Prueba E2E del SDK real contra el servidor real (arnés fuera del repo): el login BFF, refresh,
  rotación, revocación y logout local funcionan; se confirmaron los defectos listados abajo.

## Estado por área

Leyenda: `[x]` resuelto con evidencia, `[ ]` pendiente, `[~]` en curso, `[P]` acción del propietario.

### A. Componente de integración, federación y SSO

- [ ] **FED-01** La federación empresarial (OIDC/SAML entrante) no está conectada al login hospedado
  ni al flujo `/oauth/authorize`: `oidc/complete` y `saml/acs` devuelven tokens en JSON, no existe
  callback OIDC en servidor y el login no ofrece descubrimiento por dominio.
- [~] **FED-02** `login_hint`, `idp` y `domain_hint` se ignoran en `/oauth/authorize`.
  *Avance:* `login_hint` se valida, se guarda en la interacción y prellena el login; `id_token_hint` se valida contra el cliente. Pendiente (F8): `idp` y `domain_hint` para el descubrimiento de IdP.
- [x] **SSO-01** El login hospedado autentica contra el campo "Aplicación" (por defecto
  `AUTHCENTER`); usuarios de otras aplicaciones reciben `401 ACCESS_DENIED`. `LoginUrl` no está
  documentado.
  *Resuelto:* `/oauth/authorize` guarda la aplicación del cliente en la interacción y el login hospedado la lee de `GET /oauth/interactions/{id}/context` (aplicación, `login_hint`, `AllowPasswordLogin`, frescura); ya no hay campo "Aplicación" y el usuario inicia sesión en la aplicación del cliente con sus políticas y MFA. `LoginUrl` documentado en README (`https://<host>/login`). La consola administrativa pide un inicio de sesión en `AUTHCENTER` si la sesión SSO se abrió para otra aplicación (la nueva autenticación continúa la misma sesión). Pruebas: `SingleSignOnTests.InteractionContext_*`, e2e "a single sign-on session opened for another application…".
- [x] **SSO-02** Las políticas de acceso y el MFA de la aplicación destino no se evalúan al
  completar la autorización, canjear el código ni renovar tokens OAuth.
  *Resuelto:* una única evaluación "sesión SSO × aplicación del cliente" (acceso activo, política publicada con la IP y el riesgo del navegador, `RequireMfa` de la aplicación, MFA del usuario y el `acr_values` menos exigente soportado) decide en el atajo SSO y al completar: la denegación vuelve al cliente como `access_denied` (auditada), una sesión débil recibe `STEP_UP_REQUIRED` sin consumir la interacción y el login hospedado hace step-up (TOTP/passkey) con `POST /oauth/interactions/{id}/step-up`, conservando la sesión y su `sid`; `prompt=none` responde `login_required`. Cada refresh OAuth vuelve a evaluar acceso y política (red de la sesión de inicio) y revoca la familia si se deniega. El código se emite sólo tras la evaluación, por lo que el canje no la repite. Pruebas: `SingleSignOnPolicyTests` (step-up con TOTP y mismo `sid`, `MFA_SETUP_REQUIRED`, `acr_values`, denegación por política, refresh tras salir del grupo permitido).
- [x] **SSO-03** La sesión SSO dura lo mismo que un access token (15 min, sin renovación); no hay
  `prompt`/`max_age`; `auth_time` es la hora de emisión; no se emiten `amr`/`acr`.
  *Resuelto:* sesión SSO propia (`Sso:SessionLifetimeMinutes`, 480 por defecto, cookie `SameSite=Lax` con el registro de sesión verificado en cada petición); `/oauth/authorize` responde directamente si hay sesión válida; `prompt=none|login|consent|select_account`, `max_age` (incluido `max_age=0`), `id_token_hint`, `form_post`; `auth_time` real, `amr` y `acr` persistidos en la sesión, el código y los refresh OAuth. Reautenticarse con la misma cuenta conserva la sesión y su `sid`; otra cuenta cierra la sesión anterior. **Requisito de despliegue:** migración `20260926092052_AddSingleSignOnSessionContext`. Pruebas: `SingleSignOnTests` (21).
- [x] **SSO-04** El `interaction_id` no está ligado al navegador que inició la autorización.
  *Resuelto:* cookie `__Host-AuthCenter.Browser` (HttpOnly, aleatoria); la interacción guarda su hash y sólo ese navegador puede leer el contexto, completarla o recibir la respuesta `form_post` de un solo uso (`INTERACTION_BINDING_MISMATCH`). Pruebas: `Interaction_CannotBeContinuedFromAnotherBrowser`, `FormPost_FromTheHostedLogin_*`.
- [x] **TOK-01** El claim de roles se emite como URI
  `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`; el SDK espera `role`, por lo que
  `IsInRole`/`RequireRole` fallan en los sistemas integrados.
  *Resuelto:* los access tokens emiten `role` (constante `DomainConstants.Claims.Role`); la cookie de la UI lo mapea a `ClaimTypes.Role` y el SDK guarda los roles bajo el `RoleClaimType` de la identidad aceptando también el URI legacy. Pruebas: `SdkContractTests` (`IsInRole` y `RequireRole` en BFF y API).
- [x] **TOK-02** El access token siempre tiene `aud = client_id`; no hay `resource` (RFC 8707) ni
  catálogo de APIs y scopes propios.
  *Resuelto:* catálogo `ApiResource`/`ApiScope` por aplicación (`/api/api-resources`: identificador URI absoluto, scopes únicos entre APIs); los clientes pueden tener scopes de API (validados contra el catálogo). Autorización y token aceptan scopes de API y `resource` (repetible en autorización, uno por token): la audiencia es la API (más `urn:authcenter:userinfo` con `openid`), los roles/permisos son los de la aplicación dueña de la API y el usuario necesita acceso a ella; un grant de varias APIs exige `resource` en el endpoint de token, también en refresh (downscoping); `client_credentials` acepta `resource`. SDK: `AuthCenterBffOptions.Resource`, `BuildAuthorizationUri(resources)`, `ClientCredentialsForResourceAsync`, `RefreshForResourceAsync`, validación de audiencia del BFF. Samples `dotnet-api`/`dotnet-web` alineados (audiencia `https://orders.example.com/api`, scope `orders.read`, permiso `ORDERS_READ`). **Requisito de despliegue:** migración `AddApiResources`. Pruebas: `ApiResourceTests`, `SdkContractTests.Bff_WithAResource_*`.
- [x] **TOK-03** Higiene de tokens: `email_verified` como string en el ID token, sin `sid`/`azp`,
  access token sin `typ: at+jwt` ni `iat`, respuesta de token con campos `null`.
  *Resuelto:* access tokens con `typ: at+jwt` e `iat`; ID token con `azp`, `email_verified` booleano, `sid`, `auth_time` real, `amr` (arreglo JSON) y `acr`; token y userinfo omiten campos nulos. Pruebas: `OAuthFlowTests`, `SingleSignOnTests.ExistingSession_*`, `SecondFactorSignIn_IsReportedAsMultiFactorInTheIdToken`.
- [x] **LOG-01** No hay logout global: faltan `end_session_endpoint`, `post_logout_redirect_uris`,
  `sid` y back-channel logout; el refresh de una aplicación sigue vivo tras cerrar sesión.
  *Resuelto:* `end_session_endpoint` (`/oauth/logout`, GET y POST→303) con `id_token_hint`/`client_id`, `post_logout_redirect_uri` exacto y `state`; sin un ID token de la sesión actual el usuario confirma en la página hospedada `/logout` (ligada al navegador). Fin de sesión SSO único (`ISingleSignOnSessionService`) usado por logout hospedado, RP-initiated, revocación desde el portal, "cerrar todas" y revocaciones administrativas: revoca la sesión y los refresh OAuth con su `sid`, y encola back-channel logout (`logout+jwt`, `sid`, `sub`, `events`) para cada cliente que recibió tokens con esa sesión (tabla `SingleSignOnSessionClients`); el outbox firma un token nuevo en cada intento (hasta 10). Clientes: `PostLogoutRedirectUris`, `BackchannelLogoutUri`, `BackchannelLogoutSessionRequired` (API, validación y consola). Discovery: `end_session_endpoint`, `backchannel_logout_supported`, `backchannel_logout_session_supported`. SDK: `GET /auth/logout?sid=` (cierre global con `id_token_hint`), `/auth/backchannel-logout` (validación completa, anti-replay por `jti`, marca de `sid` en caché distribuida) y rechazo de la cookie en cada instancia. **Requisito de despliegue:** migración `20260926101304_AddSingleSignOnLogout`; registrar las URIs de logout de cada cliente. Pruebas: `LogoutTests` (14), `BackchannelLogoutRelationalTests` (SQL Server), `SdkContractTests.Bff_GlobalLogout_*`, `Bff_BackchannelLogout_*`, e2e del editor de clientes.
- [x] **RL-01** Límites de tasa sólo por IP (5 logins/min, 60 llamadas/min a `/oauth/token`).
  *Resuelto:* un solo middleware (`RateLimitMiddleware`) con reglas por política y dimensión: IP, cuenta (email del cuerpo JSON, normalizado), cliente OAuth (Basic o `client_id`) e IP sólo sin cliente. Login 5/min por IP y 20/15 min por cuenta; recuperación de contraseña, magic link, reenvío de confirmación y registro también por cuenta (evita bombardeo de correo); `/oauth/token`, `/oauth/revoke` e `/oauth/introspect` 1200/min por cliente (un BFF ya no se limita como una IP) y 60/min por IP sin cliente. Almacén en memoria o SQL compartido (`RateLimiting:DistributedEnabled`), reglas configurables (`RateLimiting:Rules`) y activación evaluada en ejecución. Pruebas: `RateLimitAndCorsTests` (límite por cuenta sin bloquear otras cuentas, por cliente e IP anónima).
- [x] **CORS-01** CORS global con credenciales; no hay orígenes por cliente OAuth.
  *Resuelto:* `AuthCenterCorsPolicyProvider` decide por endpoint: discovery/JWKS públicos (GET, sin credenciales); `/oauth/token`, `/oauth/revoke` y `/oauth/userinfo` sólo para los orígenes registrados en clientes activos (`AllowedCorsOrigins`, sin credenciales, caché invalidada al editar clientes); `/api/*` conserva `Cors:AllowedOrigins` con credenciales; `/ui-api/*`, `/oauth/authorize` y las páginas hospedadas no responden CORS, de modo que un origen permitido ya no puede leer el CSRF ni actuar con la cookie de sesión. Campo en API, validación (sólo esquema://host[:puerto]) y consola. **Requisito de despliegue:** migración `AddClientCorsOrigins`; registrar los orígenes de las SPA en sus clientes. Pruebas: `RateLimitAndCorsTests`.
- [x] **DISC-01** Discovery incompleto (`claims_supported`, logout, parámetros no soportados) y sin
  validación `issuer` = origen público.
  *Avance:* discovery publica `claims_supported` (incl. `sid`, `auth_time`, `amr`, `acr`), `response_modes_supported` (`query`, `form_post`), `prompt_values_supported`, `acr_values_supported` y declara no soportados `request`, `request_uri` y `claims`; fuera de desarrollo el arranque exige que `Jwt:Issuer` identifique la misma URL que `Oidc:PublicOrigin` (prueba `IssuerDifferentFromPublicOrigin_FailsStartupOutsideDevelopment`). **Requisito de despliegue:** ambos valores deben coincidir. `end_session_endpoint`, soporte back-channel (F5), `introspection_endpoint` y el grant de token exchange (F6) publicados.
- [x] **OIDC-01** No hay endpoint de introspección (RFC 7662).
  *Resuelto:* `POST /oauth/introspect` para clientes confidenciales (Basic o post): un access token sólo se muestra activo a su cliente o a las APIs de la aplicación del llamador, y sólo mientras el usuario y la sesión SSO (`sid`, ahora también en los access tokens OAuth) sigan activos; los refresh tokens sólo al cliente titular. Discovery publica `introspection_endpoint`. SDK: `IntrospectAsync`. Pruebas: `Introspection_ShowsTokensOnlyToTheirApis_AndReflectsSignOut`.
- [x] **OIDC-02** No hay token exchange (RFC 8693).
  *Resuelto:* grant `urn:ietf:params:oauth:grant-type:token-exchange` (sólo clientes confidenciales autorizados): el `subject_token` debe haberse emitido al cliente o a una API de su aplicación; el token nuevo conserva `sub`, agrega `act` (anidando cadenas previas), se limita a los scopes del cliente en la API destino, usa los permisos de la aplicación de esa API y nunca dura más que el original. SDK: `ExchangeTokenAsync`. Pruebas: `TokenExchange_LetsAnApiCallAnotherApiOnBehalfOfTheUser`, contrato SDK.
- [ ] **SAML-01** AuthCenter no puede actuar como IdP SAML para aplicaciones que sólo hablan SAML.
- [ ] **DIS-01** El SDK no se distribuye: NuGet sólo como artefacto de CI, npm `private`, sólo
  `net10.0`.

### B. SDK de integración

- [x] **SDK-01** El parámetro documentado `?return_url=` se ignora (se enlaza como `returnUrl`).
  *Resuelto:* el login del BFF lee `return_url` (y acepta `returnUrl`).
- [x] **SDK-02** `ClientCredentialsAsync()` falla con los scopes por defecto (`invalid_scope`).
  *Resuelto:* sin scopes explícitos no se envía `scope` y AuthCenter emite los scopes de máquina permitidos.
- [ ] **SDK-03** Endpoints `/oauth/*` codificados: ignoran discovery y el path base.
- [ ] **SDK-04** No se incluye un coordinador de refresh distribuido.
- [x] **SDK-05** Un fallo de validación tras el refresh termina en HTTP 500.
  *Resuelto:* un token rotado que no valida cierra la sesión y responde `REFRESH_TOKEN_INVALID` (401).
- [x] **SDK-06** No hay prueba de contrato SDK↔servidor en CI (la prueba unitaria usa claims
  sintéticos).
  *Resuelto:* `SdkContractTests` (categoría Conformance) ejecuta el SDK real contra el servidor: discovery, login BFF con `return_url`, roles y permisos, refresh, logout, API con `at+jwt`/audiencia/roles/permisos/scopes, refresh y revocación de bajo nivel y `client_credentials`.
- [ ] **SDK-07** El SDK TypeScript no valida ID token/nonce/`iss`, no procesa el callback y no tiene
  userinfo ni logout.
- [ ] **SDK-08** El quickstart SPA sólo tiene el botón de login.
- [ ] **SDK-09** Falta sobrecarga basada en `IConfiguration` y documentación de `LoginUrl`,
  recursos y logout.
- [x] **SDK-10** *(nuevo)* El BFF no permitía pedir `prompt`, `max_age`, `login_hint` ni
  `acr_values`, y un `prompt=none` sin sesión terminaba en un error genérico.
  *Resuelto:* `/auth/login` reenvía esos parámetros si están bien formados y `/auth/error` informa
  `error=login_required|consent_required|interaction_required|…`. Pruebas:
  `Bff_ForwardsWellFormedSingleSignOnParameters`,
  `Bff_SilentSignInWithoutSession_ReportsLoginRequiredToTheApplication`.

### C. Seguridad

- [x] **SEC-01** Los 39 validadores FluentValidation están registrados pero nunca se ejecutan (la API
  aceptó un cliente con redirect `http://`, grant `password` y tokens de un año).
  *Resuelto:* filtro global `RequestValidationFilter` que ejecuta el validador de cada argumento; los códigos estables se conservan con `WithErrorCode`. Pruebas: `RequestValidationTests`.
- [x] **SEC-02** Redirección abierta en el login hospedado (`return_url=/\evil.example`).
  *Resuelto:* `safeLocalPath` resuelve la ruta contra el origen y rechaza barras invertidas y caracteres de control. Pruebas: `tests/AuthCenter.HostedUi.Tests`.
- [~] **SEC-03** La federación reactiva accesos revocados por un administrador.
  *Código corregido:* sólo se concede acceso JIT si nunca hubo asignación; un registro revocado o pendiente se rechaza con `ACCESS_DENIED` y auditoría. La prueba de extremo a extremo llega con el IdP de pruebas de FED-01.
- [ ] **SEC-04** `/api/federation/route` es anónimo y permite sondear grupos/atributos por email.
- [x] **SEC-05** Los usuarios con contraseña temporal no pueden entrar por el login hospedado.
  *Resuelto:* el login hospedado recibe `requiresPasswordChange` y completa el cambio en `POST /ui-api/session/forced-change`. Pruebas: `ForcedPasswordChangeTests`.
- [ ] **SEC-06** Interoperabilidad con IdPs: issuer con `/` final, `email_verified` obligatorio,
  firma SAML sólo a nivel Response, entity IDs no HTTPS, aserciones cifradas.
- [x] **SEC-07** `AllowPasswordLogin` no se aplica en el login (no hay aplicaciones sólo federadas).
  *Resuelto:* `AllowPasswordLogin=false` rechaza con `PASSWORD_LOGIN_DISABLED` antes de verificar la contraseña. Prueba: `PasswordLogin_DisabledForApplication_IsRejectedWithoutRevealingPasswordValidity`.
- [x] **SEC-08** *(nuevo, hallado durante la remediación)* El cambio de contraseña forzado emitía
  tokens sin pasar por la política de acceso ni por el MFA: con la contraseña de un usuario con MFA
  y cambio pendiente se evitaba el segundo factor.
  *Resuelto:* el cambio forzado ejecuta la misma compuerta que el login (`MFA_REQUIRED` cuando
  corresponde). Prueba: `ForcedChange_WithEnabledMfa_StillRequiresTheSecondFactor`.

- [x] **SEC-09** *(nuevo, hallado durante la remediación)* Los permisos administrativos de
  AuthCenter se comparaban sólo por código: un token emitido para otra aplicación con un permiso
  homónimo (p. ej. `AUTHCENTER_USERS_READ` creado en esa aplicación) abría la API administrativa
  (verificado: `GET /api/users` respondía 200).
  *Resuelto:* `PermissionAuthorizationHandler` exige además que la sesión se haya emitido para
  `AUTHCENTER`, y el prefijo `AUTHCENTER_` queda reservado (`RESERVED_PERMISSION_CODE`). Pruebas:
  `PermissionOfAnotherApplication_DoesNotOpenTheAuthCenterAdminApi`,
  `AuthCenterPermissionCodes_AreReservedForAuthCenter`.

### D. Backend administrativo y Event Hooks

- [x] **HOOK-01** Reenviar dos veces la misma entrega fallida no la vuelve a encolar.
  *Resuelto:* al volver a dead-letter se limpia la clave del replay anterior y la consola envía un `Idempotency-Key` nuevo por acción. Prueba relacional: `EventHookReplayRelationalTests`.
- [x] **HOOK-02** El listado de entregas se corta en 20 elementos.
  *Resuelto:* la consola usa el contrato paginado con filtros; la forma legacy devuelve hasta 100 elementos y `X-Total-Count`.
- [ ] **HOOK-03** Faltan catálogo de tipos de evento, rotación de secreto y `CreatedAt` en entregas.
- [ ] **ADM-01** Filtro de auditoría por entidad (`EntityName`/`EntityId`).
- [ ] **ADM-02** Concurrencia optimista en usuarios, aplicaciones, roles, grupos, permisos,
  clientes, políticas y esquemas.
- [ ] **ADM-03** Idempotency keys en mutaciones reintentables.
- [ ] **ADM-04** Prueba de conexión con el IdP.
- [ ] **ADM-05** Operadores de group rules además de `eq`.
- [ ] **ADM-06** Permisos dedicados para hooks, federación y provisioning.
- [ ] **ADM-07** SCIM: `/Schemas`, `/ResourceTypes`, PUT, orden, ETag y diagnóstico.
- [ ] **ADM-08** Mapeo de claims del IdP a grupos del directorio.

### E. Consola administrativa

- [ ] **UI-01** Gestión completa de Event Hooks.
- [ ] **UI-02** Dashboard con datos reales.
- [ ] **UI-03** System Log completo (actor, detalle, exportación, enlaces por entidad, debounce).
- [ ] **UI-04** Editor de esquemas de perfil.
- [ ] **UI-05** Observabilidad: ErrorBoundary, `traceId` (hoy siempre nulo), versión, errores
  globales.
- [ ] **UI-06** E2E contra backend real.
- [ ] **UI-07** Accesibilidad: axe ampliado y navegación por teclado (la revisión manual humana queda
  como acción del propietario).
- [ ] **UI-08** Retiro de `/admin` (borra URLs de branding; el portal enlaza ahí).
- [ ] **UI-09** Selectores limitados a 100, filtro de acceso pendiente, componentes faltantes,
  código muerto, permiso de Event Hooks, indicador de entorno.
- [ ] **UI-10** Advertencia de React "uncontrolled → controlled" en reglas de enrutamiento.
- [ ] **UI-11** Pantallas para recursos de API, proveedores de servicio SAML, nuevos campos de
  clientes OAuth y gobierno.
- [x] **UI-12** *(nuevo)* E2E intermitente "creates, rotates and revokes a scoped provisioning
  token": tras rotar, la página reutilizaba la instancia del token anterior y el diálogo de
  reautenticación se desmontaba al cargar el nuevo token.
  *Resuelto:* una instancia de página por token (`key`) y la prueba espera al token rotado antes de
  actuar. Verificado con ejecuciones repetidas.

### F. Login hospedado y portal

- [ ] **HL-01** El login no envía el OTP por email, no guía la inscripción MFA, no ofrece
  recuperación de contraseña ni magic link, exige email para passkey y no maneja interacciones
  expiradas.
- [ ] **PORTAL-01** El portal no permite inscribir factores MFA, códigos de respaldo, cambiar
  contraseña o email, eliminar la cuenta, ver aplicaciones ni vincular/desvincular proveedores.

### G. Gobierno de accesos

- [ ] **GOV-01** Owners por aplicación, solicitudes de acceso con aprobación, revisiones periódicas
  y segregación de funciones.

### H. Operación

- [P] **OPS-01** El CI de `main` (run 69) no llegó a ejecutarse (sin runner ni logs): revisar
  minutos/facturación de GitHub Actions.
- [P] **OPS-02** `main` no tiene protección de rama.
- [ ] **OPS-03** Confirmar la migración 24 en Azure SQL; readiness que detecte migraciones
  pendientes.
- [P] **OPS-04** Carga inicial productiva (administrador inicial).
- [P] **OPS-05** Primera rotación de la llave de firma.
- [P] **OPS-06** Purgar del historial la llave RSA retirada (requiere reescritura autorizada).
- [P] **OPS-07** Origen real del frontend en CORS y ActionLinks.
- [ ] **OPS-08** Vulnerabilidades moderadas npm (vitest) y parches NuGet 10.0.12.
- [x] **OPS-09** Las pruebas relacionales "pasan" sin ejecutarse cuando no hay SQL Server.
  *Resuelto:* atributo `[RelationalFact]`: sin SQL Server las pruebas se reportan como omitidas.
- [P] **OPS-10** Ramas remotas ya integradas.
- [ ] **OPS-11** Dependabot, presupuesto de bundle y cobertura en CI.
- [ ] **OPS-12** Pruebas de carga con directorios grandes.
- [x] **OPS-13** *(nuevo)* Prueba intermitente del SDK (`IDX10511` al validar el ID token bajo
  ejecución paralela).
  *Causa raíz:* una prueba validaba con un `RsaSecurityKey` sobre un `RSA` que luego liberaba; el
  proveedor de firma queda en la caché global `CryptoProviderFactory.Default` indexado por la huella
  de la llave, la misma que usan el BFF y la API del SDK. *Resuelto:* la prueba construye la llave
  con `RSAParameters`. Verificado con ejecuciones repetidas de la suite completa.

### I. Documentación

- [ ] **DOC-01** Conteos y estados desactualizados (plan admin, roadmap "Fase 5 lista",
  REMEDIACION, TODO).
- [ ] **DOC-02** README: `traceId`, descripción del rol Admin, forma legacy de entregas.
- [ ] **DOC-03** Guía de integración: `LoginUrl`, recursos de API, logout, federación y SDK.
- [ ] **DOC-04** Runbooks de operador y soporte para la consola.

## Bitácora de avance

| Fecha | Cambio | Puntos |
|---|---|---|
| 2026-09-26 | Documento de seguimiento creado a partir del análisis. | — |
| 2026-09-26 | F1: validación en la frontera de la API, redirección abierta, contraseña temporal y bypass de MFA, `AllowPasswordLogin`, acceso JIT, Event Hooks, pruebas relacionales y defectos del SDK. | SEC-01/02/03/05/07/08, HOOK-01/02, OPS-09, SDK-01/02/05 |
| 2026-09-26 | F2: contrato de claims `role`, `at+jwt`, higiene de ID token y respuestas, discovery e issuer, prueba de contrato SDK↔servidor. | TOK-01, TOK-03, DISC-01, SDK-06 |
| 2026-09-26 | F3: sesión SSO real en `/oauth/authorize` (`prompt`, `max_age`, `id_token_hint`, `form_post`), login hospedado guiado por la interacción, interacciones ligadas al navegador, `sid`/`auth_time`/`amr`/`acr`, continuidad de sesión, parámetros SSO en el SDK; hallazgos nuevos SEC-09 (permisos entre aplicaciones) y OPS-13 (prueba intermitente). | SSO-01/03/04, TOK-03, FED-02, DISC-01, SEC-09, SDK-10, OPS-13 |
| 2026-09-26 | F4: políticas, MFA y `acr_values` de la aplicación destino en el atajo SSO, al completar y al renovar; step-up en el login hospedado conservando la sesión. | SSO-02 |
| 2026-09-26 | F5: logout global — `end_session_endpoint` con confirmación hospedada, fin de sesión SSO en cascada (grants OAuth y back-channel logout por outbox), configuración de logout por cliente (API y consola), cierre global y receptor back-channel en el SDK; E2E intermitente corregido. | LOG-01, DISC-01, UI-12 |
| 2026-09-26 | F6: catálogo de APIs (RFC 8707) con audiencia y permisos de la aplicación dueña, token exchange (RFC 8693), introspección (RFC 7662), SDK y samples alineados; la consola acepta scopes de API y el grant de token exchange (el editor del catálogo queda en UI-11). | TOK-02, OIDC-01, OIDC-02, DISC-01 |
| 2026-09-26 | F7: límites de tasa por IP, cuenta y cliente OAuth con reglas configurables; CORS por endpoint con orígenes por cliente y sin CORS para la sesión hospedada. | RL-01, CORS-01 |
