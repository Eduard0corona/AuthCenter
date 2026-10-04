# Anexo B — Auditoría UX de la consola de administración

Parte del [análisis de arquitectura, producto y UX](../ANALISIS-PRODUCTO-UX.md).

Base: commit `0320243`. Convención de rutas `file:line`: el código de la consola es relativo a `src/AuthCenter.Admin/src/` (p. ej. `app/AppShell.tsx`, `features/users/UsersPage.tsx`; cuando no hay ambigüedad se cita sólo el nombre del archivo); `e2e/`, `index.html` y `node_modules/` son relativos a `src/AuthCenter.Admin/`; los archivos del backend se citan por su nombre (únicos en el repositorio) o con ruta completa desde la raíz (`src/AuthCenter.Api/Program.cs`). Sólo se afirma lo que se leyó en el código.

## Resumen ejecutivo

- **Causas en código de lo observado**: las etiquetas en inglés salen del array `navigation` (`app/AppShell.tsx:19-70`); el contorno negro del H1 es el anillo por defecto del navegador sobre un H1 enfocado por programa (`components/PageHeader.tsx:12,18`) que el CSS no estiliza (`styles.css:36,82`); el error de "Login URL" vacío es el mensaje de formato de `features/oauth-clients/oauth-client.ts:44` (y el servidor también lo exige, `CreateOAuthClientRequestValidator.cs:22-26`); el aterrizaje en el portal lo decide el login hospedado (`wwwroot/assets/login.js:691`); "Cerrado" es el valor por defecto de `features/applications/application.ts:72`; los 12 indicadores y 7 tarjetas están codificados en `features/dashboard/DashboardPage.tsx:10-18,76-91`.
- **Problemas más graves no visibles en las capturas**: (1) una contraseña incorrecta en la reautenticación devuelve 401 y la consola redirige a login (en los formularios de federación se pierde lo escrito) (ADM-UX-05); (2) los errores de dominio y de validación del servidor llegan en inglés y concatenados a frases en español (ADM-UX-04); (3) la propia documentación dice que Login URL debe ser el login hospedado y que no aplica a clientes máquina, pero la consola sugiere la URL de la app y lo exige siempre (ADM-UX-07); (4) el System Log no puede encontrar los fallos de una persona por correo, no muestra el motivo y `/oauth/authorize` no audita (ADM-UX-24); (5) la primera regla activa de una política convierte el "permitir por defecto" en "denegar por defecto" sin aviso (ADM-UX-08).
- **Fortalezas a conservar**: guardas de permiso por ruta y re-autorización en servidor; filtros y paginación en la URL; diálogos nativos con foco gestionado (`hooks/useModalDialog.ts`); step-up para acciones sensibles y secreto de un solo uso con copiar; simuladores (políticas, routing de federación, mappings), "Probar conexión", diagnóstico SCIM e importación de metadatos SAML; webhooks con verificación, snippet de firma y replay; claves de idempotencia, renovación de CSRF, detección de build obsoleto y control de concurrencia por versión; pruebas axe en todas las rutas y reflow a 320 px.
- **32 hallazgos** (1 crítico, 8 altos, 19 medios, 4 bajos) y **10 recomendaciones** priorizadas; la de mayor impacto es un asistente "Registrar aplicación" apoyado en un hub de aplicación con pestañas.

## 1. Arquitectura de información

### 1.1 Rutas (path → componente) — `app/Router.tsx`
Base `BrowserRouter basename="/admin-v2"` (`main.tsx:20`). Todas las rutas cuelgan de `<AppShell/>` (`Router.tsx:54`) y todas salvo `/` y `/404` se envuelven en `PermissionRoute` con un permiso `AUTHCENTER_*` (`Router.tsx:56-109`). Todos los componentes son `lazy()` (`Router.tsx:7-46`) con un único fallback global "Cargando módulo" (`Router.tsx:48`).

| Ruta | Componente | Permiso |
|---|---|---|
| `/` (index) | `DashboardPage` | ninguno (`:55`) |
| `/users`, `/users/new`, `/users/invite`, `/users/:userId` | `UsersPage`, `UserProvisioningPage mode=create|invite`, `UserEditorPage` | USERS_READ/WRITE |
| `/groups`, `/groups/new`, `/groups/:groupId` | `GroupsPage`, `GroupEditorPage` | GROUPS_* |
| `/profile-schema`, `/new`, `/:definitionId` | `ProfileSchemaPage`, `ProfileSchemaEditorPage` | PROFILE_SCHEMAS_* |
| `/applications`, `/new`, `/:applicationId` | `ApplicationsPage`, `ApplicationEditorPage` | APPLICATIONS_* |
| `/oauth-clients`, `/new`, `/:clientId` | `OAuthClientsPage`, `OAuthClientEditorPage` | OAUTH_CLIENTS_* |
| `/api-resources`, `/new`, `/:resourceId` | `ApiResourcesPage`, `ApiResourceEditorPage` | **OAUTH_CLIENTS_*** (`:69-71`) |
| `/saml-apps`, `/new`, `/:providerId` | `SamlAppsPage`, `SamlAppEditorPage` | SAML_APPS_* |
| `/provisioning-tokens`, `/new`, `/:tokenId` | `ProvisioningTokensPage`, `ProvisioningTokenEditorPage` | PROVISIONING_* |
| `/profile-mappings`, `/new`, `/:mappingId` | `ProfileMappingsPage`, `ProfileMappingEditorPage` | **USERS_*** (`:78-80`) |
| `/group-rules`, `/new`, `/:ruleId` | `GroupRulesPage`, `GroupRuleEditorPage` | GROUPS_* |
| `/federation`, `/federation/providers/new`, `/federation/providers/:providerId` | `FederationPage`, `ProviderEditorPage` | FEDERATION_* |
| `/access-policies`, `/access-policies/:applicationId` | `AccessPoliciesPage`, `AccessPolicyEditorPage` | ACCESS_POLICIES_READ (no hay ruta `/new`) |
| `/roles`, `/new`, `/:roleId` · `/permissions`, `/new`, `/:permissionId` | `RolesPage`/`RoleEditorPage` · `PermissionsPage`/`PermissionEditorPage` | ROLES_* · PERMISSIONS_* |
| `/access-requests` · `/access-reviews`, `/new`, `/:reviewId` · `/sod-rules`, `/new`, `/:ruleId` | `AccessRequestsPage` · `AccessReviews*` · `SodRule*` | GOVERNANCE_* |
| `/system-log` | `SystemLogPage` | AUDIT_LOGS_READ |
| `/event-hooks`, `/new`, `/deliveries`, `/:hookId` | `EventHooksPage`, `EventHookEditorPage`, `EventDeliveriesPage` | EVENT_HOOKS_* |
| `/404`, `*` → `/404` | `PageState "Ruta no encontrada"` | — (`:110-111`) |

Observaciones: 57 rutas (incluidas `/404` y `*`) para ~20 "módulos"; no existe ninguna ruta orientada a tarea (asistente, "primeros pasos", "probar inicio de sesión") ni sub-rutas por pestaña dentro de una aplicación (`/applications/:id/clients`, etc.). La configuración de una app vive en 6 árboles de rutas distintos: `/applications/:id`, `/oauth-clients/:clientId`, `/saml-apps/:providerId`, `/access-policies/:applicationId`, `/federation?applicationId=` y el diálogo de branding.

### 1.2 Navegación tal como está codificada — `app/AppShell.tsx:19-70`
Array estático `navigation` con **8 grupos y 20 ítems** (filtrados por permiso en `:100`, un grupo sin ítems visibles se oculta en `:101`):

1. **Inicio** → "Overview" (`:20`)
2. **Directorio** → Usuarios, Grupos, Esquema de perfil (`:22-27`)
3. **Aplicaciones** → Aplicaciones, "OAuth clients", Recursos de API, Aplicaciones SAML (`:29-36`)
4. **Lifecycle** → "Provisioning tokens", "Profile mappings", "Group rules" (`:39-44`)
5. **Federación** → "Proveedores y routing" (`:46`)
6. **Seguridad** → Roles, Permisos, Políticas de acceso (`:48-53`)
7. **Gobierno** → Solicitudes de acceso, Revisiones de acceso, Segregación de funciones (`:56-61`)
8. **Operación** → "Event Hooks", "System Log" (`:64-67`)

- Las etiquetas en inglés salen literalmente de este array; "LIFECYCLE" en mayúsculas lo produce `.nav-group h2 { text-transform: uppercase }` (`styles.css:57`).
- Un grupo con un único ítem ("Inicio", "Federación") y un grupo cuyo nombre repite su primer ítem ("Aplicaciones › Aplicaciones") añaden niveles sin información.
- Recursos de API y Profile mappings se controlan con permisos de otros módulos (OAUTH_CLIENTS_READ, USERS_READ: `AppShell.tsx:34,42`), así que la agrupación no refleja el modelo de permisos.
- La topbar sólo tiene "Mi cuenta" (`href="/portal"`) y "Cerrar sesión" (`AppShell.tsx:125-126`): no hay búsqueda global, ayuda/documentación, selector de tema ni notificaciones.

