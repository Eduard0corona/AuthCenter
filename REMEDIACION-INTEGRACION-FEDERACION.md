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
- [ ] **FED-02** `login_hint`, `idp` y `domain_hint` se ignoran en `/oauth/authorize`.
- [ ] **SSO-01** El login hospedado autentica contra el campo "Aplicación" (por defecto
  `AUTHCENTER`); usuarios de otras aplicaciones reciben `401 ACCESS_DENIED`. `LoginUrl` no está
  documentado.
- [ ] **SSO-02** Las políticas de acceso y el MFA de la aplicación destino no se evalúan al
  completar la autorización, canjear el código ni renovar tokens OAuth.
- [ ] **SSO-03** La sesión SSO dura lo mismo que un access token (15 min, sin renovación); no hay
  `prompt`/`max_age`; `auth_time` es la hora de emisión; no se emiten `amr`/`acr`.
- [ ] **SSO-04** El `interaction_id` no está ligado al navegador que inició la autorización.
- [ ] **TOK-01** El claim de roles se emite como URI
  `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`; el SDK espera `role`, por lo que
  `IsInRole`/`RequireRole` fallan en los sistemas integrados.
- [ ] **TOK-02** El access token siempre tiene `aud = client_id`; no hay `resource` (RFC 8707) ni
  catálogo de APIs y scopes propios.
- [ ] **TOK-03** Higiene de tokens: `email_verified` como string en el ID token, sin `sid`/`azp`,
  access token sin `typ: at+jwt` ni `iat`, respuesta de token con campos `null`.
- [ ] **LOG-01** No hay logout global: faltan `end_session_endpoint`, `post_logout_redirect_uris`,
  `sid` y back-channel logout; el refresh de una aplicación sigue vivo tras cerrar sesión.
- [ ] **RL-01** Límites de tasa sólo por IP (5 logins/min, 60 llamadas/min a `/oauth/token`).
- [ ] **CORS-01** CORS global con credenciales; no hay orígenes por cliente OAuth.
- [ ] **DISC-01** Discovery incompleto (`claims_supported`, logout, parámetros no soportados) y sin
  validación `issuer` = origen público.
- [ ] **OIDC-01** No hay endpoint de introspección (RFC 7662).
- [ ] **OIDC-02** No hay token exchange (RFC 8693).
- [ ] **SAML-01** AuthCenter no puede actuar como IdP SAML para aplicaciones que sólo hablan SAML.
- [ ] **DIS-01** El SDK no se distribuye: NuGet sólo como artefacto de CI, npm `private`, sólo
  `net10.0`.

### B. SDK de integración

- [ ] **SDK-01** El parámetro documentado `?return_url=` se ignora (se enlaza como `returnUrl`).
- [ ] **SDK-02** `ClientCredentialsAsync()` falla con los scopes por defecto (`invalid_scope`).
- [ ] **SDK-03** Endpoints `/oauth/*` codificados: ignoran discovery y el path base.
- [ ] **SDK-04** No se incluye un coordinador de refresh distribuido.
- [ ] **SDK-05** Un fallo de validación tras el refresh termina en HTTP 500.
- [ ] **SDK-06** No hay prueba de contrato SDK↔servidor en CI (la prueba unitaria usa claims
  sintéticos).
- [ ] **SDK-07** El SDK TypeScript no valida ID token/nonce/`iss`, no procesa el callback y no tiene
  userinfo ni logout.
- [ ] **SDK-08** El quickstart SPA sólo tiene el botón de login.
- [ ] **SDK-09** Falta sobrecarga basada en `IConfiguration` y documentación de `LoginUrl`,
  recursos y logout.

### C. Seguridad

- [ ] **SEC-01** Los 39 validadores FluentValidation están registrados pero nunca se ejecutan (la API
  aceptó un cliente con redirect `http://`, grant `password` y tokens de un año).
- [ ] **SEC-02** Redirección abierta en el login hospedado (`return_url=/\evil.example`).
- [ ] **SEC-03** La federación reactiva accesos revocados por un administrador.
- [ ] **SEC-04** `/api/federation/route` es anónimo y permite sondear grupos/atributos por email.
- [ ] **SEC-05** Los usuarios con contraseña temporal no pueden entrar por el login hospedado.
- [ ] **SEC-06** Interoperabilidad con IdPs: issuer con `/` final, `email_verified` obligatorio,
  firma SAML sólo a nivel Response, entity IDs no HTTPS, aserciones cifradas.
- [ ] **SEC-07** `AllowPasswordLogin` no se aplica en el login (no hay aplicaciones sólo federadas).

### D. Backend administrativo y Event Hooks

- [ ] **HOOK-01** Reenviar dos veces la misma entrega fallida no la vuelve a encolar.
- [ ] **HOOK-02** El listado de entregas se corta en 20 elementos.
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
- [ ] **OPS-09** Las pruebas relacionales "pasan" sin ejecutarse cuando no hay SQL Server.
- [P] **OPS-10** Ramas remotas ya integradas.
- [ ] **OPS-11** Dependabot, presupuesto de bundle y cobertura en CI.
- [ ] **OPS-12** Pruebas de carga con directorios grandes.

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
