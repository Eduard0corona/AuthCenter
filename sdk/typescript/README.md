# @authcenter/client

SDK sin dependencias de ejecución para Authorization Code + PKCE, refresh y revocación. No
persiste tokens automáticamente: una SPA debe mantener access tokens en memoria y delegar
refresh tokens a un backend/BFF con cookie `HttpOnly`.

```ts
const client = new AuthCenterClient({
  authority: "https://identity.example.com",
  clientId: "my-spa",
  redirectUri: `${location.origin}/callback`
});
const pkce = await AuthCenterClient.createPkce();
location.assign(client.authorizationUrl({ state: AuthCenterClient.randomState(), nonce: AuthCenterClient.randomState(), pkce }));
```
