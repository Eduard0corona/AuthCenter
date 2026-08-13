# Plan de implementacion del frontend administrativo de AuthCenter

> Estado del documento: implementacion activa en `feat/admin-access-policies`, actualizada el
> 2026-08-13. Las casillas marcadas cuentan con codigo y evidencia automatizada local; Azure sigue
> pendiente hasta integrar la rama en `main`.
>
> Este documento distingue deliberadamente entre lo que existe, los defectos confirmados, las
> dependencias de backend y el objetivo de producto. Una casilla solo debe marcarse como lista
> cuando exista codigo, pruebas y evidencia de funcionamiento.

## 1. Objetivo

Construir una consola administrativa de nivel productivo para AuthCenter que permita operar las
capacidades de identidad ya disponibles en el backend con una experiencia segura, accesible,
escalable y auditable.

El objetivo no es replicar visualmente a Okta. El objetivo es alcanzar un nivel comparable de:

- cobertura de flujos administrativos;
- proteccion de operaciones sensibles;
- claridad operativa y explicacion de decisiones;
- accesibilidad WCAG 2.2 AA verificable;
- rendimiento predecible con directorios grandes;
- pruebas automatizadas y despliegues sin assets obsoletos.

## 2. Alcance

Incluye:

- consola administrativa nueva hospedada en `/admin-v2` durante la migracion progresiva, con
  reemplazo de `/admin` solo despues de alcanzar paridad y evidencia de produccion;
- shell, navegacion, componentes y design system;
- integracion con las APIs administrativas de AuthCenter;
- cambios de backend indispensables para operar la UI de forma segura;
- pruebas unitarias, de componentes, integracion y E2E;
- observabilidad, rendimiento, accesibilidad y CI/CD del frontend;
- migracion gradual desde el frontend vanilla actual.

No incluye inicialmente:

- multi-tenancy SaaS entre organizaciones independientes;
- aplicacion movil nativa;
- custom domains administrados;
- gobierno avanzado de entitlements, revisiones periodicas o segregacion de funciones, que sigue
  siendo una iniciativa de producto separada;
- reemplazo de los protocolos OIDC, OAuth, SAML, SCIM o WebAuthn ya implementados.

## 3. Resumen ejecutivo

El backend de AuthCenter contiene capacidades de directorio, RBAC, politicas, OAuth/OIDC,
federacion, lifecycle, SCIM, passkeys, auditoria y Event Hooks. La consola actual solo expone una
fraccion pequena de esas capacidades.

La UI desplegada es un MVP de cinco secciones:

1. dashboard con conteos basicos;
2. usuarios con activacion/desactivacion;
3. aplicaciones con edicion parcial de branding;
4. System Log con los primeros 50 resultados;
5. entregas de Event Hooks con replay de dead letters.

La declaracion `Fase 5 lista` de `OKTA-LEVEL-ROADMAP.md` debe interpretarse actualmente como
"MVP disponible". No debe considerarse una consola administrativa terminada hasta cerrar este
plan y producir evidencia E2E, de accesibilidad y de operacion.

## 4. Evidencia revisada

### 4.1 Implementacion actual

| Archivo | Responsabilidad | Tamano aproximado |
|---|---|---:|
| `src/AuthCenter.Api/wwwroot/admin.html` | Shell y cinco paneles administrativos | 26 lineas |
| `src/AuthCenter.Api/wwwroot/assets/admin.js` | Carga y mutaciones de la consola | 122 lineas |
| `src/AuthCenter.Api/wwwroot/assets/shared.js` | Cliente HTTP, CSRF, sesion y utilidades | 103 lineas |
| `src/AuthCenter.Api/wwwroot/assets/app.css` | Estilos compartidos de login, portal y admin | 80 lineas |

Se comprobo que los cuatro recursos desplegados en Azure coinciden con los archivos de `main`
despues de normalizar saltos de linea y decodificarlos como UTF-8.

### 4.2 Limitacion de la revision visual

No habia una instancia de navegador conectada durante la auditoria. Por tanto:

- se verificaron codigo, contratos, cabeceras HTTP y assets desplegados;
- no se afirma que exista validacion pixel-perfect;
- no se probaron lector de pantalla, navegacion real por teclado, zoom, contraste renderizado ni
  breakpoints en dispositivos fisicos;
- esas validaciones forman parte obligatoria del plan de pruebas.

### 4.3 Evidencia de la primera entrega

