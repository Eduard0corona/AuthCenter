# SLO y presupuesto de error

El contrato legible por herramientas está en `ops/slo/authcenter-slos.yaml`. Las métricas sólo
usan operación, resultado, banda de intento y entorno; quedan prohibidos usuario, correo, IP,
user-agent, token, URL cruda y query string.

| Servicio | Disponibilidad 30 días | p95 | Presupuesto mensual aproximado |
|---|---:|---:|---:|
| Login | 99.9% | 750 ms | 43.2 min |
| Token | 99.95% | 400 ms | 21.6 min |
| Directory/SCIM | 99.9% | 800 ms | 43.2 min |
| Event Hooks | 99.5% entregado en 5 min | n/a | 3.6 h |

Un 4xx esperado no consume disponibilidad; un 5xx, timeout o dead letter sí. Las alertas multi-
ventana evitan paginar por picos aislados: fast burn requiere 1h y 5m; slow burn requiere 6h y
30m. Si se consume 50% del presupuesto antes de la mitad del periodo, se congelan cambios no
relacionados con confiabilidad hasta recuperar tendencia.

## Consultas Azure Monitor

Los nombres exactos pueden adquirir el prefijo del exporter; verifica primero `customMetrics | distinct name`.

```kusto
customMetrics
| where name == "authcenter.operation.requests"
| extend operation=tostring(customDimensions["authcenter.operation"]), outcome=tostring(customDimensions["authcenter.outcome"])
| summarize total=sum(value), failures=sumif(value, outcome == "server_error") by operation, bin(timestamp, 5m)
| extend availability=1.0 - failures / total
```

```kusto
customMetrics
| where name == "authcenter.event_hook.deliveries"
| where tostring(customDimensions["authcenter.outcome"]) == "dead_letter"
| summarize deadLetters=sum(value) by bin(timestamp, 5m)
```

Antes de habilitar paging, ejecuta cada consulta contra 24 horas de datos, confirma volumen y
enlaza el Action Group de guardia. Las reglas equivalentes para un backend Prometheus están en
`ops/alerts/prometheus-rules.yaml`.
