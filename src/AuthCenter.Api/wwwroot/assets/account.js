import { api, appendTheme, errorMessage, legalLinks, passwordProblem, passwordRules, setCsrf, status } from "./shared.js";

// Pages opened from the links AuthCenter emails: set a password (reset or invitation) and
// confirm an email address or an email change. The single-use token leaves the address bar
// before anything else happens, and every action needs an explicit click so that link
// scanners opening the page do not consume it.
const message = document.querySelector("#status");
const params = new URLSearchParams(location.search);
const token = params.get("token") || "";
const emailAddress = params.get("email") || "";
const userId = params.get("userId") || "";
const applicationCode = /^[A-Za-z0-9_.-]{1,50}$/.test(params.get("application") || "") ? params.get("application") : "";
const page = location.pathname;
history.replaceState(null, "", page);

const pages = {
  "/reset-password": {
    title: "Crea una contraseña nueva",
    subtitle: "Elige una contraseña que no uses en otros sitios. Se cerrarán tus sesiones abiertas.",
    submit: "Guardar contraseña",
    done: "Tu contraseña se cambió. Ya puedes iniciar sesión con ella.",
    kind: "password"
  },
  "/accept-invitation": {
    title: "Activa tu cuenta",
    subtitle: "Te invitaron a usar una aplicación. Define tu contraseña para empezar.",
    submit: "Activar cuenta",
    done: "Tu cuenta está lista. Ya puedes iniciar sesión.",
    kind: "password"
  },
  "/confirm-email": {
    title: "Confirma tu correo",
    subtitle: "Confirma que esta dirección es tuya para terminar de configurar tu cuenta.",
    detail: `Confirmar la dirección ${emailAddress}.`,
    submit: "Confirmar correo",
    done: "Tu correo quedó confirmado. Ya puedes iniciar sesión.",
    kind: "confirm-email"
  },
  "/confirm-email-change": {
    title: "Confirma tu nuevo correo",
    subtitle: "Tu cuenta empezará a usar esta dirección para iniciar sesión y recibir avisos.",
    detail: `Usar ${emailAddress} como el correo de tu cuenta.`,
    submit: "Confirmar cambio",
    done: "Tu cuenta ahora usa el correo nuevo. Úsalo para iniciar sesión.",
    kind: "email-change"
  }
};
const config = pages[page];
const passwordForm = document.querySelector("#password-form");
const confirmForm = document.querySelector("#confirm-form");

let brandName = "";
let markReady;
const ready = new Promise(resolve => { markReady = resolve; });

passwordForm.addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  const newPassword = document.querySelector("#new-password").value;
  const problem = passwordProblem(newPassword);
  if (problem) return status(message, problem, "error");
  if (newPassword !== document.querySelector("#confirm-password").value) return status(message, "Las contraseñas no coinciden.", "error");
  status(message, "Guardando…");
  try {
    await api("/api/auth/reset-password", { method: "POST", body: JSON.stringify({ email: emailAddress, token, newPassword }) });
    passwordForm.reset();
    passwordForm.hidden = true;
    finish(config.done, "success");
  } catch (error) { fail(error); }
});

confirmForm.addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  status(message, "Confirmando…");
  try {
    if (config.kind === "confirm-email")
      await api("/api/auth/confirm-email", { method: "POST", body: JSON.stringify({ email: emailAddress, token }) });
    else
      await api("/api/auth/email-change/confirm", { method: "POST", body: JSON.stringify({ userId, newEmail: emailAddress, token }) });
    confirmForm.hidden = true;
    finish(config.done, "success", config.kind === "confirm-email" ? pendingSignInPath() : null);
  } catch (error) { fail(error); }
});