- ADR `docs/adr/0001-admin-frontend-stack.md` y scaffold estricto en `src/AuthCenter.Admin`.
- `npm run lint`, `npm run typecheck`, `npm test` y `npm run build` exitosos.
- Siete pruebas unitarias/de componente exitosas; validacion del editor y preservacion de URLs de branding incluidas.
- Doce escenarios Playwright exitosos en Chromium desktop, tablet 768x1024 y emulacion Pixel 7; axe cubre shell, rutas y dialog de branding, y el acceso read-only se prueba sin controles de escritura.
- Bundle actual: 132.10 KB gzip de JavaScript emitido en chunks y 4.70 KB gzip de CSS; las rutas y el editor de branding se cargan bajo demanda.
- Cuatro pruebas de integracion verifican redirect, deep link, `no-store` e assets `immutable`.
- Suite .NET completa exitosa: 65 pruebas unitarias y 125 pruebas de integracion.
- Entrega RBAC: 65 pruebas unitarias, 128 de integracion, 9 frontend y 18 escenarios E2E; roles, permisos, matriz atomica y rol predeterminado cubiertos.
- Bundle actual tras RBAC: 141.08 KB gzip de JavaScript emitido en chunks y 4.81 KB gzip de CSS; cada modulo conserva carga diferida.
- Entrega de grupos: 65 pruebas unitarias, 131 de integracion, 10 frontend y 24 escenarios E2E; alta, consulta, edicion, activacion, membresias paginadas y reemplazo atomico de acceso heredado cubiertos.
- `SuperAdmin` no puede heredarse desde grupos; las mutaciones de acceso revocan sesiones afectadas dentro de la misma transaccion.
- Bundle actual tras grupos: 24 chunks JavaScript con carga diferida (el editor de grupos pesa 4.13 KB gzip) y 4.89 KB gzip de CSS.
- Entrega de detalle de usuario: 65 pruebas unitarias, 135 de integracion, 11 frontend y 33 escenarios E2E; origen directo/heredado, perfil universal, acceso atomico, step-up administrativo e invariante del ultimo `SuperAdmin` cubiertos.
- Bundle actual tras detalle de usuario: el editor se mantiene como chunk diferido de 5.63 KB gzip y el CSS total en 5.09 KB gzip.
- Entrega de alta e invitacion: 65 pruebas unitarias, 139 de integracion, 13 frontend y 42 escenarios E2E; contraseña temporal generada en memoria, cambio obligatorio en primer acceso, asignacion inicial por aplicacion/rol, invitacion sin exponer tokens y orden estable del listado cubiertos.
- Bundle actual tras alta e invitacion: el modulo nuevo se mantiene como chunk diferido de 3.44 KB gzip y el CSS total permanece en 5.09 KB gzip.
- Entrega de OAuth clients: 65 pruebas unitarias, 140 de integracion, 19 frontend y 45 escenarios E2E; CRUD, filtros, redirects exactos, grants/scopes, secretos de un solo uso, step-up y auditoria sin secretos cubiertos.
- Bundle actual tras OAuth clients: listado y editor permanecen como chunks diferidos de 1.75 KB y 5.03 KB gzip; el CSS total es 5.14 KB gzip.
- Entrega de politicas de acceso: 65 pruebas unitarias, 140 de integracion, 22 frontend y 48 escenarios E2E; drafts/versiones inmutables, reglas ordenadas, condiciones completas, simulacion explicable, diff, fallback de `AUTHCENTER`, step-up y revocacion de sesiones cubiertos.
- Bundle actual tras politicas: listado y editor permanecen como chunks diferidos de 0.97 KB y 6.49 KB gzip; el CSS total es 5.37 KB gzip.
- La inspeccion visual manual con navegador integrado sigue pendiente porque no habia una instancia
  disponible; la evidencia automatizada no se presenta como sustituto de esa revision.

## 5. Capacidades actuales que deben conservarse

- [x] Cookie de sesion `HttpOnly`, `Secure` y `SameSite=Strict`.
- [x] Proteccion CSRF para escrituras autenticadas por cookie.
- [x] CSP sin scripts inline y `frame-ancestors 'none'`.
- [x] Reautorizacion de cada API en el servidor; la UI no es la frontera de seguridad.
- [x] Navegacion y acciones condicionadas por permisos efectivos.
- [x] Insercion de datos remotos mediante `textContent`, sin `innerHTML` dinamico.
- [x] Skip link, foco visible, regiones semanticas y mensajes `aria-live` basicos.
- [x] Layout responsive basico y tablas con desplazamiento horizontal.
- [x] Cierre de sesion y redireccion al login cuando la carga inicial recibe `401`.
- [x] Assets y rutas de UI servidos con MIME correcto en Azure.

