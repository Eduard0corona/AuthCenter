# Remediación integral de arquitectura, seguridad, performance y escalabilidad

Registro vivo de la remediación realizada en la rama
`fix/comprehensive-security-architecture-hardening`. Todos los puntos detectados quedaron
implementados y verificados. Las casillas se marcaron únicamente después de contar con validación
automatizada o una comprobación equivalente.

## Seguridad crítica

- [x] Aplicar realmente el bloqueo de cuenta durante el login. Se usa `SignInManager` con lockout y existe una regresión de cinco intentos fallidos.
- [x] Dejar de confiar en encabezados reenviados de proxies desconocidos. La característica está deshabilitada por defecto y exige una lista exacta de proxies al activarse.
- [x] Sustituir callbacks suministrados por el cliente por URLs configuradas por aplicación. Los campos heredados se conservan por compatibilidad, pero se ignoran.
- [x] Codificar de forma segura todo contenido dinámico insertado en correos HTML.
- [x] Hacer de un solo uso, de forma atómica, authorization codes, refresh tokens y backup codes MFA mediante tokens de concurrencia y transacciones.
- [x] Impedir que usuarios inactivos o eliminados renueven tokens OAuth o consulten UserInfo.
- [x] Validar issuer y tenant exactos de Microsoft en modo multitenant.
- [x] Dejar de usar `preferred_username` como identidad o dirección verificada.
- [x] Exigir `email_verified=true` en identidades de Apple.
- [x] Eliminar el enlace automático de identidades externas por email y ofrecer un endpoint autenticado de vinculación explícita.
- [x] Migrar TOTP a AES-GCM autenticado con formato versionado, identificador de llave, llaves anteriores y lector de transición AES-CBC.
- [x] Aplicar revocación inmediata de access tokens de primera parte mediante el claim `sid` y comprobación de la sesión en cada autenticación.
- [x] Aplicar filtro global de exclusión para cuentas eliminadas y revocar sesiones al desactivar, eliminar, retirar acceso o modificar roles.

## Consistencia y arquitectura

- [x] Ejecutar registro local/externo, creación e invitación de usuarios, accesos y roles como unidades atómicas sobre proveedores relacionales.
- [x] Evitar fallos silenciosos de auditoría: se registran con nivel de error y se propagan al llamador.
- [x] Introducir outbox durable y protegido con Data Protection para correos, con claim multiinstancia, backoff y reintentos.
- [x] Separar responsabilidades del servicio de autenticación: emisión de sesiones, vínculos externos, enlaces de acción, estado efímero, gestión de cuenta y entrega de efectos tienen servicios dedicados.
- [x] Restringir el proveedor dinámico de policies al catálogo explícito de permisos; policies desconocidas vuelven al proveedor estándar.
- [x] Resolver roles por aplicación: `Name` es ahora canónico (`APP:rol`) y `DisplayName` conserva el contrato y los claims visibles.

## Escalabilidad y resiliencia

- [x] Compartir el key ring de ASP.NET Data Protection en SQL Server y exigir protección por certificado PKCS#12 fuera de desarrollo/pruebas.
- [x] Sustituir el rate limiting local por buckets SQL serializables compartidos; el limitador en memoria queda sólo para desarrollo/pruebas.
- [x] Añadir limpieza periódica por lotes de refresh tokens, authorization codes, estados transitorios, dispositivos, auditoría, outbox y buckets expirados.
- [x] Serializar migración y seed multi-réplica mediante `sp_getapplock` y mantenerlos opt-in fuera de desarrollo.
- [x] Separar liveness de readiness; readiness sólo se publica cuando se configura un host de administración.

## Performance

- [x] Convertir la búsqueda de usuarios a prefijos indexables sobre `FullName` y `NormalizedEmail`.
- [x] Reemplazar revocaciones masivas cargadas en memoria por `ExecuteUpdateAsync` en bases relacionales.
- [x] Paginar el listado de clientes OAuth con límite máximo de 100 elementos.
- [x] Eliminar el sink de archivos locales; los contenedores escriben logs estructurados a consola.
- [x] Aplicar pooling tanto al `DbContext` solicitado por request como a su factory.

## Configuración, protocolo y cadena de suministro

- [x] Validar issuer, audience, llave RSA y rangos de expiración JWT/MFA al arrancar.
- [x] Exigir issuer OIDC y origen público HTTPS fuera de desarrollo/pruebas.
- [x] Añadir `Cache-Control: no-store` y `Pragma: no-cache` a respuestas de autenticación/token.
- [x] Restringir `AllowedHosts` y construir discovery exclusivamente desde `Oidc:PublicOrigin`, no desde `Host`.
- [x] Migrar solución, imágenes y CI de .NET 9 fuera de soporte a .NET 10 LTS y actualizar todos los paquetes directos disponibles.
- [x] Endurecer GitHub Actions con permisos mínimos, timeouts, concurrencia, auditoría de dependencias y acciones fijadas por SHA.
- [x] Sustituir el perfil de publicación permanente de Azure por federación OIDC, limitar la identidad a `Website Contributor` sobre el App Service y corregir el artefacto de despliegue para el runtime Windows de `authcenter`.

## Pruebas y verificación

- [x] Añadir regresiones para lockout, callbacks controlados, no-cache, usuarios eliminados e identidades externas explícitas.
- [x] Probar concurrencia de authorization codes, refresh tokens y backup codes.
- [x] Ejecutar migraciones y pruebas relacionales reales contra SQL Server/LocalDB.
- [x] Probar Data Protection y rate limiting compartidos entre instancias/contextos distintos.
- [x] Ejecutar build Release sin advertencias: **0 errores, 0 advertencias**.
- [x] Ejecutar pruebas: **47 unitarias + 83 de integración/relacionales = 130 aprobadas**.
- [x] Verificar paquetes vulnerables: **ninguna vulnerabilidad conocida**.
- [x] Verificar paquetes desactualizados: **ninguna actualización directa disponible**.

## Cambios de operación requeridos

Antes de desplegar esta versión se deben aplicar las migraciones nuevas y configurar:

- `Jwt:Issuer` y `Oidc:PublicOrigin` como URLs HTTPS públicas.
- `AllowedHosts` con los hosts exactos y `ActionLinks:DefaultBaseUrl` con el frontend permitido.
- `DataProtection:ApplicationName` idéntico en todas las réplicas y un certificado PKCS#12 compartido.
- `RateLimiting:DistributedEnabled=true` fuera de desarrollo/pruebas.
- `ForwardedHeaders:KnownProxies` sólo cuando exista un reverse proxy conocido.
- `HealthChecks:ReadinessHost` únicamente en la interfaz/host de administración si se desea readiness.

Las migraciones agregan las tablas `DataProtectionKeys`, `DistributedRateLimitBuckets` y
`OutboxMessages`, además de separar el nombre visible e interno de los roles. La validación
relacional aplicó la cadena completa de migraciones sobre una base nueva y la eliminó al terminar.
