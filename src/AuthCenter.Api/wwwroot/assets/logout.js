import { api, appendTheme, errorMessage, legalLinks, setCsrf, status } from "./shared.js";

// Confirms an application's sign-out request that could not be verified on its own (no ID token
// of this session), as OpenID Connect RP-Initiated Logout requires. Opened without a request, it
// offers to end the session of this browser.
const message = document.querySelector("#status");
const description = document.querySelector("#logout-description");
const actions = document.querySelector("#logout-actions");
const confirmButton = document.querySelector("#logout-confirm");
const cancel = document.querySelector("#logout-cancel");
const logoutId = new URLSearchParams(location.search).get("logout_id");

let pending = null;
try { pending = logoutId ? await api(`/oauth/logout/${encodeURIComponent(logoutId)}`) : null; }
catch { pending = null; }

// The page carries the branding of the application that asked, and names no product otherwise.
const applicationCode = pending?.applicationCode || "AUTHCENTER";
appendTheme(applicationCode);
let branding = null;
try { branding = await api(`/api/applications/branding/${encodeURIComponent(applicationCode)}`); }
catch { /* No branding: the page stays neutral. */ }
const brandName = branding?.displayName ?? "";
document.querySelector("#brand-name").textContent = brandName;
const logo = document.querySelector("#brand-logo");
if (branding?.logoUrl) { logo.src = branding.logoUrl; logo.alt = brandName ? "" : "Logo de la aplicación"; logo.hidden = false; }
document.querySelector("#brand").hidden = !brandName && logo.hidden;
document.querySelector("#legal").replaceChildren(...legalLinks(branding));
document.title = brandName ? `Cerrar sesión · ${brandName}` : "Cerrar sesión";

let session = null;
try {
  session = await api("/ui-api/session");
  setCsrf(session.csrfToken);
} catch { /* No session: there is nothing to end. */ }

if (logoutId && !pending)
  status(message, "Esta solicitud de cierre de sesión expiró o se abrió en otro navegador.", "error");

if (session) {
  const who = session.user.email || session.user.name || "tu cuenta";
  const origin = pending?.clientDisplayName ? ` Te lo pide ${pending.clientDisplayName}.` : "";
  description.textContent = `¿Quieres cerrar la sesión de ${who}?${origin}`;
  actions.hidden = false;
} else if (pending) {
  // Nothing to end here, but the application waits for the answer to its request.
  description.textContent = "No tienes una sesión abierta.";
  confirmButton.textContent = "Continuar";
  cancel.hidden = true;
  actions.hidden = false;
} else {
  description.textContent = "No tienes una sesión abierta.";
  document.querySelector("#sign-in").hidden = false;
}

const logoutErrors = {
  INVALID_LOGOUT_REQUEST: "Esta solicitud de cierre de sesión expiró o ya se usó."
};

confirmButton.addEventListener("click", async () => {
  status(message, "Cerrando sesión…");
  try {
    if (pending) {
      const result = await api(`/oauth/logout/${encodeURIComponent(logoutId)}/confirm`, { method: "POST" });
      // The server only returns the registered post-logout URI or a local page.
      location.replace(result.redirectUrl);
    } else {
      await api("/ui-api/session/logout", { method: "POST" });
      location.replace("/login?signed_out=1");
    }
  } catch (error) { status(message, logoutErrors[error.code] ?? errorMessage(error), "error"); }
});

// Staying signed in goes back to where the request came from, or to the account.
cancel.addEventListener("click", () => {
  if (history.length > 1) history.back();
  else location.replace("/portal");
});