## 6. Hallazgos y deuda confirmada

### 6.1 P0: integridad, seguridad y continuidad administrativa

#### FE-001: el editor de branding elimina enlaces existentes

**Estado:** corregido en `/admin-v2`; pendiente validar el artefacto desplegado en Azure.

`admin.js` siempre envia `privacyUrl: null` y `termsUrl: null`. Editar solo el nombre o un color
puede borrar esos valores existentes.

- [x] Agregar campos de privacidad y terminos al formulario.
- [x] Inicializar todos los campos desde el DTO actual.
- [x] Enviar el estado completo sin reemplazar datos no editados por `null`.
- [x] Agregar preview de logo, colores y enlaces.
- [x] Validar contraste WCAG antes de guardar.
- [x] Cubrir la conservacion de todos los campos con una prueba E2E.

#### BE-001: no existe invariante del ultimo SuperAdmin

**Estado:** corregido para asignaciones directas y operaciones administrativas; pendiente prueba
concurrente multioperacion.

El backend permite desactivar o eliminar usuarios y retirar roles sin comprobar si el objetivo es
el operador actual o el ultimo administrador efectivo.

- [x] Impedir en transaccion serializable desactivar o eliminar al ultimo `AUTHCENTER:SuperAdmin` activo.
- [x] Impedir retirar el ultimo rol o acceso que conserva administracion efectiva.
- [x] Requerir asignacion directa y atribuible de `SuperAdmin`; el rol no puede heredarse por grupos.
- [x] Devolver `LAST_SUPER_ADMIN` y `SYSTEM_ROLE_ASSIGNMENT_FORBIDDEN` como codigos estables.
- [ ] Agregar pruebas concurrentes para evitar que dos operaciones eliminen simultaneamente a los
  dos ultimos administradores.
- [x] Mostrar en UI el error accionable retornado por la API.

#### BE-002: auditoria administrativa incompleta

**Estado:** pendiente.

Los servicios de usuarios, aplicaciones, roles y permisos no integran de forma consistente
`IAuditService`. Acciones realizadas desde la consola pueden no aparecer en System Log.

- [x] Auditar altas, ediciones, invitaciones, accesos, roles, activaciones, desactivaciones y eliminaciones de usuarios.
- [ ] Auditar grants/revokes de aplicaciones, roles y permisos.
- [ ] Auditar cambios de configuracion y branding de aplicaciones.
- [ ] Registrar actor, objetivo, aplicacion, resultado, trace ID y metadata no sensible.
- [ ] No registrar passwords, tokens, secretos, assertion payloads ni PII innecesaria.
- [ ] Probar eventos positivos, fallidos y rechazados.

#### SEC-001: operaciones sensibles sin step-up administrativo

**Estado:** pendiente.

La autorizacion por permiso es correcta, pero las operaciones de mayor impacto no requieren
reautenticacion reciente.

- [ ] Definir una matriz de operaciones que requieren password/passkey step-up.
- [ ] Exigir prueba de reautenticacion en el backend, no solo en el componente visual.
- [ ] Incluir como minimo: eliminar usuario, reset MFA, retirar SuperAdmin, rotar secretos,
  publicar politicas, cambiar federacion y revocar provisioning tokens.
- [ ] Usar pruebas de un solo uso, con proposito y expiracion corta.
- [ ] Mostrar el dialogo de step-up sin almacenar la prueba en storage persistente.

### 6.2 P1: funcionalidad y experiencia

#### FE-002: listados truncados

Usuarios, aplicaciones y System Log estan fijados a `page=1&pageSize=50`.

- [x] Agregar paginacion controlada por servidor.
- [x] Mostrar total, pagina, rango y selector de tamano.
- [x] Agregar busqueda con debounce y cancelacion de requests.
- [x] Implementar filtros soportados por cada API.
- [x] Mantener filtros y pagina en la URL.
- [x] Definir estados de carga, vacio, error y retry por panel.

#### FE-003: mutaciones con UX insuficiente

- [x] Confirmar acciones destructivas con nombre y alcance del recurso.
- [x] Deshabilitar el boton mientras la operacion esta en curso.
- [x] Prevenir doble submit.
- [x] Mostrar exito o error junto a la accion afectada.
- [x] Capturar errores en activar/desactivar y replay; actualmente pueden terminar como promesas
  rechazadas sin feedback consistente.
- [x] Preservar el foco y anunciar el resultado a tecnologia asistiva.

