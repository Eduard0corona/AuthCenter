import { api, safeLocalPath, setCsrf, status } from "./shared.js";

// Confirms an application's sign-out request that AuthCenter could not verify on its own (no ID
// token of this session), as OpenID Connect RP-Initiated Logout requires.
const message = document.querySelector("#status");
const description = document.querySelector("#logout-description");
const actions = document.querySelector("#logout-actions");
const logoutId = new URLSearchParams(location.search).get("logout_id");

let pending = null;
try { pending = logoutId ? await api(`/oauth/logout/${encodeURIComponent(logoutId)}`) : null; }
catch { pending = null; }

let signedIn = false;
try {
  const session = await api("/ui-api/session");
  setCsrf(session.csrfToken);
  signedIn = true;
  const who = session.user.email || session.user.name || "tu cuenta";
  const origin = pending?.clientDisplayName ? ` La solicitud viene de ${pending.clientDisplayName}.` : "";
  description.textContent = `¿Quieres cerrar la sesión de ${who} en AuthCenter?${origin}`;
} catch { /* No session: there is nothing to end. */ }

if (!pending) {
  status(message, "Esta solicitud de cierre de sesión expiró o se abrió en otro navegador.", "error");
} else {
  if (!signedIn) description.textContent = "No tienes una sesión activa en AuthCenter.";
  document.querySelector("#logout-confirm").textContent = signedIn ? "Cerrar sesión" : "Continuar";
  actions.hidden = false;
}

document.querySelector("#logout-confirm").addEventListener("click", async () => {
  status(message, "Cerrando sesión…");
  try {
    const result = await api(`/oauth/logout/${encodeURIComponent(logoutId)}/confirm`, { method: "POST" });
    // The server only returns the registered post-logout URI or a local page.
    location.replace(result.redirectUrl);
  } catch (error) { status(message, error.message, "error"); }
});

document.querySelector("#logout-cancel").addEventListener("click", () => {
  location.replace(safeLocalPath("/portal", location.origin, "/portal"));
});