function fail(error) {
  if (error.status === 429) return status(message, "Demasiados intentos. Espera un momento e inténtalo de nuevo.", "error");
  if (error.code === "WEAK_PASSWORD") return status(message, `La contraseña no cumple la política de seguridad. ${passwordRules}`, "error");
  if (error.code === "VALIDATION_FAILED" && config.kind === "password") return status(message, passwordProblem(""), "error");
  // The link is still good after a network or server failure: the form stays for another try.
  if (error.code === "NETWORK_ERROR" || error.status >= 500) return status(message, errorMessage(error), "error");
  passwordForm.hidden = true;
  confirmForm.hidden = true;
  finish("El enlace no es válido, expiró o ya se usó. Pide uno nuevo.", "error");
}

function finish(text, type, nextPath = null) {
  status(message, text, type);
  const next = document.querySelector("#next");
  const link = document.querySelector("#next-link");
  link.href = nextPath ?? (applicationCode && applicationCode !== "AUTHCENTER" ? `/login?application=${encodeURIComponent(applicationCode)}` : "/login");
  link.textContent = nextPath ? "Continuar e iniciar sesión" : "Iniciar sesión";
  next.hidden = false;
}

// A new account confirmed in the browser that created it continues the sign-in that was under way
// there (the hosted login saved it), within the minutes that request lasts.
function pendingSignInPath() {
  let pending = null;
  try { pending = JSON.parse(localStorage.getItem("authcenter.pendingSignIn") || "null"); } catch { return null; }
  if (!pending || !(pending.expiresAt > Date.now())) return null;
  if (pending.interactionId)
    return `/login?${pending.saml ? "saml_interaction" : "interaction_id"}=${encodeURIComponent(pending.interactionId)}`;
  if (!pending.returnUrl) return null;
  const query = new URLSearchParams();
  if (pending.application && pending.application !== "AUTHCENTER") query.set("application", pending.application);
  query.set("return_url", pending.returnUrl);
  return `/login?${query}`;
}

// The application the link was sent for is the brand; without one the page names no product.
async function loadBranding() {
  const code = applicationCode || "AUTHCENTER";
  appendTheme(code);
  let branding = null;
  try { branding = await api(`/api/applications/branding/${encodeURIComponent(code)}`); }
  catch { /* No branding: the page stays neutral. */ }
  brandName = branding?.displayName ?? "";
  document.querySelector("#brand-name").textContent = brandName;
  const logo = document.querySelector("#brand-logo");
  if (branding?.logoUrl) { logo.src = branding.logoUrl; logo.alt = brandName ? "" : "Logo de la aplicación"; logo.hidden = false; }
  document.querySelector("#brand").hidden = !brandName && logo.hidden;
  document.querySelector("#legal").replaceChildren(...legalLinks(branding));
}

async function initialize() {
  await loadBranding();
  // A browser that already holds a session must send its CSRF token even on these pages.
  try { setCsrf((await api("/ui-api/session")).csrfToken); } catch { /* Not signed in. */ }

  if (!config || !token || !emailAddress || (config.kind === "email-change" && !userId)) {
    document.querySelector("#page-title").textContent = "Enlace incompleto";
    document.title = brandName ? `Enlace incompleto · ${brandName}` : "Enlace incompleto";
    finish("El enlace está incompleto o ya no es válido. Pide uno nuevo desde la página de inicio de sesión.", "error");
  } else {
    document.querySelector("#page-title").textContent = config.title;
    document.querySelector("#page-subtitle").textContent = config.subtitle;
    document.title = brandName ? `${config.title} · ${brandName}` : config.title;
    if (config.kind === "password") {
      document.querySelector("#account-email").value = emailAddress;
      document.querySelector("#password-submit").textContent = config.submit;
      passwordForm.hidden = false;
      document.querySelector("#new-password").focus();
    } else {
      document.querySelector("#confirm-detail").textContent = config.detail;
      document.querySelector("#confirm-submit").textContent = config.submit;
      confirmForm.hidden = false;
      document.querySelector("#confirm-submit").focus();
    }
  }
  markReady();
}

await initialize();
