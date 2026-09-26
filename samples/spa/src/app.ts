import { AuthCenterClient, AuthCenterError, type IdTokenClaims, type TokenSet } from "@authcenter/client";

const client = new AuthCenterClient({
  authority: import.meta.env.VITE_AUTHCENTER_AUTHORITY,
  clientId: import.meta.env.VITE_AUTHCENTER_CLIENT_ID,
  redirectUri: `${location.origin}/callback`,
  postLogoutRedirectUri: `${location.origin}/`,
  scopes: (import.meta.env.VITE_AUTHCENTER_SCOPES ?? "openid profile email").split(" ").filter(Boolean)
});
const apiUrl: string | undefined = import.meta.env.VITE_API_URL || undefined;
const apiResource: string | undefined = import.meta.env.VITE_API_RESOURCE || undefined;

// Tokens live only in memory: a reload signs in again through AuthCenter's single sign-on session.
let session: { tokens: TokenSet; claims: IdTokenClaims } | null = null;

const element = <T extends HTMLElement>(selector: string) => document.querySelector<T>(selector)!;
const status = element<HTMLParagraphElement>("#status");
const output = element<HTMLPreElement>("#output");

function show(message: string, error = false): void {
  status.textContent = message;
  status.className = error ? "error" : "";
}

function render(): void {
  element("#signed-out").hidden = session !== null;
  element("#signed-in").hidden = session === null;
  element<HTMLButtonElement>("#call-api").hidden = !apiUrl;
  const claims = element<HTMLDListElement>("#claims");
  claims.replaceChildren();
  if (!session) return;
  for (const name of ["sub", "name", "email", "sid", "amr", "acr", "auth_time"]) {
    const value = session.claims[name];
    if (value === undefined) continue;
    const term = document.createElement("dt");
    term.textContent = name;
    const detail = document.createElement("dd");
    detail.textContent = Array.isArray(value) ? value.join(" ") : name === "auth_time" ? new Date(Number(value) * 1000).toLocaleString() : String(value);
    claims.append(term, detail);
  }
}

async function completeCallback(): Promise<void> {
  try {
    const result = await client.handleCallback();
    if (!result.claims) throw new Error("AuthCenter did not return an ID token.");
    session = { tokens: result.tokens, claims: result.claims };
    show(`Hola, ${result.claims.name ?? result.claims.email ?? result.claims.sub}.`);
    history.replaceState(null, "", result.returnTo ?? "/");
  } catch (error) {
    history.replaceState(null, "", "/");
    if (error instanceof AuthCenterError && error.code === "login_required") show("No hay una sesión de AuthCenter activa. Inicia sesión.");
    else if (error instanceof AuthCenterError && error.code === "access_denied") show("AuthCenter no permitió el acceso a esta aplicación.", true);
    else show(error instanceof Error ? error.message : "No se pudo completar el inicio de sesión.", true);
  }
}

element<HTMLButtonElement>("#login").onclick = () =>
  void client.signInRedirect({ returnTo: "/", ...(apiResource ? { resources: [apiResource] } : {}) }).catch((error: unknown) => show(String(error), true));

// prompt=none: AuthCenter answers at once with the existing SSO session or with login_required.
element<HTMLButtonElement>("#silent").onclick = () =>
  void client.signInRedirect({ prompt: "none", returnTo: "/" }).catch((error: unknown) => show(String(error), true));

element<HTMLButtonElement>("#userinfo").onclick = async () => {
  if (!session) return;
  try { output.textContent = JSON.stringify(await client.userInfo(session.tokens.access_token), null, 2); }
  catch (error) { show(error instanceof Error ? error.message : "UserInfo falló.", true); }
};

element<HTMLButtonElement>("#call-api").onclick = async () => {
  if (!session || !apiUrl) return;
  const response = await fetch(apiUrl, { headers: { authorization: `Bearer ${session.tokens.access_token}` } });
  output.textContent = `${response.status} ${response.statusText}\n${await response.text()}`;
};

// RP-initiated logout ends the AuthCenter session for every application, then returns here.
element<HTMLButtonElement>("#logout").onclick = () => {
  const idTokenHint = session?.tokens.id_token;
  session = null;
  void client.signOutRedirect(idTokenHint ? { idTokenHint } : {}).catch((error: unknown) => show(String(error), true));
};

if (location.pathname === "/callback") await completeCallback();
render();
