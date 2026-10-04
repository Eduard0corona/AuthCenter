# Anexo C — Arquitectura, experiencia de desarrollador (DX) y de operador

Parte del [análisis de arquitectura, producto y UX](../ANALISIS-PRODUCTO-UX.md).

Base: `main` @ `0320243`. Fecha: 2026-10-04.
Método: lectura de código y documentación; conteos con `wc -l`/`grep -c`. Todas las rutas son relativas a la raíz del repo.


## Resumen ejecutivo

- **El núcleo de protocolo es serio**: OIDC con PKCE obligatorio, SSO y logout con back-channel, SAML IdP/SP, SCIM, gobierno, idempotencia y concurrencia optimista, y CI con compuertas de calidad. El problema principal no es de funcionalidad. Está en **cómo se consume**: la integración local no funciona tal como viene, los errores no ayudan, todo depende de la configuración y no hay capa de textos.
- **Las 5 cosas que más frenan hoy** (detalle en las secciones 2 a 4):
  1. Con la configuración por defecto, **ningún SDK puede hablar con un AuthCenter local** (el issuer `AuthCenter` no es una URL, todo va por HTTP y los puertos no coinciden). → DX-02
  2. Los **SDK no están publicados** (OPS-14), así que los comandos `dotnet add package` y `npm install` de las guías fallan. → DX-01
  3. Un `redirect_uri` mal registrado o un `client_id` desconocido producen **JSON crudo en el navegador**. → DX-05
  4. **No hay capa de localización**: las UIs están en español con mapas de códigos duplicados, la API y los correos en inglés, y los mensajes en inglés se filtran a la UI. Además, el correo del enlace mágico dice "24 horas" cuando el enlace dura 15 minutos. → ARQ-04
  5. La **operación se hace con variables de App Service y scripts**: SMTP, llaves de firma, dominio, alta de aplicaciones y primer administrador no están en la consola. → OPS-UX-01…06
- **La arquitectura frena los cambios de UX**: hay servicios "dios" de 1.300 a 1.700 líneas, un `ApiResponse<object>` sin tipos en 302 sitios (sin OpenAPI útil ni codegen) y reglas copiadas entre servidor, consola y páginas hospedadas (la política de contraseñas está en 9 lugares).

## 1. Mapa de arquitectura

### 1.1 Proyectos y responsabilidades (líneas C# sin migraciones, `wc -l`)

| Proyecto | Archivos / líneas | Responsabilidad real (según el código) |
|---|---|---|
| `AuthCenter.Domain` | 66 / 1.473 | 48 entidades EF anémicas: sólo 1 archivo de entidad tiene un método público (`ApplicationRegistrationSettings.AllowsSelfRegistration`). Contiene enums, `DomainConstants` y el catálogo `Events/EventTypes.cs` (132 líneas). |
| `AuthCenter.Contracts` | 138 / 2.538 | DTOs de petición y respuesta, `ApiResponse<T>`, `PagedResult<T>` y `PaginationQuery`. No depende de nada. |
| `AuthCenter.Application` | 113 / 2.534 | Sólo **interfaces** (44), validadores FluentValidation, `OperationResult` y modelos. **No contiene casos de uso.** |
| `AuthCenter.Infrastructure` | 130 / 20.734 | **Toda la lógica de negocio** (servicios que usan `AuthCenterDbContext` sin repositorios), EF Core con 47 `DbSet` y 35 migraciones (68.218 líneas generadas), Identity, JWT, SAML, SCIM, correo y 4 `BackgroundService`. |
| `AuthCenter.Api` | 57 / 6.096 | 29 controladores (4.177 líneas, 286 endpoints), middleware, filtros, `Program.cs` (640 líneas) y las páginas estáticas `wwwroot`. |
| `src/AuthCenter.Admin` | 97 archivos TS/TSX, 8.312 líneas (+21 tests, 901 líneas) | Consola React 19 servida bajo `/admin-v2/`. |
| `wwwroot` (UI de usuario final) | 2.806 líneas | `login.html` y `assets/login.js` (837), `portal.html` y `portal.js` (815), `account.js` (159), `shared.js` (175), `app.css` (106). JS sin build. |
| `sdk/dotnet` | 21 archivos / 1.787 | `AuthCenter.Client`: BFF, validación JWT y cliente OAuth. |
| `sdk/typescript` | `src/index.ts` (460) | Cliente OIDC sin dependencias. |
| `samples` | 2 `.cs` (58) + SPA (`app.ts`, 85) | Quickstarts mínimos. |

### 1.2 Los 15 archivos C# más grandes (`src/`, sin migraciones ni tests)

| # | Líneas | Archivo | Rol |
|---|---|---|---|
| 1 | 1.562 | `Infrastructure/Services/OAuthAuthorizationService.cs` | Servidor OAuth completo: authorize, interacciones, consentimiento, step-up, token (code, refresh, client_credentials, token exchange), introspección, revocación y UserInfo. Unos 18 métodos públicos y 46 privados. |
| 2 | 1.329 | `Infrastructure/Services/AuthService.cs` | Login (password, social, magic link, passkey), registro, MFA, cambio forzado y step-up. **23 dependencias inyectadas** (`AuthService.cs:22-44`). |
| 3 | 1.031 | `Infrastructure/Services/UserAccessService.cs` | Usuarios, accesos por aplicación, roles efectivos e invitaciones. |
| 4 | 945 | `Infrastructure/Services/AccessPolicyService.cs` | Motor de políticas versionadas: borrador, publicación, simulación y evaluación. |
| 5 | 699 | `Infrastructure/Services/ScimService.cs` | SCIM Users. Es un partial con `ScimService.Groups.cs` (419), **1.118 líneas en total**. |
| 6 | 640 | `Api/Program.cs` | Composición, esquemas de autenticación con validación de sesión inline, CSP, bootstrap de la base de datos con `sp_getapplock` y `ValidateStartupConfiguration` (16 reglas). |
| 7 | 623 | `Infrastructure/Services/Governance/AccessReviewService.cs` | Campañas de revisión de acceso. |
| 8 | 606 | `Api/Controllers/AuthController.cs` | 41 endpoints `/api/auth/*`: login JSON, MFA, sesiones, cuenta y social. |
| 9 | 569 | `Infrastructure/Services/UserProfileService.cs` | Esquema y valores del perfil universal. |
| 10 | 529 | `Infrastructure/Services/DirectoryGroupService.cs` | Grupos, membresías y asignaciones. |
| 11 | 517 | `Infrastructure/Services/TotpService.cs` | TOTP, OTP por correo y códigos de respaldo. |
| 12 | 517 | `Infrastructure/Services/Saml/SamlIdentityProviderService.cs` | AuthCenter como IdP SAML. |
| 13 | 502 | `Infrastructure/Services/FederationService.cs` | Federación entrante. Es un partial de 5 archivos que suman **1.729 líneas** y **15 dependencias**. Es el servicio más grande en tamaño lógico. |
| 14 | 474 | `Api/Controllers/OAuthController.cs` | Endpoints `/oauth/*`. |
| 15 | 464 | `Infrastructure/Services/FederationService.Saml.cs` | La parte SAML de la federación. |

Fuera de `src/`, los tests más grandes son `tests/.../OAuthFlowTests.cs` (1.045) y `FederationHostedLoginTests.cs` (860).

### 1.3 Violaciones de dirección de capas

