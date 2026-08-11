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

## Matriz de capacidades

| Área | Estado actual | Objetivo de producto | Estado |
|---|---|---|---|
| OAuth 2.0 / OIDC | Authorization Code + PKCE, Client Credentials, refresh, JWKS y revocación | Interoperabilidad completa, PAR, Device Authorization, introspección y token exchange cuando exista un consumidor real | Fundación lista |
| Directorio universal | Usuarios, grupos, membresías, perfiles tipados y asignaciones efectivas | Reglas dinámicas de membresía y mappings en Fase 4 | Fase 1 lista |
| Políticas de acceso | Motor versionado con usuario, grupo, red, horario, riesgo, assurance y simulación explicable | Señales adaptables y passkey step-up en Fase 2 | Fase 1 lista |
| Autenticadores | Password, TOTP, email OTP, magic link, backup codes, passkeys passwordless y step-up de sesión | Portal de autoservicio y recuperación asistida en Fase 5 | Fase 2 lista |
| Federación | Proveedores OIDC y SAML configurables, routing por dominio/usuario y JIT controlado | Catálogo y UX administrativa en Fase 5 | Fase 3 lista |
| Lifecycle | SCIM 2.0 Users/Groups, tokens acotados, deprovisioning, mappings y reglas dinámicas | Importaciones masivas y conectores en Fase 5 | Fase 4 lista |
| System Log | Auditoría consultable correlacionada con W3C trace IDs, outbox y event hooks con métricas/dead-letter | Exportación analítica de largo plazo según retención corporativa | Fase 6 lista |
| Experiencia administrativa | Consola admin, portal de usuario y login hospedado accesibles, con branding y consentimiento revocable | Custom domains administrados | Fase 5 lista |
| Plataforma para desarrolladores | Discovery/JWKS, SDKs .NET/TypeScript, quickstarts y perfil de conformidad OIDC/SCIM | Publicación automatizada de paquetes cuando exista un registry organizacional | Fase 5 lista |
| Operación | OpenTelemetry/Azure Monitor, SLO/burn rate, capacity/soak, scripts DR y runbooks además de CI/CD/Key Vault/health | Failover regional cuando se apruebe una segunda región | Fase 6 lista |
| Gobierno | RBAC por aplicación | Entitlements, owners, solicitudes, revisiones periódicas y segregación de funciones | Pendiente |

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
