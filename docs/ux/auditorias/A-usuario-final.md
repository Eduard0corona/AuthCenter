# Anexo A — Auditoría UX de la experiencia del usuario final

Parte del [análisis de arquitectura, producto y UX](../ANALISIS-PRODUCTO-UX.md). Base: `main` @ `0320243`, 2026-10-04.

Alcance: páginas estáticas servidas por la API (`src/AuthCenter.Api/wwwroot`), endpoints `/ui-api/...` que las alimentan y correos transaccionales. Solo lectura de código; evidencia como `archivo:línea` + cita breve.


## 1. Inventario de pantallas, estados y flujos

### 1.1 Rutas y documentos

| Ruta | Documento / JS | Propósito | Evidencia |
|---|---|---|---|
| `/` | redirección a `/login` | — | `Program.cs:484` |
| `/login` (`/login.html`) | `login.html` + `login.js` | Login alojado: todos los pasos (SPA de vistas `[data-view]`) | `Program.cs:485-487` |
| `/magic-link` | `login.html` (mismo JS) | Canjea el enlace de acceso **al cargar** | `Program.cs:497`, `login.js:25,828` |
| `/reset-password`, `/accept-invitation`, `/confirm-email`, `/confirm-email-change` | `account.html` + `account.js` | Páginas de enlaces enviados por correo (acción con clic explícito) | `HostedPages.cs:11-14`, `account.js:16-47` |
| `/logout` | `logout.html` + `logout.js` | Confirmar logout iniciado por un RP (requiere `logout_id`) | `Program.cs:488-490`, `EndSessionService.cs:94` |
| `/portal` | `portal.html` + `portal.js` | Portal del usuario final | `Program.cs:491-493` |
| `/oauth/authorize/response/{id}`, `/saml/idp/response/{id}` | HTML generado "Continuando…" + `form-post.js` | Autoenvío form_post al cliente/SP | `AuthorizationResponseResult.cs:19-28` |
| Errores SAML IdP | HTML generado en servidor | "No se pudo iniciar sesión en la aplicación" | `SamlIdpController.cs:220-230` |
| Errores `/oauth/authorize`, `/oauth/logout`, `/oauth/authorize/response/{id}` caducado | **JSON crudo** (`ApiResponse.Fail`) | Errores de cliente/redirect_uri/logout | `OAuthController.cs:92-93,205,236-237` |

### 1.2 Login alojado: estructura y vistas (`login.html`)

Cabecera **fija para todas las vistas** (fuera de `[data-view]`): `#brand-logo`, `#brand-name`, eslogan sin id ("Identidad segura para tus aplicaciones", `login.html:20`), `#page-title` ("Inicia sesión", `:24`), `#page-subtitle` (`:25`). Pie compartido: `#status` (`role=status`, `:121`), `#restart`/`#restart-link` (`:122`), `#legal` (`:123`). Panel lateral `aside.auth-hero` (`:125-127`).

| Vista | Elementos (ids) | Entrada / acción |
|---|---|---|
| `#login-form` | `#application` (hidden, `AUTHCENTER` por defecto), `#email`, `#federation-hint`, `#password-field`/`#password`, "Continuar", `#forgot-link`, `#magic-link`, `#passkey`, `#register-prompt`/`#register-link` | correo + contraseña |
| `#register-form` | `#register-name`, `#register-email`, `#register-password` (+`#register-password-hint`), `#register-confirm`, "Crear cuenta", `[data-back]` | nombre, correo, contraseña ×2 |
| `#register-sent-view` | `#register-sent-title`, `#register-sent-description`, `#register-resend`, `[data-back]` | reenviar confirmación |
| `#forgot-form` | `#forgot-email`, "Enviar enlace", "Volver" | correo |
| `#mfa-form` | `#mfa-description`, `#mfa-label`, `#mfa-code` (maxlength 12), `#mfa-send-email`, `#mfa-use-backup`, "Verificar", "Cancelar" | TOTP 6 dígitos / OTP correo / código de respaldo (10 hex) |
| `#enroll-totp-form` | `#totp-qr`, `details.secret` → `#totp-secret`, `#totp-copy`, `#totp-open`; `#totp-code` (maxlength 6) | escanear QR + código |
| `#backup-codes-view` | `#backup-codes-list`, `#backup-copy`, `#backup-download`, `#backup-saved`, `#backup-continue` (deshabilitado hasta marcar) | confirmar guardado |
| `#enroll-passkey-view` | `#enroll-passkey-description`, `#enroll-passkey-create`, "Cancelar" | ceremonia WebAuthn |
| `#password-change-form` | `#new-password`, `#confirm-password`, "Guardar y continuar", "Cancelar" | contraseña ×2 |
| `#consent` (`.card`) | `#consent-description`, `#consent-scopes` (badges), `#consent-allow`, `#consent-deny` | permitir / denegar |
| Estado "bloqueado" | todas las vistas ocultas; sólo `#status` (+ `#restart` si no hay interacción) | ninguna (`login.js:97-104`) |

### 1.3 Flujos (pasos, entradas, endpoints)