#### FE-004: cache largo sin assets versionados

Los assets no HTML usan `Cache-Control: public,max-age=86400`, pero conservan nombres estables como
`admin.js` y `app.css`. Un navegador puede usar codigo antiguo hasta 24 horas despues de un deploy.

- [x] Generar bundles con hash de contenido.
- [x] Servir assets hasheados con `immutable`.
- [x] Servir el documento HTML con `no-store` o revalidacion obligatoria.
- [x] Agregar un smoke post-deploy que confirme que el HTML referencia un asset hasheado existente.
- [x] Definir recuperacion cuando HTML y assets pertenezcan a versiones distintas: rollback del
  artefacto conjunto y consola estable `/admin` durante la migracion.

#### FE-005: manejo incompleto de expiracion de sesion

- [x] Centralizar el manejo de `401` en el cliente HTTP.
- [x] Redirigir al login conservando un `return_url` seguro.
- [x] Diferenciar `401`, `403`, validacion, conflicto y error transitorio.
- [x] Evitar multiples redirecciones cuando fallen requests paralelos.

#### FE-006: navegacion no restaurable

- [x] Inicializar el panel desde la ruta o hash actual.
- [x] Soportar atras/adelante del navegador.
- [x] Crear rutas de detalle enlazables para usuarios, aplicaciones, grupos y politicas.
- [x] Crear rutas de detalle enlazables para aplicaciones y grupos.
- [x] Mover el foco al encabezado del panel al navegar.
- [x] Implementar breadcrumb en la vista de detalle de aplicaciones; se reutilizara en las siguientes vistas.

#### FE-007: carga inicial excesiva

La consola solicita en paralelo usuarios, aplicaciones, auditoria y hasta 200 entregas aunque el
operador no visite esas secciones.

- [x] Cargar cada modulo al navegar hacia el.
- [x] Cancelar requests obsoletos.
- [x] Cachear server state con invalidacion por recurso.
- [ ] Usar paginacion en entregas en lugar de un `Take(200)` fijo.
- [x] Evitar recargar listados completos despues de cada mutacion.

#### FE-008: evidencia de calidad insuficiente

Las pruebas actuales no ejecutan `admin.js`, no recorren flujos reales ni ejecutan un motor de
accesibilidad.

- [x] Agregar unit tests para utilidades y validadores.
- [x] Agregar component tests para formularios, tablas, permisos y estados.
- [x] Agregar baseline E2E con Playwright para shell y navegacion responsive.
- [x] Ejecutar axe-core sobre las rutas cubiertas por el baseline.
- [ ] Verificar teclado, foco, zoom 200/400 %, reduced motion y contraste.
- [x] Probar desktop, tablet y mobile en CI.

## 7. Matriz de cobertura funcional

| Dominio | Backend actual | UI actual | Implementacion requerida |
|---|---|---|---|
| Usuarios | CRUD, invitaciones, accesos, roles, MFA, password y borrado | Lista y activar/desactivar | Gestion completa y vista de detalle |
| Aplicaciones | CRUD, estado, registro y branding | Lista, alta, detalle/edicion, estado, registro y branding completo | Selector de rol predeterminado al implementar roles |
| Roles | CRUD y permisos | Ausente | Matriz RBAC y asignaciones |
| Permisos | CRUD y estado | Ausente | Catalogo por aplicacion y dependencias |
| Grupos | CRUD, miembros, apps y roles | Ausente | Directorio de grupos y asignaciones |
| Perfil universal | Esquemas y valores | Ausente | Editor de esquema y perfil de usuario |
| OAuth clients | CRUD y rotacion de secret | Ausente | Clientes, redirects, grants, scopes y secret reveal |
| Politicas de acceso | Versiones, reglas, publish y simulacion | Ausente | Editor visual, diff, simulacion y explicacion |
| Federacion | Proveedores OIDC/SAML y routing | Ausente | Catalogo, formularios, metadata y pruebas |
| Lifecycle | Mappings y reglas dinamicas | Ausente | Listado, edicion, simulacion y estado |
| SCIM/provisioning | Tokens y endpoints SCIM | Ausente | Tokens, scopes, expiracion y rotacion |
| Event Hooks | Create/verify/delete, deliveries y replay | Solo deliveries/replay | Gestion completa del hook y diagnostico |
| System Log | Consulta filtrada y correlacion | Primeros 50 eventos | Filtros, detalle, trace, exportacion y deep links |
| Dashboard | Datos disponibles en multiples fuentes | Tres conteos | Read model operativo y seguridad agregada |

