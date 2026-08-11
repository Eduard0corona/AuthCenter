# SPA quickstart

Ejecuta `npm install`, copia `.env.example` a `.env.local` y luego usa `npm run dev`. Configura
`VITE_AUTHCENTER_AUTHORITY` y `VITE_AUTHCENTER_CLIENT_ID`, y registra exactamente
`${location.origin}/callback`. El ejemplo conserva sólo PKCE/state temporal en `sessionStorage`;
no almacena access/refresh tokens de larga vida. Para producción se recomienda el BFF incluido en
AuthCenter o un backend propio con cookie `HttpOnly`.