- **Contraseña**: correo + contraseña → (si `federationAvailable`) `POST /ui-api/session/federation/discover` → `POST /ui-api/session/login` → `continueSignIn` decide: cambio forzado / MFA / enrolamiento TOTP / enrolamiento passkey / sesión (`login.js:133-193`) → `finishLogin`: sin interacción → `return_url` o `/portal`; OIDC → `GET /oauth/interactions/{id}` → consentimiento o `POST /oauth/authorize/complete`; SAML → `POST /saml/idp/interactions/{id}/complete` (`login.js:686-776`).
- **Enlace mágico**: correo → "Envíame un enlace de acceso" → `POST /api/auth/magic-link/request`; guarda `authcenter.pendingSignIn` en `localStorage` 15 min (`login.js:283-293`) → correo → `/magic-link?token&email&application` → canje automático `POST /ui-api/session/magic-link` (`login.js:267-280`). Token: 15 min (`JwtSettings.cs:10`, máx. 30).
- **Passkey**: correo opcional → `POST …/passkey/options` → `navigator.credentials.get` → `POST …/passkey/complete` (`login.js:539-557`). Sin mediación condicional (autofill) aunque `#email` declara `autocomplete="username webauthn"` (`login.html:30`).
- **Federación**: `idp`/`domain_hint` o HRD por correo → `…/federation/start` → IdP externo → vuelve a `/login?federation_result=…` o `?federation_error=CODE` → `…/federation/complete` (`login.js:584-682`).
- **MFA TOTP / correo / respaldo**: un solo campo `#mfa-code`; correo: envío automático al entrar (`login.js:384`) y botón de reenvío; respaldo: alterna etiqueta e `inputmode` (`login.js:387-403`). Token pendiente 300 s (`MfaSettings.cs:8`), 5 intentos (`AuthService.cs:1296`). `trustDevice: false` fijo (`login.js:432`).
- **Enrolamiento forzado**: TOTP (QR + clave + código → códigos de respaldo con casilla obligatoria, `login.js:443-492`), passkey (`login.js:494-529`). Ventana 600 s (`UiSessionController.cs:310-312`).
- **Cambio forzado de contraseña**: `POST /ui-api/session/forced-change` (`login.js:158-176`).
- **Step-up** exigido por la aplicación: `POST /oauth/interactions/{id}/step-up` → mismas vistas de MFA/enrolamiento (`login.js:702-719`).
- **Consentimiento**: `#consent` con `clientDisplayName`, `applicationName` y scopes crudos (`login.js:741-758`).
- **Registro + "Revisa tu correo"**: `POST /ui-api/session/register` → `confirmationRequired` → `#register-sent-view` (reenvío `POST /api/auth/resend-email-confirmation`) | `approvalRequired` → vuelve al login con aviso | sesión (`login.js:320-372`).
- **Olvido / restablecer**: `#forgot-form` → `POST /api/auth/forgot-password` → mensaje neutro; correo → `/reset-password` (correo readonly + contraseña ×2 → `POST /api/auth/reset-password`, `account.js:55-69`).
- **Invitación**: `/accept-invitation` = mismo formulario y endpoint que reset (token de reset de Identity, `UserAccessService.cs:292-294`).
- **Confirmar correo**: `/confirm-email` → clic "Confirmar correo" → `POST /api/auth/confirm-email` → "Continuar e iniciar sesión" si hay sign-in pendiente en `localStorage` (`account.js:71-116`).
- **Cambio de correo**: portal `#email-form` → diálogo de reautenticación → `POST /api/auth/email-change/request` (correo al nuevo + aviso al anterior, `AccountManagementService.cs:236-241`) → `/confirm-email-change?token&email&userId` → clic → `POST /api/auth/email-change/confirm`.
- **Logout**: portal "Cerrar sesión" → `POST /ui-api/session/logout` → `/login` (`shared.js:73-76`). RP-initiated: `/oauth/logout` → cierre inmediato si `id_token_hint` coincide; si no, `/logout?logout_id=` → confirmar (`POST /oauth/logout/{id}/confirm`) / "Seguir conectado" (→ `/portal`) → `post_logout_redirect_uri` o `/login?signed_out=1` (`EndSessionService.cs:21`).
- **Portal** (`portal.html`): nav de 7 ítems + "Aprobaciones" condicional (`:12-21`). Paneles: Resumen (`#overview-grid`, 5 tarjetas), Seguridad (`#password-form`: `#current-password`, `#changed-password`, `#changed-password-confirm`; MFA: `#mfa-totp-setup`, `#mfa-email-setup`, `#mfa-backup-regenerate`, `#mfa-disable`; `#add-passkey`, `#passkeys-list`), Sesiones (`#revoke-all-sessions`, `#sessions-list`, `#devices-list`), Aplicaciones (`#applications-list`, `#request-access-form`: `#request-application`, `#request-role`, `#request-justification`; `#requests-list`), Aprobaciones (`#approval-requests-list`, `#reviews-list`, `#review-detail`), Proveedores (`#providers-list`, `#linkable-list`), Consentimientos (`#consents-list`), Cuenta (`#email-form`/`#new-email`, `#delete-form`: `#delete-password`, `#delete-confirm`). Diálogos: `#reauth-dialog`, `#decision-dialog`, `#code-dialog`, `#codes-dialog`; además `window.prompt`/`window.confirm` nativos (`portal.js:651,666,682`).
- **Páginas de error/estados terminales**: estado bloqueado del login (`login.js:97-108`), "Enlace incompleto" (`account.js:137-139`), `/logout` sin `logout_id` (`logout.js:24-25`), página de error SAML (`SamlIdpController.cs:220-230`), JSON crudo de `/oauth/*` (ver 1.1), página en blanco "Continuando…" (form_post).

## 2. Hallazgos

Índice:

| ID | Sev. | Tema |
|---|---|---|
| UX-01 | Alta | H1 y subtítulo fijos en todas las vistas del login |
| UX-02 | Alta | Errores en una única línea de estado al final, sin ARIA de campo |
| UX-03 | Alta | Códigos sin mapear: muestran texto del servidor en inglés o "Request failed (N)" |
| UX-04 | Media | `USER_CREATION_FAILED` mostrado como "contraseña débil" |
| UX-05 | Media | Quien espera aprobación ve "No tienes acceso" |
| UX-06 | Media | Sin estados ocupados, doble envío, parpadeo de marca |
| UX-07 | Media | Rate limit compartido que corta flujos legítimos; 429 sin tiempo de espera |
| UX-08 | Media | Reglas de contraseña ocultas; validación poco específica |
| UX-09 | Alta | Dispositivo MFA perdido sin salida; intentos sin aviso; enrolamiento sólo TOTP |
| UX-10 | Alta | Enlaces caducados o usados sin salida; enlace mágico sin clic |
| UX-11 | Alta | Interacción caducada o bloqueada sin acción; TTL incoherentes |
| UX-12 | Media | Logout sin salida, sin marca y sin confirmación final |
| UX-13 | Alta | Portal inaccesible directamente sin acceso a `AUTHCENTER`; consejo de desvinculación imposible de seguir |
| UX-14 | Media | Sesión caducada en el portal: "Request failed (401)"; carga todo o nada |
| UX-15 | Media | Foco entre vistas y nombres accesibles |
| UX-16 | Media | Foco 2.15:1, bordes 1.48:1, objetivos pequeños |
| UX-17 | Alta | Desborde del portal en móvil (pista `1fr` + nav sin salto de línea) |
| UX-18 | Alta | Marca parcial o invisible; contraste medido sobre el par equivocado |
| UX-19 | Baja | Sin modo oscuro |
| UX-20 | Baja | Tokens duplicados respecto a la consola |
| UX-21 | Alta | Sin arquitectura i18n |
| UX-22 | Alta | Consentimiento con scopes crudos |
| UX-23 | Media | Sin contraseña y passkeys: acción principal equivocada; dispositivos de confianza inutilizables |
| UX-24 | Alta | Correos en inglés, sólo HTML, sin marca, vigencias falsas |
| UX-25 | Media | Errores como JSON crudo o HTML sin estilo |
| UX-26 | Baja | Detalles del portal |

Formato: **Severidad** · Evidencia · Impacto · Recomendación. "Por código" = deducido de la lectura, no observado en pantalla.

### A. Estructura del login y patrón de mensajes

**UX-01 · Alta — H1 y subtítulo fijos en todas las vistas del login.**
- Evidencia: `#page-title` "Inicia sesión" y `#page-subtitle` "Usa tu cuenta organizacional…" están fuera de los `[data-view]` (`login.html:23-26`). `showView()` sólo alterna `[data-view]` (`login.js:80-85`: `for (const item of views) item.hidden = item !== view`). Ningún código de `login.js` toca `#page-title`/`#page-subtitle` (sólo `account.js:138-143` lo hace en su página). `document.title` se fija una vez (`login.js:791`). Cada vista añade su propio `<h2>` debajo ("Verificación en dos pasos", "Autorizar aplicación"…), así que quedan dos encabezados que compiten.
- Impacto: en consentimiento, códigos de respaldo o "Revisa tu correo" el encabezado principal dice "Inicia sesión". La jerarquía de títulos queda mal para lectores de pantalla y la pestaña no refleja el paso. El texto "cuenta organizacional" no sirve para apps con registro abierto (B2C).
- Recomendación: un mapa `vista → {título, subtítulo, document.title}` aplicado en `showView`. Promover el `<h2>` de cada vista a H1 y eliminar el bloque fijo. Hacer el subtítulo configurable o neutro ("Continúa en {app}").

**UX-02 · Alta — Errores en una única línea de estado al final del panel.**
- Evidencia: un solo `<p id="status" role="status" aria-live="polite">` después de todos los formularios (`login.html:121`; igual en `account.html:40` y `portal.html:24`). El helper `status()` sólo cambia texto y clase (`shared.js:58-61`). Todas las validaciones lo usan, por ejemplo `login.js:136,146,163,165,328,332,335,337,423,465`. No existe `aria-invalid` ni `aria-describedby` de error en ningún archivo; el único `aria-describedby` es la pista de contraseña del registro (`login.html:48`). El foco va al campo culpable sólo en 4 casos (`login.js:145,257,327,331`).
- Impacto: el error aparece lejos del campo y, en móvil con teclado abierto (p. ej. el registro con 4 campos), queda fuera de la vista. Un lector de pantalla oye un aviso "polite" pero el campo no queda marcado como inválido. Además, progreso, éxito y error comparten el mismo hueco.
- Recomendación: error en línea bajo cada campo (`aria-invalid` + `aria-describedby`) y un resumen arriba del formulario con `role="alert"` que enfoque el primer campo inválido. Dejar `#status` sólo para progreso y éxito.

