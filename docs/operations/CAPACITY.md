# Capacity, load, soak y chaos

`ops/load/authcenter.js` es un escenario k6 sin credenciales embebidas. El perfil público siempre
corre; login sólo se habilita con una cuenta sintética dedicada en variables de entorno.

```powershell
$env:AUTHCENTER_BASE_URL='https://identity.example.com'
k6 run ops/load/authcenter.js
```

Para capacity incrementa llegada en pasos de 15 minutos hasta que p95 o error rate exceda SLO;
detén antes de afectar usuarios. Para soak usa el 60% de ese límite durante 60 minutos y observa
CPU, memoria, conexiones SQL, lock time, outbox y hook backlog. Chaos se ejecuta únicamente en un
slot/entorno no productivo: restart del proceso, bloqueo temporal de egress del proveedor y
latencia SQL mediante proxy. Liveness debe permanecer independiente de SQL y readiness reflejar
la degradación.

## Directorio grande

Las lecturas administrativas que crecen con el directorio (usuarios, grupos, miembros, dashboard,
violaciones de segregación de funciones y revisiones de acceso) tienen una prueba de escala contra
SQL Server: `DirectoryScaleRelationalTests`. Siembra el directorio con
`ops/load/seed-large-directory.sql` (usuarios `scale-<n>@load.test` sin contraseña, un grupo por
cada 100 usuarios, tres membresías por usuario) y exige 2 s por lectura y 90 s para crear la
revisión de acceso de toda la aplicación. No corre en cada PR: el workflow `Directory scale` la
ejecuta los lunes y a demanda (20 000 usuarios por omisión) y publica la tabla en el resumen del run.

```bash
AUTHCENTER_RELATIONAL_TEST_CONNECTION="Server=...;TrustServerCertificate=true" \
AUTHCENTER_SCALE_TESTS=1 AUTHCENTER_SCALE_USERS=100000 \
dotnet test tests/AuthCenter.IntegrationTests --filter Category=Scale --logger "console;verbosity=detailed"
```

Medición de referencia (build Release, SQL Server 2022 en Docker en el mismo equipo, segunda
llamada de cada lectura; Azure SQL tendrá más latencia de red):

| Operación | 20 000 usuarios, 200 grupos | 100 000 usuarios, 1 000 grupos |
|---|---:|---:|
| Usuarios, primera página | 288 ms | 463 ms |
| Usuarios, última página | 347 ms | 612 ms |
| Usuarios, búsqueda por correo | 104 ms | 128 ms |
| Usuarios de la aplicación | 362 ms | 393 ms |
| Ficha de usuario | 16 ms | 12 ms |
| Grupos, búsqueda | 110 ms | 94 ms |
| Miembros de un grupo | 15 ms | 12 ms |
| Dashboard | 55 ms | 59 ms |
| Violaciones de segregación de funciones | 18 ms | 12 ms |
| Crear la revisión de acceso de la aplicación (snapshot) | 11,0 s | 43,9 s |
| Ítems de la revisión, primera página | 147 ms | 314 ms |

- La prueba encontró el snapshot de las revisiones cuadrático (usuarios × membresías): 18,2 s con
  20 000 usuarios. Ahora indexa por usuario y es lineal; lo que queda es insertar los ítems
  (≈0,44 ms por acceso con el lote por omisión de EF Core, que rinde mejor que lotes mayores).
  A ese ritmo, una sola aplicación con ~500 000 accesos se acercaría al límite de 230 s del
  front-end de App Service; por debajo de eso la creación cabe en una petición.
- El cierre de una campaña que revoca lo no revisado es lineal y corre en segundo plano en lotes de
  200, renovando el reclamo de la campaña en cada lote para que otra instancia no la tome a medias.
- `ops/load/directory.js` (k6) repite las lecturas contra un entorno **no productivo** sembrado con
  el mismo script, con el objetivo Directory/SCIM de `SLO.md` (p95 < 800 ms, error < 1%):

```bash
AUTHCENTER_BASE_URL=https://identity-staging.example.com AUTHCENTER_ADMIN_TOKEN=<token de un administrador> \
k6 run ops/load/directory.js
```

Nunca ejecutes el script de siembra en producción: crea usuarios con acceso a la aplicación indicada.