1. **Domain depende de ASP.NET Core.** `src/AuthCenter.Domain/AuthCenter.Domain.csproj` incluye `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, porque `ApplicationUser`/`ApplicationRole` heredan de Identity (`Entities/ApplicationUser.cs:1`). `docs/architecture.md` dice que sólo usa `Microsoft.Extensions.Identity.Core`, así que la documentación no está al día.
2. **Api se salta Application y usa Infrastructure directamente.** 11 archivos de Api importan `AuthCenter.Infrastructure.*`:
   - `AdminDashboardController.cs:13,25-39` hace 14 consultas `db.*` dentro del controlador.
   - `UsersController.cs:221-243` implementa en el controlador la regla de negocio "¿este cambio quita a alguien su SuperAdmin efectivo?" (la que decide si se exige step-up) con consultas directas a `_db.Roles`, `_db.UserRoles` y `_db.UserApplicationAccesses`.
   - `Program.cs:107-124` y `:145-171` duplican la validación de sesión (cookie y JWT) con consultas a `RefreshTokens`.
   - `AuthController`, `UiSessionController`, `FederationController` y `WellKnownController` leen `IOptions<*Settings>` de `Infrastructure.Settings`.
3. **Application está acoplado al contrato HTTP.** 27 de las 44 interfaces de `Application/Interfaces` importan `AuthCenter.Contracts`, y 33 servicios de Infrastructure también. Cambiar un DTO de la API arrastra el cambio hasta la capa de negocio.
4. **"Clean Architecture" sólo de nombre.** Application tiene 2.534 líneas frente a las 20.734 de Infrastructure: las reglas viven junto a EF Core. No es una violación de referencias, pero sí de responsabilidades.

### 1.4 Dónde viven los aspectos transversales

| Aspecto | Ubicación | Observación |
|---|---|---|
| Validación | FluentValidation en `Application/Validators` (registrado en `ApplicationServiceExtensions.cs`), ejecutado por `Api/Filters/RequestValidationFilter.cs:15-45` | Las reglas también se copian a mano en zod (consola) y en JS (páginas hospedadas). Ver ARQ-03. |
| Errores | Sobre propio `{success,data,errorCode,message,details,traceId}` (`Contracts/Responses/ApiResponse.cs:5-37`) y `ExceptionHandlingMiddleware.cs:35-97` | **No usa ProblemDetails.** Hay 4 formatos de error en paralelo (ver ARQ-06). |
| Auditoría | `IAuditService` (`AuditService.cs`, con su propio `DbContextFactory`), 147 llamadas y **172 acciones como literales**. `AdministrativeMutationAuditMiddleware.cs` audita los rechazos. | Un test mantiene `EventTypes.cs` sincronizado. No hay una clase de constantes. |
| Rate limiting | Middleware propio `RateLimitMiddleware.cs` + `RateLimitRules.cs` + `DistributedRateLimitStore.cs` (en SQL) | Obligatorio fuera de Development (`Program.cs:548-549`). |
| Idempotencia | `Filters/IdempotencyFilter.cs` (atributo `[Idempotent]`) | Respuestas cifradas guardadas 24 h. |
| Concurrencia | `ConcurrencyConflictResultFilter.cs` y `IVersionedEntity` | Convierte 400 `CONCURRENCY_CONFLICT` en 409. |
| Outbox | `OutboxEmailService.cs` (encola), `OutboxDispatcherService.cs` (sondeo cada 5 s, backoff de hasta 60 min) | Los correos **se reintentan sin límite** (`:91-101`; sólo el back-channel tiene un máximo). OPS-17 dice que "quedan como dead letters", lo cual no es exacto. |
| Caché | `IMemoryCache`: ajustes de aplicación 5 min (`ApplicationService.cs:24,140-153`), orígenes CORS 60 s (`AuthCenterCorsPolicyProvider.cs:17`). `[ResponseCache(300, Any)]` en branding (`ApplicationsController.cs:26,37`). | La invalidación es **local a cada instancia**. Ver ARQ-09. |
| Tareas en segundo plano | `RetentionCleanupService`, `OutboxDispatcherService`, `EventHookDispatcherService`, `GovernanceMaintenanceService` (`InfrastructureServiceExtensions.cs:66,148,167,176`) | No hay vista de su estado en la consola. |
| Observabilidad | `ObservabilityExtensions.cs`, `PlatformTelemetryMiddleware.cs`, Serilog, `/health/live` y `/health/ready` | `/health/ready` sólo existe si `HealthChecks:ReadinessHost` está configurado (`Program.cs:515-524`). |

### 1.5 Modelo de tenencia

- **Un solo tenant y un solo directorio.** No existe ninguna entidad `Tenant` ni `Organization` (`grep` sólo encuentra el `TenantId` de Microsoft y SCIM). Es una decisión explícita: "La frontera fuerte actual es `ApplicationSystem`; no se añadirá multi-tenancy SaaS…" (`OKTA-LEVEL-ROADMAP.md:105-107`).
- **`ApplicationSystem` es la partición** (`Domain/Entities/ApplicationSystem.cs:4-25`). Los usuarios son globales y el acceso a cada aplicación se concede con `UserApplicationAccess`. Roles, permisos, políticas, federación, branding, owners y clientes OAuth cuelgan de la aplicación.
- **Los ajustes por aplicación están repartidos en dos sitios:**
  - *En la base de datos:* `ApplicationRegistrationSettings` (modo de registro, toggles de login social, magic link y password, `RequireMfa`, dominios), `ApplicationBrandingSettings` (nombre, 2 colores, logo y 3 enlaces), las políticas versionadas y `FederationProvider`.
  - *En la configuración del host:* `ActionLinks:ApplicationBaseUrls:{code}` (`Settings/ActionLinkSettings.cs:6`), que es el origen de los enlaces de correo de cada aplicación y obliga a reiniciar App Service.
  - *Global sin opción por aplicación:* las credenciales de Google, Microsoft, GitHub y Apple (`Authentication:*`), el SMTP, la duración de sesión SSO (`Sso:SessionLifetimeMinutes`), `Jwt:RefreshTokenDays` y la **política de contraseñas, fija en código** (`InfrastructureServiceExtensions.cs:93-101`).

### 1.6 UI de usuario final (estática + `/ui-api`) frente a la consola SPA

| Dimensión | Login/portal (`wwwroot`) | Consola (`src/AuthCenter.Admin`) |
|---|---|---|
| Stack | HTML + JS sin build, módulos ES, `shared.js` | React 19, React Router 7, TanStack Query (234 líneas con `useQuery`/`useMutation`), react-hook-form + zod, Vite |
| APIs que llama | `/api/auth/*` (42 referencias) **y** `/ui-api/session/*` (24): la frontera de `/ui-api` es difusa | `/api/*` con cookie + `/ui-api/session` (CSRF) |
| Estilos | `assets/app.css` (106 líneas, 8 tokens semánticos: `--brand-primary`, `--danger: #b91c1c`, `--success: #047857`) | `src/styles.css` (315 líneas, 22 tokens de paleta: `--red-700: #b42318`, `--green-700: #027a48`) |
| Tokens compartidos | **Ninguno**: dos sistemas de tokens con rojos y verdes distintos. Además, el correo usa un tercer azul `#0066cc` (`SmtpEmailService.cs:67,100,185`) | — |
| Validación | `shared.js:171-175` (`passwordProblem`) y `login.html:47` (texto con las reglas) | zod por feature (`features/*/*.ts`) |
| Mensajes | Mapas código→español en `login.js:195-224` (28 códigos) y `portal.js:753-790` (35 códigos) | `api/errors.ts:4-19` (14 genéricos) y 7 mapas por feature |
| Componentes | Ninguno (DOM imperativo) | 15 componentes propios en `src/components`, **sin librería de componentes** |

**Duplicado:** hay reglas de validación, textos de error (por ejemplo, `PASSKEY_LIMIT_REACHED` tiene una redacción en `login.js:202` y otra en `portal.js:771`, y `SELF_APPROVAL_FORBIDDEN` otra en `errors.ts:14` y `portal.js:783`), tokens de color y el cliente HTTP con su renovación de CSRF (`api/client.ts:75-93` frente a `shared.js`). **No se comparte nada:** ni tokens, ni catálogo de mensajes, ni tipos.

## 2. Hallazgos de arquitectura (ARQ)

**ARQ-01 · Servicios y controladores "dios" — alta**
- Evidencia:
  - `OAuthAuthorizationService.cs` tiene 1.562 líneas y unos 18 métodos públicos y 46 privados. Mezcla authorize, consentimiento, step-up, canje de código (`:804`), token exchange (`:1110-1174`), introspección, revocación y UserInfo.
  - `AuthService.cs` tiene 1.329 líneas y 23 dependencias (`:22-44`); `IAuthService` expone 24 operaciones.
  - `FederationService` es un partial de 5 archivos que suma 1.729 líneas con 15 dependencias. `ScimService` es un partial de 1.118 líneas.
  - `AuthController.cs` tiene 41 endpoints, y la rama `MFA_REQUIRED` se repite 7 veces (`:59-203`), otra vez en `UiSessionController.cs:307-313` y otra en `FederationController.cs:203`.
- Impacto:
  - Añadir un paso al login (por ejemplo, un captcha o aceptar términos) obliga a tocar `AuthService` (1.329 líneas), dos controladores con ramas paralelas y `login.js` (837 líneas).
  - Hay mucho riesgo de regresión y de conflictos de merge, y la revisión es lenta.
- Recomendación:
  - Partir por caso de uso: `PasswordSignIn`, `MfaChallenge`, `Registration`, `TokenEndpoint` (uno por grant), `Introspection`, etc.
  - Mover las decisiones de "siguiente paso" (MFA, enrolamiento, cambio forzado) a un único `SignInOutcome` tipado que ambos controladores traduzcan.

**ARQ-02 · Contrato de API sin tipos y sin OpenAPI publicado — alta**
- Evidencia:
  - 302 usos de `ApiResponse<object>` y ninguno tipado. 0 `ProducesResponseType` y sin `GenerateDocumentationFile` en `AuthCenter.Api.csproj`.
  - Swagger sólo existe en Development (`Program.cs:426-430`).
  - La consola mantiene a mano `src/api/types.ts` (750 líneas), y el SDK .NET tiene sus propios modelos (`OAuthModels.cs`).
- Impacto:
  - Cada cambio de API exige sincronizar 3 representaciones a mano.
  - No es posible generar clientes. El desarrollador externo no tiene referencia (ver DX-04).
  - Los cambios de UX que necesitan un campo nuevo tardan más.
- Recomendación: tipar las respuestas (`ApiResponse<T>` o `Results<Ok<T>,…>`), publicar `/openapi/v1.json` también en producción (al menos la superficie pública) y generar `types.ts` en CI.

**ARQ-03 · Reglas duplicadas que ya divergen — alta**
- Evidencia de la política de contraseñas, que está en 9 lugares:
  - Las opciones de Identity (`InfrastructureServiceExtensions.cs:93-97`).
  - 5 validadores: `Register`, `ResetPassword`, `ForcedChangePassword`, `CreateUser` y `ChangePassword` (`Application/Validators/*`).
  - `wwwroot/assets/shared.js:171-175` y el texto fijo de `login.html:47`.
  - La consola, que exige **12** caracteres (`features/users/provisioning.ts:15`) frente a los **8** del servidor.
- Evidencia de los límites de aplicación:
  - Servidor: código ≤50 y `^[A-Z0-9_]+$`, nombre ≤200 (`CreateApplicationRequestValidator.cs:12-19`).
  - Consola: código 2–32 que empieza por letra, nombre ≤120 (`features/applications/application.ts:7-9`).
- Evidencia de los valores por defecto:
  - Por API, una aplicación nueva nace `Open` y **sin** confirmación de correo (`CreateApplicationRequest.cs:8,15`).
  - Por consola nace `Closed` y **con** confirmación (`application.ts:72,79`).
- Evidencia de los permisos:
  - `AdminApiMetadataController.cs:32-55` copia a mano unas 45 parejas operación→permiso que ya están en los `[Authorize(Policy=…)]`.
  - `Router.tsx:56-109` repite los permisos como literales.
- Impacto: un cambio de política (por ejemplo, pasar a 12 caracteres) exige tocar 9 archivos en 3 lenguajes. Hoy la UI ya promete reglas distintas de las que aplica el servidor.
- Recomendación:
  - Que el servidor sea la única fuente: un endpoint público `GET /ui-api/policy` (contraseña, longitudes) que consuman las páginas hospedadas y la consola.
  - Los defaults, en un solo sitio (`ApplicationRegistrationSettings.Defaults`).
  - Generar el mapa de permisos por reflexión sobre los endpoints.

**ARQ-04 · Textos de usuario sin capa de localización y en dos idiomas — alta**
- Evidencia:
  - No hay `.resx`, ni `IStringLocalizer`/`AddLocalization`, ni librerías i18n en `package.json`. Los 5 documentos HTML fijan `lang="es"`.
  - Las UIs tienen el español en el código: `login.html`, `login.js:195-224`, `portal.js:753-790`, `api/errors.ts:4-19` y las páginas TSX. La consola mezcla etiquetas en inglés ("Login URL", "Grant types", "Allowed scopes", "Auto consent"; `OAuthClientEditorPage.tsx:122-136`).
  - La API produce mensajes en inglés: 255 códigos distintos en `Failure("…")`, cada uno con su mensaje. Esos mensajes **llegan a la UI** cuando falta la traducción: `login.js:230` (`?? error.message`), `portal.js:793` y `errors.ts:34` ("domain errors keep the server's message").
  - Los correos están en inglés y con HTML dentro del C# (`SmtpEmailService.cs:23-193`). No llevan la marca de la aplicación: el pie fijo es "AuthCenter - centralized identity service" y el asunto "… - AuthCenter".
  - **Error de contenido:** todo correo de acción dice "valid for 24 hours" (`SmtpEmailService.cs:184`), también el enlace mágico (`:54`), que vence a los 15 min por defecto (`JwtSettings.cs:10`) y como máximo a los 30 (`JwtSettingsValidator.cs:23`).
  - El parámetro OIDC `ui_locales` se lee (`OAuthController.cs:74`, `AuthorizeRequest.cs:25`) pero ningún servicio lo usa.
- Impacto:
  - Un usuario de Paquetenvia ve el login en español, el correo en inglés y con marca "AuthCenter", y errores en inglés en los casos sin traducir.
  - Cambiar un texto exige un deploy del backend o del JS.
  - No se puede atender a otro mercado ni idioma.
- Recomendación: un catálogo de mensajes por código (en servidor, con `IStringLocalizer` y recursos `es`/`en`) que la API devuelva ya localizado según `Accept-Language`/`ui_locales`, y plantillas de correo por aplicación e idioma (ver la sección 6).

**ARQ-05 · Códigos de error como literales dispersos — media**
- Evidencia:
  - 255 códigos distintos en `Failure("…")` sin clase de constantes ni catálogo documentado.
  - 32 comparaciones `ErrorCode ==`/`is`/`switch` en los controladores, por ejemplo `OAuthClientsController.cs:84,103,123` y `SamlServiceProvidersController.cs:66,78`.
  - Helpers `Map`/`Failure` copiados en `EventHooksController.cs:36-38` y `LifecycleAutomationController.cs:28-30`.
  - Un catálogo parcial (12 códigos) en `AdminApiMetadataController.cs:15-24`.
- Impacto: el estado HTTP depende de cada controlador (casi todo es 400). Las UIs no pueden saber qué códigos esperar, y por eso aparecen los mapas incompletos de ARQ-04.
- Recomendación: un `enum`/registro `ErrorCatalog` con código, estado HTTP y clave de mensaje, más un único `ToActionResult()`.

**ARQ-06 · Consistencia de API — media**
- Evidencia:
  - **4 formatos de error:** el sobre `ApiResponse`; OAuth `{error,error_description}` (`OAuthController.cs:467-473`); SCIM; y ProblemDetails, que aparece en `WellKnownController.cs:30` y en el `/auth/error` del SDK BFF (`AuthCenterBffEndpointRouteBuilderExtensions.cs:152`).
  - **Códigos OAuth no estándar** en `/oauth/token`: el controlador pasa a minúsculas el código interno (`OAuthController.cs:332-337`) y salen `code_already_used`, `code_expired`, `redirect_uri_mismatch`, `invalid_code_verifier` e `invalid_client_credentials` (`OAuthAuthorizationService.cs:830-843,938`). RFC 6749 §5.2 sólo define `invalid_grant`, `invalid_client`, etc. El test lo fija: `OAuthFlowTests.cs:287`.
  - **Sin versionado** (ni `/v1`, ni `Asp.Versioning`).
  - **Raíces de rutas mezcladas:** `/api/oauth/clients`, `/api/saml` frente a `/saml/idp`, `/ui-api/session` y `/api/auth/passkeys` (usuario final dentro de `/api`).
  - **Enums inconsistentes:** `ClientType` es entero 0/1 (`CreateOAuthClientRequestValidator.cs:92-94`) y `RegistrationMode` es cadena.
  - **Paginación** `page/pageSize≤100` con `PagedResult` en la mayoría, pero hay listas sin paginar (`FederationController.cs:46-48`, `ProfileSchemaController` `Get`) y SCIM usa `startIndex/count`.
- Impacto: los clientes genéricos de OAuth y las certificaciones no reconocen esos códigos, y cada consumidor programa contra excepciones distintas.
- Recomendación: mapear a los códigos RFC (dejar el detalle en `error_description`), adoptar ProblemDetails (RFC 9457) con `code` y `traceId` como extensiones, y versionar por ruta antes de publicar los SDK.

**ARQ-07 · Dos APIs de inicio de sesión paralelas además de OIDC — media**
- Evidencia:
  - `/api/auth/*`: 41 endpoints que emiten JWT y refresh directamente (`AuthController.cs:45-583`).
  - `/ui-api/session/*`: 20 endpoints (`UiSessionController.cs:68-356`).
  - Las páginas hospedadas usan las dos (42 y 24 referencias).
  - El README documenta `/api/auth/login` como endpoint principal (`README.md:363-379`), mientras que la guía recomienda OIDC/BFF (`docs/integration-quickstarts.md:11-18`).
  - Las únicas pruebas de login que propone el README son `curl` contra `/api/auth/login` y `/api/auth/google`, y además al puerto equivocado, 7001 (`README.md:956-976`).
- Impacto: un integrador que lee primero el README puede montar un login de contraseña propio (antipatrón tipo ROPC) en vez de OIDC. Además, cada regla de login se mantiene en dos controladores.
- Recomendación: declarar `/api/auth/*` como "first-party/legacy", sacarlo de la portada del README y converger la UI hospedada en `/ui-api`.

**ARQ-08 · Configuración desbordada y documentación desfasada — media**
- Evidencia:
  - `src/AuthCenter.Api/appsettings.json` tiene **65 ajustes hoja en 23 secciones**, con **15 placeholders `REPLACE_WITH_…`**. Hay además 15 clases `*Settings` (unas 59 propiedades) y claves que el código lee pero que no figuran en `appsettings.json`:
    - `Governance:*`, `AdminConsole:EnvironmentName`;
    - `Authentication:Microsoft|GitHub|Apple`;
    - `RateLimiting:Rules|Enabled`;
    - `Jwt:AdditionalValidationKeysPem`, `Jwt:MagicLinkTokenMinutes`;
    - `Saml:IdentityProviderEntityId`.
  - `Program.cs:540-639` aplica **16 reglas** de arranque en producción, pero el README enumera 5 (`README.md:216-222`).
  - El README dice que `Jwt:SigningKey` pide "min 32 chars" (`:179`) cuando producción exige 64 (`Program.cs:572-577`).
  - `docs/operations/OWNER-ACTIONS.md` lista **13 acciones del propietario**. Al menos 5 (OPS-04, 05, 07, 15 y 17) son "editar variables de App Service y reiniciar o redesplegar".
  - Un ajuste por aplicación vive en la configuración del host: `ActionLinks:ApplicationBaseUrls:{code}`.
- Impacto: arrancar o mover un entorno es propenso a errores y la puesta en marcha falla por reglas no documentadas. Dar de alta una aplicación con enlaces propios exige un deploy.
- Recomendación: generar la referencia de configuración desde las clases `*Settings` y sus validadores, y mover a la base de datos (con consola) los ajustes operativos: SMTP, orígenes de enlaces por aplicación y política de contraseñas.

**ARQ-09 · Cachés locales por instancia en decisiones de login — media**
- Evidencia:
  - `ApplicationService.cs:24,140-153` guarda en `IMemoryCache` durante 5 min la aplicación con `RegistrationSettings` (`IsActive`, `RequireMfa`, `AllowPasswordLogin`). `AuthService` la consume en 10 puntos (`:98,198,219,397,687,730,976,997,1038,1186`), y la invalidación (`:249,261,295`) sólo afecta a la instancia que hizo el cambio.
  - Los orígenes CORS se guardan 60 s (`AuthCenterCorsPolicyProvider.cs:17,57-59`).
  - El branding tiene `[ResponseCache(Duration=300, Location=Any)]` (`ApplicationsController.cs:26,37`).
- Impacto:
  - Con varias instancias, desactivar una aplicación o exigir MFA tarda hasta 5 min en aplicarse en las demás. Contradice la regla del propio roadmap (`OKTA-LEVEL-ROADMAP.md:111-112`).
  - El operador percibe que "guardé y no cambió".
- Recomendación: un `IDistributedCache` con invalidación por versión (la entidad ya es `IVersionedEntity`) o leer las banderas de seguridad sin caché, y avisar en la consola de la propagación del branding.

**ARQ-10 · Arquitectura del frontend de la consola — media**
- Lo positivo:
  - Rutas *lazy* (`Router.tsx:7-46`).
  - TanStack Query para el estado del servidor (234 líneas con `useQuery`/`useMutation`) y estado de sesión en un Context (`auth/session.tsx:14-52`).
  - react-hook-form + zod.
  - Errores con `traceId` (`api/errors.ts:26-38`).
  - E2E con axe.
- Los problemas:
  - **No hay librería de componentes ni design system:** 15 componentes propios y un `styles.css` de 315 líneas. **13 páginas redefinen un `Field` local** aunque existe `components/Field.tsx`, que sólo importan 6. 4 redefinen `Checkbox`.
  - JSX muy denso: 134 líneas de más de 400 caracteres, y la más larga tiene 2.896 (`AccessPolicyEditorPage.tsx:105`). El número de líneas subestima la complejidad y los diffs de UX son ilegibles.
  - Tipos a mano (ARQ-02).
  - Los selectores descargan catálogos completos de hasta 50×100 elementos (`api/catalog.ts:5-21`).
  - Los permisos de ruta son literales (`Router.tsx:56-109`).
- Recomendación: extraer primitivas (Field, Checkbox, FormSection, DataTable), aplicar Prettier con `printWidth` razonable en el lint, usar selectores con búsqueda en servidor y tipos generados.

**ARQ-11 · Páginas hospedadas imperativas, con textos fijos y sin tokens compartidos — media**
- Evidencia:
  - `login.js` (837 líneas) y `portal.js` (815) manipulan el DOM directamente. Hay un estado por vista con `hidden` en `login.html` (10 vistas `data-view`).
  - Textos de marketing fijos para cualquier aplicación cliente: "Identidad segura para tus aplicaciones" (`login.html:20`) y "Passwordless · MFA · OIDC · SAML / Una identidad, controles consistentes." (`login.html:125-126`).
  - El branding se limita a nombre, 2 colores, logo y 3 enlaces (`ApplicationBrandingSettings.cs:3-17`).
  - Los tokens de color difieren de los de la consola (ver la sección 1.6).
- Impacto: personalizar el texto por aplicación, cambiar el orden de los pasos o traducir exige editar JS y redesplegar. La consistencia visual entre login, portal y consola depende de copiar a mano.
- Recomendación: un paquete de tokens compartido (CSS variables generadas una vez para las dos UIs), textos del login en un catálogo por aplicación e idioma, y a medio plazo componer el login con los mismos primitivos.

**ARQ-12 · Deriva documental y estilo — baja**
- Evidencia:
  - `docs/architecture.md:117` dice "SameSite=Strict", pero la cookie es `Lax` (`Program.cs:94`). También dice que Domain sólo depende de `Identity.Core` (`:23`).
  - Hay controladores escritos en una línea por endpoint (`EventHooksController.cs:15-34`, `ProvisioningTokensController.cs:17-23`, `LifecycleAutomationController.cs:15-26`) junto a otros detallados.
- Impacto: la documentación pierde credibilidad y cuesta más incorporar gente.
- Recomendación: `dotnet format` con reglas de estilo y una revisión de la documentación en cada PR que toque `Program.cs` o la configuración.

## 3. Experiencia de desarrollador (DX)

### 3.1 Documentación de entrada (conteos)

- `README.md` tiene **1.043 líneas**: 1 H1 real (las otras dos coincidencias de `^# `, en `:1009` y `:1033`, son comentarios dentro de un bloque bash), **10 H2 y 27 H3**. Documenta **114** filas de endpoints de los **286** que existen en el código. Mezcla tres cosas: quick start, referencia de configuración y referencia de API.
  - El "Quick Start" (`:30-170`) incluye unas **85 líneas de descripción de módulos de la consola** (`:69-154`) entre el paso 3 y Docker.
- `docs/integration-quickstarts.md` (155 líneas, en español) es la guía real de integración. `docs/production-idp-integration.md` (103) es el procedimiento de alta.
- Los READMEs de los SDK están en español: `sdk/dotnet/AuthCenter.Client/README.md` (201) y `sdk/typescript/README.md` (74). El de `samples/spa/README.md` (19) también. `samples/dotnet-web` y `samples/dotnet-api` **no tienen README**.
- **Idiomas mezclados:** README, `architecture.md` y `authentication-flow.md` están en inglés; quickstarts, SDKs, operaciones y ADR en español.
- La raíz tiene 7 `.md`, entre ellos planes y remediaciones (`REMEDIACION-INTEGRACION-FEDERACION.md` con 494 líneas, `FRONTEND-ADMIN-IMPLEMENTATION-PLAN.md` con 722) que compiten con el README.

### 3.2 De cero a un inicio de sesión funcionando (BFF .NET local), según la documentación

| # | Paso documentado | Obstáculo encontrado en el código |
|---|---|---|
| 1 | Instalar .NET 10, Node 24 y SQL Server (`README.md:23-28`) | LocalDB (el valor por defecto, `appsettings.json:3`) sólo existe en Windows. |
| 2 | Pasar 6 secretos con `dotnet user-secrets` (`README.md:34-45`) | El bloque es **PowerShell con ruta Windows** (`C:\path\authcenter-private-key.pem`) y no dice cómo generar la llave RSA. Eso sólo aparece en la ruta Docker (`:163`). |
| 3 | `dotnet ef database update` o migración automática | — |
| 4 | `dotnet run` → "Swagger UI: `https://localhost:7001/swagger`" (`:67`) | `launchSettings.json:8,17` usa **5199/7157**, y `dotnet run` toma el primer perfil (`http`). La consola en modo `npm run dev` hace proxy a **7001** (`vite.config.ts:23-26`). `/admin-v2/` responde 404 si no se compiló antes la SPA (`Program.cs:296-307,507-513`; `dist/` no está versionado). |
| 5 | Crear la aplicación y conceder acceso al usuario | No está en el quick start. Sin `UserApplicationAccess`, `/oauth/authorize` devuelve `access_denied` de inmediato si ya hay sesión SSO (`SsoAccessGate.cs:64`, `OAuthAuthorizationService.cs:234-235,466-485`). |
| 6 | Crear el cliente OAuth en `/admin-v2/oauth-clients` | El campo **"Login URL"** sugiere `https://app.example.com/login` (`OAuthClientEditorPage.tsx:133`), pero debe ser **el login de AuthCenter** (`integration-quickstarts.md:40,46`). Con la URL de la app, authorize redirige a la app con `interaction_id` (`OAuthAuthorizationService.cs:263-267`). Además, los scopes por defecto de la consola son `openid profile email` (`oauth-client.ts:124`) y el BFF pide `offline_access` por defecto (`AuthCenterBffOptions.cs:8`), así que el primer login falla con `invalid_scope`. |
| 7 | `dotnet add package AuthCenter.Client` (`integration-quickstarts.md:24`) | **El paquete no está publicado** (ver DX-01). |
| 8 | Configurar `Authority` en el sample y ejecutarlo | El SDK exige una authority **HTTPS** cuyo `issuer` coincida con ella. En Development el issuer es `"AuthCenter"` (ver DX-02). |

Resultado: **8 pasos documentados, de los cuales al menos 4 son bloqueantes y no están documentados**. La configuración que sí funciona existe, pero está escondida en el arnés E2E: `tests/AuthCenter.HostedUi.Tests/playwright.config.mjs:42-43,83-88` fija `Jwt__Issuer`, `Oidc__PublicOrigin` y `Email__DevelopmentPickupDirectory` y genera los certificados con `openssl`.

### 3.3 Hallazgos DX

**DX-01 · Los SDK de los quickstarts no se pueden instalar — crítica**
- Evidencia:
  - `integration-quickstarts.md:24-25`, `sdk/dotnet/AuthCenter.Client/README.md:4` y `sdk/typescript/README.md:16` mandan instalar desde NuGet/npm.
  - La publicación está pendiente del propietario: OPS-14 (`OWNER-ACTIONS.md:18,150-162`) y "Lista salvo publicar" (`OKTA-LEVEL-ROADMAP.md:32`). Tampoco hay `LICENSE` (OPS-14, paso 1).
  - Los samples usan referencias locales: `ProjectReference` en `samples/dotnet-*/…csproj` y `"file:../../sdk/typescript"` en `samples/spa/package.json`.
- Impacto: un equipo fuera del monorepo no puede integrar con los SDK. El tiempo hasta la primera integración depende de copiar código.
- Recomendación: publicar (o, como mínimo, un feed interno de GitHub Packages) y, mientras tanto, decirlo en la primera línea de cada guía.

**DX-02 · Un AuthCenter local no es compatible con sus propios SDK — crítica**
- Evidencia:
  - `appsettings.Development.json` define `Jwt:Issuer = "AuthCenter"` (no es una URL), `Oidc:PublicOrigin = http://localhost:5000` (ningún perfil de `launchSettings.json` usa ese puerto), los orígenes de passkeys `http://localhost:5000` y `https://localhost:5001`, `ActionLinks:DefaultBaseUrl = http://localhost:3000` y SMTP vacío sin `DevelopmentPickupDirectory`.
  - `docker-compose.yml` publica `http://localhost:8080` y no sobrescribe `Jwt__Issuer`.
  - Los dos SDK rechazan cualquier authority que no sea HTTPS y exigen `issuer == authority`:
    - `.NET`: `ServiceCollectionExtensions.cs:285-290`, `RequireHttpsMetadata = true` en `:86,226`, `AuthCenterBffOptions.cs:40-42`, `AuthCenterClient.cs:50-55`.
    - `TS`: `sdk/typescript/src/index.ts:136`.
  - No hay excepción para loopback.