### B. Mapeo de códigos de error a mensajes

**UX-03 · Alta — Mapeo incompleto: los códigos sin entrada muestran el texto del servidor en inglés o "Request failed (N)".**
- Evidencia:
  - Fallback genérico: `api()` usa `payload?.message || … || \`Request failed (${response.status})\`` (`shared.js:20`). `handleSignInError` hace `signInMessages[error.code] ?? error.message` (`login.js:230`), `handleInteractionError` hace `status(message, error.message)` (`login.js:738`), `logout.js:38` usa `error.message` y el portal `errorMessages[error.code] ?? error.message` (`portal.js:791-794`).
  - Login, códigos que el backend puede devolver a estos endpoints y que no tienen entrada en `signInMessages` (`login.js:195-224`):
    - `APP_NO_SETTINGS`, `ROLE_ASSIGN_FAILED` (`AuthService.cs:107,166`; reenviados en `UiSessionController.cs:282-284`)
    - `NO_LOCAL_PASSWORD` (`AuthService.cs:728`)
    - `MFA_ALREADY_ENABLED` ("Two-factor authentication is already enabled. Sign in again.", `AuthService.cs:1169`)
    - `MFA_NOT_CONFIGURED`, `MFA_NOT_SETUP` (`TotpService.cs:51,102`)
    - `USER_NOT_FOUND`, `PASSKEY_STORAGE_FAILED`, `INVALID_PASSKEY_REQUEST` (`PasskeyService.cs:62,112,180`)
    - `INVALID_CSRF_TOKEN` (`UiCsrfMiddleware.cs:24`)
    - `INTERNAL_ERROR` ("An unexpected error occurred.", `ExceptionHandlingMiddleware.cs:77`)
    - 401 sin cuerpo ("Request failed (401)").
  - Portal, códigos sin entrada en `errorMessages` (`portal.js:753-789`):
    - `VALIDATION_FAILED` ("One or more validation errors occurred.", `RequestValidationFilter.cs:39`)
    - `NO_LOCAL_PASSWORD`, `MFA_NOT_ENABLED`, `PROVIDER_NOT_FOUND`, `PASSKEY_NOT_FOUND`
    - `INVALID_PASSKEY` (nombre de más de 100 caracteres escrito en el `window.prompt`, `PasskeyService.cs:86`)
    - `CONSENT_NOT_FOUND`, `DELETION_NOT_CONFIRMED`, `FEDERATION_LINK_REQUIRES_SESSION`, `JIT_PROVISIONING_FAILED`.
  - Otros casos:
    - `account.js:87` concatena el texto de Identity en inglés: "…política de seguridad: ${error.message}".
    - `/logout`: "The sign-out request expired or was already used." (`EndSessionService.cs:115-117`).
    - Hay tres diccionarios duplicados con textos distintos para el mismo código. `INVALID_MFA_CODE` dice cosas diferentes en `login.js:211` y en `portal.js:755`. `USER_INACTIVE` está fijado en el portal a "La cuenta de quien la pidió está inactiva." (`portal.js:786`) pero también lo devuelve la vinculación de proveedor (`FederationService.cs:307`), donde ese texto no aplica.
- Impacto: mensajes en inglés o técnicos en una UI en español, y acciones fallidas sin explicación accionable.
- Recomendación: un catálogo único `code → mensaje` (y acción sugerida) compartido por las 4 páginas, con contexto donde haga falta y un fallback genérico en español. No mostrar nunca `error.message` del servidor. Cubrir 0 (red), 401, 403, 429, 500 y CSRF.

**UX-04 · Media — Un fallo al crear el usuario se presenta como "contraseña débil".**
- Evidencia: el controlador traduce `USER_CREATION_FAILED` a `WEAK_PASSWORD` (`UiSessionController.cs:280-281`), y `login.js:218` muestra "La contraseña no cumple la política de seguridad." Pero `CreateAsync` también falla por `DuplicateEmail` (carrera de doble clic) o por `InvalidUserName`, porque `UserName = request.Email` (`AuthService.cs:135`) con los caracteres permitidos por defecto de Identity, sin personalizar. El validador sólo exige `EmailAddress()` (`RegisterRequestValidator.cs`), y el navegador acepta, por ejemplo, un apóstrofo en la parte local.
- Impacto: el usuario cambia una contraseña que estaba bien y no puede registrarse sin saber por qué.
- Recomendación: distinguir los códigos de Identity (`DuplicateEmail` lleva a la respuesta neutra de registro, `InvalidUserName` a "Este correo no es compatible") o permitir esos caracteres en el nombre de usuario.

**UX-05 · Media — Quien espera aprobación ve "No tienes acceso a esta aplicación."**
- Evidencia: con `ApprovalRequired`, el acceso se crea inactivo (`AuthService.cs:155-156`). Al iniciar sesión, `HasActiveAccessAsync` falla y se devuelve `ACCESS_DENIED` (`AuthService.cs:263-268`), que el login muestra como "No tienes acceso a esta aplicación." (`login.js:207`). `APPROVAL_REQUIRED` sólo se emite en el registro (`AuthService.cs:188`).
- Impacto: tras registrarse, el usuario cree que fue rechazado. No ve el estado de su solicitud ni dónde consultarlo.
- Recomendación: un código específico (`ACCESS_PENDING`) con un mensaje que diga quién decide y que se le avisará por correo.

### C. Estados de carga, respuesta y doble envío

**UX-06 · Media — Sin estados ocupados: botones siempre activos, doble envío y marca que "parpadea".**
- Evidencia:
  - No se deshabilitan botones en `login.js` ni en `account.js`. Las únicas excepciones son `#backup-continue` (`login.js:487`) y el submit del diálogo de código del portal (`portal.js:617-620`).
  - Hay texto de progreso sólo en algunas acciones (`login.js:148,166,269,338,445,509,543,610,649`). No hay ninguno para verificar MFA (`login.js:419-439`), enviar el olvido de contraseña (`:242-252`), pedir un enlace mágico (`:254-265`), reenviar (`:363-372`) ni en el consentimiento (`:756-757`).
  - Consecuencias del doble envío (por código):
    - **Reset de contraseña**: el primer POST cambia el sello de seguridad (`AuthService.cs:943`) y el segundo devuelve `INVALID_TOKEN`. Entonces `fail()` sobrescribe el éxito con "El enlace no es válido, expiró o ya se usó" (`account.js:55-69,85-92`).
    - **Registro**: el segundo POST entra por `EMAIL_TAKEN` y envía al mismo usuario el aviso "Sign-up attempt with your email" (`UiSessionController.cs:271-272`, `AuthService.cs:207`), o cae en UX-04.
    - **MFA**: el segundo POST devuelve `TOKEN_ALREADY_USED`, que manda de vuelta al login (`login.js:228-229`).
  - Arranque: `initialize()` encadena 3-4 peticiones (contexto/opciones, branding y sesión, `login.js:803-825`) antes de aplicar la marca y `ready`. Mientras tanto se ve "AuthCenter" con los colores por defecto y los clics esperan sin feedback.
- Impacto: errores falsos, correos de seguridad confusos y sensación de lentitud o de que "no pasa nada".
- Recomendación: un helper `withBusy(button, fn)` que ponga `disabled` + `aria-busy` y un spinner, usado en todos los submits. Enviar el branding en el HTML (render en servidor o un `<link>` del tema por `application`) o al menos paralelizar las peticiones. Mostrar un esqueleto hasta `ready`.

