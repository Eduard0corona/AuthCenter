# Runbooks operativos

Todos los comandos deben registrar incidente, operador, hora UTC y `traceId` relacionado. Nunca
copies tokens, cadenas de conexión, claves ni cuerpos de autenticación al ticket.

## Credencial o llave comprometida

1. Revoca sesiones/familias afectadas y desactiva el cliente o token de provisioning.
2. Crea una versión nueva en Key Vault; no edites el repositorio ni la configuración con el valor.
3. Para RS256 publica primero ambas claves públicas, después firma con la nueva y retira la vieja
   sólo tras la vida máxima de token. Verifica JWKS desde dos instancias.
4. Busca uso posterior a la revocación en System Log por acción y `traceId`.
5. Si hubo exposición en Git, rota primero y solicita autorización separada para reescribir historia.

## Proveedor OIDC/SAML externo

1. Desactiva únicamente el proveedor afectado; conserva password/passkey si la política lo permite.
2. Revisa discovery/JWKS, issuer, certificado, clock skew y últimos eventos sin descargar secretos.
3. Reactiva en una aplicación piloto, prueba JIT/linking y confirma que no se crea una cuenta
   duplicada antes del rollout general.

## Correo

1. Revisa profundidad/edad del outbox y salud del proveedor; no vuelques cuerpos del mensaje.
2. Pausa reintentos si el proveedor rechaza credenciales para evitar lockout.
3. Rota la credencial en Key Vault, restaura el dispatcher y confirma magic-link/OTP con cuenta de
   prueba sin informar si un correo real existe.

## Azure SQL

1. Liveness debe seguir 200; readiness y operaciones dependientes pueden degradarse.
2. Consulta throttling/failover de Azure y correlaciona 5xx por `traceId`.
3. Confirma que el retry de SqlClient recupera errores transitorios; no habilites migración al
   arranque como mitigación.
4. Si hay pérdida/corrupción, ejecuta `Invoke-AzureSqlRestoreDrill.ps1` con un nombre temporal
   acotado, valida y promueve sólo mediante un plan de cambio aprobado.

## Health checks de App Service

1. Configura `HealthChecks__ReadinessHost` con el hostname exacto del App Service; la aplicación no
   publica `/health/ready` para hosts distintos.
2. Configura `healthCheckPath=/health/ready` en Site Config para que Azure retire del balanceador
   instancias que no puedan atender dependencias. Conserva `/health/live` para distinguir proceso
   vivo de instancia lista.
3. Verifica ambos endpoints después de cada cambio de configuración. Nunca uses una URL de login,
   discovery o token como health probe.

```powershell
az webapp config appsettings set -g <resource-group> -n <app-name> `
  --settings "HealthChecks__ReadinessHost=<app-hostname>" -o none
az webapp config set -g <resource-group> -n <app-name> `
  --generic-configurations healthCheckPath=/health/ready -o none
```

## Key Vault

1. Consulta el estado de referencias de App Service; diferencia `AccessToKeyVaultDenied` de secreto
   ausente/versión deshabilitada.
2. Verifica identidad administrada y rol `Key Vault Secrets User` a nivel de secreto.
3. Restaura una versión válida o el permiso mínimo; nunca amplíes lectura al vault completo.
4. Reinicia sólo después de que todas las referencias indiquen `Resolved`.

## Event Hook atascado

1. Examina destino, intento, `LastError` acotado y estado dead-letter; no copies el payload.
2. Confirma HTTPS público y que DNS no resuelva a red privada.
3. Corrige el consumidor, usa replay explícito y deduplica por `X-AuthCenter-Event-Id`.

## Cierre

Documenta impacto, RTO/RPO real, presupuesto consumido, rotaciones y acciones preventivas. Cierra
sólo después de liveness/readiness, login/token sintéticos y ausencia de nuevos dead letters.