### 1.3 Página de aterrizaje
- **Dentro de la consola**: `/admin-v2/` → `DashboardPage` (`Router.tsx:55`).
- **Tras iniciar sesión**: `GET /` redirige a `/login` (`src/AuthCenter.Api/Program.cs:484`); al terminar sin `return_url`, `finishLogin()` hace `location.replace(safeLocalPath(returnUrl, location.origin, "/portal"))` (`src/AuthCenter.Api/wwwroot/assets/login.js:691`). Por eso un administrador cae en el **portal de usuario final**; el acceso a la consola es un botón "Administración" que `portal.js:67` sólo muestra (`hidden = !…startsWith("AUTHCENTER_")`). La consola sólo se abre directamente si se navega a `/admin-v2/` (que redirige a login con `return_url`, `api/client.ts:111-116`).
- Si la sesión SSO pertenece a otra app, la consola redirige a login de nuevo (`auth/session.tsx:24-27`) mostrando "Redirigiendo".

## 2. Trabajos clave del administrador (pantallas, clics, campos, conocimiento previo)

Convención: "clic" = acción explícita del operador; los campos se cuentan tal como aparecen en el formulario.

### 2.1 Registrar una aplicación para que sus usuarios inicien sesión (OIDC)
| Paso | Pantalla / acción | Campos y evidencia |
|---|---|---|
| 1 | Menú Aplicaciones › **Aplicaciones** → "Nueva aplicación" (`applications/ApplicationsPage.tsx:59`) | Código (inmutable, regex), Nombre, Descripción, **Modo de registro** (primera opción y valor por defecto `Closed`/"Cerrado": `application.ts:72`, `ApplicationEditorPage.tsx:136`), Dominios permitidos, 6 métodos de autenticación, Confirmación de email, MFA obligatorio = **13 controles**. "Rol predeterminado" no existe hasta después de crear (`:143`). |
| 2 | "Crear aplicación" | `navigate('/applications/:id', { replace: true })` sin mensaje ni siguiente paso (`ApplicationEditorPage.tsx:62-64`; el `setFeedback` sólo corre en edición, `:66-67`). La ficha no muestra clientes OAuth, apps SAML, usuarios con acceso ni políticas; sólo identidad/registro, Responsables (`:164`) y "Operación" (branding/desactivar, `:166-171`). |
| 3 | Cambiar de módulo: Menú › **OAuth clients** → "Nuevo client" (`OAuthClientsPage.tsx:52`) — la app no se preselecciona | Aplicación, Nombre, **Client ID tecleado** (`OAuthClientEditorPage.tsx:122`), Tipo, 4 *Grant types* con nombres de protocolo (`:128`), 4 *Allowed scopes* (`:129`), Scopes de APIs (`:130`), Redirect URIs (`:131`), **Login URL** (`:133`), vida del access token (`:134`), Requerir PKCE + Auto consent (`:136`), Orígenes CORS (`:137`), Post-logout URIs, Back-channel logout URI, "Incluir sid…(backchannel_logout_session_required)" (`:139-144`) = **16 grupos de controles (≥21 casillas/campos)**, todos visibles a la vez, sin presets por tipo de app. |
| 4 | "Crear OAuth client" → diálogo de secreto (confidencial) → "Ya guardé el secreto" → ficha del client (`:63-66`, `closeSecret`) | No se muestran issuer, URL de discovery, endpoints ni snippet; no hay "probar inicio de sesión". |
| 5 | Dar acceso a usuarios: Menú › Usuarios › usuario → "Acceso directo y heredado" → marcar app → "Guardar acceso directo" (`users/UserEditorPage.tsx:91`), o Grupos › grupo → "Acceso heredado" + "Agregar usuario" (`groups/GroupEditorPage.tsx:71,81`), o abrir el autorregistro (modo Abierto) para cuentas nuevas | Sin acceso, el login responde `ACCESS_DENIED` (`AuthService.cs:267`); nada en la ficha de la app lo advierte. |
| 6 (opc.) | Seguridad › Políticas de acceso › app → "Crear draft" → "Nueva regla" → "Revisar y publicar" + contraseña (`access-policies/AccessPolicyEditorPage.tsx:94,107,109`) | 18 controles por regla, con 7 casillas de días como un grupo (`PolicyRuleForm.tsx:31-38`). |
| 7 (opc.) | Branding: botón "Branding" de la tarjeta o "Editar branding" en la ficha (diálogo, `BrandingDialog.tsx`) | 7 campos + vista previa. |

**Total**: 3–4 módulos, ≥5 pantallas, ≥30 campos, ≥8 clics de navegación. **Conocimiento previo**: tipos de cliente (confidencial/público), grants (authorization_code, refresh_token, client_credentials, token exchange RFC 8693), PKCE, scopes OIDC y la dependencia `offline_access`→refresh_token (`oauth-client.ts:107-109`), exactitud de redirect URIs, CORS para SPA y, sobre todo, que **"Login URL" debe ser el login hospedado de AuthCenter** — esto sólo lo dice la documentación (`docs/integration-quickstarts.md:40,46`), mientras el placeholder sugiere la app propia (`https://app.example.com/login`, `OAuthClientEditorPage.tsx:133`).

### 2.2 Registrar un SAML SP
1. Menú › **Aplicaciones SAML** → "Nueva aplicación SAML" (`saml-apps/SamlAppsPage.tsx:45`); exige una aplicación de AuthCenter previa y `AUTHCENTER_APPLICATIONS_READ` (`SamlAppEditorPage.tsx:95`).
2. Opcional (y la mejor parte del flujo): pegar XML → "Leer metadatos" rellena entity ID, ACS, certificados y NameID (`SamlAppEditorPage.tsx:110-116`).
3. Campos: Aplicación de AuthCenter, Nombre, Entity ID, URLs de ACS, SLO, Formato de NameID, Vigencia, Firmar respuesta, Cifrar aserción, Certificado de cifrado, Exigir solicitudes firmadas, Certificado de firma, N filas de atributos, Permitir inicio desde AuthCenter, RelayState (`:119-175`) = 14 controles + filas de atributos.
4. "Crear aplicación SAML" → ficha sin mensaje (`:71`).
5. Los datos que el SP necesita de AuthCenter (URL de metadatos, Entity ID, SSO, SLO, huella) se muestran como texto `<dd class="mono">` sin botón de copiar ni descarga (`SamlAppsPage.tsx:86-95`), y en el editor aparecen **al final**, después del formulario (`SamlAppEditorPage.tsx:179`).
6. Acceso de usuarios: igual que 2.1 paso 5 (otra sección). **Conocimiento previo**: entity ID, ACS, NameID, firma/cifrado, RelayState, PEM.

### 2.3 Invitar/crear un usuario y darle acceso, roles y grupos
1. Usuarios → "Invitar usuario" o "Crear usuario" (`users/UsersPage.tsx:69`).
2. Campos: Nombre completo, Correo, [crear: Contraseña temporal + "Generar contraseña segura" + "Mostrar"], "Conceder acceso a una aplicación ahora", Aplicación, "Conceder acceso activo inmediatamente", Roles directos (`UserProvisioningPage.tsx:66-78`). **No se pueden asignar grupos** en el alta.
3. Éxito: alerta con enlace "Administrar {nombre}" (`:58`).
4. Ficha del usuario: 3 formularios con 3 botones de guardar ("Guardar identidad" `UserEditorPage.tsx:64`, "Guardar perfil universal" `:79`, "Guardar acceso directo" `:91`) + panel de operaciones (`:68`). Grupos: sólo lectura — "Las asignaciones heredadas sólo pueden cambiarse desde el grupo de origen." (`:67`); hay que ir a Grupos › grupo › "Agregar usuario" (`GroupEditorPage.tsx:71`).
5. **Conocimiento previo**: diferencia acceso directo vs heredado, roles por aplicación (un rol sólo se habilita si su app está marcada, `UserEditorPage.tsx:91`).

### 2.4 Configurar registro, MFA y política de contraseñas
- **Registro y MFA por aplicación**: Aplicaciones › app › sección "Registro y acceso" (modo, dominios, métodos, confirmación de email, MFA obligatorio) → "Guardar configuración" (`ApplicationEditorPage.tsx:131-160`). No hay valores por defecto a nivel organización: se repite en cada app.
- **MFA condicional / riesgo / red / horario / assurance**: Seguridad › Políticas de acceso › app → draft → reglas → simulación → publicar con contraseña (`AccessPolicyEditorPage.tsx:94-109`). La semántica por defecto es trampa: sin reglas se permite todo (`:95,100`), pero en cuanto existe una regla activa lo que no coincide se deniega (`PolicyRuleForm.tsx:28` "deny-by-default"; `AccessPolicyService.cs:356,364,415-423`); el diff de publicación no lo advierte (`AccessPolicyEditorPage.tsx:107`).
- **Política de contraseñas**: **no existe UI**. Está fija en servidor (longitud 8, dígito, mayúscula, minúscula; bloqueo 5 intentos/15 min: `src/AuthCenter.Infrastructure/Extensions/InfrastructureServiceExtensions.cs:93-101`), mientras la consola exige 12 caracteres a las contraseñas temporales (`users/provisioning.ts:25,84`).

