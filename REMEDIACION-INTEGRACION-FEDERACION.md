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

- [x] **FED-01** La federación empresarial (OIDC/SAML entrante) no está conectada al login hospedado
  ni al flujo `/oauth/authorize`: `oidc/complete` y `saml/acs` devuelven tokens en JSON, no existe
  callback OIDC en servidor y el login no ofrece descubrimiento por dominio.
  *Resuelto:* el login hospedado descubre el IdP por dominio (`POST /ui-api/session/federation/discover`) o usa `idp`/`domain_hint`, y `POST /ui-api/session/federation/start` devuelve la redirección ligada al navegador (OIDC code+PKCE+nonce con `prompt=login` si la solicitud exige autenticación fresca; SAML AuthnRequest firmado con `ForceAuthn`). Nuevo callback OIDC en servidor (`GET /api/federation/oidc/callback`, por defecto el callback de cada proveedor) y el ACS SAML responden 303 al login con un resultado de un solo uso ligado a la cookie `__Host-AuthCenter.Browser`; el login lo canjea con `POST /ui-api/session/federation/complete`, donde corren la política de acceso y el MFA de la aplicación antes de crear la sesión SSO, y la interacción OAuth continúa (consentimiento, código). Un callback entregado a otro navegador no inicia sesión (CSRF de login). Hallado y corregido durante la prueba: el ACS rechazaba con `INVALID_CSRF_TOKEN` la respuesta de un IdP del mismo sitio si el navegador ya tenía sesión (el ACS queda exento: lo protegen la firma y el RelayState de un solo uso). La API JSON sigue disponible para integraciones con callback propio. **Requisito de despliegue:** migración `AddFederationHostedLogin`, `Oidc:PublicOrigin` configurado y el callback `https://<host>/api/federation/oidc/callback` registrado en cada IdP OIDC. Pruebas: `FederationHostedLoginTests` (14, IdP OIDC y SAML simulados en proceso).
- [x] **FED-02** `login_hint`, `idp` y `domain_hint` se ignoran en `/oauth/authorize`.
  *Resuelto:* `login_hint` se valida, se guarda en la interacción y prellena el login; `idp` debe ser un proveedor activo de la aplicación del cliente y `domain_hint` un dominio (normalizado a minúsculas/punycode); ambos se exponen en el contexto de la interacción y el login redirige solo al IdP. SDK: `/auth/login?idp=&domain_hint=` y `BuildAuthorizationUri(identityProvider, domainHint)`. Pruebas: `AuthorizeParameters_ValidateTheProviderAndExposeTheDomainHint`, `OidcFederation_RequestedWithIdp_*`, `SdkContractTests.Bff_ForwardsWellFormedSingleSignOnParameters`.
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
- [x] **DIS-01** El SDK no se distribuye: NuGet sólo como artefacto de CI, npm `private`, sólo
  `net10.0`.
  *Resuelto:* `AuthCenter.Client` 1.1.0 compila para `net8.0` (LTS, paquetes 8.0.31) y `net10.0`, con símbolos (`snupkg`) y metadatos; `@authcenter/client` 1.1.0 deja de ser privado (`publishConfig` público con provenance). Workflow `SDK release` (`.github/workflows/sdk-release.yml`): en una etiqueta `sdk-vX.Y.Z` comprueba que ambas versiones coinciden con la etiqueta, prueba, empaqueta y publica en NuGet y npm desde el entorno protegido `sdk-release`; manualmente sólo empaqueta salvo que se pida publicar. Los nombres `AuthCenter.Client` y `@authcenter/client` estaban libres al 2026-09-26. **Acción del propietario (OPS-14):** credenciales y entorno de publicación.
