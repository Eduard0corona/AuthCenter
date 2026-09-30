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
| OPS-15 | Dominio propio de AuthCenter (hecho el 2026-09-27: `authcenter.info`) | Operador de App Service |
| OPS-16 | Alta de Paquetenvia (aplicación, cliente OAuth y secreto) | Administrador de AuthCenter con MFA; Azure CLI con acceso al Key Vault de Paquetenvia |
| OPS-17 | Correo saliente de AuthCenter (SMTP) | Cuenta del proveedor de correo, DNS de `authcenter.info`, operador de App Service |
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

## OPS-15 · Dominio propio de AuthCenter

Hecho el 2026-09-27: AuthCenter responde en `https://authcenter.info`. Para otro dominio, repite los
pasos con su nombre.

1. Vincula el dominio y su certificado al App Service (*Custom domains*).
2. En *Environment variables* del App Service, guarda:

   | Variable | Valor |
   |---|---|
   | `AllowedHosts` | `authcenter.info;<app>.azurewebsites.net` |
   | `Jwt__Issuer` | `https://authcenter.info` |
   | `Oidc__PublicOrigin` | `https://authcenter.info` |
   | `ActionLinks__DefaultBaseUrl` | `https://authcenter.info` |
   | `Passkeys__RelyingPartyId` | `authcenter.info` |
   | `Passkeys__AllowedOrigins__0` | `https://authcenter.info` |

   En `AllowedHosts` va el hostname real del App Service (el de `AZURE_WEBAPP_NAME`), porque la
   verificación del despliegue entra por él. Un host que no esté en la lista recibe
   `HTTP Error 400. The request hostname is invalid.` antes de llegar a AuthCenter.
3. Espera a que la app se reinicie con los valores nuevos.
- **Comprobación:** `https://authcenter.info/.well-known/openid-configuration` responde 200 con
  `"issuer": "https://authcenter.info"` y todos los endpoints en ese dominio, y
  `https://<app>.azurewebsites.net/health/live` sigue en 200. El job `deploy` repite esta
  comprobación en cada despliegue.
- **Efectos:** los usuarios inician sesión de nuevo en el dominio nuevo y las passkeys registradas
  para el host anterior dejan de servir. También cambian el entity ID SAML por defecto
  (`{Jwt:Issuer}/saml/idp/metadata`) y el callback de federación
  (`{Oidc:PublicOrigin}/api/federation/oidc/callback`), que deben actualizar las aplicaciones SAML y
  los proveedores ya registrados.

## OPS-16 · Alta de Paquetenvia

`scripts/ops/Register-Paquetenvia.ps1` hace el alta que pide Paquetenvia en
`docs/development/auth-001-authcenter-bff.md` (§10 y §10.1):

- **Aplicación `PAQUETENVIA`:** registro abierto, contraseña y enlace mágico. Exige confirmar el
  correo y no exige MFA a todos: los roles privilegiados de Paquetenvia suben de nivel con
  `acr_values`.
- **Cliente confidencial `paquetenvia-web-prod`:**
  - `authorization_code` y `refresh_token`, con PKCE;
  - scopes `openid profile email offline_access`;
  - redirect URI `https://paquetenvia.com/signin-authcenter`;
  - post-logout `https://paquetenvia.com/login`;
  - back-channel logout `https://paquetenvia.com/auth/backchannel-logout`, con `sid`;
  - `LoginUrl` `https://authcenter.info/login` y consentimiento automático.
- **Secreto del cliente:** va directo al secreto `authcenter-paquetenvia-client-secret` del Key Vault
  del piloto, sin mostrarse ni escribirse en otro lado.

La confirmación de correo no es opcional. Paquetenvia vincula las membresías pendientes al correo
de un ID token con `email_verified=true`. Si la aplicación no exige confirmación, AuthCenter
registra la cuenta como verificada sin comprobar nada, y quien se registre con el correo de otra
persona se queda con sus membresías.

Requisitos: OPS-04 hecho (administrador con app de autenticación), PowerShell 7.2 o posterior y la
CLI de Azure con `az login` en la suscripción de Paquetenvia. En el Key Vault del piloto necesitas el
rol `Key Vault Secrets Officer` y permiso para cambiar sus reglas de red. El vault niega el tráfico
público: el script agrega una regla temporal para tu IP y la quita al terminar, igual que
`deploy/azure/pilot/kv-firewall.sh`.