- Impacto:
  - El primer contacto falla con `IDX…`/"discovery document belongs to another issuer".
  - Los correos de confirmación, enlace mágico y reset nunca llegan en local, y el enlace apunta a `:3000`. Esto bloquea el registro cuando la aplicación exige confirmación.
  - Las passkeys fallan en los puertos de `launchSettings`.
- Recomendación:
  - Un perfil `Development` coherente (issuer = origen = `https://localhost:7157`, pickup dir activado y passkeys en ese origen).
  - Que los SDK acepten `http://localhost` en desarrollo con una opción explícita.
  - Un `dev-up` (script o `docker compose`) que reutilice lo que ya hace `playwright.config.mjs`.

**DX-03 · El quick start y Docker tienen errores de contenido — alta**
- Evidencia:
  - Los puertos no coinciden (paso 4 de la tabla 3.2).
  - Docker anuncia "health at `http://localhost:8080/health`" (`README.md:167-168`), pero sólo existen `/health/live` y un `/health/ready` condicionado (`Program.cs:514-524`).
  - `JWT_RSA_PRIVATE_KEY_PEM=` está vacío en `.env.example:15` sin explicar cómo meter un PEM de varias líneas en `.env`.
  - **Riesgo a verificar:** el `Dockerfile` ejecuta `dotnet publish` sin `-p:SkipAdminFrontendBuild=true`, lo que dispara `npm ci` y `npm run build` (`AuthCenter.Api.csproj:33-37`). La imagen base es `dotnet/sdk:10.0` y el Dockerfile no instala Node. `.dockerignore` tampoco excluye `node_modules`. CI no construye la imagen (no hay `docker` en `.github/workflows/*.yml`), así que nada detectaría que falla.
