# ADR 0001: frontend administrativo React con migración progresiva

- Estado: aceptada; migración completada
- Fecha: 2026-08-11
- Actualización 2026-09-26: la consola React cubre todos los módulos de `/admin` (y los que le
  faltaban: Event Hooks, System Log completo, esquema de perfil, catálogo de APIs). `/admin` y
  `/admin.html` redirigen a `/admin-v2/`, sus archivos se eliminaron y la forma legacy
  `deadLettersOnly` de las entregas dejó de existir. Ver `REMEDIACION-INTEGRACION-FEDERACION.md`
  (UI-08).

## Contexto

La consola `/admin` existente es JavaScript sin build, navegación por paneles y cobertura parcial.
El plan `FRONTEND-ADMIN-IMPLEMENTATION-PLAN.md` requiere rutas enlazables, carga diferida, estado de
servidor, formularios tipados, assets inmutables y pruebas de componentes/E2E sin debilitar la
sesión por cookie ni CSRF.

## Decisión

Se crea `src/AuthCenter.Admin` con React 19, TypeScript estricto, Vite, React Router, TanStack Query,
React Hook Form y Zod. Vitest/Testing Library cubren lógica y componentes; Playwright y axe-core
forman el baseline E2E/accesible.

TypeScript queda fijado temporalmente en 6.0.3: al tomar la decisión, `typescript-eslint` 8.67.0
declara peer support hasta `<6.1.0`, mientras TypeScript 7.0.2 ya aparece como `latest`. No se
fuerzan peers incompatibles; la actualización se hará cuando el toolchain declare soporte.

La migración será progresiva bajo `/admin-v2`. `/admin` permanece disponible hasta que los flujos
críticos tengan paridad y evidencia E2E. Vite produce bundles con hash en
`wwwroot/admin-v2/assets`; ASP.NET sirve el documento con `no-store` y los assets hasheados con
cache `immutable`.

El frontend usa exclusivamente la cookie segura existente y el token CSRF de `/ui-api/session`.
No persiste access tokens, refresh tokens, secretos ni pruebas de reautenticación.

## Consecuencias

- Las rutas y features pueden cargarse bajo demanda y probarse de forma aislada.
- El pipeline necesita Node.js además de .NET y debe construir el frontend antes de publicar.
- Durante la migración existen dos consolas; los operadores mantienen un rollback inmediato a
  `/admin`.
- Las reglas de autorización siguen perteneciendo al backend. Los permission gates sólo mejoran
  la experiencia.

## Alternativas descartadas

- Reescribir `/admin` de una vez: eleva el riesgo de regresión y elimina el rollback inmediato.
- Continuar con JavaScript sin build: no resuelve rutas, contratos tipados, code splitting ni
  assets versionados.
- Guardar bearer tokens en el navegador: contradice el modelo de seguridad hospedado.