1. En PowerShell 7, desde la raíz del repositorio:
   ```powershell
   ./scripts/ops/Register-Paquetenvia.ps1 -AdminEmail <tu-correo> -GrantAdminAccess
   ```
   Pide tu contraseña y el código de la app de autenticación, muestra el plan y pide confirmación.
   - `-WhatIf` sólo muestra el plan.
   - `-GrantAdminAccess` da acceso a Paquetenvia a tu cuenta: el registro abierto sólo da acceso a
     las cuentas nuevas.
   - Busca el vault en `rg-pv-pilot`. Con `-KeyVaultName` usas otro; con `-Subscription`, otra
     suscripción.
2. Reinicia la API de Paquetenvia (la revisión activa de su Container App) para que lea el secreto.

Puedes volver a ejecutarlo: sólo corrige lo que difiera del alta. Rota el secreto si falta en Key
Vault, si ya no autentica al cliente o si pasas `-RotateSecret`. Una aplicación o un cliente
desactivados se reactivan a mano, en la consola. Para el ambiente dev, cuando exista:
`-Environment Dev -KeyVaultName <vault de dev>` registra `paquetenvia-web-dev` con
`https://dev.paquetenvia.com`.

- **Comprobación:** el script termina con `Done: the secret in <vault>/authcenter-paquetenvia-client-secret authenticates paquetenvia-web-prod`.
  Esa línea sale después de probar el secreto guardado contra `/oauth/introspect`. Después de
  reiniciar la API, "Iniciar sesión" en `https://paquetenvia.com/login` lleva al login de
  AuthCenter y regresa con sesión.
- **Cuentas nuevas:** las personas sin cuenta usan "Crear cuenta" en ese mismo login, confirman su
  correo y regresan a Paquetenvia.
- **Depende de OPS-17:** sin correo saliente, las cuentas nuevas no reciben la confirmación ni el
  enlace mágico, y no pueden entrar.

## OPS-17 · Correo saliente de AuthCenter

Sin SMTP, AuthCenter encola los correos pero no puede enviarlos: se agotan los reintentos y quedan
como dead letters. Eso afecta confirmaciones de correo, enlaces mágicos, restablecimientos de
contraseña, invitaciones, códigos por correo y avisos de seguridad. La configuración de ejemplo no
trae servidor, y hasta ahora el despliegue no documentaba uno.

1. Contrata un proveedor SMTP, por ejemplo Azure Communication Services Email, SendGrid o Mailgun.
   Verifica en él el dominio `authcenter.info` y publica en su DNS los registros SPF, DKIM y DMARC
   que indique. Sin ellos, los correos llegan a spam o se rechazan.
2. Guarda la contraseña o clave SMTP en el Key Vault de AuthCenter y da a la identidad del App
   Service `Key Vault Secrets User` sobre ese secreto, como con las demás referencias.
3. En *Environment variables* del App Service:

   | Variable | Valor |
   |---|---|
   | `Email__Host` | servidor SMTP del proveedor |
   | `Email__Port` | `587` |
   | `Email__EnableSsl` | `true` |
   | `Email__UserName` | usuario SMTP del proveedor |
   | `Email__Password` | `@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/<secreto>)` |
   | `Email__FromAddress` | una dirección del dominio verificado, por ejemplo `no-reply@authcenter.info` |
   | `Email__FromName` | `AuthCenter` |

   Usa el puerto 587 (STARTTLS). El cliente SMTP de .NET no habla TLS implícito, así que el puerto
   465 no funciona.
4. Espera a que la app se reinicie y a que la referencia de Key Vault indique `Resolved`.
- **Comprobación:** en `https://authcenter.info/login`, "¿Olvidaste tu contraseña?" con tu cuenta
  entrega el correo, y su enlace abre `https://authcenter.info`. El log registra
  `Password reset email sent` y no aparecen dead letters nuevos en el outbox.

## UI-07 · Revisión manual con lectores de pantalla

Las pruebas automáticas (axe, con la regla WCAG 2.2 AA `target-size`, en las 39 rutas de la consola,
cada paso del login hospedado, los paneles del portal y las páginas de enlaces de correo; reflow a
320 px; teclado y diálogos) no sustituyen una revisión humana. Con NVDA + Firefox o Chrome, y VoiceOver + Safari:

1. Login hospedado completo (contraseña, segundo factor, error de credenciales, enlace por correo).
2. Portal: seguridad, sesiones, solicitar acceso y aprobaciones.
3. Consola: navegación, un listado con filtros, un editor con errores de validación, un diálogo de
   confirmación y el System Log.
- Registra hallazgos como defectos con la ruta y el lector usado.