### 2.5 Diagnosticar un inicio de sesión fallido
1. Desde el dashboard, la tarjeta "Inicios de sesión rechazados" enlaza a `/system-log?action=LOGIN_FAILED` (`DashboardPage.tsx:89`), pero el número suma **5** acciones en 24 h (`AdminDashboardController.cs:15-16,38`) y la lista filtra 1 acción sin fechas: las cifras no coinciden.
2. System Log: filtros de texto libre "Acción" (coincidencia exacta, `AuditService.cs:112-113`), "Aplicación" (código), "Tipo de entidad" (nombre de clase, placeholder `ApplicationUser`), "ID de entidad", **"ID del actor" (UUID)**, "Trace ID", fechas (`system-log/SystemLogPage.tsx:101-110`). No se puede buscar por correo; teclear un correo en "ID del actor" envía un `userId` no-GUID (`AuditLogQuery.cs`) y la consola sólo puede mostrar el genérico "La solicitud no pudo completarse (400)." (`api/client.ts:133`).
3. Columnas: Fecha, Acción (código crudo), Actor, Aplicación, Entidad (`:120`). **No hay columna de motivo/resultado**: el motivo (`reason: "InvalidPassword" | "UserNotFound" | "NoAccess" | "EmailNotConfirmed"…`, `AuthService.cs:222-266`) sólo aparece en el JSON crudo del diálogo "Detalle" (`:169-170`). El CSV tampoco lo incluye (`AuditLogsController.cs:44-46`).
4. Lagunas: `reason=UserNotFound` se registra sin usuario ni correo (`AuthService.cs:230`); los errores de `/oauth/authorize` (redirect_uri no registrado, scope inválido, cliente inactivo) no se auditan (sin `LogAsync` en `OAuthAuthorizationService.cs` ni en `OAuthController.cs`); "Ver historial" en la ficha del usuario filtra por **entidad** (`HistoryLink.tsx:9`, `entities.ts:41-43`), no por actor, así que no muestra sus inicios de sesión.
5. **Conocimiento previo**: códigos de evento (LOGIN_FAILED, LOGIN_LOCKED_OUT, MFA_VERIFY_FAILED, PASSKEY_LOGIN_FAILED, FEDERATION_LOGIN_FAILED, SAML_SSO_DENIED…), UUID del usuario, leer JSON.

### 2.6 Rotar el secreto de un client
OAuth clients → fila "Configurar" → bajar hasta "Credencial y estado" (debajo del formulario) → "Rotar secreto" → diálogo "Step-up obligatorio" con contraseña → diálogo de secreto con "Copiar secreto" → "Ya guardé el secreto" (`OAuthClientEditorPage.tsx:148-150`, `SecretRevealDialog.tsx`). 4 clics + contraseña. El secreto anterior "dejará de funcionar de inmediato" (`:149`), a diferencia de los Event Hooks, que mantienen el anterior 24 h (`EventHookEditorPage.tsx:157`). Si la contraseña es incorrecta, la API responde **401** (`ReauthenticationController.cs:37-39`) y `apiRequest` redirige a login ante cualquier 401 (`api/client.ts:56`): el operador sale de la consola (ver ADM-UX-05).

### 2.7 Configurar un proveedor de federación empresarial
1. Federación › Proveedores y routing → elegir aplicación (`federation/FederationPage.tsx:85`) → "Nuevo proveedor" (`:83`).
2. Campos (`ProviderEditorPage.tsx:101-159`): Aplicación, Nombre, Protocolo, Issuer; OIDC: Client ID, Callback URL, Discovery endpoint, Client secret, exigir `email_verified`; SAML: URL de SSO, certificado PEM; Vinculación de cuentas (opción literal "Disabled", por defecto), JIT (desactivado por defecto: `federation.ts:56-57`), Activo, Confiar en MFA del IdP, claim de grupos = 11 (SAML) a 14 (OIDC) controles + tabla de mapeos.
3. "Verificar y crear" → contraseña (`:165,178`). La "Metadata del SP" (SAML) sólo existe después de guardar (`:88,125`); "Probar conexión" también (`:168-173`).
4. Volver a Federación → "Nueva regla" (`FederationPage.tsx:100`) → Proveedor, Prioridad, Dominio, Grupo, Atributo → "Verificar y crear" → **otra** contraseña (`:120`). Sin regla "ningún usuario de esta aplicación se enruta a un IdP externo" (`:103`). Reordenar = otra contraseña (`:106`).
5. Se repite por aplicación. **Conocimiento previo**: issuer, discovery, callback, JIT, vinculación por correo verificado, claims de grupos, prioridades.

### 2.8 Crear un Event Hook
Operación › Event Hooks → "Nuevo hook" (`event-hooks/EventHooksPage.tsx:44`) → Nombre, URL, Alcance, Tipos de evento (selector con filtro, grupos y comodín `*`, `EventTypePicker.tsx`) → "Crear hook" → diálogo de secreto → ficha → "Verificar endpoint" (`EventHookEditorPage.tsx:143-148`): sin verificar no recibe eventos. Es el flujo mejor resuelto: estado vacío con CTA (`EventHooksPage.tsx:53`), errores mapeados (`EventHookEditorPage.tsx:23-31`), snippet de verificación de firma (`:152`) y entregas con detalle/replay. Falta un "enviar evento de prueba" y los tipos de evento siguen siendo códigos crudos (`EventTypePicker.tsx:57`).

## 3. Hallazgos

Severidad: **Crítica** (bloquea o provoca pérdida de datos/seguridad), **Alta** (un trabajo clave falla o requiere conocimiento experto), **Media** (fricción frecuente o inconsistencia visible), **Baja** (pulido).

### 3.1 Terminología, jerga e idioma

**ADM-UX-01 · Alta · Navegación y etiquetas mezclan inglés, nombres de protocolo y nombres de clase**
- Evidencia: menú `AppShell.tsx:20` "Overview", `:33` "OAuth clients", `:39-44` "Lifecycle / Provisioning tokens / Profile mappings / Group rules", `:66-67` "Event Hooks / System Log", `:96` "Admin Console". Formulario OAuth: "Grant types" (`OAuthClientEditorPage.tsx:128`), "Allowed scopes" (`:129`), "Login URL" (`:133`), "Auto consent" (`:136`), "Post-logout redirect URIs", "Back-channel logout URI", "(backchannel_logout_session_required)" (`:141-143`), "Nuevo client". Políticas: "Allow/Deny" (`PolicyRuleForm.tsx:33`), "Assurance", "Phishing resistant", "trusted device bypass" (`:37-38`), "deny-by-default" (`:28`), estados de versión crudos `{version.status}` = Draft/Published (`AccessPolicyEditorPage.tsx:95`), riesgos "Unknown/Low/…" (`:105`). Federación: opción "Disabled" (`ProviderEditorPage.tsx:134`), "Just-in-time provisioning" (`:137`), "Routing rules". Otros: "Dead letters" (`EventDeliveriesPage.tsx:24,27`), "Preview:" (`GroupEditorPage.tsx:81`), "Step-up obligatorio" (`ReauthenticationDialog.tsx:25`), eyebrow "RBAC" (`RoleEditorPage.tsx:56`). El System Log pide "Tipo de entidad" con placeholder de clase .NET `ApplicationUser` (`SystemLogPage.tsx:104`).
- Impacto: un administrador hispanohablante no técnico no reconoce dónde está cada tarea; el producto parece a medio traducir; aumenta errores de configuración.
- Recomendación: glosario único en español con el término de protocolo como apoyo ("URIs de redirección · redirect_uri", "Cliente OAuth", "Registro del sistema", "Webhooks de eventos", "Ciclo de vida", "Tokens SCIM", "Mapeos de perfil", "Reglas de grupo", "Permitir/Denegar", "Nivel de autenticación"); mapear todos los enums a etiquetas (como ya hace `governance.ts:4-28` con `requestStatusLabels`).

**ADM-UX-02 · Media · Descripciones escritas desde la implementación, no desde la tarea**
- Evidencia: "Opera AuthCenter desde módulos cargados bajo demanda. Cada acción vuelve a autorizarse en el servidor." (`DashboardPage.tsx:44`); "Frontera de seguridad server-side … La API valida de nuevo sesión, CSRF y permisos." (`:68-71`); "…desde rutas que puedes compartir." (`ApplicationsPage.tsx:59`); "…desde una ruta enlazable." (`ApplicationEditorPage.tsx:115`); "La autorización efectiva siempre se vuelve a comprobar en el backend." (`:132`); "…sin descargarlo completo." (`UsersPage.tsx:69`); "La API volverá a validar tus permisos." en la confirmación de desactivar (`UsersPage.tsx:96`); "Catálogo de capacidades que la API vuelve a autorizar…" (`PermissionsPage.tsx:28`); "Toda escritura exige reautenticación." (`FederationPage.tsx:83`); "Edita únicamente drafts … revisar el diff." (`AccessPolicyEditorPage.tsx:91`); fallback "Cargando módulo" (`Router.tsx:48`).
- Impacto: ocupa el espacio de ayuda contextual con información irrelevante para la decisión; no dice qué es la entidad ni qué hacer después.
- Recomendación: patrón "qué es + cuándo lo necesitas + siguiente paso" (p. ej. Aplicaciones: "Cada aplicación agrupa sus clientes de inicio de sesión, quién puede entrar y cómo. Empieza registrando una."), revisado por redacción UX.

