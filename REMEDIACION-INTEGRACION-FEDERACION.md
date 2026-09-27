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

## Estado verificado al cierre (2026-09-26, después de F15)

- Puntos: 89 resueltos con evidencia (`[x]`) y 7 que dependen del propietario (`[P]`: OPS-02,
  OPS-04, OPS-05, OPS-06, OPS-07, OPS-10 y OPS-14), cada uno con su
  procedimiento en [`docs/operations/OWNER-ACTIONS.md`](docs/operations/OWNER-ACTIONS.md), igual que
  la revisión manual con lectores de pantalla de UI-07. Ningún punto queda pendiente de código.
- Build .NET 10 Release: 0 errores, 0 advertencias; ningún cambio del modelo sin migración (35
  migraciones).
- Pruebas .NET: 74 unitarias y 345 de integración con SQL Server 2022 real (la prueba de escala se
  omite salvo con `AUTHCENTER_SCALE_TESTS=1`); cobertura combinada 85,1 % de líneas y 64,5 % de ramas.
- Consola admin: lint, typecheck, 79 pruebas unitarias (lógica: 77,6 % de líneas), build dentro del
  presupuesto (64,9 KB de entrada y 264 KB en total, gzip) y 301 escenarios Playwright, con axe
  (incluida la regla WCAG 2.2 AA `target-size`) y reflow a 320 px en las 39 rutas.
- Login hospedado, portal y consola contra la API Release y SQL Server: 6 pruebas unitarias y 31
  escenarios end-to-end, axe incluido.
- SDK TypeScript 11/11; quickstart SPA compila; `npm audit` sin vulnerabilidades en los cuatro
  paquetes; `dotnet list package --vulnerable --include-transitive` sin hallazgos; Gitleaks limpio.