- [P] **OPS-14** *(nuevo)* Publicación de los SDK: crear el entorno `sdk-release` con revisores,
  los secretos `NUGET_API_KEY` y `NPM_TOKEN` (o trusted publishing de npm), la organización npm
  `authcenter`, reservar el ID NuGet y confirmar la licencia (`package.json` declara MIT; falta el
  archivo `LICENSE` y `PackageLicenseExpression` en NuGet). Después, publicar con la etiqueta
  `sdk-v1.1.0`.

### B. SDK de integración

- [x] **SDK-01** El parámetro documentado `?return_url=` se ignora (se enlaza como `returnUrl`).
  *Resuelto:* el login del BFF lee `return_url` (y acepta `returnUrl`).
- [x] **SDK-02** `ClientCredentialsAsync()` falla con los scopes por defecto (`invalid_scope`).
  *Resuelto:* sin scopes explícitos no se envía `scope` y AuthCenter emite los scopes de máquina permitidos.
- [x] **SDK-03** Endpoints `/oauth/*` codificados: ignoran discovery y el path base.
  *Resuelto:* `AuthCenterClient` lee sus endpoints del discovery (`GetDiscoveryDocumentAsync`, caché de una hora compartida por authority; exige `issuer` = authority y endpoints HTTPS) para token, revocación, introspección, UserInfo y logout; nuevos `BuildAuthorizationUriAsync`, `GetUserInfoAsync` y `BuildEndSessionUriAsync`; el path base del authority se conserva. SDK TypeScript igual (discovery validado). Pruebas: `SdkContractTests.LowLevelClient_UsesDiscovery_ForUserInfoAndLogout`, pruebas del SDK TS.
- [x] **SDK-04** No se incluye un coordinador de refresh distribuido.
  *Resuelto:* `DistributedAuthCenterRefreshCoordinator` sobre el `IDistributedCache` de los tickets: una instancia renueva y las demás esperan su resultado (compartido 30 s, cifrado con Data Protection; un rechazo también se comparte para no reutilizar el token), lock confirmado por relectura y opción de implementación propia para locks atómicos. Se activa con `UseDistributedRefreshCoordination` o `AddAuthCenterDistributedRefreshCoordination()`. Pruebas: `DistributedRefreshCoordinator_RefreshesOnceAcrossInstances_AndSharesTheResult`, `DistributedRefreshCoordinator_SharesARejectionInsteadOfReplayingTheToken`.
- [x] **SDK-05** Un fallo de validación tras el refresh termina en HTTP 500.
  *Resuelto:* un token rotado que no valida cierra la sesión y responde `REFRESH_TOKEN_INVALID` (401).
- [x] **SDK-06** No hay prueba de contrato SDK↔servidor en CI (la prueba unitaria usa claims
  sintéticos).
  *Resuelto:* `SdkContractTests` (categoría Conformance) ejecuta el SDK real contra el servidor: discovery, login BFF con `return_url`, roles y permisos, refresh, logout, API con `at+jwt`/audiencia/roles/permisos/scopes, refresh y revocación de bajo nivel y `client_credentials`.
- [x] **SDK-07** El SDK TypeScript no valida ID token/nonce/`iss`, no procesa el callback y no tiene
  userinfo ni logout.
  *Resuelto:* `@authcenter/client` 1.1.0: `createSignInUrl`/`signInRedirect` (state, nonce y PKCE de un solo uso en `sessionStorage`, `prompt`, `max_age`, `login_hint`, `acr_values`, `idp`, `domain_hint`, `resource`, `returnTo` local), `handleCallback` (errores OAuth como `AuthCenterError`, canje PKCE, validación del ID token: firma RS256 con JWKS y rotación de llave, `iss`, `aud`/`azp`, `exp`/`iat`/`nbf`, `nonce`, `max_age`), `refresh` con `resource`, `revoke`, `userInfo`, `endSessionUrl`/`signOutRedirect`; endpoints por discovery. Pruebas: 11 (`sdk/typescript/test`), incluidos firma alterada, nonce, audiencia, issuer, expiración, reuso de state y rutas de retorno inseguras.
