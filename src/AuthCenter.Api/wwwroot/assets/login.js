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
// An upstream identity provider sends the browser back with a single-use result for this
// browser (or an error code). Both leave the address bar at once so a reload or a shared link
// cannot replay them.
const federationResult = params.get("federation_result");
const federationError = params.get("federation_error");
if (federationResult || federationError) {
  params.delete("federation_result");
  params.delete("federation_error");
  history.replaceState(null, "", `${location.pathname}${params.size ? `?${params}` : ""}`);
}
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
// An application that only allows federated sign-in keeps the email step for home realm
// discovery but never asks for a password.
const passwordAllowed = !interaction || interaction.allowPasswordLogin;
if (!passwordAllowed) {
  password.closest("label").hidden = true;
  password.required = false;
}
document.querySelector("#federation-hint").hidden = !interaction?.federationAvailable;
if (interactionId && !interaction) showUnavailable();
else if (federationResult) await completeFederation(federationResult);
else if (federationError) showFederationError(federationError);
else if (interaction?.requiresFreshLogin) {
  await primeCsrf();
  if (!await federateFromHints()) status(message, `Confirma tu identidad para continuar en ${interaction.applicationName}.`);
}
else if (!await resumeExistingSession()) await federateFromHints();
loginForm.addEventListener("submit", async event => {
  event.preventDefault();
  if (!email.validity.valid) return status(message, "Introduce un correo válido.", "error");
  // Home realm discovery first: an email of a federated domain signs in at its organization.
  if (await federateEmail(email.value)) return;
  if (!passwordAllowed)
    return status(message, "Esta aplicación usa el inicio de sesión de tu organización y no encontramos uno para este correo.", "error");
  if (!password.value) {
    password.focus();
    return status(message, "Introduce tu contraseña.", "error");
  }
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
  catch { return false; /* An anonymous visit is expected. */ }
  setCsrf(current.csrfToken);
  if (!email.value && current.user.email) email.value = current.user.email;
  // Authorization requests are checked against the client's application by the server. A direct
  // visit only reuses a session issued for the requested application; otherwise the user signs
  // in to it, which continues the same single sign-on session.
  if (!interactionId && !current.user.applications.includes(application.value)) return false;
  await finishLogin();
  return true;
}

// Federation requested by the client: idp names the provider, domain_hint lets home realm
// discovery pick it. Returns true when the browser is on its way to the provider.
async function federateFromHints() {
  if (interaction?.identityProvider) return startFederation(interaction.identityProvider);
  if (interaction?.domainHint) return federate({ domain: interaction.domainHint });
  return false;
}

async function federateEmail(value) {
  if (interaction && !interaction.federationAvailable) return false;
  return federate({ email: value });
}

async function federate(hint) {
  let route;
  try {
    route = await api("/ui-api/session/federation/discover", {
      method: "POST",
      body: JSON.stringify({ ...hint, ...targetOfSignIn() })
    });
  } catch (error) {
    if (error.status === 429) status(message, "Demasiados intentos. Espera un momento e inténtalo de nuevo.", "error");
    return false;
  }
  return route.federated ? startFederation(route.provider) : false;
}

async function startFederation(provider) {
  status(message, `Redirigiendo a ${provider.name}…`);
  try {
    const result = await api("/ui-api/session/federation/start", {
      method: "POST",
      body: JSON.stringify({
        providerId: provider.id,
        ...targetOfSignIn(),
        returnUrl: interactionId ? undefined : params.get("return_url") || undefined,
        loginHint: email.value || undefined
      })
    });
    location.assign(result.redirectUrl);
    return true;
  } catch (error) {
    showFederationError(error.code, error.message);
    return false;
  }
}

// The application of the sign-in: the authorization request's, or the one of a direct visit.
function targetOfSignIn() {
  return interactionId ? { interactionId } : { applicationCode: application.value };
}

// Back from the identity provider: this browser redeems the result, and the application's
// policy and second factor apply exactly as after a password.
async function completeFederation(handle) {
  status(message, "Completando el inicio de sesión con tu organización…");
  await primeCsrf();
  try {
    const result = await api("/ui-api/session/federation/complete", {
      method: "POST",
      body: JSON.stringify({ handle })
    });
    await continueSignIn(result);
  } catch (error) { showFederationError(error.code, error.message); }
}

const federationMessages = {
  FEDERATION_STATE_INVALID: "El inicio de sesión con tu organización expiró o ya se usó. Vuelve a intentarlo.",
  FEDERATION_RESULT_INVALID: "El inicio de sesión con tu organización expiró o ya se usó. Vuelve a intentarlo.",
  INTERACTION_BINDING_MISMATCH: "Este inicio de sesión se inició en otro navegador. Vuelve a la aplicación e inténtalo de nuevo.",
  FEDERATION_CANCELLED: "Cancelaste el inicio de sesión en tu organización.",
  FEDERATION_EMAIL_NOT_VERIFIED: "Tu organización no confirmó tu correo electrónico, así que no podemos vincular tu cuenta.",
  ACCOUNT_LINKING_REQUIRED: "Ya existe una cuenta con este correo. Pide a un administrador que la vincule con tu organización.",
  JIT_PROVISIONING_DISABLED: "Tu cuenta aún no está habilitada en esta aplicación. Solicita acceso a tu administrador.",
  ACCESS_DENIED: "No tienes acceso a esta aplicación.",
  USER_INACTIVE: "Tu cuenta está inactiva.",
  ACCESS_POLICY_DENIED: "La política de acceso de esta aplicación no permite este inicio de sesión.",
  MFA_SETUP_REQUIRED: "Esta aplicación exige verificación en dos pasos. Actívala en tu portal (/portal) y vuelve a intentarlo.",
  PASSKEY_REQUIRED: "Esta aplicación exige una passkey. Usa \"Usar una passkey\" para continuar.",
  PASSKEY_ENROLLMENT_REQUIRED: "Esta aplicación exige una passkey. Registra una en tu portal (/portal) y vuelve a intentarlo.",
  FEDERATION_PROVIDER_NOT_FOUND: "El proveedor de identidad de tu organización no está disponible para esta aplicación.",
  FEDERATION_CALLBACK_NOT_HOSTED: "El proveedor de identidad de tu organización no está disponible en este momento.",
  OIDC_DISCOVERY_FAILED: "El proveedor de identidad de tu organización no está disponible en este momento.",
  SAML_NOT_CONFIGURED: "El proveedor de identidad de tu organización no está disponible en este momento.",
  INVALID_INTERACTION: "Esta solicitud de inicio de sesión expiró o se abrió en otro navegador. Vuelve a la aplicación e inténtalo de nuevo."
};

function showFederationError(code, fallback) {
  for (const form of [mfaForm, passwordChangeForm, document.querySelector("#consent")]) form.hidden = true;
  loginForm.hidden = false;
  const text = federationMessages[code]
    ?? (fallback && !/^(OIDC|SAML|INVALID_SAML|INVALID_OIDC|FEDERATION_UPSTREAM)/.test(code ?? "") ? fallback : "Tu organización no pudo completar el inicio de sesión. Si continúa, contacta a soporte.");
  status(message, text, "error");
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