**UX-07 · Media — Límites de frecuencia que penalizan flujos legítimos y avisos sin tiempo de espera.**
- Evidencia: la política `Login` (5/min por IP, `RateLimitingExtensions.cs:45`) se comparte entre `login`, `passkey/options`, `passkey/complete`, `mfa/enrollment/start`, `passkey/enrollment/options|complete` y `step-up` (`UiSessionController.cs:91,121,133,232,243,251`; `OAuthController.cs:176`). La clave del contador es `{policy}:{dimensión}:{partición}` (`RateLimitMiddleware.cs:41`). El enrolamiento forzado de passkey gasta 5 permisos (login + options + complete + options + complete), así que un error de contraseña previo produce un 429 en el último paso (por código). El 429 trae `Retry-After` (`RateLimitMiddleware.cs:56`), pero la UI muestra "Espera un momento" sin plazo (`login.js:227`, `portal.js:792`).
- Impacto: un bloqueo tras crear la passkey, más fricción detrás de NAT corporativo.
- Recomendación: políticas separadas por endpoint o contar sólo los fallos. Mostrar "Inténtalo en N s" leyendo `Retry-After`.

### D. Validación

**UX-08 · Media — Reglas de contraseña ocultas hasta fallar y validación poco específica.**
- Evidencia:
  - La pista de reglas sólo aparece en el registro (`login.html:49`). No está en el cambio forzado (`login.html:106-112`), ni en reset/invitación (`account.html:28-33`), ni en el cambio desde el portal (`portal.html:33-38`). Ahí sólo aparece tras el error (`shared.js:169-175`).
  - Los `details` de validación del servidor se descartan (`shared.js:20-21`).
  - En el portal, `PASSWORD_CHANGE_FAILED` no distingue "contraseña actual incorrecta" de "política" (`portal.js:759`; `AccountManagementService.cs:56` las junta en un solo mensaje).
  - `#totp-code maxlength="6"` (`login.html:87`) trunca un código pegado con espacio ("123 456") y la regex `^\d{6}$` (`login.js:465`) lo rechaza. Los códigos de respaldo no normalizan espacios ni guiones (`TotpService.cs:469-470`).
  - No hay botón de mostrar contraseña.
- Recomendación: pista visible y validación en vivo en los 4 formularios de contraseña, normalizar los códigos (quitar espacios y guiones antes de validar), separar los códigos de error del cambio de contraseña y añadir un botón de mostrar/ocultar.

### E. Rutas de recuperación

**UX-09 · Alta — Dispositivo MFA perdido: sin salida guiada; intentos agotados sin aviso.**
- Evidencia: la vista MFA sólo ofrece "Usar un código de respaldo" y, para el factor de correo, el reenvío (`login.html:71-74`). No hay enlace "¿Perdiste el acceso?" ni texto de soporte. El enlace de soporte sólo existe si la marca define `supportUrl`, y aparece en el pie (`login.js:796-797`). Tras 5 fallos el token se consume (`AuthService.cs:585-591,1296`) y el siguiente intento muestra "Este paso ya se usó. Inicia sesión de nuevo." (`login.js:213,228-229`), sin haber avisado de los intentos restantes. El enrolamiento forzado sólo admite TOTP (`AuthService.cs:665,1139` con `EnrollmentKinds.Totp`; `login.html:78-89`), aunque el portal sí ofrece códigos por correo (`portal.html:45`).
- Impacto: un usuario sin el móvil o sin app de autenticación queda bloqueado sin saber qué hacer.
- Recomendación: un enlace "No tengo acceso a mi verificación" con instrucciones y contacto (de la marca o global), un contador "Te quedan N intentos" y la opción "Recibir códigos por correo" en el enrolamiento forzado cuando la política lo permita.

**UX-10 · Alta — Enlaces caducados o usados que terminan en callejón sin salida.**
- Evidencia:
  - **Correo sin confirmar**: el login muestra "Confirma tu correo…" (`login.js:198,206`), pero no hay botón de reenvío en esa vista. `#register-resend` sólo existe en `#register-sent-view` (`login.html:57`), y `registeredEmail` sólo se asigna allí (`login.js:355`).
  - **Invitación caducada**: el invitado se crea con `HasLocalPassword = false` (`UserAccessService.cs:271`), y para esas cuentas el olvido de contraseña no hace nada (`AuthService.cs:911-915`). Sin embargo, la página de error le dice "Pide uno nuevo" (`account.js:91`).
  - **Errores transitorios tratados como enlace inválido**: `fail()` convierte cualquier error no previsto (red, 500) en "El enlace no es válido, expiró o ya se usó" y oculta el formulario (`account.js:85-92`). Como el token ya se quitó de la barra (`account.js:14`), recargar lleva a "Enlace incompleto" (`account.js:137-139`).
  - **CTA de salida inadecuado**: tras el fallo el botón es "Iniciar sesión" (`account.js:98-99`). No hay "Pedir un enlace nuevo" ni un enlace directo a la vista de olvido.
  - **Enlace mágico consumido por escáneres**: se canjea automáticamente al cargar (`login.js:828`), al contrario que las demás páginas, que exigen un clic precisamente para que los escáneres de enlaces no consuman el token (`account.js:3-6`). Resultado posible: "Este enlace de acceso ya se usó" (`login.js:277`).
- Impacto: soporte manual por cada enlace caducado. Usuarios invitados que no pueden activar su cuenta.
- Recomendación:
  - Reenvío de confirmación desde el login cuando llega `EMAIL_NOT_CONFIRMED`.
  - Para las invitaciones, que el olvido de contraseña envíe una nueva invitación o un mensaje "pide al administrador…".
  - En `fail()`, distinguir red/500 (mantener el formulario y permitir reintento) de `INVALID_TOKEN`.
  - Un CTA contextual ("Pedir un enlace nuevo" que abra el login en la vista adecuada).
  - Exigir un clic en `/magic-link` ("Continuar como …").

**UX-11 · Alta — Interacción caducada o bloqueada: pantalla sin acción y tiempos incoherentes.**
- Evidencia:
  - `showBlocked()` oculta todas las vistas y oculta también el enlace de reinicio cuando hay interacción (`login.js:97-104`), así que sólo queda el texto de error bajo "Inicia sesión".
  - La interacción vive 10 min (`OAuthAuthorizationService.cs:26`), menos que el sign-in pendiente guardado en el navegador, que dura 15 min (`login.js:290`). Si alguien confirma su correo pasados 10 min, "Continuar e iniciar sesión" (`account.js:105-110`) lleva a `showUnavailable()` (`login.js:827`), un callejón sin salida. En cambio `/magic-link` sí se recupera con `interactionId = null` (`login.js:805`).
  - No hay aviso previo a la expiración (`login.js:116-125`). El paso MFA (5 min) devuelve al inicio sin advertir (`login.js:111-114`).
  - El cliente OAuth no tiene una URL de inicio que permita ofrecer "Volver a la aplicación" (campos en `OAuthClient.cs:10-44`).
- Recomendación:
  - Estado bloqueado con una acción principal ("Volver a {app}", si se registra una URL de inicio en el cliente) y una secundaria ("Ir a mi cuenta").
  - Igualar los TTL o aplicar el mismo fallback que `/magic-link` en el regreso desde la confirmación.
  - Un aviso "Esta solicitud caduca en 1 min".

**UX-12 · Media — Cierre de sesión: página sin salida, sin marca y sin confirmación final.**
- Evidencia:
  - Abierta directamente, `/logout` (sin `logout_id`) mantiene la pregunta estática (`logout.html:22`) con los botones ocultos (`logout.html:24`) y muestra el error "expiró…" (`logout.js:24-25`). No hay ningún enlace de salida.
  - La página no tiene logo, tema ni enlaces legales: "AuthCenter" va fijo (`logout.html:14-19`) aunque el logout pendiente conoce el cliente y la aplicación (`EndSessionService.cs:92`).
  - Los errores de confirmación salen en inglés (`logout.js:38`).
  - El destino tras el logout, `/login?signed_out=1` (`EndSessionService.cs:21`), no se procesa en `login.js` (no aparece `signed_out` en ningún archivo JS).
  - "Cerrar sesión", "Cerrar todas las sesiones" y "Eliminar mi cuenta" del portal llevan a `/login` sin mensaje (`shared.js:75`, `portal.js:427,464`).
