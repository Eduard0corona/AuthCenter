# Acciones del propietario

Procedimientos para los puntos de `REMEDIACION-INTEGRACION-FEDERACION.md` marcados `[P]`: dependen
de cuentas, permisos o decisiones que el código no puede tomar (GitHub, Azure, datos reales,
licencia). Cada uno dice qué hacer, dónde y cómo comprobar que quedó hecho. Registra en el ticket
de cambio quién lo hizo y cuándo (UTC); nunca pegues secretos, cadenas de conexión ni tokens.

| Punto | Acción | Requiere |
|---|---|---|
| OPS-01 | Reactivar la ejecución de GitHub Actions (hecho el 2026-09-26) | Administrador de la organización / facturación |
| OPS-02 | Proteger `main` | Administrador del repositorio |
| OPS-03 | Aplicar las migraciones pendientes en Azure SQL (hecho el 2026-09-26: 35 migraciones) | Administrador Microsoft Entra de la base |
| OPS-04 | Carga inicial productiva (primer administrador) | Operador con acceso a App Service y Key Vault |
| OPS-05 | Primera rotación de la llave de firma | Operador con acceso a Key Vault |
| OPS-06 | Purgar del historial la llave RSA retirada | Autorización expresa: reescribe el historial |
| OPS-07 | Origen real del frontend en CORS y ActionLinks | Operador de App Service |
| OPS-10 | Borrar ramas remotas ya integradas | Autorización expresa: borra ramas |
| OPS-14 | Publicar los SDK | Cuentas NuGet/npm, entorno protegido, licencia |
| UI-07 | Revisión manual con lectores de pantalla | Persona con NVDA/JAWS/VoiceOver |

## OPS-01 · GitHub Actions no ejecuta

**Hecho:** el 2026-09-26 el PR #23 pasó el workflow `CI/CD` completo. Si vuelve a ocurrir: el run 69
de `main` quedó sin runner ni logs, síntoma de minutos agotados, límite de gasto en cero o Actions
deshabilitado.

1. *Settings → Billing and plans → Plans and usage* de la cuenta u organización dueña: revisa los
   minutos de Actions consumidos y el *spending limit* (un límite de 0 USD detiene los jobs de
   repositorios privados al agotar los minutos incluidos).
2. *Repositorio → Settings → Actions → General*: "Allow all actions" o la lista permitida debe
   incluir `actions/*`, `azure/*` y los pines SHA del workflow.
3. Vuelve a ejecutar `CI/CD` desde *Actions → CI/CD → Run workflow* sobre `main`.
4. **Comprobación:** el job `build-and-test` muestra logs y termina en verde; el despliegue corre
   sólo en `main`.

## OPS-02 · Protección de `main`

*Settings → Rules → Rulesets → New branch ruleset* (o *Branches → Add rule* en planes sin rulesets):

- Objetivo: `main`. Bloquear *force pushes* y borrado.
- *Require a pull request before merging*: 1 aprobación, descartar aprobaciones al hacer push,
  resolver conversaciones.
- *Require status checks to pass*: `build-and-test` (del workflow `CI/CD`), con la rama al día.
- Opcional: *Require signed commits* y un `CODEOWNERS` para `src/AuthCenter.Infrastructure/Persistence/Migrations/`.
- **Comprobación:** un push directo a `main` es rechazado; un PR sin CI verde no puede fusionarse.

## OPS-03 · Migraciones en Azure SQL

La identidad de la aplicación sólo tiene `db_datareader`, `db_datawriter` y `EXECUTE`: las
migraciones se aplican fuera de banda, **antes** de desplegar el código que las necesita. Desde esta
remediación, `/health/ready` responde 503 mientras falte alguna (`database-schema`), así que App
Service no enruta tráfico a una instancia con el esquema atrasado.

1. Descarga el artefacto `database-migrations` del run de `CI/CD` del commit a desplegar, o genera
   el mismo script idempotente (aplica sólo las migraciones que falten; la última del repositorio
   al cerrar la remediación es `20260926170104_AddAccessGovernance`):
   ```bash
   dotnet tool restore
   dotnet ef migrations script --idempotent --project src/AuthCenter.Infrastructure \
     --startup-project src/AuthCenter.Api --output migrations.sql
   ```
