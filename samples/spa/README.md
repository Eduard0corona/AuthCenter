# SPA quickstart

Aplicación de una sola página que usa `@authcenter/client`: inicio de sesión con Authorization
Code + PKCE, callback con validación del ID token, SSO silencioso (`prompt=none`), UserInfo,
llamada a una API con el access token y cierre de sesión global.

1. Registra en AuthCenter un cliente **público** con PKCE:
   - Redirect URI: `http://localhost:5173/callback` (en producción, el origen HTTPS de la SPA).
   - Post-logout redirect URI: `http://localhost:5173/`.
   - Orígenes CORS permitidos: `http://localhost:5173` (la SPA llama a `/oauth/token` y `/oauth/userinfo`).
   - Scopes: `openid profile email` y, si vas a llamar a la API de ejemplo, `orders.read` (catálogo de APIs).
2. Copia `.env.example` a `.env.local` y ajusta `VITE_AUTHCENTER_AUTHORITY`,
   `VITE_AUTHCENTER_CLIENT_ID` y, opcionalmente, `VITE_API_URL`/`VITE_API_RESOURCE`.
3. `npm install` y `npm run dev`.

Los tokens sólo viven en memoria: al recargar, "Reanudar sesión" usa la sesión SSO de AuthCenter
sin pedir credenciales. El estado temporal del inicio de sesión (state, nonce y verificador PKCE)
vive en `sessionStorage` hasta el callback y es de un solo uso. Para aplicaciones con backend se
recomienda el BFF de `AuthCenter.Client` (.NET), que guarda los tokens en el servidor.
