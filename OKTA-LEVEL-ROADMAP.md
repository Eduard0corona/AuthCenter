# Hoja de ruta hacia una plataforma de identidad nivel Okta

## Objetivo

AuthCenter debe ofrecer a los proyectos de la organización una experiencia de integración,
seguridad, administración y operación comparable con una plataforma moderna de Identity as a
Service. "Nivel Okta" no significa copiar todo su catálogo comercial: significa cubrir con calidad
productiva los casos que usamos, basarnos en estándares abiertos y no dejar controles críticos como
configuración manual o conocimiento tribal.

Esta hoja de ruta es el registro canónico. Una capacidad sólo se marca lista cuando incluye modelo,
API, autorización administrativa, migración, auditoría, documentación, pruebas negativas/positivas
y evidencia de operación.

El análisis del 2026-09-26 encontró que varias marcas de "lista" no cumplían esa regla (sesión SSO,
logout global, federación en el login, consola y SDK incompletos). La remediación
(`REMEDIACION-INTEGRACION-FEDERACION.md`, fases F1 a F15) cerró esos huecos; la matriz indica en
qué fase y lo que aún depende del propietario (`docs/operations/OWNER-ACTIONS.md`).

## Matriz de capacidades

| Área | Estado actual | Objetivo de producto | Estado |
|---|---|---|---|
| OAuth 2.0 / OIDC | Authorization Code + PKCE, Client Credentials, refresh, JWKS y revocación; sesión SSO (`prompt`, `max_age`, `id_token_hint`), logout global con back-channel, catálogo de APIs (RFC 8707), introspección y token exchange, CORS por cliente | PAR y Device Authorization cuando exista un consumidor real | Lista (F2–F7) |
| Directorio universal | Usuarios, grupos, membresías, perfiles tipados, asignaciones efectivas, reglas dinámicas con operadores tipados y mappings; probado con 100 000 usuarios | Importaciones masivas | Lista (Fases 1 y 4, F12, escala en F15) |
| Políticas de acceso | Motor versionado con usuario, grupo, red, horario, riesgo, assurance y simulación explicable; señales adaptables y step-up con passkey; aplicado también en OAuth y SAML | — | Lista (Fases 1 y 2, F4, F13) |
| Autenticadores | Password, TOTP, email OTP, magic link, backup codes, passkeys passwordless y step-up de sesión; inscripción guiada al iniciar sesión, portal de autoservicio y restablecimiento de MFA por soporte | — | Lista (Fase 2, F10) |
| Federación | Proveedores OIDC y SAML configurables, routing por dominio/usuario y JIT controlado, conectados al login hospedado y a `/oauth/authorize`, con prueba de conexión y consola; AuthCenter también es IdP SAML para aplicaciones | Catálogo de proveedores preconfigurados | Lista (F8, F13) |
| Lifecycle | SCIM 2.0 Users/Groups completo (descubrimiento, PUT, PATCH de Entra ID/Okta, ETag, diagnóstico por token), tokens acotados, deprovisioning, mappings y reglas dinámicas | Importaciones masivas y conectores | Lista (Fase 4, F12) |
| System Log | Auditoría consultable correlacionada con W3C trace IDs, outbox y event hooks con métricas/dead-letter | Exportación analítica de largo plazo según retención corporativa | Fase 6 lista |
| Experiencia administrativa | Consola admin completa (concurrencia optimista, idempotencia, System Log, Event Hooks, gobierno), portal de usuario y login hospedado accesibles (axe con la regla WCAG 2.2 AA en las 39 rutas de la consola, cada paso del login, el portal y los enlaces de correo; reflow a 320 px), con branding y consentimiento revocable; runbooks de soporte | Custom domains administrados | Lista (F10, F11, F15); falta la revisión manual con lectores de pantalla (UI-07, propietario) |
| Plataforma para desarrolladores | Discovery/JWKS, SDKs .NET (net8/net10) y TypeScript con prueba de contrato contra el servidor, quickstarts (BFF, API, SPA) y perfil de conformidad OIDC/SCIM; paquetes y workflow de publicación listos | Paquetes publicados en NuGet/npm | Lista salvo publicar: credenciales y licencia del propietario (OPS-14) |
| Operación | OpenTelemetry/Azure Monitor, SLO/burn rate, capacity/soak, scripts DR y runbooks además de CI/CD/Key Vault/health; readiness del esquema, script de migraciones por commit, compuertas de cobertura y bundle, Dependabot y prueba de escala semanal | Failover regional cuando se apruebe una segunda región | Lista (Fase 6, F15); pendientes del propietario OPS-02 y OPS-04 a OPS-07 |
| Gobierno | RBAC por aplicación, owners por aplicación, solicitudes de acceso con aprobación, revisiones periódicas con recurrencia y segregación de funciones preventiva y detectiva | Entitlements finos por recurso y flujos de aprobación de varios niveles | Lista (F14 de la remediación) |

## Fases de entrega

### Fase 0 — Fundación del authorization server

