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
| Directorio universal | Usuarios, aplicaciones, roles y permisos fijos | Grupos, perfiles/esquemas extensibles, atributos y reglas de membresía | Pendiente |
| Políticas de acceso | Configuración básica por aplicación | Motor versionado de reglas por aplicación, grupo, red, horario, riesgo y assurance; simulación antes de publicar | Pendiente |
| Autenticadores | Password, TOTP, email OTP, magic link y backup codes | Passkeys/WebAuthn phishing-resistant, enrollment policy, recuperación y step-up | Pendiente |
| Federación | Google, Microsoft, GitHub y Apple implementados directamente | Proveedores OIDC y SAML configurables, routing por dominio/usuario y JIT controlado | Pendiente |
| Lifecycle | CRUD e invitaciones administrativas | SCIM 2.0, importación, deprovisioning, grupos y profile mappings | Pendiente |
| System Log | Auditoría consultable y outbox durable para correo | Catálogo estable de eventos, correlación, exportación, event hooks firmados y reintentos observables | Pendiente |
| Experiencia administrativa | API y Swagger | Consola admin, portal de usuario, consentimiento, branding y custom domains | Pendiente |
| Plataforma para desarrolladores | Discovery, JWKS y documentación HTTP | SDKs, middleware de referencia, widget/login hospedado, quickstarts y pruebas de conformidad | Pendiente |
| Operación | CI/CD, Key Vault, health, limpieza y rate limiting distribuido | Métricas/SLO, tracing, alertas, capacity tests, DR probado y runbooks automáticos | Parcial |
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
- [ ] Añadir atributos de perfil extensibles con esquema, tipo, obligatoriedad y validación.
- [ ] Implementar políticas versionadas y ordenadas con deny-by-default.
- [ ] Evaluar reglas por usuario, grupo, aplicación, red, horario y contexto de autenticación.
- [ ] Añadir endpoint de simulación y explicación de decisiones antes de publicar una política.
- [ ] Invalidar sesiones/claims cuando cambien membresías, roles o políticas relevantes.

### Fase 2 — Passkeys y autenticación adaptable

- [ ] Registrar, listar, renombrar y revocar credenciales WebAuthn/passkey.
- [ ] Implementar login passwordless y passkey como step-up phishing-resistant.
- [ ] Aplicar enrollment y assurance desde el motor de políticas.
- [ ] Proteger recuperación, cambio de factores y operaciones sensibles con reautenticación.
- [ ] Incorporar señales de IP, dispositivo, geovelocidad y eventos anómalos sin almacenar datos
      innecesarios.

### Fase 3 — Federación empresarial

- [ ] Proveedores OIDC configurables por aplicación/realm con discovery, JWKS y rotación.
- [ ] SAML 2.0 inbound y outbound con metadata, certificados, replay protection y clock skew acotado.
- [ ] Routing rules por dominio, aplicación, grupo y atributos.
- [ ] JIT provisioning y account linking explícitos, configurables y auditados.

### Fase 4 — Lifecycle y automatización

- [ ] SCIM 2.0 Users y Groups: filtros `eq`, paginación, PATCH, activación y deprovisioning.
- [ ] Tokens de provisioning con scopes, rotación y expiración.
- [ ] Profile mappings y fuentes de verdad configurables.
- [ ] Group rules y asignación automática de aplicaciones/roles.
- [ ] Event hooks HTTPS verificados, firmados, at-least-once, idempotentes y con dead-letter.

### Fase 5 — Consolas y experiencia del desarrollador

- [ ] Consola administrativa accesible con separación clara de duties.
- [ ] Portal de usuario para sesiones, factores, passkeys, dispositivos, consentimientos y apps.
- [ ] Login hospedado con branding por aplicación y accesibilidad WCAG 2.2 AA.
- [ ] SDKs/middleware de referencia para .NET y TypeScript, más quickstarts de SPA, web y API.
- [ ] Suite automática de conformidad OIDC/SCIM y ejemplos ejecutables sin secretos.

### Fase 6 — Operación de plataforma

- [ ] OpenTelemetry para traces, métricas y correlación con System Log sin datos sensibles.
- [ ] SLO definidos para login, token, directory y hooks; alertas basadas en burn rate.
- [ ] Pruebas de carga, soak, caos, failover y recuperación documentadas y repetibles.
- [ ] Backups/restores y rotaciones de llaves probados con RTO/RPO registrados.
- [ ] Runbooks de incidentes de credenciales, proveedor externo, correo, SQL y Key Vault.

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