**ADM-UX-03 · Alta · Códigos de permiso crudos como copia de interfaz**
- Evidencia: 18 apariciones en 17 archivos, p. ej. "Sin responsables: … un administrador con AUTHCENTER_GOVERNANCE_WRITE." (`ApplicationGovernancePanel.tsx:63`), "Necesitas AUTHCENTER_ROLES_READ para cambiarlo." (`ApplicationEditorPage.tsx:142`), "Solicita el permiso AUTHCENTER_APPLICATIONS_WRITE…" (`:161`), "Solicita AUTHCENTER_OAUTH_CLIENTS_WRITE…" (`OAuthClientEditorPage.tsx:146`), "Necesitas AUTHCENTER_APPLICATIONS_READ…" (`UserProvisioningPage.tsx:60`, `OAuthClientEditorPage.tsx:98`, `SamlAppEditorPage.tsx:95`…). La matriz de permisos de un rol usa el código como etiqueta principal (`<strong className="mono">{permission.code}</strong>`, `RoleEditorPage.tsx:59`). En cambio, la pantalla de acceso denegado no dice qué permiso falta: "contacta a un SuperAdmin" (`PermissionRoute.tsx:8`).
- Impacto: mensajes ininteligibles para quien no administra RBAC; justo donde se necesita saber qué pedir, no se dice.
- Recomendación: nombre legible por permiso ("Editar aplicaciones") con el código como detalle secundario/tooltip; en `PermissionRoute` nombrar el permiso legible y quién puede concederlo; en la matriz, nombre primero y agrupación por módulo.

### 3.2 Cómo se producen los mensajes de error

**ADM-UX-04 · Alta · Errores de dominio y de validación del servidor llegan en inglés, a veces dentro de una frase en español**
- Mecánica: `apiRequest` construye `ApiError` con `envelope.message` del servidor o un texto español por estado HTTP (`api/client.ts:61-68,127-134`). `errorMessage` traduce sólo 14 códigos genéricos y declara explícitamente "domain errors keep the server's message" (`api/errors.ts:3-19,34`); además añade `details` crudos (`:35-36`).
- Evidencia: `VALIDATION_FAILED` llega con `details` = mensajes FluentValidation en inglés (`RequestValidationFilter.cs:37-40`), así que el operador ve "Revisa los datos del formulario. LoginUrl must be an absolute HTTPS URI, or an HTTP loopback URI…" (`CreateOAuthClientRequestValidator.cs:26`). Errores de dominio sin mapear: "A client with this ClientId already exists." (`OAuthClientService.cs:51`), "OAuth clients cannot be created for an inactive application." (`:47`), "Application code 'X' is already in use." (`ApplicationService.cs:160`). Sólo 9 archivos (SAML, Recursos de API, Event Hooks, Esquema de perfil y 5 de gobierno) pasan un diccionario propio (`SAML_ERRORS`, `RESOURCE_ERRORS`, `HOOK_ERRORS`, `SCHEMA_ERRORS`, `governanceMessages`); Aplicaciones, OAuth clients, Usuarios, Grupos, Roles, Permisos, Federación, Tokens, Políticas, Mappings y Group rules no. También se muestran en crudo textos explicativos generados en inglés: razones de la simulación de políticas (`AccessPolicyEditorPage.tsx:105` ← "The first matching rule allows access.", `AccessPolicyService.cs:404`; "All configured conditions matched.", `:382`), detalles de "Probar conexión" (`ProviderEditorPage.tsx:171` ← `FederationService.Diagnostics.cs:27`), avisos de metadatos SAML (`SamlAppEditorPage.tsx:114` ← `SamlServiceProviderService.cs:163,169`). `ReauthenticationDialog` muestra `caught.message` sin pasar por `errorMessage` (`ReauthenticationDialog.tsx:24`).
- Impacto: el error más común del alta de cliente aparece en inglés y como bloque al principio de la página, lejos del campo; frases bilingües restan confianza.
- Recomendación: (1) catálogo central código→español en `errors.ts` para todos los códigos de dominio; (2) que la API devuelva `details` estructurados `{field, code}` y que la consola los pinte inline junto al campo (`setError` de react-hook-form); (3) nunca mostrar texto libre del servidor: si el código no está mapeado, mensaje genérico en español + referencia; (4) traducir/codificar las razones de simulación y diagnósticos.

**ADM-UX-05 · Crítica · Una contraseña mal escrita en la reautenticación expulsa al operador a la pantalla de login**
- Evidencia: el endpoint de step-up responde **401** si la contraseña es incorrecta (`ReauthenticationController.cs:37-39`, `ReauthenticationService.cs:66-69` "Reauthentication failed."); `send()` llama `redirectToLoginOnce()` ante **cualquier** 401 (`api/client.ts:56`), que hace `window.location.replace('/login?…')` (`:111-116`). Ninguna prueba e2e cubre una contraseña incorrecta: `mockStepUp` siempre responde 200 (`e2e/admin-shell.spec.ts:969-974`).
- Impacto: al guardar un proveedor de federación o una regla de routing (formularios largos, con el secreto del IdP tecleado: la reautenticación se pide después de rellenarlos, `ProviderEditorPage.tsx:96,178`), un error tipográfico descarta lo escrito; en cualquier acción sensible (rotar secreto, publicar política, restablecer MFA, anonimizar) saca al operador de la consola sin explicación.
- Recomendación: devolver 400/403 con `INVALID_REAUTHENTICATION` (o excluir `/api/auth/reauth/*` del redirect en el cliente), mostrar "La contraseña no es correcta. Te quedan N intentos." dentro del diálogo y conservar el formulario.

### 3.3 Arquitectura, valores por defecto y divulgación progresiva

**ADM-UX-06 · Alta · La configuración de una aplicación está repartida en seis lugares y la ficha de la app no muestra sus piezas**
- Evidencia: rutas independientes `/applications/:id`, `/oauth-clients/:clientId`, `/saml-apps/:providerId`, `/access-policies/:applicationId`, `/federation?applicationId=` (`Router.tsx:60-88`) y diálogo de branding (`ApplicationsPage.tsx:68`, `ApplicationEditorPage.tsx:168`). La ficha sólo tiene Identidad, Registro y acceso, Responsables y Operación (`ApplicationEditorPage.tsx:122-171`); no lista clientes OAuth/SAML, APIs, usuarios/grupos con acceso ni la política vigente. Las listas ya aceptan filtro por aplicación (`OAuthClientsPage.tsx:22`, `SamlAppsPage.tsx:24`, `UsersPage.tsx:28`), pero la ficha no enlaza a ellas. Al revés, el editor de client no enlaza a su aplicación (sólo el código en el eyebrow, `OAuthClientEditorPage.tsx:112`).
- Impacto: el trabajo n.º 1 (2.1) exige saber de antemano qué módulos existen y en qué orden; es fácil dejar una app sin cliente o sin usuarios con acceso.
- Recomendación: **hub de aplicación con pestañas** (Resumen con estado/checklist · Inicio de sesión (OIDC/SAML) · Acceso (usuarios y grupos) · Políticas · Federación · Marca · Historial). Paso rápido (S): en la ficha, tarjetas con recuentos y enlaces a `/oauth-clients?applicationId=…`, `/saml-apps?application=…`, `/users?application=…`, `/access-policies/:id`, y "Nuevo client" con la app preseleccionada.

**ADM-UX-07 · Alta · El alta de OAuth client expone 16 grupos de controles de protocolo (≥21 entradas) sin presets; Client ID a mano; "Login URL" obligatorio, mal explicado y contradictorio con la documentación**
- Evidencia: todos los controles visibles a la vez (`OAuthClientEditorPage.tsx:117-144`); Client ID tecleado con regex de minúsculas (`:122`, `oauth-client.ts:36-37`); por defecto confidencial + `authorization_code`+`refresh_token` + `openid profile email` + 900 s (`oauth-client.ts:122-135`). `loginUrl: z.string().trim().refine(secureBrowserUrl, "Usa HTTPS o HTTP loopback, sin fragmentos ni credenciales.")` (`oauth-client.ts:44`): un campo vacío falla con el mensaje de formato, no "obligatorio", y no lleva marca de obligatorio. El servidor también lo exige siempre (`CreateOAuthClientRequestValidator.cs:22-26`), aunque la propia guía dice que para el cliente "Máquina" no aplica ("—") y que **debe ser el login hospedado de AuthCenter** (`docs/integration-quickstarts.md:34-46`); el placeholder sugiere lo contrario (`https://app.example.com/login`, `OAuthClientEditorPage.tsx:133`) y la consola, que conoce su propio origen, no lo precompleta (`oauth-client.ts:127`). La misma guía ya define los presets BFF/SPA/Máquina en una tabla (`docs/integration-quickstarts.md:36-45`).
- Impacto: el alta se bloquea por un campo cuyo propósito no se explica; quien lo rellena con la URL de su app rompe el SSO; para M2M obliga a inventar una URL.
- Recomendación: asistente con tipo de aplicación (Web con backend · SPA · Nativa/móvil · Máquina a máquina · API) que fije tipo, grants, PKCE y scopes; Client ID generado a partir del nombre (editable en "Avanzado"); Login URL oculto y precargado con `${origin}/login`; opcional para `client_credentials` en cliente y servidor; mensajes "Este campo es obligatorio" vs. formato; logout/CORS/token lifetime bajo "Opciones avanzadas".