- [x] **SDK-08** El quickstart SPA sólo tiene el botón de login.
  *Resuelto:* `samples/spa` completo: login, callback validado, SSO silencioso (`prompt=none` con manejo de `login_required`), claims, UserInfo, llamada a una API con el access token (`VITE_API_URL`/`VITE_API_RESOURCE`) y logout global; tokens sólo en memoria; README con el registro del cliente (redirect, post-logout y CORS). El build compila TypeScript antes de Vite.
- [x] **SDK-09** Falta sobrecarga basada en `IConfiguration` y documentación de `LoginUrl`,
  recursos y logout.
  *Resuelto:* `AddAuthCenterBff(IConfiguration)`, `AddAuthCenterClient(IConfiguration)` y `AddAuthCenterJwtBearer(IConfiguration)` (errores con la ruta exacta de la clave faltante); samples .NET los usan. README del SDK y `docs/integration-quickstarts.md` documentan `LoginUrl`, registro del cliente, recursos, logout, federación y varias instancias. Prueba: `Configuration_BindsTheBffAndTheResourceServer`.
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
- [x] **SEC-03** La federación reactiva accesos revocados por un administrador.
  *Resuelto:* sólo se concede acceso JIT si nunca hubo asignación; un registro revocado o pendiente se rechaza con `ACCESS_DENIED` y auditoría. Prueba de extremo a extremo con IdP simulado: `AccessRevokedByAnAdministrator_IsNotRegrantedByFederation`.
- [x] **SEC-04** `/api/federation/route` es anónimo y permite sondear grupos/atributos por email.
  *Resuelto:* el descubrimiento anónimo del login sólo evalúa reglas por dominio; las condiciones de grupo o atributo sólo aplican al usuario que ya inició sesión en ese navegador. `/api/federation/route` (simulación administrativa que evalúa todo e informa la regla aplicada) exige `AUTHCENTER_APPLICATIONS_READ`; el descubrimiento tiene límite de tasa por IP y por correo. Prueba: `HomeRealmDiscovery_UsesDomainRulesForAnonymousCallers_AndDirectoryRulesOnlyForTheSignedInUser`.
- [x] **SEC-05** Los usuarios con contraseña temporal no pueden entrar por el login hospedado.
  *Resuelto:* el login hospedado recibe `requiresPasswordChange` y completa el cambio en `POST /ui-api/session/forced-change`. Pruebas: `ForcedPasswordChangeTests`.
- [x] **SEC-06** Interoperabilidad con IdPs: issuer con `/` final, `email_verified` obligatorio,
  firma SAML sólo a nivel Response, entity IDs no HTTPS, aserciones cifradas.
  *Resuelto:* issuer OIDC comparado sin importar la `/` final (se guarda tal como se escribe); `RequireVerifiedEmail` por proveedor (sin `email_verified` sólo se confía en los dominios de sus reglas); `client_secret_basic` cuando el IdP sólo lo admite; algoritmos RSA/ECDSA para ID tokens. SAML: firma de Response, de aserción o ambas (RSA SHA-256+, toda firma presente debe validar, referencias al elemento exacto con ID único), entity IDs con cualquier URI absoluta (https, http, urn), aserciones cifradas con RSA-OAEP y AES-CBC/GCM (RSA 1.5 rechazado; metadata publica la llave de cifrado), réplica bloqueada por Response y por aserción. Pruebas: `IssuerWithTrailingSlash_AndUnverifiedEmailOfARoutedDomain_AreAccepted`, `SamlFederation_WithAnEncryptedAssertionSignedAlone_*`, `SamlFederation_WithATamperedAssertion_IsRejected`.
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