- Impacto: la ruta "rápida" puede ser la más lenta y el primer error llega antes de ver el login.
- Recomendación: corregir los puertos y la URL de salud, añadir `docker build` a CI y un script `generate-dev-secrets` multiplataforma (bash + pwsh).

**DX-04 · Cuesta descubrir endpoints, client id y secreto — alta**
- Lo que funciona:
  - Hay discovery estándar (`WellKnownController.cs:24-66`).
  - El secreto se muestra una sola vez tras crear o rotar (`README.md:113-118`).
  - La página de cada token SCIM muestra la URL base (`README.md:125-127`) y federación expone `GET /api/federation/service-provider`. Son buenos ejemplos de ayuda en contexto.
- Lo que falta:
  - No hay OpenAPI fuera de Development (`Program.cs:426-430`) y sus respuestas no tienen esquema (ARQ-02).
  - El editor de clientes no muestra authority ni URL de discovery, ni la configuración lista para copiar (`appsettings`, `.env` de la SPA), ni los valores de redirect por defecto del SDK. El placeholder de redirect es `/oauth/callback` (`OAuthClientEditorPage.tsx:131`) y el SDK usa `/signin-authcenter`.
  - `ClientType` se envía como `0`/`1` en la API (`CreateOAuthClientRequestValidator.cs:92-94`).
  - `LoginUrl` es obligatorio (`:22-26`) aunque casi siempre es la misma URL del login hospedado.
