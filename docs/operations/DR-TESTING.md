# Pruebas de resiliencia y recuperación

## Cadencia

- Mensual: smoke de discovery/JWKS, login sintético y Event Hook verificado.
- Trimestral: restart controlado de App Service y restore point-in-time a base temporal.
- Semestral: rotación RS256 en dos despliegues y ejercicio de indisponibilidad de proveedor.
- Anual: revisión de topología regional, RTO/RPO y capacity/soak de 60 minutos.

## Objetivos

- App Service restart RTO: 300 s.
- Azure SQL point-in-time restore RTO: 1,800 s.
- RPO de Azure SQL: 5 minutos o el valor contratado que Azure muestre para el tier activo.
- Ninguna prueba puede apuntar a una base restaurada cuyo nombre no empiece por
  `authcenter-drill-`; el script elimina esa base salvo `-KeepRestoredDatabase` explícito.

## Ejecución

Los scripts son dry-run sin `-Execute`/`-ExecuteRestart`, resuelven los recursos exactos antes de
mutar y requieren confirmación de PowerShell. Conserva la salida sin secretos como evidencia del
incidente/cambio.

```powershell
./scripts/ops/Test-AppServiceRecovery.ps1 -ResourceGroup authcenter -AppName authcenter `
  -BaseUrl https://identity.example.com -ExecuteRestart

./scripts/ops/Invoke-AzureSqlRestoreDrill.ps1 -ResourceGroup db-factorh -ServerName factorh `
  -DatabaseName AuthCenter -Execute
```

Una topología de una sola región no demuestra failover regional. Ese riesgo queda explícito: antes
de declarar RTO regional se debe aprovisionar réplica/failover group, ejecutar el cambio de endpoint
y registrar el resultado con `Test-AzureSqlFailoverGroup.ps1`. El script es dry-run por defecto,
resuelve ambos servidores y sólo ejecuta/failback con switches explícitos. Las pruebas de
restart/restore no se presentan como sustituto.

## Última evidencia controlada

El 2026-08-11 se ejecutaron los drills contra los recursos de producción existentes, sin desplegar
código de la rama:

- Azure SQL point-in-time restore: `Online` en 1,175.5 s, dentro del RTO de 1,800 s. La base
  temporal con prefijo `authcenter-drill-` se eliminó automáticamente.
- App Service restart: `/health/live` recuperó HTTP 200 en 8.2 s, dentro del RTO de 300 s.
- App Service readiness: el Health Check nativo quedó apuntando a `/health/ready`; liveness y
  readiness recuperaron HTTP 200 en 49.6 s después del cambio de Site Config.
- Failover regional: no ejecutado porque la topología actual es de una sola región; no se declara
  un RTO regional hasta contar con réplica y failover group aprobados.