**ADM-UX-08 · Media · Valores por defecto que dejan la configuración inútil sin decirlo**
- Evidencia: aplicación nueva con registro "Cerrado" (`application.ts:72`) y redirección muda a la ficha (`ApplicationEditorPage.tsx:62-64`), sin indicar que nadie podrá entrar hasta crear un cliente y conceder acceso. Proveedor federado nuevo con JIT desactivado y vinculación "Disabled" (`federation.ts:56-57`): el servidor rechaza a quien ya tiene cuenta (`ACCOUNT_LINKING_REQUIRED`) y a quien no la tiene (`JIT_PROVISIONING_DISABLED`) (`FederationService.cs:251-255`), así que con los valores por defecto el primer inicio de sesión federado sólo funciona para identidades ya vinculadas; además "Sin reglas, ningún usuario … se enruta" (`FederationPage.tsx:103`). Política de acceso: sin reglas se permite todo (`AccessPolicyEditorPage.tsx:95,100`), pero con la primera regla activa todo lo demás pasa a denegarse (`PolicyRuleForm.tsx:28`, `AccessPolicyService.cs:415-423`) y el diff de publicación no lo advierte (`AccessPolicyEditorPage.tsx:107`).
- Impacto: configuraciones "creadas" pero no operativas; bloqueos masivos al publicar la primera regla `Allow` de un grupo.
- Recomendación: tras cada alta, panel "Siguientes pasos" con estado (✔/pendiente); explicar cada modo de registro inline; en la primera regla activa, aviso explícito "Todo lo que no coincida será denegado" con recuento simulado de usuarios afectados; en federación, preguntar en el asistente "¿Crear cuentas nuevas en el primer acceso?" y "¿Vincular cuentas existentes por correo verificado?".

**ADM-UX-09 · Media · Política de contraseñas y bloqueo sin pantalla; MFA sólo por aplicación**
- Evidencia: reglas fijas en código (8 caracteres, dígito, mayúscula, minúscula; bloqueo 5/15 min, `InfrastructureServiceExtensions.cs:93-101`); la consola exige 12 en contraseñas temporales (`provisioning.ts:25,84`); MFA obligatorio es un checkbox por app (`ApplicationEditorPage.tsx:152-155`) y el MFA condicional vive en otra sección (Políticas).
- Impacto: el trabajo "configurar política de contraseñas" no se puede hacer desde la consola; requisitos distintos según dónde se cree la contraseña.
- Recomendación: sección "Seguridad › Autenticación" con contraseña, bloqueo, MFA por defecto de la organización y passkeys; las apps heredan y pueden endurecer.

### 3.4 Formularios

**ADM-UX-10 · Media · Sin marcas de obligatorio/opcional, validación sólo al enviar, errores no asociados al control y 14 implementaciones de `Field`**
- Evidencia: el `Field` compartido mete ayuda y error dentro del `<label>` y no pone `aria-invalid` ni `aria-describedby` (`components/Field.tsx:5`); hay 13 copias locales (`ApplicationEditorPage.tsx:178`, `OAuthClientEditorPage.tsx:154`, `ProviderEditorPage.tsx:182`, `UserProvisioningPage.tsx:86` con `<small>`…). Ningún `useForm` define `mode` (validación por defecto al enviar). La única marca de obligatorio es " *" en atributos de perfil (`UserEditorPage.tsx:83`); "Opcional" sólo aparece como texto de ayuda en 4 campos.
- Impacto: el operador descubre los obligatorios a base de errores; los lectores de pantalla oyen el error como parte del nombre del campo; cada formulario se comporta distinto.
- Recomendación: un único `Field` con `required`/`optional`, `aria-invalid`, `aria-describedby` (ayuda + error), `mode: "onTouched"`, y resumen de errores enlazado al inicio del formulario.

**ADM-UX-11 · Media · Varios botones "Guardar" por página, feedback lejos de la acción y errores agregados**
- Evidencia: ficha de usuario con "Guardar identidad" (`UserEditorPage.tsx:64`), "Guardar perfil universal" (`:79`), "Guardar acceso directo" (`:91`) y operaciones (`:68`); un único banner de error que combina 9 mutaciones y aparece arriba (`:58,62`). Grupo: "Guardar identidad" + "Guardar acceso heredado" (`GroupEditorPage.tsx:69,81`). Rol: "Guardar identidad" + "Guardar matriz" (`RoleEditorPage.tsx:58-59`). Aplicación: "Guardar configuración" + "Guardar responsables" + diálogo de branding (`ApplicationEditorPage.tsx:160`, `ApplicationGovernancePanel.tsx:78`).
- Impacto: cambios perdidos por guardar la sección equivocada; el mensaje de éxito/error queda fuera de la vista cuando se guarda al final de una página larga.
- Recomendación: pestañas por tema con una barra de acciones fija que muestre "Cambios sin guardar" y un solo Guardar por pestaña; errores junto a la sección que los produjo; confirmación tipo toast.

**ADM-UX-12 · Media · Ninguna protección de cambios sin guardar**
- Evidencia: no hay `beforeunload`, `useBlocker` ni uso de `isDirty` en todo `src/`; "Cancelar" es un `Link` directo (p. ej. `OAuthClientEditorPage.tsx:146`); un 401 a mitad de edición redirige con `location.replace` (`api/client.ts:56,111-116`).
- Impacto: formularios de 15–20 campos (client, proveedor, SAML) se pierden con un clic en el menú, "Cancelar" o una sesión expirada.
- Recomendación: guard al navegar/cerrar cuando el formulario está sucio (requiere `createBrowserRouter` para `useBlocker`, o `beforeunload` + confirmación en "Cancelar"); conservar borrador en `sessionStorage` para formularios largos.

**ADM-UX-13 · Media · El conflicto de concurrencia tiene cinco textos distintos y siempre descarta lo editado**
- Evidencia: `SaveError.tsx:17` ("Alguien más cambió este registro… Carga la versión actual y vuelve a aplicar tus cambios." + "Cargar la versión actual"); `errors.ts:9` ("…Recarga la página para ver la versión actual.", usado p. ej. en `EventHookEditorPage.tsx:120` junto a un botón "Cargar la versión actual"); `FederationPage.tsx:89` ("Una routing rule cambió…" + "Recargar"); `ProviderEditorPage.tsx:94`; `ProfileMappingEditorPage.tsx:100`; fallback `client.ts:130` ("El recurso cambió. Actualiza la página…").
- Impacto: instrucciones contradictorias ("recarga la página" pierde los cambios); no se sabe qué cambió ni quién.
- Recomendación: un componente de conflicto único: quién y cuándo cambió (si la API lo expone), diferencias campo a campo y dos acciones: "Revisar y volver a aplicar mis cambios" / "Descartar mis cambios".

**ADM-UX-14 · Baja · Placeholders que parecen valores reales**
- Evidencia: `https://app.example.com/oauth/callback`, `https://app.example.com/login`, `https://app.example.com` (`OAuthClientEditorPage.tsx:131,133,137`); `LOGIN_FAILED`, `AUTHCENTER`, `ApplicationUser` (`SystemLogPage.tsx:102-104`); `https://api.example.com/orders`, `Orders API`, `orders.read`, `Leer pedidos` (`ApiResourceEditorPage.tsx:97-98,108-109`); `CRM corporativo`, `https://crm.example.com/saml` (`SamlAppEditorPage.tsx:125-126`); `Entra ID corporativo`, `https://login.example.test` (`ProviderEditorPage.tsx:103,106`); `empresa.com, filial.mx` (`ApplicationEditorPage.tsx:139`); `SIEM corporativo`, `https://hooks.example.com/authcenter` (`EventHookEditorPage.tsx:126-127`).
- Impacto: en capturas y en uso real parecen campos ya rellenos; el usuario cree que hay un valor guardado (o que el ejemplo es el valor correcto, como en Login URL).
- Recomendación: ejemplos en el texto de ayuda ("Ej.: …"), placeholders neutros o vacíos.

### 3.5 Tablas y listas

**ADM-UX-15 · Media · Capacidades de tabla inconsistentes: búsqueda, orden y acciones masivas**
- Evidencia: búsqueda de texto sólo en Usuarios, Grupos, OAuth clients, Recursos de API, SAML, Event Hooks y gobierno; Roles, Permisos, Tokens, Profile mappings y Group rules sólo tienen selects de aplicación/estado; Aplicaciones (`ApplicationsPage.tsx:26-29`) y Políticas de acceso (`AccessPoliciesPage.tsx:11-14`) no tienen ni búsqueda ni filtros. Orden sólo en Usuarios y mediante un `<select>` "Orden" (`UsersPage.tsx:25,75`); ninguna cabecera ordenable. Ninguna selección múltiple ni acción masiva. La columna Roles muestra 2 roles sin "+N" (`UsersPage.tsx:88`), a diferencia de Recursos de API, que sí lo hace (`ApiResourcesPage.tsx:62`). La búsqueda de OAuth clients actualiza la URL en cada tecla, sin *debounce* y añadiendo una entrada de historial por carácter (`OAuthClientsPage.tsx:42-48,54`; compárese con `DebouncedTextField` + `replace` en `UsersPage.tsx:65,71`). `GroupsPage` reescribe `page=1` al montar, así que un enlace a `?page=3` se pierde (`GroupsPage.tsx:24`). En listas sin `placeholderData` (Aplicaciones, OAuth clients, Roles, Permisos, Grupos, Tokens, Mappings, Group rules) cada cambio de página/filtro sustituye la tabla por "Cargando…". La paginación es coherente (rango, tamaño 20/50/100, anterior/siguiente: `components/Pagination.tsx:10-28`) pero sin ir a una página ni primera/última; Políticas de acceso y Esquema de perfil no paginan.
- Impacto: con decenas de apps o roles no hay forma de encontrar uno; operaciones repetitivas (desactivar 20 usuarios, revocar accesos) se hacen fila a fila; "Atrás" del navegador se comporta de forma errática.
- Recomendación: componente `DataTable` compartido (búsqueda con debounce, filtros como chips, orden por columna, selección múltiple con barra de acciones, menú "⋯" por fila, `keepPreviousData`), usado en todas las listas; Aplicaciones con vista tabla/tarjetas y búsqueda.