- Recomendación: un panel "Conecta tu aplicación" por cliente con authority, client_id, URLs y snippets por stack (BFF .NET, SPA, API), y `LoginUrl` precargado con `{Oidc:PublicOrigin}/login`.

**DX-05 · Mensajes ante configuración incorrecta — alta**

| Error de configuración | Lo que ve el desarrollador o el usuario |
|---|---|
| `redirect_uri` no registrado o `client_id` desconocido | **JSON crudo en el navegador** (HTTP 400): `{"success":false,"errorCode":"INVALID_REDIRECT_URI","message":"The redirect_uri is not registered for this client.","traceId":…}` o `INVALID_CLIENT` "Unknown, inactive, or unlinked OAuth client." (`OAuthController.cs:91-93`, `OAuthAuthorizationService.cs:76-88`). No muestra el URI recibido ni enlaza a la consola. No hay página de error HTML. |
| Scope no permitido | Redirige a la app con `error=invalid_scope` y "One or more requested scopes are not allowed." **sin decir cuál** (`OAuthAuthorizationService.cs:112-115`). |
| Lo mismo con el BFF .NET | `invalid_scope`, `unauthorized_client` e `invalid_request` no están en la lista de errores que el BFF reenvía (`AuthCenterChallengeParameters.cs:22-25`). Responde **401 ProblemDetails genérico** "Authentication failed / The AuthCenter sign-in could not be completed" (`AuthCenterBffEndpointRouteBuilderExtensions.cs:149-158`) y descarta `error_description` aunque el servidor lo envió. |
| Usuario sin acceso a la aplicación | `access_denied` + "The user does not have access to this application." (`OAuthAuthorizationService.cs:481-484`). El BFF reenvía sólo el código. En el login hospedado, "No tienes acceso a esta aplicación." (`login.js:207`) sin enlace para solicitar acceso. |
| Secreto incorrecto en `/oauth/token` | 401 `{"error":"invalid_client_credentials"}`, un código no estándar (ARQ-06). |
| `Oidc:PublicOrigin` sin configurar | Discovery 503 ProblemDetails "OIDC public origin is not configured." (`WellKnownController.cs:29-30`). Es un buen mensaje. |
| Authority HTTP en el SDK | `ArgumentException` "AuthCenter authority must be an absolute HTTPS URI." al arrancar. Es claro, pero no explica cómo resolverlo en local (DX-02). |