## 8. Dependencias de backend para completar la UI

### 8.1 APIs que faltan o son incompletas

- [ ] Provisioning tokens: listar metadata no sensible, estado, scopes y expiracion.
- [ ] Event Hooks: listar, obtener detalle y actualizar hooks.
- [ ] Profile mappings: listar, obtener, actualizar, validar y simular.
- [ ] Group rules: listar, obtener, actualizar y ejecutar preview.
- [ ] Federation routing rules: listar, actualizar, ordenar, activar y eliminar.
- [ ] Dashboard: endpoint agregado para indicadores operativos y de postura de seguridad.
- [ ] Event deliveries: paginacion, filtros por hook, estado, evento y rango de fecha.
- [ ] System Log: exportacion asincrona o paginada con limites y auditoria.
- [ ] Errores: correlation/trace ID publico para soporte sin exponer detalles internos.

### 8.2 Contratos transversales

- [ ] Concurrencia optimista para ediciones administrativas (`ETag`/`If-Match` o version explicita).
- [ ] Codigos de error estables y catalogados.
- [ ] Idempotency keys para mutaciones que puedan reintentarse.
- [ ] Respuestas paginadas consistentes.
- [ ] Ordenamiento y filtros declarados en OpenAPI.
- [ ] Permisos requeridos publicados por operacion.
- [ ] Auditoria y reautenticacion integradas de forma uniforme.

## 9. Arquitectura objetivo

### 9.1 Decision propuesta

Crear una SPA administrativa en `src/AuthCenter.Admin` con:

- React y TypeScript en modo `strict`;
- Vite para build, code splitting y assets hasheados;
- React Router para rutas y deep links;
- TanStack Query para server state, cache, cancelacion e invalidacion;
- React Hook Form y esquemas de validacion tipados;
- cliente TypeScript generado desde OpenAPI;
- Vitest y Testing Library;
- Playwright y axe-core para E2E/accesibilidad.

La seleccion final de dependencias debe registrarse en un ADR antes de iniciar el scaffold. Se debe
mantener un presupuesto pequeno de dependencias y revisar advisories en CI.

### 9.2 Reglas de seguridad frontend

- La UI nunca decide autorizacion.
- No almacenar access tokens, refresh tokens, secretos o pruebas de reautenticacion en
  `localStorage`, `sessionStorage`, IndexedDB o logs.
- Mantener autenticacion por cookie segura y CSRF.
- Mostrar secretos de OAuth/SCIM una sola vez y mantenerlos solo en memoria.
- No incluir PII ni cuerpos sensibles en telemetria.
- Conservar CSP sin `unsafe-inline` ni `unsafe-eval`.
- Todo link externo configurable debe ser HTTPS y abrirse con protecciones apropiadas.

### 9.3 Estructura sugerida

```text
src/AuthCenter.Admin/
  src/
    app/                 # bootstrap, router, providers y layout
    api/                 # cliente generado, fetch/CSRF y errores
    components/          # design system y componentes compartidos
    features/
      dashboard/
      users/
      groups/
      applications/
      roles/
      policies/
      oauth-clients/
      federation/
      lifecycle/
      event-hooks/
      system-log/
    security/            # permission gates y step-up
    observability/       # errores y correlacion sanitizada
    test/                # fixtures, MSW y utilidades
```

### 9.4 Arquitectura de navegacion

```text
Overview
Directory
  Users
  Groups
  Profile schemas
Applications
  Applications
  OAuth clients
Security
  Roles and permissions
  Access policies
Federation
Lifecycle
  Provisioning tokens
  Profile mappings
  Group rules
Workflows
  Event hooks
  Deliveries
System Log
```

Cada entrada y accion debe usar permisos explicitos. Una ruta sin permiso debe devolver una vista
`403` comprensible, mientras el backend conserva la decision definitiva.

## 10. Requisitos por modulo

### 10.1 Usuarios

- [x] Tabla con busqueda, filtros, orden estable y seleccionable, paginacion y estado.
- [x] Crear usuario con password temporal segura.
- [x] Invitar usuario sin exponer tokens de invitacion.
- [x] Vista de detalle con perfil universal, roles, grupos y aplicaciones.
- [x] Aprobar o revocar acceso pendiente.
- [x] Asignar y retirar aplicaciones y roles directos mostrando cada origen heredado.
- [x] Activar/desactivar con impacto de sesiones explicado.
- [x] Forzar cambio de password y revocar sesiones.
- [x] Reset administrativo de MFA con step-up de un solo uso.
- [x] Borrado/anonymizacion con step-up y proteccion de ultimo administrador.