- GitHub Actions: el PR [Eduard0corona/AuthCenter#23](https://github.com/Eduard0corona/AuthCenter/pull/23) pasó el workflow `CI/CD` completo en 11 min 40 s.
- Producción: con las 35 migraciones aplicadas en Azure SQL, el PR se fusionó (`9cdb8c1`) y el run `CI/CD` #73 lo desplegó en Azure con la verificación de salud, esquema y consola en verde.
- Escala: con 100 000 usuarios y 1 000 grupos todas las lecturas administrativas responden en menos
  de 0,62 s ([`docs/operations/CAPACITY.md`](docs/operations/CAPACITY.md#directorio-grande)).

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
- [x] **SAML-01** AuthCenter no puede actuar como IdP SAML para aplicaciones que sólo hablan SAML.
  *Resuelto:* AuthCenter es IdP SAML 2.0 (Web Browser SSO). Metadatos en `/saml/idp/metadata` (entity ID configurable con `Saml:IdentityProviderEntityId`, por defecto esa URL), SSO en `/saml/idp/sso` (HTTP-Redirect y HTTP-POST; el POST cruzado se convierte en un 303 a un GET propio porque la cookie de sesión es `SameSite=Lax`) y SLO en `/saml/idp/slo`. Cada aplicación SAML (entidad `SamlServiceProvider`, versionada) pertenece a una aplicación de AuthCenter, y una compuerta compartida con OAuth (`ISsoAccessGate`, extraída de `OAuthAuthorizationService`) aplica acceso, política, riesgo y MFA de esa aplicación a cada aserción. Confianza: emisor registrado y activo, frescura (5 min + `ClockSkewSeconds`), `Destination`, ACS registrado, binding POST, repetición rechazada y firma verificada (RSA-SHA256/512; query de HTTP-Redirect o firma XML envolvente de la raíz en HTTP-POST) cuando se exige o viene; una solicitud no confiable termina en una página de error propia (CSP `form-action 'none'`) y nunca se envía a la dirección que nombra; tras confiar, las negativas vuelven al SP como estado SAML (`InvalidNameIDPolicy`, `NoPassive`, `RequestDenied`). Sin sesión SSO continúa en el login hospedado (`/login?saml_interaction=`) con la marca de la aplicación y sin consentimiento; `RequestedAuthnContext` MFA (REFEDS, Microsoft) provoca step-up, `ForceAuthn` exige un inicio de sesión posterior a la solicitud e `IsPassive` responde `NoPassive`. Aserción siempre firmada (RSA-SHA256, C14N exclusiva) y respuesta firmada salvo que se desactive; cifrado opcional (AES-256-CBC + RSA-OAEP); NameID email, persistente (HMAC con sal por aplicación) o no especificado; atributos `email`, `name`, `userId`, `roles`, `permissions`, `groups` y `profile:<clave>`; contexto MFA real de la sesión. Inicio desde AuthCenter (`/saml/idp/sso/initiate/{id}`, botón **Abrir** del portal) con `DefaultRelayState`. SLO iniciado por el SP: cierra la sesión indicada por `SessionIndex` (o la del navegador) si pertenece al NameID, con back-channel a los clientes OAuth; **limitación documentada:** no se propaga a las otras aplicaciones SAML de la sesión. API `/api/saml/service-providers` (CRUD con versión e `Idempotency-Key`, lectura de metadatos del SP, datos del IdP) con permisos `AUTHCENTER_SAML_APPS_READ/WRITE` (migración que los otorga a los roles con `AUTHCENTER_OAUTH_CLIENTS_*`), eventos `SAML_*` para auditoría y hooks, límite `saml-idp` 60/min por IP. **Requisito de despliegue:** migración `20260926161414_AddSamlIdentityProvider` y el certificado `Saml:SigningCertificateBase64` (sin él los endpoints responden 503 y la consola lo advierte). Pruebas: `SamlIdentityProviderTests` (11: metadatos; aserción firmada y solicitud de un solo uso; continuación en el login sólo desde el mismo navegador; negativas al SP por `IsPassive`, formato de NameID y usuario sin acceso; página de error para solicitudes no confiables; binding POST firmado; NameID persistente y cifrado; `ForceAuthn` y step-up MFA; inicio desde AuthCenter; SLO; validación de registros y lectura de metadatos; la compuerta compartida conserva las pruebas de políticas de OAuth), E2E del login hospedado contra la API real (`tests/AuthCenter.HostedUi.Tests/e2e/saml.spec.mjs`: solicitud del SP → login → aserción firmada con el certificado de los metadatos; portal **Abrir** y SSO sin volver a pedir credenciales; SP desconocido rechazado sin publicar) y consola (`e2e/saml.spec.ts`).
- [x] **DIS-01** El SDK no se distribuye: NuGet sólo como artefacto de CI, npm `private`, sólo
  `net10.0`.
  *Resuelto:* `AuthCenter.Client` 1.1.0 compila para `net8.0` (LTS, paquetes 8.0.31) y `net10.0`, con símbolos (`snupkg`) y metadatos; `@authcenter/client` 1.1.0 deja de ser privado (`publishConfig` público con provenance). Workflow `SDK release` (`.github/workflows/sdk-release.yml`): en una etiqueta `sdk-vX.Y.Z` comprueba que ambas versiones coinciden con la etiqueta, prueba, empaqueta y publica en NuGet y npm desde el entorno protegido `sdk-release`; manualmente sólo empaqueta salvo que se pida publicar. Los nombres `AuthCenter.Client` y `@authcenter/client` estaban libres al 2026-09-26. **Acción del propietario (OPS-14):** credenciales y entorno de publicación.
- [P] **OPS-14** *(nuevo)* Publicación de los SDK: crear el entorno `sdk-release` con revisores,
  los secretos `NUGET_API_KEY` y `NPM_TOKEN` (o trusted publishing de npm), la organización npm
  `authcenter`, reservar el ID NuGet y confirmar la licencia (`package.json` declara MIT; falta el
  archivo `LICENSE` y `PackageLicenseExpression` en NuGet). Después, publicar con la etiqueta
  `sdk-v1.1.0`.
  *Procedimiento:* [`docs/operations/OWNER-ACTIONS.md`](docs/operations/OWNER-ACTIONS.md#ops-14--publicación-de-los-sdk).

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

- [x] **SEC-11** *(nuevo, hallado durante la remediación)* Un código TOTP podía reutilizarse
  dentro de su ventana de validez (±30 s): un código observado o capturado por phishing servía de
  nuevo.
  *Resuelto:* cada paso de tiempo aceptado se recuerda por usuario (RFC 6238 §5.2) al iniciar
  sesión, activar, regenerar códigos o desactivar. Prueba: `AuthenticatorCode_IsAcceptedOnlyOnce`.
- [x] **SEC-12** *(nuevo)* Desvincular un proveedor empresarial y volver a entrar con él violaba el
  índice único (proveedor, sujeto) y respondía 500.
  *Resuelto:* el vínculo inactivo se reutiliza y reactiva. Prueba:
  `UnlinkedIdentity_SignsInAgain_ByReusingItsLink`.
- [x] **SEC-13** *(nuevo, F11)* SSRF en Event Hooks: el cliente HTTP seguía redirecciones (una URL pública podía redirigir a una IP interna), la IP se validaba antes de conectar pero la conexión volvía a resolver DNS (DNS rebinding) y no se bloqueaban IPv6 ULA (`fc00::/7`), NAT64/6to4 con IPv4 privada, CGNAT ni rangos reservados.
  *Resuelto:* el cliente `EventHooks` no sigue redirecciones y su `ConnectCallback` resuelve y valida la dirección que realmente marca; clasificación de direcciones ampliada (IPv4 reservadas y de documentación, ULA, Teredo, 6to4 y NAT64 según la IPv4 embebida). Pruebas: `OutboundUrlSafetyTests` (21 direcciones y rechazo de conexión a una IP interna).

### D. Backend administrativo y Event Hooks

- [x] **HOOK-01** Reenviar dos veces la misma entrega fallida no la vuelve a encolar.
  *Resuelto:* al volver a dead-letter se limpia la clave del replay anterior y la consola envía un `Idempotency-Key` nuevo por acción. Prueba relacional: `EventHookReplayRelationalTests`.
- [x] **HOOK-02** El listado de entregas se corta en 20 elementos.
  *Resuelto:* la consola usa el contrato paginado con filtros; la forma legacy devuelve hasta 100 elementos y `X-Total-Count`.
- [x] **HOOK-03** Faltan catálogo de tipos de evento, rotación de secreto y `CreatedAt` en entregas.
  *Resuelto (backend):* catálogo `EventTypes` (Domain) con cada acción auditada agrupada por área y el comodín `*`; `GET /api/event-hooks/event-types`; crear o editar un hook con un tipo desconocido responde `UNKNOWN_EVENT_TYPE`. `POST /api/event-hooks/{id}/rotate-secret` (prueba `admin.event-hook.rotate-secret`) devuelve el secreto nuevo una sola vez y, durante 24 h, cada entrega lleva dos firmas (`X-AuthCenter-Signature: v1=<nuevo>,v1=<anterior>`) para que el receptor actualice sin cortes. Las entregas tienen `CreatedAt` (índice, backfill con la mejor fecha conocida), se filtran y ordenan por él, y `GET /api/event-hooks/deliveries/{id}` devuelve el payload. Migración `AddEventHookSecretRotation` (backfill con `EXEC` para el script idempotente). Pruebas: `EventTypes_AreListedByArea_AndUnknownTypesAreRejected`, `RotatingTheSecret_NeedsAProof_AndSignsWithBothSecretsForTheGracePeriod`, `EveryAuditedAction_IsInTheCatalog` (recorre el código y exige que cada acción auditada esté en el catálogo). Consola: UI-01.
- [x] **HOOK-04** *(nuevo)* Sólo los eventos registrados con `AuditService` llegaban a los hooks; los servicios que agregan `AuditLog` directamente (invitaciones, aplicaciones, federación, SCIM…) nunca los disparaban.
  *Resuelto:* las entregas se encolan en `AuthCenterDbContext.SaveChanges` para cada `AuditLog` agregado, en la misma transacción que el cambio auditado, sin duplicados. Prueba: `AuditEventsWrittenByAnyService_ReachTheSubscribedHooks`.
- [x] **ADM-01** Filtro de auditoría por entidad (`EntityName`/`EntityId`).
  *Resuelto:* `GET /api/audit-logs` y la exportación filtran por `entityName`/`entityId` y cada evento trae el correo y nombre del actor. El CSV incluye id, actor, entidad y `traceId`, y neutraliza fórmulas de hoja de cálculo (`= + - @` al inicio). Pruebas: `Events_AreFilteredByEntity_AndNameTheirActor`, `Export_NeutralizesSpreadsheetFormulas`. Consola: UI-03.
- [x] **ADM-02** Concurrencia optimista en usuarios, aplicaciones, roles, grupos, permisos,
  clientes, políticas y esquemas.
  *Resuelto:* `Version` con token de concurrencia en aplicaciones, roles, grupos, permisos, clientes OAuth, reglas de política, atributos de perfil y APIs (más usuarios, cuyo guardado protege el concurrency stamp de Identity); cualquier cambio avanza la versión (`AuthCenterDbContext.SaveChanges`), los DTO la exponen y las actualizaciones aceptan `version`: si no coincide responden 409 `CONCURRENCY_CONFLICT` (filtro global que convierte a 409 los 400 con ese código y manejo de `DbUpdateConcurrencyException`); sin `version` gana la última escritura, como antes. Consola: cada editor envía la versión cargada y ofrece "Cargar la versión actual". Migración `AddAdministrativeVersions`. Pruebas: `OptimisticConcurrencyTests` (9 entidades), `VersionedRecords_RefuseTheSecondOfTwoConcurrentSaves` (SQL Server), e2e `concurrency.spec.ts`.
- [x] **ADM-03** Idempotency keys en mutaciones reintentables.
  *Resuelto:* filtro `[Idempotent]` en las altas y rotaciones administrativas (usuarios, invitaciones, aplicaciones, roles, permisos, grupos, clientes OAuth y su rotación, provisioning tokens y su rotación, hooks y su rotación, APIs, atributos de perfil, reglas, borradores y publicación de políticas, proveedores y reglas de federación, mappings y group rules): con `Idempotency-Key` la repetición devuelve la primera respuesta (`Idempotent-Replayed: true`) sin volver a ejecutar; la misma clave con otra petición → 422 `IDEMPOTENCY_KEY_REUSED`; en curso → 409; un 5xx libera la clave. Claves por usuario, método y ruta durante 24 h; las respuestas se guardan cifradas (algunas traen secretos de un solo uso). La consola envía una clave en cada POST, reutilizada por su reintento CSRF. Pruebas: `IdempotencyTests`, pruebas del cliente.
- [x] **ADM-04** Prueba de conexión con el IdP.
  *Resuelto:* `POST /api/federation/providers/{id}/test` (sin cambios, auditado): OIDC discovery sin caché, issuer, endpoints HTTPS, llaves, `code`, PKCE, callback hospedado, secret y scope `email`; SAML certificado del IdP (vigencia, tamaño de llave), URL de SSO, certificado propio y entity ID/ACS; más regla por dominio. `GET /api/federation/service-provider` da los valores a registrar en el IdP. Consola: botón "Probar conexión". Pruebas: `ConnectionTest_ChecksUpstreamMetadataAndCertificates`, e2e "maps IdP groups…".
- [x] **ADM-05** Operadores de group rules además de `eq`.
  *Resuelto:* operadores `eq`, `ne`, `in` (lista de 1 a 100 valores), `contains` y `startsWith` (texto, sin distinguir mayúsculas), `gt`/`gte`/`lt`/`lte` (números y fechas) y `exists`, validados contra el tipo del atributo (`INVALID_GROUP_RULE_OPERATOR`, `INVALID_GROUP_RULE_VALUE`) y guardados en la forma canónica del perfil (`GroupRuleEvaluator`). Un grupo con reglas activas es gestionado por ellas: basta con que se cumpla una; crear, editar, desactivar o borrar una regla recalcula la membresía de todo el directorio (`DynamicGroupMembershipService`, con el resultado en la auditoría), y cualquier cambio de perfil —SCIM o edición del administrador, antes sólo SCIM— recalcula los grupos del usuario; perder la membresía cierra sus sesiones, como un retiro manual. La vista previa evalúa igual que la membresía. Sin reglas activas el grupo conserva sus miembros y vuelve a ser manual; mientras tenga reglas, las altas y bajas manuales (consola y SCIM) se rechazan con `GROUP_MANAGED_BY_RULES` y el grupo expone `isRuleManaged`. Se evalúa el valor guardado del perfil; el valor por defecto del esquema no cuenta. De paso: un número entre comillas para un atributo numérico devolvía 500 y ahora es `PROFILE_VALUE_TYPE_MISMATCH`. Consola: operadores según el tipo del atributo, lista de valores por línea, aviso y sin altas manuales en grupos por reglas. Pruebas: `DynamicGroupRuleTests` (backfill, recálculo, OR entre reglas, fechas, `exists`/`ne`, 10 combinaciones inválidas, cambios de perfil con revocación, grupo gestionado), `UserProfileSchemaTests`, unitarias y e2e de la consola.
- [x] **ADM-06** Permisos dedicados para hooks, federación y provisioning.
  *Resuelto:* `AUTHCENTER_EVENT_HOOKS_READ/WRITE`, `AUTHCENTER_FEDERATION_READ/WRITE` y `AUTHCENTER_PROVISIONING_READ/WRITE` protegen sus controladores (antes `APPLICATIONS_*`); el rol Admin recibe los de lectura. La migración `AddOperationsPermissions` los crea y los otorga a cada rol que tenía el permiso `APPLICATIONS_*` equivalente, así nadie pierde acceso al actualizar. Pruebas: `OperationsPermissionsMigration_GrantsNewPermissionsToRolesHoldingLegacyOnes` (SQL Server) y metadatos de operaciones.
- [x] **ADM-07** SCIM: `/Schemas`, `/ResourceTypes`, PUT, orden, ETag y diagnóstico.
  *Resuelto:* descubrimiento completo y público (`ServiceProviderConfig` con sort/etag/patch, `ResourceTypes` y `ResourceTypes/{id}` con la extensión enterprise, `Schemas` y `Schemas/{id}` de User, Group y EnterpriseUser). PUT de usuarios y grupos con semántica de reemplazo; PATCH en las formas que envían Entra ID y Okta (con y sin `path`, `op` en mayúsculas, `active` como texto, extensiones URN anidadas o aplanadas, `members` quitados por lista de valores, reemplazados o seleccionados con `members[value eq "id"]`, antes una baja de Entra ID se ignoraba en silencio); `sortBy`/`sortOrder`, `startIndex`/`count` según la RFC (ya no error fuera de rango; `count=0` devuelve el total), filtro por `id`/`emails`, `attributes`/`excludedAttributes`. Cada recurso lleva `meta.version` y `ETag` débil: `If-None-Match` → 304, `If-Match` obsoleto → 412; 201 con `Location`; `emails` y `$ref` de miembros. Rutas SCIM reales para los mappings (`ScimPath`: sub-atributos, filtros de valor y extensiones; la forma con punto `urn:…:2.0:User.department` que antes nunca resolvía por el `2.0`, hoy sí) compartidas por SCIM, validación y simulación; los atributos mapeados se devuelven en su ruta y cuentan para la versión; un valor mapeado inválido responde `invalidValue` (antes 500) y las escrituras de usuario son atómicas (probado en SQL Server). Token conocido sin el scope → 403 `insufficient_scope`; revocado/expirado → 401 con motivo. Diagnóstico: cada solicitud con un token conocido se registra (`ScimRequestLogs`: método, ruta, estado, tipo SCIM, detalle, duración, traza; nunca el cuerpo) con retención `TokenHistoryDays`; `GET /api/provisioning-tokens/{id}/diagnostics` y `/requests?outcome=failed`; la consola muestra la URL base SCIM y el panel "Diagnóstico SCIM". Migración `AddScimDiagnostics`. Pruebas: `ScimProtocolTests` (7: descubrimiento, PUT/PATCH Entra/Okta, ETag, orden/página/proyección, grupos, mappings por ruta, diagnóstico), `ScimUserWrites_AreAtomic` (SQL Server), unitarias y e2e de la consola.
- [x] **ADM-08** Mapeo de claims del IdP a grupos del directorio.
  *Resuelto:* `GroupsClaim` y `GroupMappings` (tabla `FederationGroupMappings`) por proveedor; en cada inicio de sesión federado se agregan y quitan las membresías de los grupos mapeados (el resto no cambia), un cambio cierra las sesiones existentes y se audita (`FEDERATION_GROUPS_SYNCED`); con overage de Entra ID (`_claim_names`/`groups.link`) no se tocan. MFA del IdP de confianza con `TrustUpstreamMfa`. Consola: editor de mapeos. Pruebas: `GroupClaims_KeepTheMappedMembershipsInSync`, e2e.
- [x] **ADM-09** *(nuevo)* En SQL Server, las operaciones administrativas que abren una transacción (acceso heredado de un grupo, acceso directo, revocar acceso, quitar rol, desactivar y eliminar usuarios) respondían 500: el contexto usa la estrategia de reintentos de Azure SQL y EF Core rechaza una transacción abierta fuera de ella. Las pruebas usaban la base en memoria y no lo veían.
  *Resuelto:* esas operaciones corren como unidad reintentable (`RetriableUnits.RunRetriableAsync`: la estrategia repite la unidad completa, con el change tracker limpio). Nueva fábrica de pruebas `SqlServerWebApplicationFactory`, que levanta la API con su registro real de SQL Server (pool y reintentos) sobre una base aislada, y prueba `TransactionalAdminOperations_RunUnderTheRetryingExecutionStrategy`, que fallaba con 500 antes del cambio.

### E. Consola administrativa

- [x] **UI-01** Gestión completa de Event Hooks.
  *Resuelto:* lista con búsqueda y filtros; editor con alcance (plataforma o aplicación), catálogo de eventos por área con filtro, selección por área y comodín `*`; secreto de un solo uso al crear; verificación del endpoint con instrucciones; rotación del secreto con reautenticación y aviso del periodo de doble firma; fragmento para verificar la firma en el receptor; activar/desactivar; conflicto de versión con recarga. Entregas con filtros (hook, estado, tipo, evento, fechas), detalle con payload y último error, y replay con `Idempotency-Key`. Permisos `AUTHCENTER_EVENT_HOOKS_*`. E2E: `operations.spec.ts`.
- [x] **UI-02** Dashboard con datos reales.
  *Resuelto:* "Estado de la plataforma" con `GET /api/admin-dashboard` (se renueva cada minuto): usuarios, solicitudes de acceso pendientes (nuevo), aplicaciones, grupos, proveedores, tokens por vencer, hooks sin verificar, dead letters, inicios de sesión rechazados (ahora cuenta contraseña, bloqueo, MFA, passkey y federación) y riesgo alto; cada indicador enlaza a la página filtrada y se resalta cuando requiere atención. Sin `AUDIT_LOGS_READ` no se consulta. Prueba: `Dashboard_CountsRejectedSignIns_AndPendingAccessRequests`, e2e.
- [x] **UI-03** System Log completo (actor, detalle, exportación, enlaces por entidad, debounce).
  *Resuelto:* filtros por acción, aplicación, entidad, actor, traza y fechas locales, con debounce y en la URL; columna de actor (nombre y correo); enlace a la página de cada entidad; detalle con IP, agente, traza y metadatos, y atajos "eventos de este actor / de esta entidad / de esta traza"; "Ver historial" en las páginas de detalle; exportación CSV de todos los eventos filtrados (antes sólo la página visible; hasta 10 000, con aviso si se trunca). Pruebas: `Export_IncludesEveryMatchingEvent_NotJustOnePage`, e2e.
- [x] **UI-04** Editor de esquemas de perfil.
  *Resuelto:* `/admin-v2/profile-schema`: lista (con desactivados opcionales) y editor con clave estable, tipo, obligatorio (exige predeterminado), valor predeterminado tipado, longitudes, patrón, rango numérico y valores permitidos; sólo envía las restricciones del tipo; desactivar con confirmación; mensajes en español para `PROFILE_EXISTING_VALUES_INVALID` y demás códigos. E2E: `catalogs.spec.ts`.
- [x] **UI-05** Observabilidad: ErrorBoundary, `traceId` (hoy siempre nulo), versión, errores
  globales.
  *Resuelto:* el cliente toma el `traceId` del cuerpo de la respuesta y los mensajes lo muestran como referencia; mensajes en español con los detalles de validación; ErrorBoundary por página (con aviso de versión nueva cuando falta un chunk) y global; aviso de errores fuera de React; versión del servidor en la barra lateral. Hallazgo nuevo corregido: la cookie CSRF duraba 30 min mientras la sesión dura 8 h y cualquier otra pestaña la reemplazaba, así que la consola fallaba con `INVALID_CSRF_TOKEN`; ahora dura lo que la sesión y el cliente la renueva una vez y repite la solicitud.
- [x] **UI-06** E2E contra backend real.
  *Resuelto:* `tests/AuthCenter.HostedUi.Tests/e2e/admin.spec.mjs` recorre la consola (build de `src/AuthCenter.Admin/dist`) contra la API Release y SQL Server: inicio de sesión hospedado hacia `/admin-v2`, métricas, entorno y versión; esquema de perfil → historial en System Log → exportación CSV; catálogo de APIs → scope en un cliente OAuth sin alterar `backchannelLogoutSessionRequired`; renovación del token CSRF reemplazado por otra pestaña (la prueba falla si se quita la renovación); usuarios con acceso pendiente; operador sin permisos de Event Hooks (ruta restringida y 403 de la API); redirección de `/admin`. Todo con axe.
- [x] **UI-07** Accesibilidad: axe ampliado y navegación por teclado (la revisión manual humana queda
  como acción del propietario).
  *Resuelto:* `accessibility.spec.ts` corre axe en las rutas de la consola, escritorio y móvil (32 en F11; 39 desde que F13 y F14 agregaron SAML y gobierno) y prueba el teclado: salto al contenido (el `main` recibe el foco), foco en el encabezado de cada página, diálogos que atrapan el foco, cierran con Escape y lo devuelven al control que los abrió (`useModalDialog`, ahora en todos los diálogos, con nombre accesible), y el menú móvil que se cierra con Escape. Buscador de usuarios con patrón combobox ARIA y campos de scopes con nombres únicos. F15 activa la regla WCAG 2.2 AA `target-size`, verifica el reflow a 320 px de las 39 rutas y extiende axe al login hospedado, el portal y las páginas de enlaces (UI-13, HL-09). La revisión manual con lectores de pantalla sigue siendo acción del propietario ([P]).
  *Procedimiento:* [`docs/operations/OWNER-ACTIONS.md`](docs/operations/OWNER-ACTIONS.md#ui-07--revisión-manual-con-lectores-de-pantalla).
- [x] **UI-08** Retiro de `/admin` (borra URLs de branding; el portal enlaza ahí).
  *Resuelto:* `/admin` y `/admin.html` redirigen a `/admin-v2/`; `admin.html`/`admin.js` (que borraban las URLs de branding) se eliminaron junto con la forma legacy `deadLettersOnly` de las entregas; `/admin-v2` ahora recibe la CSP de las páginas propias (antes no tenía); el portal enlaza a `/admin-v2/`; el smoke de despliegue verifica la redirección y la CSP. Pruebas: `RetiredConsole_RedirectsToTheCurrentOne`, `FirstPartyUiDocuments_AlwaysApplyContentSecurityPolicy` con rutas de `/admin-v2`.
- [x] **UI-09** Selectores limitados a 100, filtro de acceso pendiente, componentes faltantes,
  código muerto, permiso de Event Hooks, indicador de entorno.
  *Resuelto:* los selectores de aplicaciones, roles, grupos y permisos recorren todas las páginas; los usuarios se buscan por nombre o correo (combobox accesible, filtrado por aplicación) en políticas de acceso; Usuarios filtra por acceso pendiente y por aplicación (el filtro de pendientes ya no incluye accesos revocados) y un enlace directo conserva su página (antes volvía a la 1); componentes compartidos (`Field`, `DebouncedTextField`, `UserPicker`, `HistoryLink`, `ErrorBoundary`, `useModalDialog`); se eliminaron `ComingSoonPage`, `PermissionGate` y `.empty-card`; permisos `AUTHCENTER_EVENT_HOOKS_*`/`FEDERATION_*`/`PROVISIONING_*` en rutas y navegación; el indicador de entorno usa el entorno real. E2E: `directory.spec.ts`.
- [x] **UI-10** Advertencia de React "uncontrolled → controlled" en reglas de enrutamiento.
  *Resuelto:* la advertencia venía del editor de proveedores (al cargar un proveedor SAML React reutilizaba el panel OIDC y un input no controlado pasaba a controlado); cada panel de protocolo tiene su propia `key`. Verificado sin advertencias en los e2e de federación.
- [x] **UI-11** Pantallas para recursos de API, proveedores de servicio SAML, nuevos campos de
  clientes OAuth y gobierno.
  *Resuelto:* F11 — catálogo de APIs (`/admin-v2/api-resources`), selector de scopes de API y `backchannelLogoutSessionRequired` en clientes OAuth. F13 — **Aplicaciones SAML** (`/admin-v2/saml-apps`: lista, datos del IdP, alta desde metadatos, edición, conflicto de versión). F14 — sección **Gobierno**: solicitudes de acceso (filtros por estado, aplicación y solicitante; aprobar con comentario o rechazar con motivo; sin acciones sobre las propias; conflicto SoD explicado en español con usuario, roles y regla), revisiones de acceso (lista con avance, alta con fecha límite, repetición y qué hacer con lo no revisado; detalle con filtros, mantener/revocar, cancelación), segregación de funciones (reglas con conteo de violaciones, editor con roles de cualquier aplicación y violaciones actuales con su origen) y panel de responsables en cada aplicación; métricas de gobierno en el dashboard y enlaces del System Log. E2E con axe (`e2e/governance.spec.ts`; `accessibility.spec.ts` cubre ahora 39 rutas).
- [x] **UI-12** *(nuevo)* E2E intermitente "creates, rotates and revokes a scoped provisioning
  token": tras rotar, la página reutilizaba la instancia del token anterior y el diálogo de
  reautenticación se desmontaba al cargar el nuevo token.
  *Resuelto:* una instancia de página por token (`key`) y la prueba espera al token rotado antes de
  actuar. Verificado con ejecuciones repetidas.
- [x] **UI-13** *(nuevo, F15)* Defectos de la consola que encontraron los flujos E2E y la prueba de
  reflow agregados en F15: (1) cuando la operación de un diálogo de confirmación fallaba (por ejemplo
  `LAST_SUPER_ADMIN` al desactivar), el diálogo modal seguía abierto y el error se pintaba detrás,
  fuera del alcance del operador; (2) en móvil los grupos de botones no se ajustaban: en la ficha de
  usuario "Operaciones de cuenta" desbordaba la página (contenido corrido, "Anonimizar usuario" fuera
  de pantalla y clics interceptados); (3) a 320 px desbordaban el alta de clientes OAuth (la etiqueta
  `backchannel_logout_session_required`) y el filtro de entregas de Event Hooks; (4) `LAST_SUPER_ADMIN`
  se mostraba en inglés.
  *Resuelto:* `ConfirmDialog` muestra dentro del diálogo el error de la confirmación (sólo el del intento en curso; al reabrirlo empieza limpio), en los 17 diálogos; `.button-group`, `.page-actions` y el control segmentado se ajustan en pantallas angostas y las etiquetas de casillas cortan palabras largas; mensaje en español para `LAST_SUPER_ADMIN`. Pruebas: `accessibility.spec.ts` "reflows at 320 pixels" (WCAG 1.4.10, 39 rutas) y axe con la regla WCAG 2.2 AA `target-size` activada (axe la trae apagada); `admin-shell.spec.ts` "deactivates a user after confirming", "explains inside the confirmation that the last SuperAdmin cannot be deactivated", "a session that expires during a change returns to the sign-in and back to the same page"; `operations.spec.ts` "narrows the System Log to one trace, typed or from an event's detail".

### F. Login hospedado y portal

- [x] **HL-01** El login no envía el OTP por email, no guía la inscripción MFA, no ofrece
  recuperación de contraseña ni magic link, exige email para passkey y no maneja interacciones
  expiradas.
  *Resuelto:* login reescrito por pasos (vistas con foco y mensajes en español): OTP por correo
  enviado al entrar al paso y reenviable, código de respaldo, hasta 5 intentos por paso pendiente
  (antes un error de tecleo obligaba a repetir la contraseña); inscripción guiada del factor que la
  aplicación exige, al iniciar sesión y en el step-up OAuth (`MFA_SETUP_REQUIRED` /
  `PASSKEY_ENROLLMENT_REQUIRED` devuelven un token de inscripción de un solo uso: app de
  autenticación con QR generado en el navegador —`qr.js`, sin scripts de terceros— y códigos de
  respaldo, o passkey y acceso con ella); la API JSON recibe un mensaje fijo. Recuperación de
  contraseña, enlace de acceso por correo (continúa la solicitud OAuth o el `return_url` en el mismo
  navegador), passkey sin correo (credenciales descubribles), opciones por aplicación en visitas
  directas (`GET /ui-api/session/login-options`) y aviso de solicitud expirada (`expiresAt` de la
  interacción). Páginas hospedadas para los enlaces de correo (`/reset-password`,
  `/accept-invitation`, `/confirm-email`, `/confirm-email-change`, `/magic-link`) sin referrer, con
  el token fuera de la barra de direcciones y acción explícita. Pruebas: suite E2E contra la API
  real (`tests/AuthCenter.HostedUi.Tests/e2e`, 16 escenarios con SQL Server, Chromium, WebAuthn
  virtual y buzón de desarrollo), `HostedAccountTests`,
  `ApplicationRequiringMfa_ForAUserWithoutMfa_EnrollsInPlaceAndCompletesTheRequest`, `qr.test.mjs`
  (decodificado con jsQR).
- [x] **PORTAL-01** El portal no permite inscribir factores MFA, códigos de respaldo, cambiar
  contraseña o email, eliminar la cuenta, ver aplicaciones ni vincular/desvincular proveedores.
  *Resuelto:* portal por secciones: contraseña; verificación en dos pasos (app de autenticación
  con QR, correo, regenerar códigos de respaldo, desactivar —con código de correo para el factor por
  correo, `POST /api/auth/mfa/email-otp/verification`—); passkeys (agregar, renombrar, eliminar);
  sesiones con la actual identificada y cierre global; dispositivos; aplicaciones
  (`GET /api/auth/applications`, directas y por grupo); proveedores vinculados con nombre del
  proveedor empresarial y desvinculación; vincular el proveedor de la organización (modo `link` de la
  federación con prueba `account.link-provider`, resultado ligado al navegador y a la cuenta);
  consentimientos; cambio de correo (el enlace incluye la cuenta) y eliminación de la cuenta. La
  reautenticación acepta contraseña o passkey. Avisos por correo al cambiar contraseña, correo,
  desactivar MFA, vincular proveedor o inscribir un factor al iniciar sesión. Pruebas: E2E del portal,
  `PortalLink_*`, `UnlinkedIdentity_SignsInAgain_ByReusingItsLink`, `HostedAccountTests`.
- [x] **HL-02** *(nuevo, hallado durante la remediación)* Las passkeys no funcionaban en el login ni
  en el portal hospedados: leían `optionsJson` y la API devuelve `publicKey`.
  *Resuelto:* ambas páginas usan `publicKey` (`getPasskey`/`createPasskey` en `shared.js`). Prueba
  E2E: `a passkey added in the portal signs in without typing the email`.
- [x] **HL-03** *(nuevo)* Los pasos ocultos del login (MFA, cambio de contraseña) se mostraban
  todos a la vez: `.stack { display: grid }` anulaba el atributo `hidden`.
  *Resuelto:* `[hidden] { display: none !important; }` en `app.css`; verificado por la suite E2E.
- [x] **HL-04** *(nuevo)* Los manejadores del login se registraban después de la inicialización
  asíncrona: un envío temprano (usuario rápido o gestor de contraseñas) hacía un submit nativo GET
  con el correo y la contraseña en la URL.
  *Resuelto:* manejadores registrados antes de cualquier `await` (esperan a la inicialización) y
  formularios con `method="post"` en login, portal y páginas de cuenta.
- [x] **HL-05** *(nuevo)* Registrarse en una aplicación con `RequireMfa` fallaba sin crear la
  cuenta, y el autorregistro en general agotaba el tiempo en SQL Server: dentro de la transacción,
  el servicio de riesgo escribía con otra conexión una fila que referencia al usuario sin confirmar
  (bloqueo hasta el timeout, HTTP 500). Con InMemory no se reproducía.
  *Resuelto:* la cuenta, su acceso y su rol se confirman primero; el primer inicio de sesión corre
  después y, si pide inscribir un factor o la política lo niega, la cuenta se conserva. Detectado y
  cubierto por la suite E2E contra SQL Server.
- [x] **HL-06** *(nuevo)* Dos inicios de sesión simultáneos del mismo usuario respondían 500:
  `UserManager.UpdateAsync` fallaba por concurrencia (resultado ignorado) y el siguiente
  `SaveChanges` de la petición relanzaba el conflicto.
  *Resuelto:* `RecordSignInAsync` recarga la fila y reintenta el registro del acceso. Prueba:
  `ConcurrentSignInsOfOneAccount_AreBothRecorded_WithoutFailingTheRequest` (falla sin el cambio).
- [x] **HL-07** *(nuevo)* Con `MigrateOnStartup` y una base inexistente la API no arrancaba: el
  bloqueo de bootstrap abría la base antes de que la migración la creara.
  *Resuelto:* se crea la base vacía (tolerando que otra instancia la cree antes) y luego se toma el
  bloqueo y se migra.
- [x] **HL-08** *(nuevo)* El enlace de cambio de correo no incluía la cuenta y
  `/api/auth/email-change/confirm` la exige: ningún enlace enviado podía confirmarse. El factor MFA
  por correo no podía desactivarse (sin código para ello y el validador ignoraba `EmailOtpCode`).
  El endpoint hospedado de magic link exigía `ApplicationCode`, que el enlace no trae.
  *Resuelto:* el enlace lleva `userId`; `POST /api/auth/mfa/email-otp/verification` y validador que
  acepta `EmailOtpCode`; `POST /ui-api/session/magic-link {token}` toma la aplicación del token
  firmado. Los enlaces de correo incluyen `application` para la marca. Pruebas: `HostedAccountTests`.
- [x] **HL-09** *(nuevo, F15)* El login hospedado y las páginas de los enlaces de correo no tenían
  análisis axe automático, y el distintivo del panel lateral tenía texto blanco sobre fondo gris claro
  (contraste ≈1,2:1, WCAG 1.4.3) en `/login` y en las páginas de enlaces.
  *Resuelto:* texto oscuro para el distintivo en el panel lateral; `tests/AuthCenter.HostedUi.Tests/e2e/accessibility.spec.mjs` corre axe (con `target-size`) contra la API Release y SQL Server en cada paso del login (formulario, contraseña incorrecta, contraseña olvidada, inscripción del segundo factor), en los siete paneles del portal y en las cinco páginas de enlaces de correo.

### G. Gobierno de accesos

- [x] **GOV-01** Owners por aplicación, solicitudes de acceso con aprobación, revisiones periódicas
  y segregación de funciones.
  *Resuelto:* permisos `AUTHCENTER_GOVERNANCE_READ/WRITE` (migración `20260926170104_AddAccessGovernance`, otorgados a los roles con `AUTHCENTER_USERS_*` y al rol Admin del seed). **Owners:** hasta 20 usuarios activos por aplicación (`/api/governance/applications/{id}`, versionado) y la opción de aceptar solicitudes desde el portal; deciden solicitudes y revisan accesos desde su portal, nunca sobre sí mismos (tampoco los administradores). **Solicitudes:** el usuario pide acceso (y opcionalmente un rol no de sistema) con justificación desde el portal; los owners reciben correo; se aprueba (acceso + rol, tras validar SoD) o se rechaza con motivo, y el solicitante recibe correo; expiran a los `Governance:AccessRequestLifetimeDays` (30) y se pueden cancelar; una por aplicación y hasta `MaxPendingRequestsPerUser` pendientes. Los registros con aprobación y las altas pendientes de un administrador crean su solicitud (la migración rellena las existentes), así que la aprobación de la página de usuarios aprueba la solicitud. **Revisiones periódicas:** campañas por aplicación con la foto de quién tiene acceso (directo y por grupos), decisiones mantener/revocar de owners o administradores (revocar quita el acceso directo al momento; el que dan los grupos queda marcado para quitar la membresía), cierre al vencer que mantiene o revoca lo no revisado, recurrencia en meses y un servicio en segundo plano (`GovernanceMaintenanceService`) que reclama cada campaña para una sola instancia. **Segregación de funciones:** reglas de dos roles (de cualquier aplicación); preventiva en asignación de rol directo, reemplazo de accesos directos, alta/invitación, membresías, roles de grupo, reemplazo de accesos de grupo, reactivación de grupo y aprobación de solicitudes (`SOD_CONFLICT` con detalles y evento `SOD_CONFLICT_BLOCKED`); detectiva para lo que llega de SCIM, reglas dinámicas o federación (`/api/governance/sod-violations`, conteo en el dashboard). Eventos `APPLICATION_GOVERNANCE_UPDATED`, `ACCESS_REQUEST_*`, `ACCESS_REVIEW_*`, `SOD_*` para System Log y Event Hooks. Portal: "Solicitar acceso", "Mis solicitudes" y "Aprobaciones" (solicitudes y revisiones). Pruebas: `AccessGovernanceTests` (7 escenarios en memoria) y `AccessGovernanceRelationalTests` (los mismos en SQL Server), E2E del portal contra la API real (`tests/AuthCenter.HostedUi.Tests/e2e/governance.spec.mjs`: solicitud → aprobación del owner → acceso; rechazo con motivo; revisión con revocación) y de la consola (`e2e/governance.spec.ts`).
- [x] **GOV-02** *(nuevo)* El flujo de acceso pendiente tenía defectos: `PATCH .../approve` reactivaba
  accesos revocados (sólo miraba `!IsActive`), SCIM dejaba a los usuarios desprovisionados como
  "pendientes de aprobación" (sin `RevokedAt`), nadie recibía aviso de lo pendiente y el conteo del
  dashboard y el filtro de usuarios no coincidían.
  *Resuelto:* la aprobación sólo aplica a acceso pendiente (`ACCESS_NOT_PENDING` para uno revocado) y pasa por la solicitud; SCIM marca `RevokedAt` al desactivar y al crear usuarios inactivos (la migración corrige los existentes); owners notificados por correo; el dashboard cuenta solicitudes pendientes reales. Pruebas: `PendingRegistrations_AreRequestsTheOwnersDecide_AndARejectedOneIsNoLongerPending`, `Dashboard_CountsRejectedSignIns_AndPendingAccessRequests`.

### H. Operación

- [x] **OPS-01** El CI de `main` (run 69) no llegó a ejecutarse (sin runner ni logs): revisar
  minutos/facturación de GitHub Actions.
  *Verificado:* GitHub Actions vuelve a ejecutar. El run 36269781523 del PR [Eduard0corona/AuthCenter#23](https://github.com/Eduard0corona/AuthCenter/pull/23) (`build-and-test` sobre `c0ec0ad`) terminó en verde en 11 min 40 s con los pasos de F15, que publicaron `database-migrations` y `coverage-report`. El job `deploy` sólo corre en `main`: su primera ejecución tras fusionar validará los secretos de Azure y la verificación de salud (con las migraciones aplicadas, OPS-03).
- [P] **OPS-02** `main` no tiene protección de rama.
  *Procedimiento:* [`docs/operations/OWNER-ACTIONS.md`](docs/operations/OWNER-ACTIONS.md#ops-02--protección-de-main).
- [x] **OPS-03** Confirmar la migración 24 en Azure SQL; readiness que detecte migraciones
  pendientes.
  *Resuelto (código):* `/health/ready` incluye el check `database-schema` (`DatabaseSchemaHealthCheck`): 503 `Unhealthy` mientras la base no tenga alguna migración de esta build (nombra la primera), `Degraded` (200) cuando la base va por delante del código (tras revertir un despliegue); la respuesta JSON lista cada check con su estado y el despliegue la imprime si falla. CI rechaza un cambio del modelo sin migración (`dotnet ef migrations has-pending-model-changes`) y publica el script idempotente de cada commit como artefacto `database-migrations`; `dotnet-ef` y ReportGenerator quedan fijados en `.config/dotnet-tools.json`. README y `RUNBOOKS.md` documentan el orden (migrar, después desplegar). Prueba: `SchemaReadinessRelationalTests.Readiness_FailsWhileMigrationsArePending_AndIsDegradedByUnknownOnes`. *Ensayo de la actualización (2026-09-26):* una base creada y sembrada por `main` (24 migraciones), con un acceso pendiente y un usuario desactivado, recibió el script con `sqlcmd` dos veces sin errores (35 migraciones; solicitudes de acceso y permisos de gobierno rellenados) y la API del PR sobre esa base, sin migrar al arrancar, respondió `/health/ready` Healthy e inició sesión y renovó el token; también desde la migración 12. *Hallazgo corregido en el ensayo:* `sqlcmd` empieza con `QUOTED_IDENTIFIER OFF` y SQL Server rechazaba la primera migración nueva (error 1934, tablas con índices filtrados) sin aplicar nada: el artefacto `database-migrations` ahora fija `QUOTED_IDENTIFIER` y `ANSI_NULLS` al inicio, el procedimiento pide `sqlcmd -I` para scripts generados a mano y `ops/load/seed-large-directory.sql` los fija también. *Aplicado:* el 2026-09-26 el propietario ejecutó el script en Azure SQL y validó 35 filas en `__EFMigrationsHistory`. Después se fusionó el PR [Eduard0corona/AuthCenter#23](https://github.com/Eduard0corona/AuthCenter/pull/23) y el run `CI/CD` #73 (36278752006, sobre `9cdb8c1`) desplegó en Azure: la verificación de salud (`/health/live`, `/health/ready` con `database-schema`, branding), la CSP de las páginas y la caché y compresión de la consola pasaron.
- [P] **OPS-04** Carga inicial productiva (administrador inicial).
  *Procedimiento:* [`docs/operations/OWNER-ACTIONS.md`](docs/operations/OWNER-ACTIONS.md#ops-04--carga-inicial-productiva).
- [P] **OPS-05** Primera rotación de la llave de firma.
  *Procedimiento:* [`docs/operations/OWNER-ACTIONS.md`](docs/operations/OWNER-ACTIONS.md#ops-05--primera-rotación-de-la-llave-de-firma).
  *Corrección (2026-09-27):* el procedimiento retiraba la llave anterior al vencer el access token,
  pero `/oauth/logout` y `/oauth/authorize` aceptan como `id_token_hint` un ID token vencido firmado
  con ella, y un BFF lo manda al cerrar sesión hasta 24 h después (8 h en Paquetenvia). Retirarla
  antes hace fallar esos cierres de sesión con la sesión de AuthCenter todavía abierta. El README y
  OPS-05 ahora piden esperar al menos 24 h tras promover la llave nueva.
- [P] **OPS-06** Purgar del historial la llave RSA retirada (requiere reescritura autorizada).
  *Estado:* el 2026-09-26 el propietario decidió hacerlo él mismo, fuera de esta remediación (la llave ya está retirada y rotada; la purga reescribe todas las ramas).
  *Procedimiento:* [`docs/operations/OWNER-ACTIONS.md`](docs/operations/OWNER-ACTIONS.md#ops-06--purgar-la-llave-rsa-retirada-del-historial-autorización-expresa).
- [P] **OPS-07** Origen real del frontend en CORS y ActionLinks.
  *Procedimiento:* [`docs/operations/OWNER-ACTIONS.md`](docs/operations/OWNER-ACTIONS.md#ops-07--origen-real-del-frontend).
- [x] **OPS-08** Vulnerabilidades moderadas npm (vitest) y parches NuGet 10.0.12.
  *Resuelto:* `vitest` 4.1.10 → 4.1.11 (con `@vitest/coverage-v8` del mismo release): `npm audit` sin vulnerabilidades en la consola, el SDK TypeScript, el quickstart SPA y las pruebas del login hospedado. ASP.NET Core, EF Core, `Microsoft.Extensions.Logging.Abstractions` y `System.Security.Cryptography.Xml` 10.0.10 → 10.0.12 en la API, Application, Infrastructure, las pruebas y el SDK .NET (su destino `net8.0` sigue en 8.0.31); `dotnet list package --vulnerable --include-transitive` sin hallazgos. Dependabot (OPS-11) los mantiene al día.
- [x] **OPS-09** Las pruebas relacionales "pasan" sin ejecutarse cuando no hay SQL Server.
  *Resuelto:* atributo `[RelationalFact]`: sin SQL Server las pruebas se reportan como omitidas.
- [P] **OPS-10** Ramas remotas ya integradas.
  *Estado:* autorizado por el propietario el 2026-09-26 para las 8 ramas integradas en `main` sin PR abierto (`feat/admin-federation`, `feat/admin-frontend-foundation`, `feat/admin-oauth-clients`, `feat/okta-phase1-complete`, `feat/production-idp-integration-kit`, `fix/azure-phase56-validation`, `fix/comprehensive-security-architecture-hardening`, `fix/ui-html-content-type`). La sesión de remediación no pudo borrarlas: sus credenciales sólo escriben en su propia rama (HTTP 403). Queda para el propietario: *Branches* en GitHub o `git push origin --delete <rama>`.
  *Procedimiento:* [`docs/operations/OWNER-ACTIONS.md`](docs/operations/OWNER-ACTIONS.md#ops-10--ramas-remotas-ya-integradas-autorización-expresa).
- [x] **OPS-11** Dependabot, presupuesto de bundle y cobertura en CI.
  *Resuelto:* `.github/dependabot.yml`: NuGet, npm (consola, SDK TypeScript, quickstart SPA y pruebas del login hospedado), Actions y Docker, semanal, con menores y parches agrupados; los mayores de ASP.NET Core, EF Core, Extensions y de las imágenes .NET se ignoran porque cambian con el framework. CI: cobertura .NET de unitarias e integración (coverlet con `coverlet.runsettings`, ReportGenerator) con resumen en la página del run, artefacto `coverage-report` y mínimos de 80 % de líneas y 60 % de ramas (`scripts/ci/coverage-gate.py`; al introducirlo 85,1 % y 64,5 %); cobertura de los módulos de lógica de la consola con mínimos en `vite.config.ts` (líneas 70, sentencias 68, ramas 58, funciones 65; hoy 77,6/73,6/65,1/70,7); presupuesto gzip de la consola (`src/AuthCenter.Admin/scripts/check-bundle-budget.mjs`: entrada 110 KB, cada chunk 45 KB, total 420 KB; hoy 64,9/22,7/263,8 KB). El job pasa de 25 a 40 minutos por la instrumentación.
- [x] **OPS-12** Pruebas de carga con directorios grandes.
  *Resuelto:* `ops/load/seed-large-directory.sql` siembra un directorio sintético (usuarios `scale-<n>@load.test` sin contraseña, un grupo por cada 100 usuarios con la aplicación asignada, tres membresías por usuario; no repite si el prefijo ya existe). `DirectoryScaleRelationalTests` (con `AUTHCENTER_SCALE_TESTS=1`) mide usuarios, grupos, miembros, dashboard, violaciones de segregación de funciones y revisiones de acceso con 2 s por lectura y 90 s para el snapshot; el workflow semanal `Directory scale` publica la tabla en el resumen del run y `ops/load/directory.js` (k6) repite las lecturas en staging contra el SLO de Directory. Con 100 000 usuarios y 1 000 grupos todas las lecturas quedan bajo 0,62 s (`docs/operations/CAPACITY.md`). *Hallazgo corregido:* el snapshot de las revisiones de acceso era cuadrático (usuarios × membresías; 18,2 s con 20 000 usuarios); ahora indexa por usuario: 11,0 s con 20 000 y 43,9 s con 100 000, lineal y dominado por la inserción de los ítems (un lote mayor en EF Core lo empeoraba: 16 s). El cierre de una campaña renueva su reclamo antes de cada lote de 200, para que una revocación masiva que dura más que el reclamo no la tome otra instancia a medias.
- [x] **OPS-13** *(nuevo)* Prueba intermitente del SDK (`IDX10511` al validar el ID token bajo
  ejecución paralela).
  *Causa raíz:* una prueba validaba con un `RsaSecurityKey` sobre un `RSA` que luego liberaba; el
  proveedor de firma queda en la caché global `CryptoProviderFactory.Default` indexado por la huella
  de la llave, la misma que usan el BFF y la API del SDK. *Resuelto:* la prueba construye la llave
  con `RSAParameters`. Verificado con ejecuciones repetidas de la suite completa.

### I. Documentación

- [x] **DOC-01** Conteos y estados desactualizados (plan admin, roadmap "Fase 5 lista",
  REMEDIACION, TODO).
  *Resuelto:* `OKTA-LEVEL-ROADMAP.md`: nota del análisis y matriz con el estado real de cada área, la fase de la remediación que lo cerró y lo que depende del propietario. `FRONTEND-ADMIN-IMPLEMENTATION-PLAN.md`: estado del documento, las 45 casillas abiertas revisadas contra el código y las pruebas (33 cerradas con su evidencia; siguen abiertas, con el motivo, la observabilidad frontend, i18n, Lighthouse, OpenAPI, la revisión manual WCAG y la matriz de navegadores) y el estado de cada criterio de cierre de la Fase 5. `TODO.md` y `TODO-improvements.md`: conteos marcados como históricos, readiness del esquema, pendientes enlazados a `OWNER-ACTIONS.md`. README: descripción del producto y del CI actual. `docs/production-idp-integration.md` afirmaba que no existía `end_session_endpoint` (existe desde F5). En este documento, el conteo de rutas de UI-07 y la sección "Estado verificado al cierre".
- [x] **DOC-02** README: `traceId`, descripción del rol Admin, forma legacy de entregas.
  *Resuelto:* el README documenta el `traceId` de cada respuesta y de los errores de la consola, el alcance del rol Admin frente a SuperAdmin y el contrato paginado de entregas (la forma legacy se retiró en F11).
- [x] **DOC-03** Guía de integración: `LoginUrl`, recursos de API, logout, federación y SDK.
  *Resuelto:* `docs/integration-quickstarts.md` reescrita: elección de flujo, paquetes, registro del cliente por tipo (redirects, `LoginUrl`, post-logout, back-channel, CORS, scopes), BFF, API protegida, SPA, parámetros SSO, logout, federación y despliegue con varias instancias.
- [x] **DOC-04** Runbooks de operador y soporte para la consola.
  *Resuelto:* `docs/operations/CONSOLE-RUNBOOKS.md`: "no puedo iniciar sesión", cuenta comprometida, accesos pendientes y solicitudes, SCIM, grupos y reglas, federación, aplicaciones SAML, clientes OAuth, gobierno, Event Hooks y mensajes de la consola, con el permiso, el evento de System Log y el step-up de cada paso (verificados contra el código). `docs/operations/OWNER-ACTIONS.md`: procedimiento y comprobación de cada acción del propietario. `RUNBOOKS.md` agrega el despliegue con migraciones. Enlazados desde el README.

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
| 2026-09-26 | F10: login hospedado por pasos (OTP por correo, códigos de respaldo, reintentos acotados, inscripción guiada de TOTP con QR o passkey al iniciar sesión y en el step-up, recuperación de contraseña, magic link, passkey sin correo, expiración), páginas de enlaces de correo, portal completo (MFA, contraseña, correo, cuenta, aplicaciones, vincular proveedor empresarial), avisos de seguridad por correo y suite E2E contra la API real; hallazgos nuevos HL-02..08, SEC-11 (repetición TOTP) y SEC-12. | HL-01..08, PORTAL-01, SEC-11, SEC-12 |
| 2026-09-26 | F11: consola administrativa completa — Event Hooks (catálogo de eventos, rotación de secreto con doble firma, entregas con payload y replay), dashboard con datos reales, System Log completo (actor, entidad, detalle, historial, CSV de todos los eventos filtrados), esquema de perfil, catálogo de APIs y selector de scopes, retiro de `/admin` (redirección y CSP en `/admin-v2`), selectores completos y buscador de usuarios, trazas y errores en español, CSRF con vida de sesión y renovación, permisos dedicados de hooks/federación/provisioning, E2E contra backend real y axe en todas las rutas; hallazgos nuevos HOOK-04 (eventos que no llegaban a los hooks) y SEC-13 (SSRF en hooks). | HOOK-03, HOOK-04, ADM-01, ADM-06, UI-01..09, UI-11 (parcial), SEC-13 |
| 2026-09-26 | F12: concurrencia optimista (`Version` en 9 entidades, 409 con recarga en la consola), idempotency keys en todas las altas y rotaciones, operadores tipados de group rules con grupos gestionados por reglas, SCIM completo (descubrimiento, PUT, PATCH de Entra ID/Okta, orden, ETag, proyección, rutas de extensión y diagnóstico por token); hallazgo nuevo ADM-09 (operaciones transaccionales con 500 en SQL Server) y fábrica de pruebas con el registro real de SQL Server. | ADM-02, ADM-03, ADM-05, ADM-07, ADM-09, DOC-02 |
| 2026-09-26 | F13: AuthCenter como IdP SAML 2.0 — SSO por HTTP-Redirect/POST, aserciones firmadas y opcionalmente cifradas, NameID persistente por aplicación, atributos del directorio, step-up por `RequestedAuthnContext`, inicio desde AuthCenter y el portal, SLO iniciado por el SP; compuerta de acceso compartida con OAuth (`ISsoAccessGate`), login hospedado para interacciones SAML, API y consola de aplicaciones SAML, permisos `AUTHCENTER_SAML_APPS_*` y E2E contra la API real con un certificado generado por la prueba. | SAML-01, UI-11 (parcial) |
| 2026-09-26 | F14: gobierno de accesos — owners por aplicación, solicitudes de acceso desde el portal con aprobación de owners o administradores (correo a ambos, expiración, cancelación, sin autoaprobación), revisiones periódicas con recurrencia, remediación por grupos y cierre automático, segregación de funciones preventiva y detectiva; portal (solicitar, mis solicitudes, aprobaciones) y consola (Gobierno, responsables, dashboard); hallazgo nuevo GOV-02 (defectos del acceso pendiente). | GOV-01, GOV-02, UI-11 |
| 2026-09-26 | F15: operación y documentación — readiness del esquema (`database-schema`, 503 con migraciones pendientes), script idempotente de migraciones por commit y verificación del modelo en CI, cobertura con mínimos (.NET y consola), presupuesto de bundle, Dependabot, parches NuGet 10.0.12 y vitest 4.1.11; prueba de escala con 100 000 usuarios (encontró el snapshot cuadrático de las revisiones de acceso, ahora lineal, y el cierre de campañas renueva su reclamo); axe con la regla WCAG 2.2 AA en consola, login, portal y enlaces y reflow a 320 px (encontraron contraste insuficiente en el login, errores ocultos tras los diálogos de confirmación y desbordes en móvil); flujos E2E mínimos del plan; runbooks de consola, procedimientos del propietario y documentación al día. | OPS-03 (código), OPS-08, OPS-11, OPS-12, UI-07, UI-13, HL-09, DOC-01, DOC-04 |
| 2026-09-26 | Verificación en GitHub: el PR #23 pasó el workflow `CI/CD` completo (11 min 40 s) con las compuertas de F15; GitHub Actions ejecuta de nuevo. | OPS-01 |
| 2026-09-26 | Ensayo de la actualización de producción: base sembrada por `main` → script de migraciones → API del PR (salud, login y refresh correctos); el ensayo encontró que `sqlcmd` sin `-I` rechazaba el script (error 1934), corregido en el artefacto, el procedimiento y el script de siembra. | OPS-03 |
| 2026-09-26 | Fusión y primer despliegue de la remediación: el propietario aplicó las 35 migraciones en Azure SQL, el PR #23 se fusionó y el run `CI/CD` #73 pasó pruebas y despliegue con la verificación de salud, esquema y consola en verde. | OPS-03 |
| 2026-09-27 | Plazo para retirar la llave de firma corregido: al revisar la integración de Paquetenvia se vio que un BFF manda como `id_token_hint` un ID token de hasta 24 h, que AuthCenter valida con la llave que lo firmó; el README y OPS-05 piden ahora esperar al menos 24 h tras promover la llave nueva. | OPS-05 |