- Recomendación: una página `/error` hospedada (HTML con la marca, en el idioma del usuario) para los errores anteriores al redirect, con el valor recibido y el `traceId`; nombrar los scopes rechazados; y en el BFF, propagar `error_description` en Development.

**DX-06 · Ergonomía de los SDK — media**
- .NET: es sólido.
  - `AddAuthCenterBff` + `MapAuthCenterBff` exponen 7 endpoints con back-channel, CSRF y coordinación de refresh (`sdk/dotnet/.../README.md:54-64`).
  - `AddAuthCenterJwtBearer` y `RequireAuthCenterScope`/`Permission` resuelven la protección de APIs.
  - La validación falla rápido con mensajes claros (`AuthCenterBffOptions.cs:37-66`).
- TypeScript: se queda corto (460 líneas en un archivo).
  - No guarda ni renueva tokens: no hay `getAccessToken()` con refresh automático y el SSO silencioso es sólo por redirect (`prompt=none`), sin iframe ni refresh en segundo plano.
  - No tiene bindings para React o Vue ni un validador de JWT para APIs en Node (`index.ts:125-314`).
  - Exige HTTPS sin excepción (DX-02).
- No hay guía para librerías OIDC genéricas (oidc-client-ts, Spring, NextAuth, Python), aunque el discovery es estándar.
- Recomendación: `getAccessToken()` con refresh y deduplicación, un paquete `@authcenter/react` mínimo y una página "otras plataformas" con la configuración genérica.

**DX-07 · Los samples no se pueden ejecutar tal cual — media**
- Evidencia:
  - `samples/dotnet-web` y `samples/dotnet-api` no tienen README ni `launchSettings`, así que el puerto queda indefinido frente al redirect exacto. Tampoco tienen `UserSecretsId` para el secreto.
  - `samples/spa/.env.example:5` apunta a `https://localhost:7101/orders`, pero la API de ejemplo expone `/api/orders` (`samples/dotnet-api/Program.cs:20`) y no configura CORS. "Llamar a la API" falla.
  - CI sólo compila los samples (`ci.yml:189-191`).
  - El seed sólo crea la aplicación `AUTHCENTER` y el administrador (`AuthCenterSeeder.cs:26-51,173-183`). No hay aplicación ni cliente demo.
- Recomendación: un seed opcional `Seed:DemoClients` que cree la aplicación y los clientes que usan los samples, más un smoke test E2E del sample BFF en CI.

**DX-08 · El login social no está disponible en el login hospedado — media**
- Evidencia:
  - Google, Microsoft, GitHub y Apple sólo existen como endpoints JSON que reciben un ID token obtenido por la propia app (`AuthController.cs:78-147`; "AuthCenter does not implement OAuth redirects", `docs/google-sign-in.md:5`).
  - `login.html` no tiene ningún botón social.
  - Aun así, la consola ofrece los toggles `allowGoogleLogin`, etc. (`features/applications/application.ts:14-17`).
- Impacto:
  - Un cliente OIDC no puede ofrecer "Continuar con Google" sin construir su propia UI y saltarse el login hospedado.
  - Los toggles prometen algo que el flujo estándar no hace.
- Recomendación: proveedores sociales como conexiones de redirect dentro del login hospedado (podría reutilizar la federación OIDC por aplicación) y aclarar en la consola el alcance real de los toggles.

**DX-09 · Estructura documental — baja**
- Evidencia: no hay sitio de documentación ni índice. La referencia de API está a mano en el README (114/286). Los documentos de remediación y planes están en la raíz. Los idiomas están mezclados (3.1).
- Recomendación: un `docs/` con índice (Empieza aquí → Conceptos → Guías por stack → Referencia generada → Operación) y un solo idioma por audiencia.

## 4. Experiencia del operador (OPS-UX)

Pasos manuales de producción según `docs/operations/OWNER-ACTIONS.md` (293 líneas, 13 acciones):

| Acción | Cómo se hace hoy | ¿Puede ser una función de la consola? |
|---|---|---|
| OPS-03 Migraciones | Script idempotente aplicado por el DBA (`:50-73`) | No (es inherente al modelo de mínimo privilegio), pero la consola podría **mostrar** el estado `database-schema` |
| OPS-04 Primer administrador | Variables `Seed__*` + reiniciar con una instancia + borrar variables (`:75-87`) | **Sí**: asistente de primer arranque o token de bootstrap de un solo uso |
| OPS-05 Rotar la llave de firma | 3 despliegues editando PEM en variables (`:89-108`, `README.md:246-271`) | **Sí**: llaves gestionadas (generar, publicar, promover, retirar) con calendario |
| OPS-07 Origen del frontend | Variables `Cors__AllowedOrigins__N` y `ActionLinks__ApplicationBaseUrls__<CODIGO>` (`:128-140`) | **Sí**: campos por aplicación en la consola |
| OPS-15 Dominio propio | 6 variables + certificado + reinicio (`:164-193`) | **Parcial**: asistente de verificación con checklist de efectos |
| OPS-16 Alta de una aplicación | `scripts/ops/Register-Paquetenvia.ps1` (**407 líneas** de PowerShell + Azure CLI; llama a `/api/applications`, `/api/oauth/clients`, `/rotate-secret`) | **Sí**: asistente "Nueva integración" con plantillas |
| OPS-17 SMTP | 7 variables `Email__*` + Key Vault + reinicio, sin prueba (`:252-281`) | **Sí**: proveedor de correo + "enviar correo de prueba" |
| UI-07 Revisión con lector de pantalla | Manual (`:283-293`) | No |

**OPS-UX-01 · El correo saliente se configura a ciegas — alta**
- Evidencia:
  - Sólo se configura por variables (OPS-17), sin endpoint de prueba (no hay nada parecido a `test-email` en `src`) y sin vista del outbox (ningún controlador lee `OutboxMessages`).
  - El arranque en producción **no valida `Email:Host`**: no está entre las 16 reglas de `Program.cs:540-639`. Sin SMTP, `SmtpEmailService.cs:127-133` lanza una excepción y el outbox reintenta **indefinidamente** cada ≤60 min (`OutboxDispatcherService.cs:91-101`).
  - El indicador de "dead letters" del dashboard cuenta sólo los Event Hooks (`AdminDashboardController.cs:37`).
  - El usuario ve "Te enviamos un código a tu correo." (`login.js:415`) aunque nunca llegue: el OTP sólo se encola (`OutboxEmailService.cs:36-37`).
  - El puerto 465 no funciona (`OWNER-ACTIONS.md:276-277`).
- Impacto: la confirmación de correo, el enlace mágico, el reset y el OTP por correo fallan en silencio. OPS-16 depende de esto (`OWNER-ACTIONS.md:249-250`).
- Recomendación (S/M):
  - Una página "Correo" con el estado (configurado o no, último envío correcto, backlog y antigüedad del más viejo) y el botón **Enviar prueba**.
  - Un indicador de outbox atascado en el dashboard.
  - Validar `Email:Host` en el arranque cuando haya aplicaciones con `RequireEmailConfirmation`.

**OPS-UX-02 · Llaves y certificados fuera de la consola — alta**
- Evidencia:
  - La rotación RSA son 3 despliegues editando PEM (`README.md:246-271`), con la regla de esperar ≥24 h y el riesgo de romper `id_token_hint` si se retira antes (`OWNER-ACTIONS.md:98-106`).
  - Ningún endpoint de administración muestra el `kid` activo, su antigüedad ni las llaves adicionales (sólo existe el JWKS público, `WellKnownController.cs:69-73`).
  - El certificado SAML sí muestra `NotAfter` (`Contracts/Responses/Saml/*`, `CONSOLE-RUNBOOKS.md:87-88`). La llave MFA (`Mfa:PreviousEncryptionKeys`) y el certificado de Data Protection no tienen ninguna visibilidad.
- Impacto: una operación de seguridad periódica depende de conocimiento tribal y de tres despliegues coordinados.
- Recomendación (M/L): un inventario de material criptográfico (tipo, `kid`/huella, creación, vencimiento y estado) con alertas, y a medio plazo la rotación desde la consola (con Key Vault como backend).