### 10.2 Aplicaciones y branding

- [x] Crear y editar aplicacion.
- [x] Configurar modos Closed, Open, InviteOnly y ApprovalRequired.
- [x] Configurar password, magic link, proveedores externos, email confirmation y MFA.
- [x] Configurar dominios permitidos.
- [x] Seleccionar un rol predeterminado activo de la misma aplicacion; el backend rechaza referencias cruzadas.
- [x] Activar/desactivar con proteccion especial para `AUTHCENTER`.
- [x] Editar todos los campos de branding sin perdida de datos.
- [x] Preview responsive y verificacion de contraste.

### 10.3 Roles, permisos y grupos

- [x] Alta, consulta, edicion y estado de roles y permisos; no hay endpoint de borrado y no se simula en UI.
- [x] Matriz rol-permiso por aplicacion con reemplazo atomico y proteccion de roles de sistema.
- [x] Diferenciar asignaciones directas y heredadas, incluyendo grupo de origen y estado efectivo.
- [x] Alta, consulta, edicion y estado de grupos, con gestion paginada de miembros; no hay endpoint de borrado y no se simula en UI.
- [x] Asignar aplicaciones y roles a grupos mediante reemplazo atomico.
- [x] Preview del impacto sobre miembros y acceso heredado antes de confirmar.

### 10.4 Politicas de acceso

- [x] Historial de versiones y estado draft/published.
- [x] Editor de reglas ordenadas.
- [x] Condiciones por usuario, grupo, IP, horario, riesgo y assurance.
- [x] Acciones allow, deny y require MFA.
- [x] Simulacion explicable antes de publicar.
- [x] Diff de version y confirmacion con step-up.
- [x] Impedir dejar `AUTHCENTER` sin fallback permitido.

### 10.5 OAuth y provisioning secrets

- [x] CRUD de OAuth clients, redirects exactos, grants y scopes.
- [x] Mostrar client secret solo en respuesta de create/rotate.
- [x] Boton de copia con aviso y cierre explicito del secret reveal.
- [ ] CRUD de provisioning tokens con scopes y expiracion.
- [x] Rotacion/revocacion con step-up y auditoria.
- [x] No permitir recuperar el valor original de un secreto.

### 10.6 Federacion y lifecycle

- [ ] Catalogo de proveedores OIDC/SAML.
- [ ] Formularios discriminados por protocolo.
- [ ] Metadata, certificados y estado de validacion sin exponer secretos.
- [ ] Routing rules ordenables y simulables.
- [ ] Profile mappings con preview de transformacion.
- [ ] Group rules con preview de miembros afectados.
- [ ] Estado y diagnostico de integraciones SCIM.

### 10.7 Event Hooks y System Log

- [ ] CRUD y verificacion de Event Hooks.
- [ ] Seleccion de tipos de evento y aplicacion.
- [ ] Entregas paginadas con estado, intento, error y proximo retry.
- [ ] Replay con confirmacion e idempotencia.
- [ ] System Log con filtros por accion, usuario, app, trace y fechas.
- [ ] Drawer de detalle con metadata sanitizada.
- [ ] Deep links desde una entidad hacia sus eventos relacionados.
- [ ] Exportacion con limites, progreso y auditoria.

## 11. Accesibilidad y design system

### 11.1 Criterios obligatorios

- [ ] WCAG 2.2 AA verificado, no solo declarado.
- [ ] Navegacion completa por teclado.
- [ ] Orden de foco predecible y restauracion al cerrar dialogs.
- [x] Contraste AA para colores configurables.
- [ ] Zoom 200 % y reflow a 400 % sin perdida funcional.
- [x] Estados que no dependan exclusivamente del color.
- [x] Labels, descripciones, errores y ayudas asociados semanticamente en los modulos migrados.
- [x] Tablas con captions, encabezados y desplazamiento responsive enfocable.
- [x] `aria-live` para feedback de mutaciones sin anuncios duplicados.
- [x] Compatibilidad con `prefers-reduced-motion`.
- [ ] Textos en UTF-8 e internacionalizacion preparada.

### 11.2 Componentes minimos

- App shell, sidebar responsive, breadcrumb y page header.
- Button, link button, icon button y loading button.
- Text field, select, combobox, checkbox, radio y date range.
- Form field con error y ayuda.
- Modal/alert dialog y step-up dialog.
- Data table con paginacion, filtros, orden y seleccion.
- Badge, alert, toast, empty state, skeleton y error state.
- Drawer de detalle.
- Secret reveal de una sola visualizacion.
- Permission gate y forbidden state.

