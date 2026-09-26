import { api, appendTheme, normalizeRequestOptions, safeLocalPath, serializeCredential, setCsrf, status } from "./shared.js";

const loginForm = document.querySelector("#login-form");
const mfaForm = document.querySelector("#mfa-form");
const passwordChangeForm = document.querySelector("#password-change-form");
const message = document.querySelector("#status");
const application = document.querySelector("#application");
const email = document.querySelector("#email");
const password = document.querySelector("#password");
const params = new URLSearchParams(location.search);
const interactionId = params.get("interaction_id");
let mfaPendingToken = "";
let passwordChangeToken = "";
let themeCode = "";
// A consent decision waiting for the step-up the client's application requires.
let pendingConsent = null;

// An authorization request names its application: the page signs the user in to that
// application (its policies, branding and hint) instead of asking the user for a code.
const interaction = interactionId ? await loadInteractionContext() : null;
application.value = interaction?.applicationCode || params.get("application") || application.value;
await loadBranding();
if (interaction?.loginHint) email.value = interaction.loginHint;
if (interaction && !interaction.allowPasswordLogin) {
  password.closest("label").hidden = true;
  loginForm.querySelector("button[type=submit]").hidden = true;
}
if (interactionId && !interaction) showUnavailable();
else if (interaction?.requiresFreshLogin) {
  await primeCsrf();
  status(message, `Confirma tu identidad para continuar en ${interaction.applicationName}.`);
}
else await resumeExistingSession();
loginForm.addEventListener("submit", async event => {
  event.preventDefault();
  status(message, "Verificando credenciales…");
  try {
    const result = await api("/ui-api/session/login", {
      method: "POST",
      body: JSON.stringify({ email: email.value, password: password.value, applicationCode: application.value })
    });
    await continueSignIn(result);
  } catch (error) { status(message, error.message, "error"); }
});

passwordChangeForm.addEventListener("submit", async event => {
  event.preventDefault();
  const newPassword = document.querySelector("#new-password").value;
  if (newPassword !== document.querySelector("#confirm-password").value)
    return status(message, "Las contraseñas no coinciden.", "error");
  status(message, "Guardando tu nueva contraseña…");
  try {
    const result = await api("/ui-api/session/forced-change", {
      method: "POST",
      body: JSON.stringify({ forcedChangePendingToken: passwordChangeToken, newPassword })
    });
    passwordChangeToken = "";
    passwordChangeForm.reset();
    await continueSignIn(result);
  } catch (error) { status(message, error.message, "error"); }
});

document.querySelector("#password-change-cancel").addEventListener("click", () => {
  passwordChangeToken = "";
  passwordChangeForm.reset();
  passwordChangeForm.hidden = true;
  loginForm.hidden = false;
  password.value = "";
});

// Each interactive step either finishes the sign-in or asks for the next one (MFA or a
// mandatory password change); pending tokens only live in this page's memory.
async function continueSignIn(result) {
  if (result.requiresPasswordChange) {
    passwordChangeToken = result.passwordChangeToken;
    loginForm.hidden = true;
    mfaForm.hidden = true;
    passwordChangeForm.hidden = false;
    document.querySelector("#new-password").focus();
    status(message, "Debes reemplazar tu contraseña temporal antes de continuar.");
    return;
  }
  if (result.requiresMfa) {
    mfaPendingToken = result.mfaPendingToken;
    loginForm.hidden = true;
    passwordChangeForm.hidden = true;
    mfaForm.hidden = false;
    document.querySelector("#mfa-code").focus();
    status(message, "Completa la verificación adicional.");
    return;
  }
  setCsrf(result.csrfToken);
  await finishLogin();
}