**OPS-UX-03 · No hay una vista de salud o readiness para el operador — media**
- Evidencia:
  - `/health/ready` sólo existe si `HealthChecks:ReadinessHost` tiene valor, sólo responde a ese host (`Program.cs:515-524`) y devuelve JSON (`HealthCheckResponses.cs:14-29`). Incluye 2 checks: SQL y esquema (`Program.cs:290-293`).
  - El dashboard tiene 15 indicadores de negocio (`AdminDashboardDto.cs`) y ninguno de sistema: versión de esquema, workers (outbox, hooks, retención, gobierno), SMTP, llaves o validación de configuración.
- Recomendación (S): una página "Estado del sistema" que reutilice `DatabaseSchemaHealthCheck` (ya calcula `latestKnown`/`latestApplied`/`pending`), los heartbeats de los 4 `BackgroundService` y los checks nuevos de SMTP y llaves.

**OPS-UX-04 · Configurar el dominio es frágil y tiene efectos en cascada — media**
- Evidencia: hay que tocar 6 variables a la vez (`OWNER-ACTIONS.md:170-179`). Cambiar de dominio invalida las passkeys, el entity ID SAML y el callback de federación (`README.md:241-244`). "Custom domains administrados" figura como objetivo del roadmap (`OKTA-LEVEL-ROADMAP.md:31`).
- Recomendación (M): un asistente que valide DNS, certificado, `AllowedHosts` y la coincidencia issuer/origen, y que liste las aplicaciones SAML y los IdP que hay que actualizar antes de aplicar el cambio.

**OPS-UX-05 · El arranque y el primer administrador van por variables — media**
- Evidencia:
  - OPS-04 (`OWNER-ACTIONS.md:75-87`).
  - En desarrollo, si faltan `Seed:*`, el seeder sólo deja un *warning* y no crea al administrador (`AuthCenterSeeder.cs:181-183`), y nadie puede entrar a la consola. `appsettings.Development.json` trae `Seed` vacío.
- Recomendación (S/M): una pantalla de *first-run* protegida con un token de un solo uso impreso en el log, o un comando `dotnet AuthCenter.Api.dll bootstrap-admin`.

**OPS-UX-06 · Dar de alta una aplicación exige un script — media**
- Evidencia:
  - `Register-Paquetenvia.ps1` (407 líneas) automatiza: crear la aplicación con confirmación de correo, crear el cliente con `loginUrl = "$authCenter/login"` y `autoConsent` (`:69,80,88`), conceder acceso, rotar el secreto y escribirlo en Key Vault.
  - Una parte del alta es configuración del host (orígenes de enlaces y CORS de primera parte; OPS-07).
- Recomendación (M): un asistente "Nueva integración" en la consola con plantillas (Web BFF, SPA, API, M2M) que precargue `LoginUrl`, scopes coherentes con el SDK y redirects por defecto, y que entregue el secreto con un "copiar como variables de entorno".

**OPS-UX-07 · Los cambios no se propagan de inmediato — baja**
- Evidencia: ARQ-09. La caché de 5 min de ajustes por instancia y la caché pública de 5 min del branding (`ApplicationsController.cs:26,37`) hacen que el operador "guarde" sin ver el efecto, y la consola no lo advierte.
- Recomendación (S): una nota "puede tardar hasta N min" en los formularios afectados mientras se corrige ARQ-09.

## 5. Evaluación de producto

### 5.1 Personas que se deducen del código y de la documentación

| Persona | Evidencia | Superficie que usa |
|---|---|---|
| **Propietario de la plataforma** (Azure, GitHub, Key Vault, licencias) | `OWNER-ACTIONS.md:1-22` ("dependen de cuentas, permisos o decisiones que el código no puede tomar") | Variables de App Service, scripts `pwsh` y CI |
| **Operador y mesa de ayuda** | `CONSOLE-RUNBOOKS.md:3-10`; roles `SuperAdmin`/`Admin` (`DomainConstants.cs:74-78`, `README.md:993-1003`) | Consola `/admin-v2/` (56 rutas con `path` en `Router.tsx`) y System Log |
| **Responsable o aprobador de la aplicación** | `ApplicationOwner`; aprobaciones en el portal (`CONSOLE-RUNBOOKS.md:43-46`, `AccountGovernanceController.cs:43-67`) | Portal → Aprobaciones |
| **Auditor o gobierno** | Revisiones, SoD y exportación CSV (`README.md:91-94,700-735`) | Consola → gobierno y System Log |
| **Desarrollador de un "proyecto hermano"** (.NET primero; Paquetenvia es el primer consumidor) | "organización que centraliza varios proyectos hermanos" (`OKTA-LEVEL-ROADMAP.md:105-107`); OPS-16 | SDK BFF .NET, quickstarts y consola de clientes OAuth |
| **Usuario final hispanohablante** (incluido B2C con registro abierto) | `lang="es"` en todas las páginas; "Crear cuenta" (`README.md:382-400`) | `/login`, `/portal` y correos (en inglés) |
| **Administrador del IdP corporativo externo** (Entra ID/Okta) | SCIM "PATCH de Entra ID/Okta" (`OKTA-LEVEL-ROADMAP.md:29`); federación por aplicación | URL SCIM, metadatos SP y "Probar conexión" |

### 5.2 Propuesta de valor

AuthCenter es un IdP **autoalojado en Azure y basado en estándares** (OIDC, OAuth, SAML, SCIM y WebAuthn) para centralizar la identidad de los proyectos de una organización. Ofrece controles "nivel Okta": políticas versionadas con simulación, step-up con passkey, solicitudes y revisiones de acceso y SoD. No tiene coste por usuario, la interfaz está en español y la seguridad es estricta por diseño: PKCE obligatorio (`CreateOAuthClientRequestValidator.cs:96-99`), secretos que se muestran una sola vez, reautenticación de un solo uso y CSP estricta.

El diferencial frente a Keycloak es la gobernanza y el cumplimiento integrados. Frente a Auth0, Okta y Entra, el diferencial es el control y el coste. **El punto débil es el consumo:** integrar y operar el producto exige conocer el repositorio.

### 5.3 Qué existe y qué no, frente a Auth0, Okta, Entra ID y Keycloak

| Capacidad | AuthCenter (verificado en el código) | Referencia en los competidores |
|---|---|---|
| OIDC: code+PKCE, refresh, client credentials, token exchange, introspección, back-channel logout | **Existe** (`OAuthController.cs`, discovery `WellKnownController.cs:31-65`) | Todos |
| Device code, PAR, CIBA, registro dinámico de clientes (DCR) | **No existe** (sin rutas ni metadatos; el roadmap lo admite para PAR y device, `OKTA-LEVEL-ROADMAP.md:24`) | Keycloak implementa device code, DCR, PAR y CIBA; Auth0 y Okta ofrecen al menos device code y DCR |
| Login social en el login hospedado | **No existe**. Sólo hay endpoints JSON con el ID token de la app (DX-08) | Auth0, Keycloak, Okta y Entra External ID lo intermedian desde el login hospedado |
| Federación empresarial OIDC/SAML por aplicación + home realm discovery | **Existe** (`FederationService*`, `domain_hint`/`idp`) | Todos |
| LDAP/AD on-prem | **No existe** (sin referencias a LDAP) | Keycloak (user federation), Okta (AD agent), Entra (Connect) |
| MFA TOTP, OTP por correo, backup codes, passkeys, magic link | **Existe** | Todos (SMS y push nativos no están en AuthCenter) |
| Política de contraseñas y bloqueo configurables, contraseñas filtradas, bot/captcha | **No existe**: fija en código (8 caracteres; bloqueo de 5 intentos durante 15 min, `InfrastructureServiceExtensions.cs:93-101`). Sin captcha ni comprobación de contraseñas filtradas | Auth0 (password policy, breached passwords, bot detection) y Keycloak (políticas por realm) |
| Branding del login | **Parcial**: nombre, 2 colores, logo y 3 enlaces por aplicación. El texto del login no se puede cambiar (ARQ-11) | Auth0 (textos e idiomas en Universal Login), Keycloak (themes y message bundles), Okta y Entra (branding localizado) |
| Localización del login, el portal y los correos | **No existe** (ARQ-04). `ui_locales` se ignora | Todos los mencionados |
| Plantillas y proveedor de correo con prueba desde la consola | **No existe** (OPS-UX-01) | Auth0 y Okta |
| Extensibilidad síncrona del pipeline (claims personalizados, hooks inline) | **No existe**. Sólo Event Hooks asíncronos (`EventHookService.cs`). Sin mapeo de claims por cliente | Auth0 Actions, Okta inline hooks, Keycloak SPI y mappers |
| Directorio: grupos, reglas dinámicas, esquema de perfil, SCIM servidor | **Existe** | Okta UD, Entra; Keycloak sin SCIM nativo |
| Importación masiva de usuarios | **No existe** (pendiente en `OKTA-LEVEL-ROADMAP.md:25,29`) | Auth0, Okta y Keycloak |
| Gobierno: solicitudes, revisiones y SoD | **Existe** (`Services/Governance`) | Okta y Entra (con licencias aparte); Keycloak no lo tiene |
| Portal "Mis aplicaciones" | **Parcial**: sólo las aplicaciones SAML con IdP-initiated tienen "Abrir" (`SamlServiceProviderService.cs:303`). `OAuthClient` no tiene URL de inicio (`Domain/Entities/OAuthClient.cs:10-40`) | Okta dashboard y Entra My Apps lanzan aplicaciones OIDC |
| Acciones de mesa de ayuda | **Parcial**: desactivar, forzar cambio de contraseña y resetear MFA (`UsersController.cs:153-193`). **No hay** desbloquear cuenta (el runbook dice "espera…", `CONSOLE-RUNBOOKS.md:16-17`), ni listar o revocar las sesiones de un usuario concreto, ni reenviar el enlace de reset | Okta y Auth0 tienen las tres |
| Dominios propios, llaves y salud gestionados | **No existen** (OPS-UX-02/03/04) | Auth0 (custom domains, signing keys), Keycloak (realm keys), Okta (custom domains) |
| Multi-tenancy y organizaciones B2B | **No existe, por decisión** (`OKTA-LEVEL-ROADMAP.md:105-107`) | Auth0 Organizations y realms de Keycloak |
| Onboarding del desarrollador (quickstart con valores precargados, SDK publicados, modo dev) | **Débil** (DX-01…07) | Auth0 (quickstarts con dominio y client id), Okta (asistente de app), Keycloak (`start-dev`) |