**ADM-UX-16 · Media · Acción destructiva roja en cada fila de usuarios y confirmación sin consecuencias**
- Evidencia: botón `button--danger-quiet` "Desactivar" junto a "Administrar" en cada fila (`UsersPage.tsx:90`); la confirmación dice "Desactivar a {nombre} ({correo}). La API volverá a validar tus permisos." (`:96`), mientras la misma acción en la ficha explica que "las sesiones anteriores serán revocadas" (`UserEditorPage.tsx:69`).
- Impacto: ruido visual (una columna de botones rojos), riesgo de clic accidental y decisiones sin saber el efecto.
- Recomendación: mover a menú "⋯"/acciones masivas; texto de confirmación con consecuencias (sesiones revocadas, pierde acceso a N apps) y verbo explícito en el botón.

### 3.6 Estados de carga, error y vacío

**ADM-UX-17 · Media · Estados de carga/vacío/error poco orientadores y semánticamente incorrectos**
- Evidencia: `PageState` siempre renderiza un `<h1>` (`components/PageState.tsx:13`), así que listas vacías o con error tienen dos H1 (p. ej. "OAuth clients" + "No hay OAuth clients", `OAuthClientsPage.tsx:52,61`) e incluso H1 dentro de paneles con H2 (`FederationPage.tsx:94-96,101-103`). Las fichas se reemplazan enteras por el spinner, sin migas ni cabecera (`ApplicationEditorPage.tsx:102`, `UserEditorPage.tsx:55`). Errores sin "Reintentar" ni "Volver" (`GroupsPage.tsx:30`, `PermissionsPage.tsx:31`, `UserEditorPage.tsx:56`). **Aplicaciones no tiene estado vacío**: con 0 apps muestra una rejilla vacía y "0–0 de 0" (`ApplicationsPage.tsx:63-70`). Sólo 2 estados vacíos tienen CTA ("Crear el primer hook", `EventHooksPage.tsx:53`; "Crear el primer atributo", `ProfileSchemaPage.tsx:35`); el resto dice "Ajusta los filtros o crea…" sin botón, aun cuando no hay filtros (`OAuthClientsPage.tsx:61`, `ProvisioningTokensPage.tsx:50`, `GroupRulesPage.tsx:51`). La navegación entre módulos no tiene indicador de progreso: `BrowserRouter` aplica `startTransition` a cada cambio de ruta (`node_modules/react-router/dist/development/chunk-OB3PAWPO.mjs:10507-10513`, v7.18.4) y el único `Suspense` está por encima del shell (`Router.tsx:52`), así que mientras se descarga un módulo la página anterior sigue en pantalla sin señal.
- Impacto: primeras visitas sin guía, saltos de contenido, jerarquía de encabezados rota para lectores de pantalla, sensación de "no pasó nada" en redes lentas.
- Recomendación: `PageState` con nivel de encabezado configurable (H2 dentro de páginas); esqueletos que conserven cabecera y migas; todos los errores con Reintentar/Volver; estados vacíos con explicación + CTA primario + enlace a guía; barra de progreso de navegación.

### 3.7 Confirmaciones destructivas y feedback

**ADM-UX-18 · Media · Huecos e incoherencias en confirmaciones**
- Evidencia: "Revocar" un acceso pendiente se ejecuta sin confirmación (`UserEditorPage.tsx:91` → `onRevoke` → `revokeAccess.mutate`), aunque revoca sesiones (`:51`). El diálogo de cancelar una revisión muestra "Cancelar" (descartar, fijo en `ConfirmDialog.tsx:40`) junto a "Cancelar revisión" (`AccessReviewDetailPage.tsx:153`). "Reintentar entrega", acción no destructiva, usa estilo rojo (`EventDeliveriesPage.tsx:124,172`). Lo positivo: step-up con contraseña para acciones sensibles (`ReauthenticationDialog`) y diálogos nativos con foco gestionado (`hooks/useModalDialog.ts`).
- Recomendación: confirmar toda revocación; botón de descarte "Volver"/"No, mantener"; reservar rojo para lo irreversible; para anonimizar/eliminar, escribir el nombre o correo además del step-up.

**ADM-UX-19 · Baja · No hay sistema de toasts; mensajes de éxito persistentes y a veces contradictorios**
- Evidencia: el éxito es un `<p class="alert alert--success" role="status">` al inicio de la página en cada pantalla (p. ej. `ApplicationEditorPage.tsx:118`); no se borra al reenviar en Aplicaciones (`:120`, sin `setFeedback("")`), así que tras un guardado correcto y otro fallido conviven el banner verde y el rojo (`:118-119`); otras pantallas sí lo limpian (`SamlAppEditorPage.tsx:117`, `EventHookEditorPage.tsx:121`). Tras crear (app, client, SAML, rol, grupo…) se navega con `replace` sin ningún mensaje (`ApplicationEditorPage.tsx:62-64`, `SamlAppEditorPage.tsx:71`).
- Recomendación: toasts (`aria-live="polite"`) para confirmaciones, con acción "Ver"/"Deshacer" donde aplique; errores inline junto al formulario; limpiar estados al reenviar; mensaje "Aplicación creada — siguientes pasos" tras cada alta.

### 3.8 Dashboard y primer uso

**ADM-UX-20 · Media · Dashboard sin jerarquía: 12 métricas iguales, 7 tarjetas que duplican el menú y una nota técnica**
- Evidencia: 12 métricas en una rejilla uniforme (`DashboardPage.tsx:76-91`, `styles.css:234-242`); el tono sólo cambia el color (attention/critical). 7 tarjetas de módulo de `min-height: 13rem` en 2 columnas que repiten el menú (`DashboardPage.tsx:10-18,60-67`, `styles.css:103-104`) y una nota "Frontera de seguridad server-side" (`:68-71`). Las métricas sólo se ven con `AUDIT_LOGS_READ` (`:34-38`; el endpoint exige esa política, `AdminDashboardController.cs:12`), aunque la mayoría no son de auditoría. "Inicios de sesión de riesgo alto" no enlaza a nada (`:90`); "Inicios de sesión rechazados" enlaza a un filtro que no reproduce la cifra (`:89` vs `AdminDashboardController.cs:15-16,38`).
- Impacto: en una instalación nueva todo es 0 y nada indica qué hacer; en una con actividad, lo urgente (dead letters, revisiones vencidas, SoD) compite con contadores de inventario.
- Recomendación: bloque "Requiere atención" (sólo métricas >0, críticas primero, con enlace que reproduzca la cifra); 3–4 KPIs con tendencia; acciones rápidas ("Registrar aplicación", "Invitar usuarios", "Conectar IdP"); checklist de primeros pasos; eliminar las tarjetas de módulo y la nota técnica; métricas filtradas por permiso de cada módulo.

**ADM-UX-21 · Alta · Primer uso sin orientación y aterrizaje en el portal de usuario final**
- Evidencia: `/` → `/login` (`src/AuthCenter.Api/Program.cs:484`); tras iniciar sesión sin `return_url` se va a `/portal` (`wwwroot/assets/login.js:691`), donde la consola es un botón "Administración" condicionado (`portal.js:67`, `portal.html:23`). En la consola no existe checklist, asistente ni estado vacío con CTA en Aplicaciones (ADM-UX-17); el dashboard no detecta "0 aplicaciones propias / 0 clientes / 0 usuarios".
- Impacto: el primer administrador no sabe que hay una consola ni por dónde empezar; el primer valor (un login funcionando en su app) requiere conocer 2.1 de memoria.
- Recomendación: redirigir a `/admin-v2/` tras el login cuando el usuario tiene permisos `AUTHCENTER_*` y no hay `return_url` (o recordar la última superficie usada); checklist persistente "Primeros pasos": 1) Registrar aplicación + cliente (asistente), 2) Probar inicio de sesión, 3) Invitar usuarios o abrir el registro, 4) Activar MFA, 5) Conectar IdP corporativo (opcional), 6) Configurar webhook (opcional).

### 3.9 Ayudas para desarrolladores

**ADM-UX-22 · Alta · Faltan los datos y herramientas que un desarrollador necesita tras registrar un cliente**
- Evidencia: el único botón de copiar de toda la consola es el del secreto (`SecretRevealDialog.tsx:17-25`; `grep clipboard`). No se muestran issuer, URL de discovery (`/.well-known/openid-configuration`, `WellKnownController.cs:10,24`), endpoints `/oauth/authorize|token|userinfo|logout` (`OAuthController.cs:40,290,402,213`) ni JWKS en ninguna pantalla de cliente. Valores para copiar mostrados como texto o input de sólo lectura sin botón: metadatos/Entity ID/SSO/SLO del IdP SAML (`SamlAppsPage.tsx:86-95`), URL base SCIM (`ProvisioningTokenEditorPage.tsx:114`), callback/Entity ID/ACS del SP de federación (`ProviderEditorPage.tsx:114,125-127`), enlace de inicio SAML (`SamlAppEditorPage.tsx:173`). Sin snippets de integración, aunque existen en `docs/integration-quickstarts.md` (SDK .NET/TS) y el patrón ya está implementado para webhooks (`EventHookEditorPage.tsx:152`). Sin "Probar inicio de sesión" para un cliente; sí hay simuladores en políticas, routing y mappings, y "Probar conexión" en federación (sólo tras guardar, `ProviderEditorPage.tsx:168-173`).
- Impacto: el desarrollador tiene que buscar la URL del emisor y los endpoints en otra parte; errores de copia manual en URIs y certificados.
- Recomendación: panel "Conectar tu aplicación" en cada cliente: Client ID, issuer, discovery, endpoints y JWKS con copiar; pestañas de snippet (.NET BFF, SPA TS, curl client_credentials) rellenadas con los valores del cliente; botón "Probar inicio de sesión" (authorize con PKCE hacia una URI de prueba registrada o hacia una página de resultado de AuthCenter que muestre claims); descarga de metadatos SAML; botón copiar en todos los valores de sólo lectura.

