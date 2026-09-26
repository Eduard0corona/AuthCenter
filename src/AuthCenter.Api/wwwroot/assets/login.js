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

application.value = params.get("application") || application.value;
await loadBranding();
await resumeExistingSession();

application.addEventListener("change", loadBranding);
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
  try {
    const current = await api("/ui-api/session");
    setCsrf(current.csrfToken);
    await finishLogin();
  } catch { /* An anonymous visit is expected. */ }
}

async function finishLogin() {
  if (interactionId) return showConsent();
  location.replace(safeLocalPath(params.get("return_url"), location.origin, "/portal"));
}

async function showConsent() {
  const interaction = await api(`/oauth/interactions/${encodeURIComponent(interactionId)}`);
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
  } catch (error) { status(message, error.message, "error"); }
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