mfaForm.addEventListener("submit", async event => {
  event.preventDefault();
  const code = document.querySelector("#mfa-code").value.trim();
  const method = document.querySelector("#mfa-method").value;
  try {
    const result = await api("/ui-api/session/mfa", {
      method: "POST",
      body: JSON.stringify({ mfaPendingToken, totpCode: method === "totp" ? code : null, emailOtpCode: method === "email" ? code : null, backupCode: method === "backup" ? code : null, trustDevice: false })
    });
    setCsrf(result.csrfToken);
    await finishLogin();
  } catch (error) { status(message, error.message, "error"); }
});

document.querySelector("#mfa-cancel").addEventListener("click", () => {
  mfaPendingToken = "";
  mfaForm.hidden = true;
  loginForm.hidden = false;
  password.value = "";
});

document.querySelector("#passkey").addEventListener("click", async () => {
  if (!window.PublicKeyCredential) return status(message, "Este navegador no admite passkeys.", "error");
  if (!email.validity.valid) return status(message, "Introduce primero un correo válido.", "error");
  try {
    status(message, "Esperando tu passkey…");
    const challenge = await api("/ui-api/session/passkey/options", {
      method: "POST", body: JSON.stringify({ email: email.value, applicationCode: application.value })
    });
    const credential = await navigator.credentials.get({ publicKey: normalizeRequestOptions(JSON.parse(challenge.optionsJson)) });
    const result = await api("/ui-api/session/passkey/complete", {
      method: "POST",
      body: JSON.stringify({ interactionId: challenge.interactionId, credentialJson: serializeCredential(credential) })
    });
    setCsrf(result.csrfToken);
    await finishLogin();
  } catch (error) { status(message, error.name === "NotAllowedError" ? "La operación con passkey fue cancelada." : error.message, "error"); }
});

async function resumeExistingSession() {
  let current;
  try { current = await api("/ui-api/session"); }
  catch { return; /* An anonymous visit is expected. */ }
  setCsrf(current.csrfToken);
  if (!email.value && current.user.email) email.value = current.user.email;
  // Authorization requests are checked against the client's application by the server. A direct
  // visit only reuses a session issued for the requested application; otherwise the user signs
  // in to it, which continues the same single sign-on session.
  if (!interactionId && !current.user.applications.includes(application.value)) return;
  await finishLogin();
}

// A browser that already holds a session must send its CSRF token even to sign in again.
async function primeCsrf() {
  try { setCsrf((await api("/ui-api/session")).csrfToken); }
  catch { /* No session: nothing to protect. */ }
}

async function finishLogin() {
  if (!interactionId) return location.replace(safeLocalPath(params.get("return_url"), location.origin, "/portal"));
  if (pendingConsent === null) return showConsent();
  const consent = pendingConsent;
  pendingConsent = null;
  return completeConsent(consent);
}

// The client's application asked for a stronger sign-in than the current session: verify a
// second factor (or a passkey) without signing in again, then complete the same request.
async function startStepUp() {
  for (const form of [loginForm, passwordChangeForm, document.querySelector("#consent")]) form.hidden = true;
  let result;
  try {
    result = await api(`/oauth/interactions/${encodeURIComponent(interactionId)}/step-up`, { method: "POST" });
  } catch (error) {
    if (error.code === "MFA_SETUP_REQUIRED")
      return showBlocked("Esta aplicación exige verificación en dos pasos. Actívala en tu portal (/portal) y vuelve a intentarlo.");
    if (error.code === "PASSKEY_REQUIRED") {
      loginForm.hidden = false;
      return status(message, "Esta aplicación exige una passkey. Usa \"Usar una passkey\" para continuar.");
    }
    if (error.code === "PASSKEY_ENROLLMENT_REQUIRED")
      return showBlocked("Esta aplicación exige una passkey. Registra una en tu portal (/portal) y vuelve a intentarlo.");
    if (error.code === "ACCESS_DENIED")
      return showBlocked("La política de acceso de esta aplicación no permite este inicio de sesión.");
    return handleInteractionError(error);
  }
  if (!result.stepUpRequired) return finishLogin();
  status(message, "Esta aplicación requiere una verificación adicional.");
  await continueSignIn(result);
}