**ADM-UX-23 · Media · Rotar el secreto de un client corta el servicio al instante**
- Evidencia: "El secreto actual dejará de funcionar de inmediato." (`OAuthClientEditorPage.tsx:149`), mientras los webhooks firman 24 h con ambos secretos (`EventHookEditorPage.tsx:151,157`) y los tokens SCIM también revocan al rotar (`ProvisioningTokenEditorPage.tsx:119`).
- Impacto: toda rotación programada implica una ventana de error en el consumidor; desincentiva rotar.
- Recomendación: (backend + UI) periodo de solapamiento configurable con fecha de caducidad del secreto anterior visible, "último uso" de cada secreto y botón "Revocar el anterior ahora".

### 3.10 Flujos específicos

**ADM-UX-24 · Alta · El System Log no permite diagnosticar un inicio de sesión fallido sin conocimiento interno**
- Evidencia: filtros de texto libre para valores enumerables (Acción, Aplicación, Tipo de entidad) con coincidencia exacta (`SystemLogPage.tsx:101-104`, `AuditService.cs:112-113`), aunque el catálogo de tipos ya existe (`GET /api/event-hooks/event-types`, usado en `EventHookEditorPage.tsx:57`) y el selector de usuarios también (`components/UserPicker.tsx`); "ID del actor" pide un UUID (`:106`) y un correo produce un 400 genérico (ver 2.5). Sin columna de resultado/motivo (`:120`); el motivo está en JSON crudo (`:169-170`) y no se exporta (`AuditLogsController.cs:44-46`). `UserNotFound` no guarda identificador (`AuthService.cs:230`); `/oauth/authorize` no audita (`OAuthAuthorizationService.cs`, 1 562 líneas sin `LogAsync`). La tarjeta del dashboard no reproduce su cifra (`DashboardPage.tsx:89`). "Ver historial" del usuario filtra por entidad, no por actor (`HistoryLink.tsx:9`). La propia consola tiene el patrón correcto en `ScimDiagnosticsPanel.tsx` (filtro "Resultado", resumen de fallos con etiquetas legibles, `:45-46,49-51`).
- Impacto: el trabajo de soporte más frecuente ("no puedo entrar") requiere conocer códigos, UUIDs y leer JSON; parte de los fallos de integración ni siquiera dejan rastro.
- Recomendación: vista "Inicios de sesión" con filtros por persona (nombre/correo), aplicación (select), resultado y motivo (etiquetas en español), rango rápido (24 h/7 d); columna "Motivo"; acción "Ver inicios de sesión" en la ficha de usuario (filtro por actor); filtro multi-acción en el API para que el dashboard enlace a la misma cifra; auditar errores de `/oauth/authorize` y registrar el identificador intentado (enmascarado) en `UserNotFound`; selects/autocompletado para Acción y Aplicación.

**ADM-UX-25 · Media · Federación: tres reautenticaciones y dos pantallas para un IdP operativo; validación sólo después de guardar**
- Evidencia: guardar proveedor = contraseña (`ProviderEditorPage.tsx:165,178`); crear la regla de routing = otra (`FederationPage.tsx:73,120`); reordenar = otra (`:106`). La metadata del SP y "Probar conexión" sólo existen con el proveedor ya guardado (`ProviderEditorPage.tsx:88,125,168-173`). Todo es por aplicación (`FederationPage.tsx:36-37,85`): un IdP corporativo para 5 apps = 5 proveedores + 5 reglas.
- Impacto: alta lenta, errores descubiertos tarde y sin el proveedor activo hasta que existe la regla; repetición por app.
- Recomendación: asistente "Conectar IdP": protocolo → valores de AuthCenter para copiar primero (callback/entity ID/ACS/metadata) → datos del IdP → prueba de conexión en seco → dominios de enrutamiento → aplicaciones donde se ofrece → una sola confirmación de identidad para el lote (requiere cambio de API).

**ADM-UX-26 · Media · Usuarios y grupos: el acceso se reparte entre pantallas y el alta no permite grupos**
- Evidencia: el alta sólo ofrece una app y roles directos (`UserProvisioningPage.tsx:72-78`); la ficha del usuario muestra grupos en sólo lectura y remite al grupo (`UserEditorPage.tsx:67`); la membresía se gestiona en la ficha del grupo con un buscador propio (`GroupEditorPage.tsx:71`), sin acción masiva. El panel de acceso del usuario pinta todas las apps y todos los roles del sistema como casillas (`UserEditorPage.tsx:36-37,91`).
- Impacto: para "dar a Ana acceso a Ventas como Editor vía el grupo Comercial" hay que visitar 2–3 pantallas; con muchas apps/roles el panel crece sin búsqueda.
- Recomendación: en la ficha del usuario, "Añadir a grupo" y "Conceder acceso" con buscador (app → rol) y vista de acceso efectivo por app (directo/heredado); en el alta, paso opcional de grupos; acciones masivas desde la lista.

**ADM-UX-27 · Media · Navegación por módulo técnico, no por tarea**
- Evidencia: 8 grupos/20 ítems (`AppShell.tsx:19-70`); grupos de un solo ítem ("Inicio", "Federación", `:20,46`); "Aplicaciones › Aplicaciones" (`:30-32`); "Recursos de API" y "Profile mappings" agrupados lejos de su permiso real (`:34,42`); topbar sin búsqueda ni ayuda (`:123-127`); no hay enlaces a documentación en ninguna pantalla (`grep -i documentaci` sin resultados).
- Recomendación: 5–6 grupos por tarea — Inicio · Aplicaciones (hub: apps, clientes, SAML, APIs) · Personas (usuarios, grupos, esquema, ciclo de vida) · Seguridad (autenticación, políticas, federación, roles/permisos) · Gobierno · Supervisión (registro, webhooks) —, búsqueda global (Ctrl/Cmd-K) de usuarios/apps/clientes, y enlace "Ayuda" contextual por pantalla.

### 3.11 Accesibilidad, responsive, sistema de diseño y modo oscuro

**ADM-UX-28 · Media · Accesibilidad: buena base (diálogos nativos, axe en CI, regiones de tabla) con fallos concretos**
- **Contorno grueso en cada H1**: `PageHeader` enfoca el H1 al montar (`components/PageHeader.tsx:12,18`, `tabIndex={-1}`) y el CSS sólo estiliza `:focus-visible` de controles interactivos (`styles.css:36`) y quita el contorno de `main` (`:82`), pero no del H1, así que se ve el anillo por defecto del navegador. El foco es intencional y está probado (`e2e/accessibility.spec.ts:66-67`): basta `h1[tabindex="-1"]:focus { outline: none }` (o limitarlo a navegación por teclado).
- **Título del documento fijo**: todas las pestañas se llaman "Administración · AuthCenter" (`index.html:7`; no hay `document.title` en `src/`) — WCAG 2.4.2 en SPA y confusión con varias pestañas.
- **H1 duplicados** por `PageState` dentro de páginas (ADM-UX-17).
- **Errores no asociados** al control (sin `aria-invalid`/`aria-describedby`, `components/Field.tsx:5`; excepción: `UserPicker.tsx:70-71`).
- **Contraste del menú**: encabezados de grupo en `#667085` a `.66rem` sobre el fondo navy (`styles.css:50,57`; también la versión, `:79`) ≈ 2.7–3.8:1 según el punto del gradiente (< 4.5:1). Las pruebas axe no lo marcan: con fondo `radial-gradient` axe-core no puede calcular el contraste y lo deja como "incomplete", y la prueba sólo revisa `violations` (`e2e/accessibility.spec.ts:42-43`).
- **Cajón móvil**: cerrado sólo se desplaza fuera de pantalla (`styles.css:294`, sin `inert`/`visibility`), así que sus 20 enlaces siguen en el orden de tabulación; al abrirlo no se mueve el foco al cajón (`AppShell.tsx:122`).
- Cobertura: axe se ejecuta en todas las rutas pero sólo con datos vacíos (`e2e/accessibility.spec.ts:5-45`), así que tablas pobladas, errores de formulario y paneles condicionales quedan fuera.
- Recomendación: los arreglos puntuales anteriores + título por ruta ("OAuth clients · Administración · AuthCenter") + pruebas axe con datos y con errores de validación.