- [x] **SEC-10** *(nuevo, hallado durante la remediación)* Los inicios de sesión federados (API
  JSON y ACS) emitían la sesión directamente, sin la política de acceso ni el MFA de la aplicación:
  un usuario con MFA vinculado a un IdP lo evitaba.
  *Resuelto:* toda sesión federada pasa por `IAuthService.CompleteFederatedSignInAsync` (acceso,
  política publicada con riesgo, `RequireMfa` y MFA del usuario); la API JSON responde
  `mfaPendingToken`. Pruebas: `FederatedUserWithMfa_CompletesTheSecondFactor_UnlessTheUpstreamMfaIsTrusted`,
  `JsonApiFederation_RunsTheApplicationMfaGate`.

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
- [x] **ADM-04** Prueba de conexión con el IdP.
  *Resuelto:* `POST /api/federation/providers/{id}/test` (sin cambios, auditado): OIDC discovery sin caché, issuer, endpoints HTTPS, llaves, `code`, PKCE, callback hospedado, secret y scope `email`; SAML certificado del IdP (vigencia, tamaño de llave), URL de SSO, certificado propio y entity ID/ACS; más regla por dominio. `GET /api/federation/service-provider` da los valores a registrar en el IdP. Consola: botón "Probar conexión". Pruebas: `ConnectionTest_ChecksUpstreamMetadataAndCertificates`, e2e "maps IdP groups…".
- [ ] **ADM-05** Operadores de group rules además de `eq`.
- [ ] **ADM-06** Permisos dedicados para hooks, federación y provisioning.
- [ ] **ADM-07** SCIM: `/Schemas`, `/ResourceTypes`, PUT, orden, ETag y diagnóstico.
- [x] **ADM-08** Mapeo de claims del IdP a grupos del directorio.
  *Resuelto:* `GroupsClaim` y `GroupMappings` (tabla `FederationGroupMappings`) por proveedor; en cada inicio de sesión federado se agregan y quitan las membresías de los grupos mapeados (el resto no cambia), un cambio cierra las sesiones existentes y se audita (`FEDERATION_GROUPS_SYNCED`); con overage de Entra ID (`_claim_names`/`groups.link`) no se tocan. MFA del IdP de confianza con `TrustUpstreamMfa`. Consola: editor de mapeos. Pruebas: `GroupClaims_KeepTheMappedMembershipsInSync`, e2e.

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
- [x] **UI-10** Advertencia de React "uncontrolled → controlled" en reglas de enrutamiento.
  *Resuelto:* la advertencia venía del editor de proveedores (al cargar un proveedor SAML React reutilizaba el panel OIDC y un input no controlado pasaba a controlado); cada panel de protocolo tiene su propia `key`. Verificado sin advertencias en los e2e de federación.
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
- [x] **DOC-03** Guía de integración: `LoginUrl`, recursos de API, logout, federación y SDK.
  *Resuelto:* `docs/integration-quickstarts.md` reescrita: elección de flujo, paquetes, registro del cliente por tipo (redirects, `LoginUrl`, post-logout, back-channel, CORS, scopes), BFF, API protegida, SPA, parámetros SSO, logout, federación y despliegue con varias instancias.
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
| 2026-09-26 | F8: federación empresarial conectada al login hospedado y a `/oauth/authorize` (descubrimiento por dominio, `idp`/`domain_hint`, callback OIDC y ACS con resultado ligado al navegador, compuerta de política y MFA), interoperabilidad OIDC/SAML (issuer, `email_verified`, firmas de aserción, cifrado), mapeo de grupos y MFA del IdP, prueba de conexión, consola y SDK; hallazgos nuevos SEC-10 (federación sin MFA/política) y ACS con sesión existente. | FED-01, FED-02, SEC-03, SEC-04, SEC-06, SEC-10, ADM-04, ADM-08, UI-10 |
| 2026-09-26 | F9: SDK TypeScript completo (callback, validación del ID token, UserInfo, logout), quickstart SPA, SDK .NET con discovery, coordinador de refresh distribuido y configuración por `IConfiguration`, multi-target net8/net10, paquetes publicables y workflow de publicación; guía de integración. | DIS-01, SDK-03/04/07/08/09, DOC-03, OPS-14 |