2. Revisa el script (sólo crea/alter/inserta lo de las migraciones nuevas) y ejecútalo con el
   administrador Entra de la base (Azure Data Studio, SSMS o `sqlcmd -G -I`) dentro de una ventana
   de cambio. `sqlcmd` sin `-I` empieza con `QUOTED_IDENTIFIER OFF` y SQL Server rechaza los
   cambios en tablas con índices filtrados: el script del artefacto ya lo fija, uno generado a mano
   no. Si se interrumpe, vuelve a ejecutarlo: sólo aplica lo que falte.
3. Despliega el código.
4. **Comprobación:** `SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId` termina en la
   última migración del repositorio y `GET /health/ready` (host configurado en
   `HealthChecks:ReadinessHost`) responde 200 con `database-schema: Healthy`.

## OPS-04 · Carga inicial productiva

1. En App Service agrega `Database__SeedOnStartup=true` y, como referencias de Key Vault de un solo
   uso, `Seed__AdminEmail`, `Seed__AdminPassword` (generada, 24+ caracteres) y `Seed__AdminFullName`.
2. Con la aplicación escalada temporalmente a una instancia, reiníciala y verifica en el log de
   arranque `Admin user ... created and assigned SuperAdmin role`.
3. **Quita** las cuatro variables, reinicia, restablece el número de instancias y borra los secretos
   de Key Vault.
4. Inicia sesión en `/login`, cambia la contraseña, activa MFA (app de autenticación y códigos de
   respaldo) y registra una passkey. Crea un segundo SuperAdmin nominal para no depender de una sola cuenta.
5. **Comprobación:** el log de arranque del paso 2 muestra la creación (el seed no escribe en System
   Log), System Log registra el primer `LOGIN_SUCCESS` del administrador y ningún `Seed__*` queda en
   la configuración.

## OPS-05 · Primera rotación de la llave de firma

Sigue *Rotating the signing key* del README: tres despliegues (publicar, promover, retirar).

1. Genera la llave nueva (RSA 3072) y guárdala como secreto PEM en Key Vault; agrega su PEM
   **público** a `Jwt__AdditionalValidationKeysPem__0`. Despliega. Comprueba que
   `/.well-known/jwks.json` publica dos `kid`.
2. Promueve: la llave nueva en `Jwt__RsaPrivateKeyPem` y la pública anterior en
   `Jwt__AdditionalValidationKeysPem__0`. Despliega. Comprueba con un login que el `kid` del token es el nuevo.
3. Al menos 24 horas después del paso 2, retira la anterior de
   `Jwt__AdditionalValidationKeysPem__0`. Despliega. Deshabilita la versión anterior del secreto.
   El plazo lo fijan los ID tokens: un BFF manda el de su sesión como `id_token_hint` al cerrar
   sesión mientras la sesión dure (hasta 24 h con `AuthCenter.Client`; 8 h en Paquetenvia), y
   AuthCenter lo acepta vencido, pero no si su llave ya no está. Si retiras antes, esos cierres de
   sesión fallan con un error y la sesión de AuthCenter sigue abierta. Si una SPA guarda ID tokens
   más tiempo, espera ese tiempo. El access token (60 min como máximo) y los tokens de un solo uso
   del login vencen antes. Si la llave anterior se filtró, retírala de inmediato y acepta esos
   fallos.
- **Comprobación:** los tokens emitidos antes del paso 2 siguen validando hasta que expiran; el SDK
  (`AuthCenter.Client`) no registra errores `IDX10503`.

## OPS-06 · Purgar la llave RSA retirada del historial (autorización expresa)

La llave está retirada, no se usa en ningún entorno y no está en el árbol actual: la frontera de
seguridad ya es la rotación. Purgarla del historial **reescribe todos los commits** posteriores y
obliga a cada colaborador a volver a clonar; GitHub puede conservar copias en caché y en forks.