- Recomendación:
  - Sin `logout_id`: si hay sesión, ofrecer "Cerrar sesión" directo; si no, "No hay sesión activa" con "Iniciar sesión".
  - Aplicar la marca de la aplicación del cliente.
  - Mostrar "Cerraste sesión" o "Tu cuenta se eliminó" en el login.

**UX-13 · Alta — El portal sólo es accesible directamente para quien tiene acceso a la app `AUTHCENTER` (por código).**
- Evidencia:
  - Sin sesión, `/portal` redirige a `/login?return_url=…`, sin `application` (`shared.js:46-47`). El login usa `AUTHCENTER` por defecto (`login.html:29`) y, sin acceso, devuelve `ACCESS_DENIED` (`AuthService.cs:263-268`). `HasActiveAccessAsync` no hace excepción para el portal (`UserAccessService.cs:639-646`). Registro e invitación sólo conceden la app de origen (`AuthService.cs:155-156`; `UserAccessService.cs:284`).
  - Afecta a "Cerrar sesión" del portal y a los CTA de los correos "Open your account" y "Review the request" (`AccessGovernanceService.cs:137`).
  - Contradicción relacionada: `CANNOT_UNLINK_LAST_PROVIDER` aconseja "Crea una contraseña o una passkey primero" (`portal.js:764`), pero el portal no permite crear contraseña a cuentas sin una (`portal.js:90-96`), la comprobación del servidor ignora las passkeys (`AccountManagementService.cs:210-211`) y el olvido de contraseña no hace nada para esas cuentas (`AuthService.cs:911-915`).
- Impacto: usuarios finales de apps cliente que, fuera de una sesión SSO previa, no pueden gestionar su cuenta (MFA, sesiones, solicitudes), además de un consejo imposible de seguir.
- Recomendación: un login de portal independiente del acceso a apps (sesión de "cuenta" para todo usuario activo). Ofrecer "Crear contraseña" a cuentas sin contraseña, mediante un enlace de verificación por correo, y alinear el mensaje con la regla real del servidor.

**UX-14 · Media — Sesión caducada o revocada con el portal abierto: "Request failed (401)" y sin camino de vuelta.**
- Evidencia:
  - La cookie devuelve 401 sin cuerpo (`Program.cs:97-100`), así que `api()` construye el mensaje "Request failed (401)" (`shared.js:20`). Sólo la carga inicial redirige al login ante un 401 (`shared.js:45-48`); `mutate()` muestra el texto tal cual (`portal.js:410-413`).
  - La sesión SSO dura 480 min fijos, sin renovación (`SingleSignOnSettings.cs:9`, `Program.cs:96`). Lo mismo ocurre en el consentimiento (`login.js:745-746,738`).
  - La carga del portal es "todo o nada": `Promise.all` de 12 peticiones (`portal.js:38-43`), y un solo fallo deja todo en "Cargando…" con un único mensaje (`portal.js:59`).
- Recomendación: un interceptor de 401 en `api()` que lleve a re-login con `return_url`, y una carga por panel con reintento individual.

### F. Gestión del foco, teclado y ARIA

**UX-15 · Media — El foco no acompaña al contenido entre vistas y los nombres accesibles son pobres.**
- Evidencia:
  - **Códigos de respaldo**: el foco va a la casilla y se salta la lista de códigos y los botones Copiar/Descargar (`login.js:490`).
  - **"Revisa tu correo"**: el foco va a "Reenviar" y se salta el título y la descripción; además se limpia el estado (`login.js:359-360`).
  - **Estado bloqueado**: `showView(null)` deja el foco en un elemento ya oculto (`login.js:84,98`).
  - **Portal**: `hashchange` (los enlaces "Ver detalles") no mueve el foco (`portal.js:30,800-803`). Tras cada acción, `refresh()` vuelve a renderizar las listas (`portal.js:339`) y el botón enfocado desaparece, con lo que el foco se pierde.
  - **Enlace de salto**: apunta a `#main`, que es todo el layout, no al formulario (`login.html:13-14`).
  - **Botones repetidos sin contexto**: "Revocar", "Eliminar", "Renombrar", "Aprobar" no indican a qué elemento se refieren (`portal.js:121-122,130,134,205-206,315,318,325`). Sólo "Abrir" tiene `aria-label` (`portal.js:379`). El texto "Ver detalles" se repite 5 veces (`portal.js:83`).
  - **Otros**: el H1 del portal es la marca "AuthCenter" (`portal.html:12`). Los scopes del consentimiento son `<span>` sin semántica de lista (`login.js:753-755`). Los errores se anuncian con `role=status` (polite).
- Recomendación:
  - Enfocar el H1 de cada vista (`tabindex=-1`), y la lista o título de los códigos de respaldo.
  - En el portal, enfocar el panel al cambiar el hash y devolver el foco al elemento equivalente tras re-renderizar.
  - `aria-label` con contexto ("Revocar sesión de Chrome en Windows").
  - `role=alert` para errores y `<ul>` para los scopes.

### G. Contraste, tamaño de objetivo y movimiento reducido

**UX-16 · Media — Anillo de foco y bordes de campo por debajo de 3:1; enlaces-botón pequeños.**
- Evidencia (ratios calculados con la fórmula WCAG):
  - Foco `#f59e0b` (`app.css:27-29`): **2.15:1** sobre blanco, 2.05:1 sobre `#f8fafc` y 2.41:1 junto al botón primario. Por debajo de 3:1 (WCAG 1.4.11).
  - Borde de input `#cbd5e1` (`app.css:7,30`): **1.48:1**.
  - `button.link` con `padding: 0` (`app.css:74`): el alto del objetivo es el de la línea de texto (≈19 px). Afecta a "¿Olvidaste tu contraseña?", "Usar un código de respaldo", "Crear cuenta" y "Enviar un código a mi correo". El axe `target-size` puede pasar por la excepción de espaciado, pero en táctil queda lejos de 44 px.
  - `textarea` y `summary` no entran en la regla de foco propia (`app.css:27`).
  - Pie de los correos `#888` sobre blanco a 12 px: 3.54:1 (`SmtpEmailService.cs:69,85,104,190`).
  - Movimiento reducido: la regla existe (`app.css:106`), pero no hay animaciones, así que no aplica. Sin problema.
  - Texto blanco sobre el primario por defecto: 5.17:1, correcto. El riesgo está en los colores de marca (UX-18).
- Recomendación: foco de 2 px oscuro más halo claro (≥3:1 en ambos fondos), bordes ≥3:1 (por ejemplo `#64748b`), `min-height: 44px` y padding en los enlaces-botón, y gris del pie ≥4.5:1.

### H. Puntos de corte y desbordamiento

**UX-17 · Alta — El portal desborda en móvil por la pista `1fr` y el ancho mínimo del nav.**
- Evidencia: por debajo de 760 px la grilla pasa a `grid-template-columns: 1fr` (`app.css:99`), es decir `minmax(auto, 1fr)`. El `aside.sidebar` no tiene `min-width: 0`, y su contenido mínimo es el nav flex con enlaces `white-space: nowrap` (`app.css:101-102`; 7-8 enlaces en `portal.html:13-20`). La columna se estira hasta ese mínimo (patrón conocido como "grid blowout"); el `overflow-x: auto` del nav no lo evita porque el aside sí aporta su min-content. `.main` tiene `min-width: 0` (`app.css:54`), pero no basta.
- Otros problemas de layout:
  - Hay un único punto de corte (760 px).
  - Entre 761 y unos 1000 px el hero conserva un mínimo de 20rem (`app.css:40`) y comprime el formulario.
  - `.topbar` es flex sin `wrap` (`app.css:55`) y un correo largo sin espacios puede empujar el ancho.
  - Aun corrigiendo el desborde, un nav con scroll horizontal esconde secciones sin pista visual.
  - La suite e2e hospedada sólo corre "Desktop Chrome" (`tests/AuthCenter.HostedUi.Tests/playwright.config.mjs`, `projects`), mientras que la consola sí verifica el reflow a 320 px (README §Running Tests).