## 12. Rendimiento y escalabilidad

### 12.1 Presupuestos iniciales

- JavaScript inicial del shell: objetivo menor a 250 KB gzip.
- CSS inicial: objetivo menor a 50 KB gzip.
- Carga diferida por modulo.
- INP objetivo menor a 200 ms en equipo representativo.
- LCP objetivo menor a 2.5 s en condiciones de red acordadas.
- Ningun listado administrativo puede depender de descargar el directorio completo.

### 12.2 Controles

- [x] Code splitting por ruta.
- [x] Assets con hash y compresion Brotli/Gzip.
- [x] Paginacion y filtros del lado servidor en usuarios, aplicaciones y System Log; usuarios ya
  publica ordenamiento por nombre, correo, alta y ultimo acceso, mientras aplicaciones y System Log
  conservan su orden fijo hasta ampliar sus contratos.
- [x] Debounce y `AbortController` para busquedas.
- [x] Invalidacion selectiva despues de mutaciones.
- [ ] Virtualizacion solo cuando el diseno realmente lo requiera.
- [ ] Lighthouse CI con budgets, sin tratarlo como sustituto de pruebas reales.

## 13. Observabilidad frontend

- [ ] Capturar errores no controlados sin cuerpo de requests ni PII.
- [ ] Propagar y mostrar correlation/trace ID en errores soportables.
- [ ] Medir navegacion, latencia y fallos por operacion, no por datos del usuario.
- [x] Diferenciar error de validacion, autorizacion, conflicto y disponibilidad.
- [ ] Registrar version del frontend y commit desplegado.
- [ ] Dashboard operativo para errores frontend y degradacion de endpoints.
- [ ] Source maps privados y restringidos al pipeline de diagnostico.

## 14. Estrategia de pruebas

### 14.1 Piramide

- Unitarias: validadores, mappers, permisos, cliente HTTP y utilidades.
- Componentes: formularios, tablas, dialogs y estados usando MSW.
- Integracion: contratos OpenAPI y composicion de features.
- E2E: flujos criticos contra un ambiente aislado con SQL real.
- Accesibilidad: axe-core mas validacion manual documentada.
- Seguridad: CSRF, CSP, permisos, step-up, session expiry y secret handling.

### 14.2 Flujos E2E minimos

- [ ] Login y entrada a la consola segun permisos.
- [ ] Usuario read-only no ve ni puede ejecutar escrituras.
- [ ] Invitar, aprobar, asignar rol/aplicacion y desactivar usuario.
- [ ] Bloqueo del ultimo SuperAdmin.
- [x] Crear aplicacion y editar branding sin perder URLs.
- [x] Crear roles/permisos, actualizar matriz RBAC y asignar rol predeterminado valido.
- [x] Crear y rotar OAuth client mostrando el secreto una sola vez.
- [x] Crear draft, simular y publicar politica con step-up.
- [ ] Crear/verificar hook y reproducir un dead letter.
- [ ] Filtrar System Log por trace ID.
- [ ] Expiracion de sesion durante una mutacion.
- [ ] Navegacion por teclado en rutas y dialogs principales.
- [x] Responsive en resoluciones mobile, tablet y desktop.

## 15. CI/CD y publicacion

El pipeline debe ejecutar, en orden:

1. instalacion reproducible con lockfile;
2. lint y formato;
3. TypeScript typecheck;
4. unit/component tests con cobertura;
5. build de produccion con budgets;
6. auditoria de dependencias;
7. build y pruebas .NET;
8. E2E contra ambiente efimero o controlado;
9. empaquetado conjunto con assets hasheados;
10. deploy con OIDC a Azure;
11. smoke de health, login, portal, admin y version del frontend.

- [x] Fallar el deploy si HTML referencia un asset inexistente.
- [x] Confirmar `Content-Type`, CSP, compresion y politica de cache post-deploy.
- [ ] Publicar manifest de version/commit sin secretos.
- [x] Mantener rollback a un artefacto frontend-backend compatible y conservar `/admin` durante la
  migracion.

## 16. Plan de entrega

### Fase A: seguridad y fundacion

- [ ] Corregir FE-001 a FE-008.
- [ ] Implementar BE-001, BE-002 y SEC-001.
- [x] Registrar ADR de stack frontend.
- [x] Crear scaffold TypeScript, design system y app shell.
- [ ] Implementar cliente OpenAPI, sesion, CSRF y errores.
- [x] Integrar build hasheado al publish de .NET.
- [x] Crear baseline Playwright/axe y CI.