1. Coordina una ventana sin PR abiertos (o rebásalos después) y avisa a los colaboradores.
2. En un clon espejo (`git clone --mirror`), arma `expresiones.txt` con las líneas base64 de la
   llave retirada tal como aparecen en los commits antiguos de
   `src/AuthCenter.Api/appsettings.Development.json` (una por línea, terminadas en
   `==>***REMOVED***`) y ejecuta `git filter-repo --replace-text expresiones.txt`. No borres el
   archivo: sigue existiendo en el árbol actual sin la llave. Guarda `expresiones.txt` fuera del
   repositorio y destrúyelo al terminar.
3. Verifica con `gitleaks git` sobre el espejo que no queda rastro.
4. `git push --force --mirror` (requiere desactivar temporalmente la protección de `main`, OPS-02).
5. Pide a GitHub Support la purga de vistas en caché y de *pull request refs*.
- **Comprobación:** `gitleaks git` limpio en un clon nuevo; todos los clones locales recreados.

## OPS-07 · Origen real del frontend

Cuando exista el dominio del frontend (por ejemplo `https://app.example.com`):

1. Si es un frontend de primera parte que llama a `/api/*` con credenciales:
   `Cors__AllowedOrigins__1=https://app.example.com` (sin barra final). El índice 0 es el host de
   Azure configurado en el despliegue inicial: no lo reemplaces.
2. Si el frontend hospeda sus propias páginas de enlaces: `ActionLinks__ApplicationBaseUrls__<CODIGO>`
   con su origen; si no, deja `ActionLinks__DefaultBaseUrl` en el origen público de AuthCenter.
3. En su cliente OAuth: redirect URIs exactas, `PostLogoutRedirectUris` y, si es una SPA,
   `AllowedCorsOrigins` con el mismo origen.
4. **Comprobación:** una petición `OPTIONS` desde el origen nuevo a `/api/auth/me` recibe
   `Access-Control-Allow-Origin` con ese origen; un correo de restablecimiento apunta al dominio correcto.

## OPS-10 · Ramas remotas ya integradas (autorización expresa)

1. Lista las ramas fusionadas en `main`: `git fetch --prune && git branch -r --merged origin/main`.
2. Excluye `main`, las ramas de PR abiertos y las de despliegue.
3. Bórralas una por una: `git push origin --delete <rama>` (o desde *Branches* en GitHub, que permite
   restaurarlas durante un tiempo).
- **Comprobación:** `git branch -r --merged origin/main` sólo muestra `origin/main`.

## OPS-14 · Publicación de los SDK

1. Decide y agrega la licencia: archivo `LICENSE` en la raíz y `PackageLicenseExpression` en
   `sdk/dotnet/AuthCenter.Client/AuthCenter.Client.csproj` (el `package.json` del SDK TypeScript
   declara MIT; debe coincidir).
2. Reserva el ID `AuthCenter.Client` en nuget.org y crea la organización npm `authcenter`.
3. *Settings → Environments → New environment* `sdk-release` con revisores requeridos; agrega los
   secretos `NUGET_API_KEY` (clave con alcance sólo a `AuthCenter.Client`) y `NPM_TOKEN` (o
   configura *trusted publishing* de npm para el workflow `sdk-release.yml`).
4. Crea la etiqueta `sdk-v1.1.0` sobre el commit a publicar; el workflow comprueba que ambas versiones
   coinciden, prueba, empaqueta y publica tras la aprobación del entorno.
- **Comprobación:** `dotnet add package AuthCenter.Client --version 1.1.0` y
  `npm view @authcenter/client@1.1.0` resuelven; los paquetes muestran la licencia.

## UI-07 · Revisión manual con lectores de pantalla

Las pruebas automáticas (axe, con la regla WCAG 2.2 AA `target-size`, en las 39 rutas de la consola,
cada paso del login hospedado, los paneles del portal y las páginas de enlaces de correo; reflow a
320 px; teclado y diálogos) no sustituyen una revisión humana. Con NVDA + Firefox o Chrome, y VoiceOver + Safari:

1. Login hospedado completo (contraseña, segundo factor, error de credenciales, enlace por correo).
2. Portal: seguridad, sesiones, solicitar acceso y aprobaciones.
3. Consola: navegación, un listado con filtros, un editor con errores de validación, un diálogo de
   confirmación y el System Log.
- Registra hallazgos como defectos con la ruta y el lector usado.