- Recomendación: `.app-shell { grid-template-columns: minmax(0,1fr) }` y `.sidebar { min-width: 0 }`; nav que envuelva (`flex-wrap`) o un selector o menú en móvil; `overflow-wrap: anywhere` en el correo; un proyecto móvil (390×844) y una comprobación de reflow (`scrollWidth <= innerWidth`) en la e2e hospedada.

### I. Marca por aplicación

**UX-18 · Alta — La marca por aplicación es parcial, en parte invisible, y su control de contraste mide el par equivocado.**
- Qué es configurable (`ApplicationBrandingSettings.cs:7-13`): nombre visible, color primario, color de fondo, logo HTTPS y URLs de soporte, privacidad y términos. El `theme.css` sólo emite `--brand-primary` y `--brand-background` (`ApplicationsController.cs:42`).
- Dónde se aplica: en el login, nombre, título, logo, legales y tema (`login.js:785-799`); en las páginas de enlaces, nombre, logo, legales y tema, aunque el `<title>` sigue terminando en "· AuthCenter" (`account.js:143`).
- Dónde **no** se aplica:
  - El portal: "AuthCenter" fijo (`portal.html:6,12`); `portal.js` no llama a `appendTheme` ni a la API de marca, y la sesión no conoce la app de origen.
  - `/logout` (`logout.html:14-19`).
  - La página de confirmación del cambio de correo: su enlace se genera sin app (`AccountManagementService.cs:238`).
  - Los correos (UX-24), el remitente (`EmailSettings.cs:11`), el emisor TOTP que ve el usuario en su app de autenticación (`MfaSettings.cs:7`, global) y la página de error SAML.
- Contenido fijo no configurable:
  - El eslogan "Identidad segura para tus aplicaciones" bajo el nombre de la app (`login.html:20`, `account.html:20`, `logout.html:17`). `loadBranding` sólo sustituye `#brand-name` (`login.js:790`).
  - El hero "Passwordless · MFA · OIDC · SAML / Una identidad, controles consistentes." (`login.html:125-127`, `account.html:44-46`), que sólo se oculta por debajo de 760 px (`app.css:98`).
  - El subtítulo "cuenta organizacional" (UX-01).
- El **color de fondo no se ve**: el `body` lo usa (`app.css:17`), pero el panel blanco (`app.css:41`) y el hero con degradado (`app.css:42`) lo tapan por completo. Sólo la vista previa de la consola lo pinta, detrás de una tarjeta centrada sin hero ni eslogan (`BrandingDialog.tsx:54-61`), así que la vista previa no se parece a la página real.
- El **control de contraste** compara primario contra fondo (`branding.ts:16-19`, y sólo en el cliente; el servidor valida únicamente el formato hex, `ApplicationService.cs:107-108`). La página real pinta texto blanco sobre el primario (`app.css:22`), enlaces primarios sobre blanco (`app.css:18,74`) y texto blanco sobre un degradado que empieza en el primario (`app.css:42`). Un primario claro "cumple AA" en la consola y resulta ilegible en el login.
- Otros:
  - Dos nombres distintos para la misma app: la cabecera usa `branding.displayName` y los textos usan `ApplicationSystem.Name` (`login.js:312-314,751`; `ApplicationService.cs:82`; `OAuthAuthorizationService.cs:346`).
  - Los enlaces legales abren en la misma pestaña en mitad del flujo (`login.js:797`).
  - El branding se cachea 300 s (`ApplicationsController.cs:26,37`).
- Recomendación:
  - Eliminar el eslogan y el hero por defecto en logins con marca, o convertirlos en campos opcionales (lema e imagen del panel).
  - Hacer visible el fondo (panel centrado sobre el fondo de marca).
  - Calcular el color de texto sobre el primario (`--brand-on-primary`) y validar en el servidor los pares reales.
  - Una vista previa que reutilice `app.css`.
  - Marca en el portal y en el logout (la app de la última sesión o `?application`) y en los correos.

### J. Modo oscuro

**UX-19 · Baja — Sin modo oscuro, y bloqueado explícitamente.**
- Evidencia: `color-scheme: light` (`app.css:10`) y `<meta name="color-scheme" content="light">` (`login.html:6`, `account.html:6`, `logout.html:6`). No hay `prefers-color-scheme` en `app.css` ni en la consola (`AuthCenter.Admin/src/styles.css:2`). La marca sólo tiene colores claros.
- Impacto: una pantalla blanca de alto brillo en sistemas en modo oscuro, típicamente de noche o en móvil.
- Recomendación: una paleta oscura por tokens bajo `@media (prefers-color-scheme: dark)`, con variantes de marca opcionales (`PrimaryColorDark`), primero en las páginas de usuario.

### K. Tokens de diseño duplicados respecto a la consola

**UX-20 · Baja — Dos sistemas de tokens paralelos con valores casi iguales pero distintos.**
- Evidencia (`app.css:1-12` frente a `Admin/src/styles.css:1-28`):

| Uso | Páginas de usuario | Consola |
|---|---|---|
| Texto | `#0f172a` | `#101828` |
| Atenuado | `#475569` | `#667085` |
| Borde | `#cbd5e1` | `#d0d5dd` |
| Peligro | `#b91c1c` | `#b42318` |
| Éxito | `#047857` | `#027a48` |
| Foco | `#f59e0b` | `#f79009` |
| Botón primario | `#2563eb` | `--blue-700 #175cd3` (`styles.css:92`) |

  Se duplican `.sr-only`, `.skip-link` y la regla de foco. `app.css` además usa colores fuera de tokens (`#e2e8f0`, `#f1f5f9`, `#334155`, `#0f172a`, `white` en `app.css:22,24,51,53,63,64,76,84,85`), no tiene escala tipográfica y tiene un único punto de corte.
- Impacto: incoherencia visual entre la consola y la experiencia de usuario, y cambios de marca o de accesibilidad que hay que hacer dos veces.
- Recomendación: un paquete único de tokens (CSS custom properties) importado por ambos, con nombres semánticos (`--color-text`, `--color-border-strong`, `--focus-ring`) sobre los que se monta la marca por aplicación.

### L. Arquitectura de i18n

**UX-21 · Alta — No hay i18n: español fijo en el cliente, inglés en el servidor y en los correos, y ninguna negociación de idioma.**
- Evidencia:
  - Los textos están escritos directamente en los 4 HTML y en diccionarios JS: `signInMessages` (28 entradas, `login.js:195-224`), `federationMessages` (16, `login.js:659-676`), `errorMessages` (35, `portal.js:753-789`), la configuración de páginas (`account.js:16-47`) y textos en `shared.js:12,136-141,169`.
  - `lang="es"` está fijo en `login.html:2`, `account.html:2`, `logout.html:2`, `portal.html:2` y en las páginas que genera el servidor (`SamlIdpController.cs:224`, `AuthorizationResponseResult.cs:20`).
  - `ui_locales` se lee y se reenvía (`OAuthController.cs:74,247`; `AuthorizeRequest.cs:25`), pero nada lo consume. No hay `Accept-Language`, `IStringLocalizer` ni `.resx` (búsqueda sin resultados), ni campo de idioma en usuario, aplicación o configuración.
  - Las fechas usan el idioma del navegador (`Intl.DateTimeFormat(undefined…)`, `shared.js:64`), lo que mezcla idiomas con un navegador en inglés. Los correos de gobierno muestran "yyyy-MM-dd HH:mm UTC" (`AccessReviewService.cs:286`).
  - La página de error SAML tiene el título en español y el mensaje en inglés (`SamlIdpController.cs:224-227` + `SamlIdentityProviderService.cs:107,119,299,308`).
  - Hay jerga técnica para el usuario final: "Passwordless · MFA · OIDC · SAML", "refresh tokens" (`portal.html:96`), "SAML"/"OpenID Connect" (`portal.js:317`), códigos internos de app en Sesiones (`portal.js:129`) y scopes crudos (`portal.js:324`).
  - La e2e fija `locale: "es-ES"` (`playwright.config.mjs`, `use`), así que la mezcla de idiomas no se detecta.