**Salida:** shell productivo, branding seguro, navegacion, cache correcto y gates de calidad.

### Fase B: directorio y aplicaciones

- [x] Usuarios e invitaciones, incluyendo contraseña temporal de cambio obligatorio e invitacion sin exponer tokens.
- [x] Aplicaciones y branding completo.
- [x] Roles, permisos y matriz de asignaciones; asignaciones a usuarios/grupos siguen en sus historias correspondientes.
- [x] Grupos y membresias, incluyendo preview de impacto, acceso heredado atomico y revocacion de sesiones.
- [x] Perfil universal tipado y editable desde el detalle de usuario.

**Salida:** operacion diaria del directorio sin depender de llamadas manuales a la API.

### Fase C: politicas e integraciones

- [x] OAuth clients.
- [x] Access policies y simulacion.
- [ ] Federacion OIDC/SAML.
- [ ] Provisioning tokens y SCIM.
- [ ] Mappings y group rules.
- [ ] Event Hooks completos.

**Salida:** integraciones y seguridad avanzada operables desde la consola.

### Fase D: operacion y refinamiento AAA

- [ ] Dashboard operativo y de seguridad.
- [ ] System Log completo.
- [ ] Observabilidad frontend.
- [ ] Validacion manual WCAG y matriz de navegadores.
- [ ] Pruebas de rendimiento y directorios grandes.
- [ ] Documentacion de operador, soporte y runbooks UI.
- [ ] Actualizar `OKTA-LEVEL-ROADMAP.md` con evidencia verificable.

**Salida:** consola administrable, observable, accesible y preparada para crecimiento.

## 17. Definition of Done

Una historia o modulo solo puede marcarse listo cuando:

- [ ] el flujo positivo esta implementado;
- [ ] permisos, validaciones y flujos negativos estan probados;
- [ ] las operaciones sensibles usan step-up cuando corresponde;
- [ ] cada escritura produce auditoria sin datos sensibles;
- [ ] existen estados loading, empty, error, success y forbidden;
- [ ] teclado, foco, contraste y responsive fueron verificados;
- [ ] unit/component/E2E relevantes pasan en CI;
- [ ] no existen secretos ni tokens persistidos por el frontend;
- [ ] rendimiento permanece dentro de los budgets;
- [ ] documentacion y runbook fueron actualizados;
- [ ] el comportamiento fue validado en Azure despues del deploy.

## 18. Riesgos y mitigaciones

| Riesgo | Mitigacion |
|---|---|
| Reescritura grande y prolongada | Migracion por rutas/features y entregas verticales pequenas |
| Duplicar reglas de negocio en UI | Cliente OpenAPI; backend como autoridad; UI solo validacion de experiencia |
| Dependencias frontend vulnerables | Lockfile, audit CI, Renovate/Dependabot y presupuesto de dependencias |
| Exposicion de secretos | One-time reveal, memoria solamente, redaccion y E2E negativos |
| Perder acceso administrativo | Invariante transaccional de ultimo SuperAdmin |
| Assets incompatibles tras deploy | Hashes, manifest de version, smoke y rollback de artefacto completo |
| Roadmap marcado listo prematuramente | Definition of Done y evidencia enlazada desde cada fase |

## 19. Decisiones pendientes antes de implementar

- [x] Aprobar React/TypeScript/Vite o registrar una alternativa en ADR.
- [x] Decidir migracion progresiva bajo `/admin-v2` o reemplazo por modulo.
- [ ] Definir entorno E2E y estrategia de datos de prueba.
- [ ] Aprobar matriz de step-up administrativo.
- [ ] Aprobar budgets de rendimiento y navegadores soportados.
- [ ] Definir retencion y mecanismo de exportacion de System Log.
- [ ] Decidir si la administracion de custom domains entra en una fase posterior.

## 20. Criterio para cerrar la Fase 5 del roadmap

La Fase 5 solo debe volver a marcarse completamente lista cuando:

1. las fases A a D de este documento esten cerradas o las exclusiones restantes esten registradas
   explicitamente como alcance posterior;
2. todos los modulos administrativos soportados por el backend sean operables desde UI;
3. la auditoria y la proteccion del ultimo administrador esten verificadas;
4. la suite E2E y axe pase en CI;
5. exista evidencia visual manual desktop/mobile y WCAG 2.2 AA;
6. el artefacto desplegado en Azure haya superado smoke, version y cache checks.