function showBlocked(text) {
  for (const form of [loginForm, mfaForm, passwordChangeForm, document.querySelector("#consent")]) form.hidden = true;
  pendingConsent = null;
  status(message, text, "error");
}

async function loadInteractionContext() {
  try { return await api(`/oauth/interactions/${encodeURIComponent(interactionId)}/context`); }
  catch { return null; }
}

function showUnavailable() {
  for (const form of [loginForm, mfaForm, passwordChangeForm, document.querySelector("#consent")]) form.hidden = true;
  status(message, "Esta solicitud de inicio de sesión expiró o se abrió en otro navegador. Vuelve a la aplicación e inténtalo de nuevo.", "error");
}

function requireFreshSignIn() {
  for (const form of [mfaForm, passwordChangeForm, document.querySelector("#consent")]) form.hidden = true;
  loginForm.hidden = false;
  password.value = "";
  status(message, "Por seguridad, vuelve a iniciar sesión para continuar.");
}

function handleInteractionError(error) {
  if (error.code === "LOGIN_REQUIRED") return requireFreshSignIn();
  if (["INVALID_INTERACTION", "INTERACTION_BINDING_MISMATCH", "INVALID_CLIENT"].includes(error.code)) return showUnavailable();
  status(message, error.message, "error");
}

async function showConsent() {
  let interaction;
  try { interaction = await api(`/oauth/interactions/${encodeURIComponent(interactionId)}`); }
  catch (error) { return handleInteractionError(error); }
  if (interaction.requiresReauthentication) return requireFreshSignIn();
  if (!interaction.requiresConsent) return completeConsent(true);
  loginForm.hidden = true;
  mfaForm.hidden = true;
  const consent = document.querySelector("#consent");
  consent.hidden = false;
  document.querySelector("#consent-description").textContent = `${interaction.clientDisplayName} solicita acceso a ${interaction.applicationName}.`;
  const scopes = document.querySelector("#consent-scopes");
  scopes.replaceChildren(...interaction.scopes.map(scope => {
    const badge = document.createElement("span"); badge.className = "badge"; badge.textContent = scope; return badge;
  }));
  document.querySelector("#consent-allow").onclick = () => completeConsent(true);
  document.querySelector("#consent-deny").onclick = () => completeConsent(false);
}

async function completeConsent(consent) {
  try {
    const result = await api("/oauth/authorize/complete", {
      method: "POST",
      headers: { "X-AuthCenter-UI": "1" },
      body: JSON.stringify({ interactionId, consent })
    });
    location.assign(result.redirectUrl);
  } catch (error) {
    if (error.code !== "STEP_UP_REQUIRED") return handleInteractionError(error);
    pendingConsent = consent;
    await startStepUp();
  }
}

async function loadBranding() {
  const code = application.value.trim() || "AUTHCENTER";
  if (code !== themeCode) { appendTheme(code); themeCode = code; }
  try {
    const branding = await api(`/api/applications/branding/${encodeURIComponent(code)}`);
    document.querySelector("#brand-name").textContent = branding.displayName;
    document.title = `Iniciar sesión · ${branding.displayName}`;
    const logo = document.querySelector("#brand-logo");
    if (branding.logoUrl) { logo.src = branding.logoUrl; logo.alt = `Logo de ${branding.displayName}`; logo.hidden = false; }
    else { logo.hidden = true; logo.removeAttribute("src"); }
    const legal = document.querySelector("#legal");
    legal.replaceChildren(...[[branding.privacyUrl, "Privacidad"], [branding.termsUrl, "Términos"], [branding.supportUrl, "Soporte"]]
      .filter(([url]) => url).map(([url, label]) => { const link = document.createElement("a"); link.href = url; link.rel = "noopener noreferrer"; link.textContent = label; return link; }));
  } catch { document.querySelector("#brand-name").textContent = "AuthCenter"; }
}