- Recomendación: catálogos por idioma (`es`, `en`) cargados por la página. Negociar en este orden: `ui_locales`, luego idioma del usuario, luego idioma por defecto de la app, luego `Accept-Language`. `lang` dinámico. El servidor devuelve sólo códigos más parámetros; correos por idioma; fechas con el idioma de la UI.

### M. Consentimiento

**UX-22 · Alta — El consentimiento muestra scopes crudos y no da contexto de cuenta ni del solicitante.**
- Evidencia:
  - Se muestran badges con `scope` literal (`login.js:753-755`), porque la interacción sólo trae los nombres: `Scopes = session.Scopes` (`OAuthAuthorizationService.cs:347`). Sin embargo, `ApiScope` sí tiene `DisplayName` y `Description` (`ApiScope.cs:12-13`), y no existe catálogo de textos para `openid`, `profile`, `email` ni `offline_access` (`DomainConstants.cs:126-133`).
  - El texto "{cliente} solicita acceso a {aplicación}." (`login.js:751`) no dice qué datos ni de quién.
  - No hay "Has iniciado sesión como …" ni "Usar otra cuenta". `resumeExistingSession` continúa con la sesión SSO actual (`login.js:569-578`).
  - `OAuthClient` no tiene logo ni URLs de política o términos (`OAuthClient.cs:10-44`).
  - La vista es una `.card` distinta del resto (`login.html:114`) y el encabezado sigue siendo "Inicia sesión" (UX-01).
- Recomendación: un catálogo de descripciones en español (OIDC estándar + `ApiScope.DisplayName/Description`) en forma de lista con iconos ("Ver tu nombre y foto", "Ver tu correo", "Mantener el acceso cuando no estés"). Bloque de cuenta con "Cambiar de cuenta". Logo y políticas del cliente. El mismo catálogo en el portal.

### N. Sin contraseña, passkeys y dispositivos de confianza

**UX-23 · Media — Flujos sin contraseña con la acción principal equivocada y opciones que no se pueden usar.**
- Evidencia:
  - **Apps sólo de enlace mágico**: el botón principal "Continuar" responde con un error que pide pulsar el enlace secundario: "Esta aplicación no usa contraseñas. Pide un enlace de acceso a tu correo." (`login.js:139-142`).
  - **Botón "Usar una passkey"**: se muestra en todo navegador compatible (`login.js:822`), sin opción por app (`LoginOptionsResponse` no la incluye, `ApplicationService.cs:78-87`). Si el usuario no tiene passkey, `NotAllowedError` se traduce como "La operación con passkey fue cancelada." (`shared.js:137`).
  - **Autocompletado de passkeys**: `autocomplete="username webauthn"` (`login.html:30`) no sirve sin mediación condicional, que `getPasskey` no usa (`shared.js:128-130`).
  - **Nombre de la passkey**: la creada al iniciar sesión siempre se llama "Passkey" (`login.js:514`); el portal en cambio sugiere el nombre del dispositivo (`portal.js:661-663`).
  - **Dispositivos de confianza**: el login envía `trustDevice: false` fijo (`login.js:432`) y nunca envía `deviceToken` (`login.js:152`), así que la sección "Dispositivos de confianza" del portal (`portal.html:59`) siempre está vacía para usuarios del login alojado, aunque la función existe en el servidor (`AuthService.cs:628-629`).
- Recomendación:
  - Con contraseña desactivada y enlace mágico activo, "Continuar" envía el enlace.
  - Una opción por app para las passkeys, más mediación condicional.
  - Un mensaje neutro cuando no hay passkey ("¿No tienes passkey? Usa otro método").
  - Nombrar la passkey por dispositivo.
  - Ofrecer "Recordar este dispositivo 30 días", o retirar la sección del portal.

### O. Correos

**UX-24 · Alta — Correos en inglés, sólo HTML, sin marca de la app y con vigencias incorrectas.**
- Dónde vive la plantilla: cadenas C# interpoladas dentro de `SmtpEmailService.cs` (plantilla de acción `:172-192`, OTP `:62-71`, aviso `:79-87`, notificación `:95-106`). No hay archivos de plantilla ni parámetros de idioma o app (`IEmailService.cs:5-16`). El outbox sólo transporta `ApplicationName` en las invitaciones (`OutboxEmailService.cs:21-44`).

| Tipo (disparador) | Asunto (`SmtpEmailService.cs`) | CTA | Notas |
|---|---|---|---|
| Restablecer contraseña (olvido) | "Reset password - AuthCenter" (`:27`) | "Reset password" | "valid for 24 hours" |
| Confirmar correo (registro/reenvío) | "Confirm email - AuthCenter" (`:34`) | "Confirm email" | No nombra la app |
| Invitación (admin) | "Invitation to {app} - AuthCenter" (`:41`) | "Accept invitation" | Única con nombre de app; no dice quién invita |
| Cambio de correo (portal) | "Confirm email change - AuthCenter" (`:48`) | "Confirm email change" | Página destino sin marca (UX-18) |
| Enlace mágico | "Sign in link - AuthCenter" (`:55`) | "Sign in" | Dice **24 h**, pero el token dura 15 min (`JwtSettings.cs:10`; máx. 30) y no avisa de "ábrelo en este navegador" |
| Código OTP (MFA, alta y baja) | "Your sign-in code - AuthCenter" (`:73`) | código | "valid for a few minutes" (real: 5 min al iniciar sesión, `TotpService.cs:357`; 10 min en el alta, `:270,289`) |
| Avisos de seguridad | "{Password changed \| Email change requested \| Email address changed \| Two-step verification enabled/disabled \| Passkey added \| Identity provider linked \| Sign-up attempt with your email} - AuthCenter" (`:88`) | ninguno | Remite a "your account portal" sin URL (`:84`) |
| Gobierno de accesos | "Access request for {app}", "Access to {app} approved/not approved", "Access request for {app} expired", "Access review for {app}" (`:107`) | "Review the request" / "Open your account" / "Start the review" | Fechas en UTC (`AccessReviewService.cs:286`). El CTA al portal sufre UX-13 |

- Formato: `IsBodyHtml = true` y ningún `AlternateView`, por lo que no hay parte de texto plano (`:135-141`). Sin `<head>`, `lang` ni charset. El CTA es un `<a>` inline con padding y color fijo `#0066cc` (`:67,100,185`). El pie global "AuthCenter - centralized identity service" (`:69,85,104,190`) tiene contraste 3.54:1. El remitente es global, "AuthCenter" (`EmailSettings.cs:11`).
- Seguridad y claridad: la plantilla de acción (reset, confirmación, invitación, cambio, enlace mágico) no incluye "si no lo pediste, ignóralo" (`:172-192`); sólo el OTP lo tiene (`:68`). La copia de la UI se contradice con el correo: el reset "caduca en poco tiempo" (`login.html:62`) y los enlaces "caducan pronto" (`account.html:45`), frente a "24 hours".
- Avisos incoherentes:
  - "Two-step verification enabled" y "Passkey added" sólo se envían si el factor se registra **al iniciar sesión** (`AuthService.cs:1190,1218`). Desde el portal no se envían: `TotpService` sólo avisa al desactivar (`:194`) y `PasskeyService` no envía nada.
  - El reset de contraseña no envía "Password changed" (`AuthService.cs:926-954`); el cambio desde el portal sí (`AccountManagementService.cs:63-64`).