**ADM-UX-29 · Media · Responsive: el indicador de producción y algunas acciones desaparecen en móvil**
- Evidencia: a ≤760 px `.environment-pill { display: none }` (`styles.css:296`): en un teléfono no se ve si la consola está en Producción. La misma regla oculta todo `.button--quiet` (`:296`), lo que quita "Mi cuenta" y también el botón "Branding" de cada tarjeta de aplicación (`ApplicationsPage.tsx:68`). Los 8 filtros del System Log se apilan en una columna antes de los resultados (`styles.css:298`, `SystemLogPage.tsx:101-110`). Positivo: tablas con desplazamiento propio y reflow a 320 px probado (`e2e/accessibility.spec.ts:47-60`).
- Recomendación: mantener una versión compacta del entorno (punto de color + "PROD") en la topbar móvil; no ocultar acciones por variante visual; filtros en un panel plegable "Filtros (n)".

**ADM-UX-30 · Media · Sistema de diseño incipiente: tokens de paleta, tipografía sin escala y componentes duplicados**
- Evidencia: los tokens son colores de paleta (`--slate-200`, `--blue-700`…, `styles.css:5-26`), no semánticos (superficie, texto, borde, peligro); 56 líneas con hex fijos fuera de tokens (p. ej. `#fecdca`, `#fff6f5`, `#7f56d9`, `styles.css:77-78,98,111-113,201`); `--slate-800` usado pero no definido (`:229`). 19 tamaños de fuente en rem distintos (de `.66rem` a `1.65rem`) más el `clamp` del H1 (`:39`). "Inter" declarada pero nunca cargada (`:3`, sin `@font-face`). Iconos como glifos Unicode ("☰" `AppShell.tsx:122`, "↗" y "✓" `DashboardPage.tsx:63,69`, "×" `BrandingDialog.tsx:40`, "↑/↓" `FederationPage.tsx:105`) y puntos en lugar de iconos en el menú (`styles.css:61`). `StatusBadge` es binario (`components/StatusBadge.tsx:1-3`): los tokens "Expirado" y "Revocado" se ven idénticos (`ProvisioningTokensPage.tsx:58-61`). 14 implementaciones de `Field` (ADM-UX-10); clase `field--search` usada pero no definida (`GroupEditorPage.tsx:71`, `RoleEditorPage.tsx:59`); `.check-list` en px frente al resto en rem (`styles.css:314-315`). Densidad baja y sin modo compacto: celdas `.82rem 1rem` (`styles.css:174`), botones de 2.55rem de alto (`:92`), paneles con 1.25rem de relleno (`:130`), tarjetas de módulo de 13rem (`:104`). Estados con etiquetas variables ("Activo/Activa/Inactivo/Inactiva/Desactivado/Deshabilitada") y ortografía mixta ("sólo" ×25, "solo" ×13).
- Impacto: cada pantalla resuelve lo mismo de forma distinta; imposible tematizar o añadir modo oscuro sin reescribir; densidad y jerarquía tipográfica inconsistentes.
- Recomendación: tokens semánticos (color, espacio, radio, sombra) + escala tipográfica de 6 pasos + set de iconos SVG inline (compatible con la CSP `default-src 'self'`) + primitivas compartidas: `Field`, `DataTable`, `Tabs`, `Toast`, `Badge` con variantes (éxito/aviso/peligro/neutral/info), `EmptyState`, `CopyField`, `ConflictBanner`; guía de redacción.

**ADM-UX-31 · Baja · Sin modo oscuro**
- Evidencia: `:root { color-scheme: light; }` (`styles.css:2`), ninguna regla `prefers-color-scheme`, `theme-color` fijo (`index.html:6`), colores nombrados por tono.
- Recomendación: tras ADM-UX-30, definir el tema oscuro sobre tokens semánticos y respetar `prefers-color-scheme` con selector manual.

**ADM-UX-32 · Baja · Fechas sin zona horaria y entrada en UTC**
- Evidencia: `formatDate` usa `es-MX` sin zona visible (`utils/format.ts:5`); las reglas de política se editan en UTC ("Días UTC", "Activa desde UTC", `PolicyRuleForm.tsx:35-36`) y la simulación en "Fecha y hora UTC" (`AccessPolicyEditorPage.tsx:105`), mientras el System Log filtra en "hora local" (`SystemLogPage.tsx:108-109`).
- Recomendación: mostrar la zona (o UTC) junto a cada fecha; permitir editar en hora local con vista previa en UTC.

## 4. Top 10 recomendaciones

| # | Recomendación | Hallazgos | Impacto | Esfuerzo |
|---|---|---|---|---|
| 1 | **Asistente "Registrar aplicación"**: tipo de app (Web con backend, SPA, nativa, máquina, SAML) → datos mínimos (nombre; redirect/ACS o metadatos) → quién puede entrar (usuarios/grupos o registro abierto) → resumen con credenciales, datos de conexión y "Probar inicio de sesión". Reutiliza `POST /api/applications`, `/api/oauth/clients`, `/api/saml/service-providers`, `PUT /api/users/{id}/access`. | 06, 07, 08, 21, 22 | Muy alto: convierte el trabajo n.º 1 de ≥5 pantallas/≥30 campos en un flujo guiado | L (MVP M) |
| 2 | **Hub de aplicación con pestañas** (Resumen+checklist, Inicio de sesión OIDC/SAML, Acceso, Políticas, Federación, Marca, Historial). Primer paso S: tarjetas con recuentos y enlaces filtrados + "Nuevo client" con la app preseleccionada. | 06, 11, 26 | Alto | M–L (S el primer paso) |
| 3 | **Rehacer el formulario de OAuth client**: presets por tipo, Client ID generado, Login URL oculto y precargado con `${origin}/login` (y opcional para `client_credentials` en cliente y servidor), "Opciones avanzadas" plegadas, etiquetas en español, mensajes "obligatorio" distintos de "formato". | 07, 10, 14 | Alto: elimina el bloqueo observado en el alta | S–M |
| 4 | **Canal de errores en español**: catálogo central de códigos, errores de campo estructurados mostrados inline, nada de texto libre del servidor; corregir que un 401 de `/api/auth/reauth/password` cierre la consola; componente único de conflicto. | 04, 05, 13 | Alto (y evita pérdida de trabajo) | M (S el bug del 401) |
| 5 | **Pasada de lenguaje**: glosario, menú y etiquetas en español con término técnico secundario, enums mapeados, descripciones orientadas a tarea, nombres legibles de permisos (incl. matriz de roles y pantalla de acceso denegado). | 01, 02, 03 | Alto | M |
| 6 | **Vista "Inicios de sesión" en el System Log**: filtros por persona/app/resultado/motivo con selects, columna Motivo, rango rápido, acción desde la ficha de usuario, enlace del dashboard con la misma cifra; en API: filtro multi-acción, auditoría de `/oauth/authorize` y del identificador intentado. | 24, 20 | Alto para soporte | M (UI) + M (API) |
| 7 | **Primer uso y dashboard**: tras login, admins a `/admin-v2/`; checklist "Primeros pasos"; bloque "Requiere atención"; acciones rápidas; quitar tarjetas duplicadas y la nota técnica; estado vacío de Aplicaciones con CTA. | 20, 21, 17 | Alto | M |
| 8 | **Navegación por tareas + búsqueda global + ayuda contextual** (5–6 grupos, Ctrl/Cmd-K, enlace "Ayuda" por pantalla a `docs/`). | 27, 01 | Medio-alto | M |
| 9 | **Panel "Conectar tu aplicación" en cada cliente**: Client ID, issuer, discovery, endpoints, JWKS con copiar; snippets .NET/TS/curl; "Probar inicio de sesión"; descarga de metadatos SAML; `CopyField` en todos los valores de sólo lectura; solapamiento al rotar secretos. | 22, 23 | Alto para desarrolladores | M (solapamiento: M en API) |
| 10 | **Fundamentos de sistema de diseño y primitivas**: tokens semánticos, escala tipográfica, iconos SVG, `Field` accesible con obligatorio/opcional, `DataTable` (búsqueda, orden, masivas, `keepPreviousData`), `Toast`, `EmptyState`, guard de cambios sin guardar; después modo oscuro. | 10, 12, 15, 16, 17, 18, 19, 28, 29, 30, 31 | Medio-alto (consistencia, accesibilidad, velocidad de desarrollo) | L (incremental) |

### Arreglos rápidos (esfuerzo S, alto retorno)
1. `h1[tabindex="-1"]:focus { outline: none; }` — elimina el contorno negro de todos los H1 (ADM-UX-28).
2. Título del documento por ruta (ADM-UX-28).
3. No tratar el 401 de reautenticación como sesión expirada (ADM-UX-05).
4. Login URL: precargar `${origin}/login`, mensaje "Este campo es obligatorio", ayuda que diga "login hospedado de AuthCenter" (ADM-UX-07).
5. Estado vacío con CTA en Aplicaciones y en todas las listas sin filtros activos (ADM-UX-17).
6. Enlaces desde la ficha de aplicación a clientes/SAML/usuarios filtrados (ADM-UX-06).
7. Indicador de entorno visible en móvil; no ocultar `.button--quiet` (ADM-UX-29).
8. Búsqueda de OAuth clients con `DebouncedTextField` + `replace`; no reiniciar `page` al montar Grupos (ADM-UX-15).
9. Confirmar "Revocar" acceso pendiente; botón de descarte "Volver" en `ConfirmDialog`; estilo neutro para "Reintentar entrega" (ADM-UX-18).
10. Reemplazar los 18 textos con `AUTHCENTER_*` por nombres legibles (ADM-UX-03).
