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