- Entrega tardía: el outbox reintenta con espera creciente de 2 a 60 min sin TTL para los correos con caducidad (`OutboxDispatcherService.cs:95`), así que un OTP o enlace puede llegar ya caducado.
- Recomendación:
  - Plantillas por tipo, idioma y app (archivos versionados), con nombre y logo de la app y color de marca.
  - Envío multipart HTML + texto, con `lang`.
  - Vigencia real por tipo, interpolada desde la configuración.
  - Línea "¿No fuiste tú?" en todas las plantillas y el dominio del portal en texto plano en los avisos.
  - Avisos simétricos en portal y login.
  - TTL de descarte en el outbox para OTP y enlaces.

### P. Páginas de error y estados terminales

**UX-25 · Media — Errores servidos como JSON crudo o como HTML sin estilo.**
- Evidencia:
  - `/oauth/authorize` con cliente o `redirect_uri` inválidos devuelve JSON en inglés (`OAuthController.cs:92-93`; `OAuthAuthorizationService.cs:77-87`). Igual `/oauth/logout` (`OAuthController.cs:236-237`) y `/oauth/authorize/response/{id}` caducado, por ejemplo al pulsar Atrás (`OAuthController.cs:204-206`).
  - La página de error SAML usa la clase `auth-card`, que no existe en `app.css` (`SamlIdpController.cs:226`; no aparece en ningún CSS), así que el contenido queda sin padding ni fondo en la primera columna de `.auth-shell`. Muestra "No se pudo iniciar sesión" también para errores de logout SLO (`SamlIdpController.cs:161`), y su único enlace va al portal (UX-13).
  - La página de form_post "Continuando…" está en blanco: sólo tiene campos ocultos (`AuthorizationResponseResult.cs:19-28`).
- Recomendación: una página de error HTML común (marca, título y acción "Volver a la aplicación" o "Ir a mi cuenta", código de soporte) para todos los errores de navegación de `/oauth/*` y `/saml/*`; corregir la clase; título específico para el logout; texto visible "Volviendo a {app}…" en el form_post.

### Q. Contenido y detalles del portal

**UX-26 · Baja — Detalles del portal que restan claridad.**
- Evidencia:
  - Se usan `window.prompt` y `window.confirm` nativos, con estilo e idioma del navegador (`portal.js:651,666,682`).
  - El estado global está arriba (`portal.html:24`), lejos de acciones situadas al final de paneles largos.
  - Las apps OIDC no tienen "Abrir": sólo se genera `LaunchUrl` para SP SAML con inicio desde el IdP (`AccountManagementService.cs:187-189`), y el cliente OIDC no tiene URL de inicio.
  - Sesiones muestra el código interno de la app (`portal.js:129`).
  - El borrado de cuenta no pide reautenticación para cuentas sin contraseña (`portal.js:456-465`; el campo se oculta en `portal.js:330`).
- Recomendación: diálogos propios (`<dialog>`) para renombrar y desvincular, avisos junto a la acción (toast con `role=status`), una URL de inicio por cliente para un lanzador real, nombre de app en lugar del código y reautenticación con passkey al borrar.

## 3. Top 10 recomendaciones

| # | Recomendación | Hallazgos | Impacto | Esfuerzo |
|---|---|---|---|---|
| 1 | **Login por pasos con encabezado propio**: título, subtítulo y `document.title` por vista; fuera eslogan y hero técnico por defecto; errores en línea (`aria-invalid` + `aria-describedby`) con resumen `role=alert`; foco al H1 de cada vista y a los códigos de respaldo. | UX-01, UX-02, UX-15 | Alto: todas las pantallas de autenticación | M |
| 2 | **Catálogo único de mensajes por código**, compartido por login, portal, enlaces y logout. Nunca `error.message` del servidor. Manejo explícito de red, 401 (redirigir a re-login), 403, 429 (con `Retry-After`), 500 y CSRF. Códigos nuevos (`ACCESS_PENDING`) y separar `USER_CREATION_FAILED`. | UX-03, UX-04, UX-05, UX-07, UX-14 | Alto: elimina textos en inglés y mensajes engañosos | S-M |
| 3 | **Recuperación sin callejones**: reenvío de confirmación en el login; "No tengo acceso a mi verificación" con contador de intentos; OTP por correo como alternativa en el enrolamiento forzado; invitación caducada que se pueda renovar; `fail()` que distinga red/500; CTA "Pedir un enlace nuevo"; clic explícito en `/magic-link`; estado bloqueado con acción ("Volver a {app}"); igualar TTL de 10 y 15 min con aviso previo; `/logout` útil sin `logout_id` y mensaje de "sesión cerrada". | UX-09, UX-10, UX-11, UX-12 | Alto: menos tickets y bloqueos | M |
| 4 | **Portal accesible para todo usuario y usable en móvil**: entrada al portal independiente del acceso a `AUTHCENTER`; `minmax(0,1fr)` + `min-width:0`; nav que envuelva o menú; "Crear contraseña" para cuentas sin contraseña; proyecto móvil y comprobación de reflow en la e2e hospedada. | UX-13, UX-17, UX-26 | Alto | S (CSS) / M (acceso) |
| 5 | **Consentimiento comprensible**: descripciones en español de los scopes (OIDC + `ApiScope.DisplayName/Description`) en forma de lista; "Conectado como … · Cambiar de cuenta"; logo y políticas del cliente; el mismo catálogo en el portal. | UX-22 | Alto: confianza y conversión | S-M |
| 6 | **Marca por aplicación de punta a punta**: color de texto sobre primario calculado, fondo visible, lema e imagen opcionales; validación de contraste de los pares reales en el servidor; vista previa basada en `app.css`; marca en portal, logout, cambio de correo, páginas de error y correos. | UX-18, UX-12, UX-25 | Alto: experiencia "white-label" coherente | M-L |
| 7 | **Correos rehechos**: plantillas versionadas por tipo, idioma y app; multipart HTML + texto; vigencia real; "¿No fuiste tú?"; nombre y logo de la app; "ábrelo en este navegador" en el enlace mágico; avisos de seguridad simétricos; TTL en el outbox. | UX-24 | Alto | M |
| 8 | **Arquitectura i18n**: catálogos por idioma; negociación `ui_locales`, luego usuario, luego app, luego `Accept-Language`; `lang` dinámico; el servidor devuelve sólo códigos; fechas con el idioma de la UI; limpiar jerga ("refresh tokens", "OIDC", códigos de app). | UX-21 (+UX-03, UX-24) | Medio-alto | L |
| 9 | **Carga y doble envío**: `withBusy` en todos los submits (`disabled` + `aria-busy` + spinner); esqueleto hasta `ready` y tema servido con el HTML (sin parpadeo); progreso en MFA, olvido, enlace y consentimiento; "Continuar" envía el enlace en apps sin contraseña; políticas de rate limit separadas. | UX-06, UX-07, UX-23 | Medio | S |
| 10 | **Sistema visual y accesibilidad compartidos con la consola**: paquete único de tokens; foco y bordes ≥3:1; objetivos táctiles ≥44 px; nombres accesibles con contexto en las listas del portal; reglas de contraseña visibles en los 4 formularios; modo oscuro por tokens. | UX-08, UX-15, UX-16, UX-19, UX-20 | Medio | M |
