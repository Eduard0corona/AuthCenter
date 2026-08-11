import { AuthCenterClient } from "@authcenter/client";

const client = new AuthCenterClient({
  authority: import.meta.env.VITE_AUTHCENTER_AUTHORITY,
  clientId: import.meta.env.VITE_AUTHCENTER_CLIENT_ID,
  redirectUri: `${location.origin}/callback`,
  scopes: ["openid", "profile", "email"]
});

document.querySelector<HTMLButtonElement>("#login")!.onclick = async () => {
  const pkce = await AuthCenterClient.createPkce();
  const state = AuthCenterClient.randomState();
  sessionStorage.setItem("authcenter.pkce", pkce.verifier);
  sessionStorage.setItem("authcenter.state", state);
  location.assign(client.authorizationUrl({ state, nonce: AuthCenterClient.randomState(), pkce }));
};