### 5.4 Las brechas de usabilidad que más importan (en orden)

1. **Tiempo hasta la primera integración** (DX-01, 02, 04, 05 y 07). Hoy pasa por leer código y tests. Es la barrera de adopción número uno para los "proyectos hermanos".
2. **Login social y registro B2C**: Paquetenvia tiene registro abierto (`OWNER-ACTIONS.md:200-202`) pero no puede ofrecer "Continuar con Google" en el login hospedado (DX-08).
3. **Idioma y marca coherentes** en el login, los correos y los errores (ARQ-04 y ARQ-11): el usuario final percibe el producto como "AuthCenter en inglés" dentro de la aplicación de otra marca.
4. **Operación autoservicio** (OPS-UX-01…06): correo, llaves, dominio y estado del sistema siguen siendo tareas de propietario con variables y reinicios.
5. **Política de contraseñas, bloqueo y protección anti-bot configurables**: hoy están fijas, y el registro abierto no tiene captcha.
6. **Mesa de ayuda**: faltan desbloquear, ver y revocar sesiones de un usuario y reenviar el enlace de reset.

## 6. Top 10 recomendaciones

Esfuerzo: S ≈ días, M ≈ 1–3 semanas, L ≈ más de un mes (incremental).

| # | Recomendación | Hallazgos | Impacto | Esfuerzo |
|---|---|---|---|---|
| 1 | **Que el desarrollo local funcione tal como viene.** Perfil `Development` coherente (issuer = `Oidc:PublicOrigin` = `https://localhost:7157`, passkeys en ese origen, `Email:DevelopmentPickupDirectory` activo, `ActionLinks` hacia AuthCenter) y unificar los puertos (`launchSettings`, `vite.config.ts`, README). Un script `dev-up` (bash + pwsh) que genere RSA/SAML/DataProtection como ya hace `playwright.config.mjs`. Una opción `AllowLoopbackHttp` en los dos SDK. `docker build` en CI. | DX-02, DX-03, OPS-UX-05 | **Muy alto**: pasa de "horas leyendo tests" a minutos | S–M |
| 2 | **Publicar los SDK** (NuGet y npm, o GitHub Packages mientras tanto), con `LICENSE` y un aviso en las guías hasta que estén publicados. El workflow `sdk-release.yml` ya existe. | DX-01 | Alto | S (requiere OPS-14) |
| 3 | **Errores que se puedan corregir.** Página `/error` hospedada (HTML con marca, idioma y `traceId`) para `INVALID_CLIENT`/`INVALID_REDIRECT_URI` que muestre el valor recibido. Nombrar los scopes rechazados. Códigos RFC 6749 en `/oauth/token`. `error_description` visible en el BFF en Development. ProblemDetails como formato único para `/api`. | DX-05, ARQ-05, ARQ-06 | Alto | S–M |
| 4 | **Asistente "Nueva integración" en la consola.** Plantillas Web BFF, SPA, API y M2M. `LoginUrl` precargado con el login hospedado. Scopes alineados con los defaults del SDK (`offline_access` para BFF). Redirects `/signin-authcenter`. Panel "Conecta tu app" con authority y snippets. Seed opcional `Seed:DemoClients` para los samples. Sustituye a `Register-Paquetenvia.ps1`. | DX-04, DX-07, OPS-UX-06, ARQ-03 | Alto | M |
| 5 | **Correo operable.** Página de proveedor con estado y **Enviar prueba**, backlog del outbox y su antigüedad en el dashboard, límite de reintentos con dead letter visible, validar `Email:Host` en el arranque y plantillas por aplicación (marca e idioma). Corregir ya el "valid for 24 hours" del enlace mágico. | OPS-UX-01, ARQ-04 | Alto: hoy bloquea el registro con confirmación | M |
| 6 | **Capa de localización y catálogo de errores.** `ErrorCatalog` (código, estado y clave) + `IStringLocalizer` es/en, respetando `Accept-Language`/`ui_locales`. La API devuelve el mensaje ya localizado y desaparecen los mapas de `login.js`, `portal.js` y `errors.ts`. Textos del login (hero y subtítulo) configurables por aplicación. | ARQ-04, ARQ-05, ARQ-11 | Alto (usuario final + mantenibilidad) | M–L |
| 7 | **Login social en el login hospedado** como conexiones de redirect (Google, Microsoft, Apple, GitHub) reutilizando la federación OIDC por aplicación. Que los toggles de la consola controlen esos botones. | DX-08, sección 5.4 | Alto para aplicaciones B2C | M |
| 8 | **Contrato de API tipado y publicado.** `ApiResponse<T>`/`TypedResults` en lugar de `ApiResponse<object>` (302 usos), OpenAPI público de la superficie de integración, `types.ts` generado en CI y versionado por ruta antes de publicar los SDK. | ARQ-02, ARQ-06, DX-04, ARQ-10 | Medio-alto (cada cambio de UX con datos nuevos se acelera) | M |
| 9 | **Consola para operar.** Página "Estado del sistema" (readiness, esquema, workers, SMTP y validación de configuración), inventario de llaves y certificados con vencimientos, checklist de dominio y avisos de propagación. Después, la rotación de llaves desde la consola. Corregir la caché local por instancia (`IDistributedCache` o invalidación por versión). | OPS-UX-02, 03, 04, 07; ARQ-09 | Medio-alto | M (estado) / L (rotación) |
| 10 | **Refactor orientado a la velocidad de UX.** Partir `AuthService`, `OAuthAuthorizationService` y `FederationService` por caso de uso con un `SignInOutcome` común para `/api/auth` y `/ui-api`. Fuente única de reglas: endpoint de política de contraseñas y longitudes, defaults de aplicación en un solo sitio y mapa de permisos por reflexión. Tokens de diseño compartidos entre login, portal y consola, y primitivas de formulario en la consola (eliminar los 13 `Field` locales y aplicar `printWidth`). | ARQ-01, ARQ-03, ARQ-07, ARQ-10, ARQ-11 | Medio (velocidad y menos regresiones) | L (incremental) |

**Orden sugerido:** 1 → 3 → 2 → 4 → 5, que en conjunto ponen la primera integración en menos de una hora. Después 6 y 7 (usuario final), y por último 8 a 10 (plataforma).

## 7. Alcance y límites de esta auditoría

- Revisión **estática** del código: esta parte no ejecutó la API, los SDK ni Docker (las capturas del informe principal sí se tomaron con la API real).
  - Por eso dejé marcado como "riesgo a verificar" que el Dockerfile falle en `npm ci` (DX-03).
  - Las conclusiones sobre lo que ve el usuario ante un error de configuración (DX-05) se deducen del código de los controladores, los servicios y el SDK.
- Conteos:
  - Líneas con `wc -l`, sin migraciones salvo donde se indica.
  - Endpoints contando atributos `[Http*]` en `src/AuthCenter.Api/Controllers`.
  - Códigos de error contando literales distintos en `Failure("…")`.
  - Acciones de auditoría contando literales distintos en `LogAsync`/`AddAudit`.
  - Ajustes de `appsettings.json` contando hojas del JSON (los arreglos vacíos cuentan como una).
- Las referencias a los competidores describen capacidades generales y públicas de cada producto. Todas las afirmaciones sobre AuthCenter citan `archivo:línea`.