- [x] RS256, rotación de llaves y JWKS.
- [x] Clientes ligados a aplicaciones y access tokens con claims aislados.
- [x] Authorization Code con PKCE S256, state, nonce y redirects exactos.
- [x] Refresh-token families, reutilización y revocación.
- [x] Client Credentials y autenticación Basic/Post.
- [x] Pruebas de interoperabilidad y proveedores externos controlados.

### Fase 1 — Directorio, grupos y políticas

- [x] Crear grupos y membresías auditables, con asignación de grupos a aplicaciones y roles.
- [x] Añadir atributos de perfil extensibles con esquema, tipo, obligatoriedad y validación.
- [x] Implementar reglas ordenadas por aplicación con allow/deny y deny-by-default al existir reglas activas.
- [x] Evaluar condiciones por usuario, grupo, aplicación y rangos de red IPv4/IPv6.
- [x] Aplicar MFA obligatorio y controlar si un dispositivo confiable puede omitir el desafío.
- [x] Versionar borradores/publicaciones y evaluar horario, riesgo y assurance de autenticación.
- [x] Añadir endpoint de simulación y explicación de decisiones antes de publicar una política.
- [x] Invalidar sesiones/claims cuando cambien membresías, roles o políticas relevantes.

### Fase 2 — Passkeys y autenticación adaptable

- [x] Registrar, listar, renombrar y revocar credenciales WebAuthn/passkey.
- [x] Implementar login passwordless phishing-resistant con user verification obligatoria.
- [x] Implementar passkey como step-up de una sesión ya autenticada.
- [x] Aplicar enrollment y assurance desde el motor de políticas.
- [x] Proteger recuperación, cambio de factores y operaciones sensibles con reautenticación.
- [x] Incorporar señales de IP, dispositivo, geovelocidad y eventos anómalos sin almacenar datos
      innecesarios.

### Fase 3 — Federación empresarial

- [x] Proveedores OIDC configurables por aplicación/realm con discovery, JWKS y rotación.
- [x] SAML 2.0 inbound y outbound con metadata, certificados, replay protection y clock skew acotado.
- [x] Routing rules por dominio, aplicación, grupo y atributos.
- [x] JIT provisioning y account linking explícitos, configurables y auditados.

### Fase 4 — Lifecycle y automatización

- [x] SCIM 2.0 Users y Groups: filtros `eq`, paginación, PATCH, activación y deprovisioning.
- [x] Tokens de provisioning con scopes, rotación y expiración.
- [x] Profile mappings y fuentes de verdad configurables.
- [x] Group rules y asignación automática de aplicaciones/roles.
- [x] Event hooks HTTPS verificados, firmados, at-least-once, idempotentes y con dead-letter.

### Fase 5 — Consolas y experiencia del desarrollador

- [x] Consola administrativa accesible con separación clara de duties.
- [x] Portal de usuario para sesiones, factores, passkeys, dispositivos, consentimientos y apps.
- [x] Login hospedado con branding por aplicación y accesibilidad WCAG 2.2 AA.
- [x] SDKs/middleware de referencia para .NET y TypeScript, más quickstarts de SPA, web y API.
- [x] Suite automática de conformidad OIDC/SCIM y ejemplos ejecutables sin secretos.

### Fase 6 — Operación de plataforma

- [x] OpenTelemetry para traces, métricas y correlación con System Log sin datos sensibles.
- [x] SLO definidos para login, token, directory y hooks; alertas basadas en burn rate.
- [x] Pruebas de carga, soak, caos y recuperación documentadas y repetibles; el arnés de
  failover rechaza topologías incompletas y la ejecución regional queda condicionada a aprobar
  una segunda región.
- [x] Backups/restores y rotaciones de llaves probados con RTO/RPO registrados. Evidencia Azure
  del 2026-08-11: restore point-in-time verificado en 1,175.5 s (objetivo 1,800 s), base temporal
  eliminada; restart de App Service recuperado en 8.2 s (objetivo 300 s).
- [x] Runbooks de incidentes de credenciales, proveedor externo, correo, SQL y Key Vault.

## Decisiones de arquitectura

- La primera topología objetivo es una organización que centraliza varios proyectos hermanos. La
  frontera fuerte actual es `ApplicationSystem`; no se añadirá multi-tenancy SaaS hasta existir un
  caso real de organizaciones administradoras independientes.
- Se priorizan estándares abiertos (OIDC, OAuth, WebAuthn, SAML y SCIM) sobre integraciones
  propietarias.
- Las políticas y eventos serán datos versionados, no condicionales hardcodeados.
- Ningún cambio de política, directorio o factor puede depender de caché local para ser correcto en
  múltiples instancias.
- La UI no recibirá secretos administrativos ni decidirá autorización; consumirá APIs protegidas y
  mostrará explicaciones producidas por el motor de políticas.

## Referencias de alcance

- Okta Policies: https://developer.okta.com/docs/concepts/policies/
- Okta Universal Directory: https://developer.okta.com/docs/concepts/universal-directory/
- Passkeys/WebAuthn: https://developer.okta.com/docs/guides/authenticators-web-authn/main/
- External Identity Providers: https://developer.okta.com/docs/concepts/identity-providers/
- SCIM: https://developer.okta.com/docs/concepts/scim/
- Event Hooks: https://developer.okta.com/docs/concepts/event-hooks/
