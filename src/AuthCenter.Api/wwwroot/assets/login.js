import {
  api, appendTheme, copyText, createPasskey, downloadText, getPasskey, groupSecret, passkeyErrorMessage,
  passwordProblem, safeLocalPath, serializeCredential, setCsrf, status
} from "./shared.js";
import { qrSvg } from "./qr.js";

const message = document.querySelector("#status");
const application = document.querySelector("#application");
const email = document.querySelector("#email");
const password = document.querySelector("#password");
const views = [...document.querySelectorAll("[data-view]")];
const loginForm = document.querySelector("#login-form");
const forgotForm = document.querySelector("#forgot-form");
const mfaForm = document.querySelector("#mfa-form");
const totpForm = document.querySelector("#enroll-totp-form");
const backupView = document.querySelector("#backup-codes-view");
const passkeyEnrollView = document.querySelector("#enroll-passkey-view");
const passwordChangeForm = document.querySelector("#password-change-form");
const consentView = document.querySelector("#consent");
const PENDING_KEY = "authcenter.pendingSignIn";

const params = new URLSearchParams(location.search);
const magicLinkPage = location.pathname === "/magic-link";
// One-time values leave the address bar at once so that a reload, the history or a shared
// link cannot replay them: an upstream result, its error, or an emailed sign-in token.
const federationResult = params.get("federation_result");
const federationError = params.get("federation_error");
const magicToken = magicLinkPage ? params.get("token") : null;
if (federationResult || federationError || magicLinkPage) {
  for (const name of ["federation_result", "federation_error", "token", "email"]) params.delete(name);
  history.replaceState(null, "", `${magicLinkPage ? "/login" : location.pathname}${params.size ? `?${params}` : ""}`);
}

// A SAML sign-in request of an application AuthCenter is the identity provider of continues through
// the same steps as an OpenID Connect authorization, on its own endpoints and without consent.
let samlInteraction = params.has("saml_interaction");
let interactionId = params.get("interaction_id") ?? params.get("saml_interaction");
let returnUrl = params.get("return_url");
if (params.get("application")) application.value = params.get("application");
let mfaPendingToken = "";
let mfaMethod = "totp";
let useBackupCode = false;
let passwordChangeToken = "";
let enrollmentToken = "";
let themeCode = "";
let leaving = false;
let expiryTimer = 0;
let stepTimer = 0;
// A consent decision waiting for the step-up the client's application requires.
let pendingConsent = null;

// The sign-in continues where the emailed link was requested, when it is opened in the same browser.
if (magicLinkPage) {
  const pending = readPendingSignIn();
  if (pending) {
    interactionId = pending.interactionId || null;
    samlInteraction = pending.saml === true && Boolean(interactionId);
    returnUrl = pending.returnUrl || null;
    if (pending.application) application.value = pending.application;
  }
}

// Filled by initialize(); the handlers below are registered first and wait for it, so an early
// click or submit is never lost (nor submitted natively).
let interaction = null;
let options = null;
let passwordAllowed = true;
let magicLinkAllowed = false;
let federationAvailable = false;
let current = null;
let markReady;
const ready = new Promise(resolve => { markReady = resolve; });

// --- Views -----------------------------------------------------------------------------

function showView(view, focus) {
  for (const item of views) item.hidden = item !== view;
  document.querySelector("#restart").hidden = true;
  clearTimeout(stepTimer);
  (focus ?? view?.querySelector("input:not([type=hidden]):not([hidden]), button"))?.focus();
}

function backToLogin(text, type = "") {
  mfaPendingToken = passwordChangeToken = enrollmentToken = "";
  pendingConsent = null;
  password.value = "";
  for (const form of [mfaForm, totpForm, passwordChangeForm, forgotForm]) form.reset?.();
  showView(loginForm, passwordAllowed && email.value ? password : email);
  status(message, text, type);
}

// A dead end: nothing on this page can continue the request.
function showBlocked(text) {
  showView(null);
  pendingConsent = null;
  status(message, text, "error");
  // An authorization request can only be restarted from the application that sent it.
  document.querySelector("#restart").hidden = Boolean(interactionId);
  document.querySelector("#restart-link").href = loginPath();
}

function showUnavailable(text = "Esta solicitud de inicio de sesión expiró o se abrió en otro navegador. Vuelve a la aplicación e inténtalo de nuevo.") {
  showBlocked(text);
}

// Each step shows how long its single-use token lasts; an expired step goes back to the start.
function expireStepAfter(seconds) {
  clearTimeout(stepTimer);
  if (seconds > 0) stepTimer = setTimeout(() => backToLogin("La verificación expiró. Inicia sesión de nuevo.", "error"), seconds * 1000);
}

function watchInteractionExpiry() {
  if (!interaction?.expiresAt) return;
  // The server's times are UTC; a value without an offset must not be read as local time.
  const value = String(interaction.expiresAt);
  const remaining = Date.parse(/(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(value) ? value : `${value}Z`) - Date.now();
  clearTimeout(expiryTimer);
  expiryTimer = setTimeout(() => {
    if (!leaving) showUnavailable("La solicitud de inicio de sesión expiró. Vuelve a la aplicación para empezar de nuevo.");
  }, Math.max(0, remaining));
}

for (const button of document.querySelectorAll("[data-back]")) {
  button.addEventListener("click", () => backToLogin(""));
}

// --- Password sign-in ------------------------------------------------------------------

loginForm.addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  if (!email.validity.valid) return status(message, "Introduce un correo válido.", "error");
  // Home realm discovery first: an email of a federated domain signs in at its organization.
  if (await federateEmail(email.value)) return;
  if (!passwordAllowed) {
    return status(message, magicLinkAllowed
      ? "Esta aplicación no usa contraseñas. Pide un enlace de acceso a tu correo."
      : "Esta aplicación usa el inicio de sesión de tu organización y no encontramos uno para este correo.", "error");
  }
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
  } catch (error) { handleSignInError(error); }
});

passwordChangeForm.addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  const newPassword = document.querySelector("#new-password").value;
  const problem = passwordProblem(newPassword);
  if (problem) return status(message, problem, "error");
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
  } catch (error) { handleSignInError(error); }
});

// Each interactive step either finishes the sign-in or asks for the next one (a second factor,
// enrolling the factor the application requires, or a mandatory password change). Pending
// tokens only live in this page's memory.
async function continueSignIn(result) {
  if (result.requiresPasswordChange) {
    passwordChangeToken = result.passwordChangeToken;
    showView(passwordChangeForm);
    expireStepAfter(result.expiresIn);
    return status(message, "Debes reemplazar tu contraseña temporal antes de continuar.");
  }
  if (result.requiresMfa) return showMfa(result);
  if (result.requiresMfaEnrollment) return startTotpEnrollment(result);
  if (result.requiresPasskeyEnrollment) return showPasskeyEnrollment(result);
  setCsrf(result.csrfToken);
  await finishLogin();
}

const signInMessages = {
  INVALID_CREDENTIALS: "El correo o la contraseña no son correctos.",
  INVALID_FORCED_CHANGE_TOKEN: "El cambio de contraseña expiró. Inicia sesión de nuevo.",
  EMAIL_CONFIRMATION_REQUIRED: "Confirma tu correo electrónico antes de iniciar sesión. Revisa tu bandeja de entrada.",
  APPROVAL_REQUIRED: "Tu acceso a esta aplicación está pendiente de aprobación.",
  APP_INACTIVE: "La aplicación no está activa.",
  EMAIL_OTP_SEND_FAILED: "No pudimos enviar el código. Inténtalo de nuevo en unos minutos.",
  PASSKEY_LIMIT_REACHED: "Alcanzaste el número máximo de passkeys. Elimina una desde tu portal.",
  INVALID_PASSKEY_ATTESTATION: "No pudimos registrar la passkey. Inténtalo de nuevo.",
  ACCOUNT_LOCKED: "Tu cuenta está bloqueada temporalmente por varios intentos fallidos. Inténtalo más tarde.",
  USER_INACTIVE: "Tu cuenta está inactiva.",
  EMAIL_NOT_CONFIRMED: "Confirma tu correo electrónico antes de iniciar sesión.",
  ACCESS_DENIED: "No tienes acceso a esta aplicación.",
  ACCESS_POLICY_DENIED: "La política de acceso de esta aplicación no permite este inicio de sesión.",
  PASSWORD_LOGIN_DISABLED: "Esta aplicación no permite iniciar sesión con contraseña.",
  PASSKEY_REQUIRED: "Esta aplicación exige una passkey. Usa \"Usar una passkey\" para continuar.",
  INVALID_MFA_CODE: "El código no es válido. Revisa que la hora de tu dispositivo sea correcta e inténtalo de nuevo.",
  INVALID_MFA_TOKEN: "La verificación expiró. Inicia sesión de nuevo.",
  TOKEN_ALREADY_USED: "Este paso ya se usó. Inicia sesión de nuevo.",
  INVALID_ENROLLMENT: "La configuración expiró. Inicia sesión de nuevo.",
  PASSKEY_CEREMONY_EXPIRED: "La operación con passkey expiró. Inténtalo de nuevo.",
  INVALID_PASSKEY_ASSERTION: "No pudimos verificar tu passkey. Inténtalo de nuevo.",
  INVALID_MAGIC_LINK_TOKEN: "El enlace de acceso no es válido o expiró. Pide uno nuevo.",
  WEAK_PASSWORD: "La contraseña no cumple la política de seguridad.",
  MFA_SETUP_REQUIRED: "Esta aplicación exige verificación en dos pasos.",
  APP_NOT_FOUND: "La aplicación no existe o no está activa."
};

function handleSignInError(error) {
  if (error.status === 429) return status(message, "Demasiados intentos. Espera un momento e inténtalo de nuevo.", "error");
  if (["INVALID_MFA_TOKEN", "TOKEN_ALREADY_USED", "INVALID_ENROLLMENT", "INVALID_FORCED_CHANGE_TOKEN"].includes(error.code))
    return backToLogin(signInMessages[error.code], "error");
  status(message, signInMessages[error.code] ?? error.message, "error");
}

// --- Forgot password and emailed sign-in links -------------------------------------------

document.querySelector("#forgot-link").addEventListener("click", async () => {
  await ready;
  document.querySelector("#forgot-email").value = email.value;
  showView(forgotForm);
  status(message, "");
});

forgotForm.addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  const address = document.querySelector("#forgot-email");
  if (!address.validity.valid) return status(message, "Introduce un correo válido.", "error");
  try {
    await api("/api/auth/forgot-password", { method: "POST", body: JSON.stringify({ email: address.value, applicationCode: application.value }) });
    email.value = address.value;
    backToLogin("Si el correo corresponde a una cuenta con contraseña, te enviamos un enlace para restablecerla. Revisa tu bandeja de entrada.", "success");
  } catch (error) { handleSignInError(error); }
});

document.querySelector("#magic-link").addEventListener("click", async () => {
  await ready;
  if (!email.validity.valid || !email.value) {
    email.focus();
    return status(message, "Introduce tu correo para enviarte un enlace de acceso.", "error");
  }
  try {
    await api("/api/auth/magic-link/request", { method: "POST", body: JSON.stringify({ email: email.value, applicationCode: application.value }) });
    savePendingSignIn();
    status(message, "Si el correo tiene acceso a esta aplicación, te enviamos un enlace. Ábrelo en este navegador para continuar aquí.", "success");
  } catch (error) { handleSignInError(error); }
});

async function redeemMagicLink(token) {
  if (!token) return showBlocked("El enlace de acceso está incompleto. Pide uno nuevo.");
  status(message, "Verificando tu enlace de acceso…");
  try {
    const result = await api("/ui-api/session/magic-link", { method: "POST", body: JSON.stringify({ token }) });
    clearPendingSignIn();
    await continueSignIn(result);
  } catch (error) {
    clearPendingSignIn();
    if (["INVALID_MAGIC_LINK_TOKEN", "TOKEN_ALREADY_USED", "APP_NOT_FOUND"].includes(error.code))
      return backToLogin(error.code === "TOKEN_ALREADY_USED" ? "Este enlace de acceso ya se usó. Pide uno nuevo." : signInMessages.INVALID_MAGIC_LINK_TOKEN, "error");
    handleSignInError(error);
  }
}

// Remembers, in this browser only, which request the emailed link should continue.
function savePendingSignIn() {
  try {
    localStorage.setItem(PENDING_KEY, JSON.stringify({
      interactionId,
      saml: samlInteraction,
      application: application.value,
      returnUrl: interactionId ? null : returnUrl,
      expiresAt: Date.now() + 15 * 60 * 1000
    }));
  } catch { /* Storage may be disabled: the link then signs in without continuing the request. */ }
}

function readPendingSignIn() {
  try {
    const value = JSON.parse(localStorage.getItem(PENDING_KEY) || "null");
    return value && value.expiresAt > Date.now() ? value : null;
  } catch { return null; }
}

function clearPendingSignIn() {
  try { localStorage.removeItem(PENDING_KEY); } catch { /* Nothing stored. */ }
}

// --- Second factor ---------------------------------------------------------------------

function showMfa(result) {
  mfaPendingToken = result.mfaPendingToken;
  mfaMethod = result.mfaMethod === "email" ? "email" : "totp";
  setBackupMode(false);
  showView(mfaForm, document.querySelector("#mfa-code"));
  expireStepAfter(result.expiresIn);
  status(message, "Completa la verificación adicional.");
  // An email factor gets its code right away; the button sends another one.
  if (mfaMethod === "email") sendEmailCode();
}

function setBackupMode(enabled) {
  useBackupCode = enabled;
  const code = document.querySelector("#mfa-code");
  code.value = "";
  code.inputMode = enabled ? "text" : "numeric";
  code.autocomplete = enabled ? "off" : "one-time-code";
  document.querySelector("#mfa-label").textContent = enabled ? "Código de respaldo" : "Código de verificación";
  document.querySelector("#mfa-description").textContent = enabled
    ? "Escribe uno de los códigos de respaldo que guardaste al activar la verificación."
    : mfaMethod === "email"
      ? "Te enviamos un código de 6 dígitos a tu correo. Escríbelo para continuar."
      : "Introduce el código de 6 dígitos de tu app de autenticación.";
  document.querySelector("#mfa-use-backup").textContent = enabled
    ? (mfaMethod === "email" ? "Usar el código del correo" : "Usar la app de autenticación")
    : "Usar un código de respaldo";
  document.querySelector("#mfa-send-email").hidden = enabled || mfaMethod !== "email";
}

document.querySelector("#mfa-use-backup").addEventListener("click", () => {
  setBackupMode(!useBackupCode);
  document.querySelector("#mfa-code").focus();
});

document.querySelector("#mfa-send-email").addEventListener("click", () => sendEmailCode(true));

async function sendEmailCode(again = false) {
  try {
    await api("/ui-api/session/mfa/email-otp", { method: "POST", body: JSON.stringify({ mfaPendingToken }) });
    status(message, again ? "Te enviamos un código nuevo." : "Te enviamos un código a tu correo.", "success");
  } catch (error) { handleSignInError(error); }
}

mfaForm.addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  const code = document.querySelector("#mfa-code").value.trim();
  if (!code) return status(message, "Escribe el código.", "error");
  try {
    const result = await api("/ui-api/session/mfa", {
      method: "POST",
      body: JSON.stringify({
        mfaPendingToken,
        totpCode: !useBackupCode && mfaMethod === "totp" ? code : null,
        emailOtpCode: !useBackupCode && mfaMethod === "email" ? code : null,
        backupCode: useBackupCode ? code : null,
        trustDevice: false
      })
    });
    mfaPendingToken = "";
    setCsrf(result.csrfToken);
    await finishLogin();
  } catch (error) { handleSignInError(error); }
});

// --- Enrolling the factor the application requires ---------------------------------------

async function startTotpEnrollment(result) {
  enrollmentToken = result.enrollmentToken;
  status(message, "Preparando la verificación en dos pasos…");
  let setup;
  try {
    setup = await api("/ui-api/session/mfa/enrollment/start", { method: "POST", body: JSON.stringify({ enrollmentToken }) });
  } catch (error) { return handleSignInError(error); }
  const qr = document.querySelector("#totp-qr");
  qr.replaceChildren(qrSvg(setup.totpUri, "Código QR para configurar tu app de autenticación"));
  document.querySelector("#totp-secret").textContent = groupSecret(setup.secretBase32);
  document.querySelector("#totp-open").href = setup.totpUri;
  document.querySelector("#totp-copy").onclick = async () =>
    status(message, await copyText(setup.secretBase32) ? "Clave copiada." : "Copia la clave manualmente.", "success");
  showView(totpForm, document.querySelector("#totp-code"));
  expireStepAfter(result.expiresIn);
  status(message, "Esta aplicación exige verificación en dos pasos. Configúrala para continuar.");
}

totpForm.addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  const code = document.querySelector("#totp-code").value.trim();
  if (!/^\d{6}$/.test(code)) return status(message, "Escribe el código de 6 dígitos que muestra la app.", "error");
  try {
    const result = await api("/ui-api/session/mfa/enrollment/complete", { method: "POST", body: JSON.stringify({ enrollmentToken, totpCode: code }) });
    enrollmentToken = "";
    setCsrf(result.csrfToken);
    showBackupCodes(result.backupCodes);
  } catch (error) {
    document.querySelector("#totp-code").value = "";
    handleSignInError(error);
  }
});

function showBackupCodes(codes) {
  const list = document.querySelector("#backup-codes-list");
  list.replaceChildren(...codes.map(code => { const item = document.createElement("li"); item.textContent = code; return item; }));
  const text = codes.join("\n");
  document.querySelector("#backup-copy").onclick = async () =>
    status(message, await copyText(text) ? "Códigos copiados." : "Copia los códigos manualmente.", "success");
  document.querySelector("#backup-download").onclick = () => downloadText("codigos-de-respaldo.txt", `${text}\n`);
  const saved = document.querySelector("#backup-saved");
  const next = document.querySelector("#backup-continue");
  saved.checked = false;
  next.disabled = true;
  saved.onchange = () => { next.disabled = !saved.checked; };
  next.onclick = () => finishLogin();
  showView(backupView, saved);
  status(message, "Verificación en dos pasos activada.", "success");
}

function showPasskeyEnrollment(result) {
  enrollmentToken = result.enrollmentToken;
  document.querySelector("#enroll-passkey-description").textContent =
    "Esta aplicación exige una passkey, resistente al phishing. Créala con el desbloqueo de este dispositivo o una llave de seguridad.";
  const create = document.querySelector("#enroll-passkey-create");
  create.textContent = "Crear passkey";
  create.onclick = enrollPasskey;
  showView(passkeyEnrollView, create);
  expireStepAfter(result.expiresIn);
  status(message, "");
}

async function enrollPasskey() {
  if (!window.PublicKeyCredential) return status(message, "Este navegador no admite passkeys. Usa otro navegador o dispositivo.", "error");
  try {
    status(message, "Sigue las indicaciones de tu dispositivo…");
    const options = await api("/ui-api/session/passkey/enrollment/options", { method: "POST", body: JSON.stringify({ enrollmentToken }) });
    const credential = await createPasskey(options.publicKey);
    await api("/ui-api/session/passkey/enrollment/complete", {
      method: "POST",
      body: JSON.stringify({ enrollmentToken, credentialJson: serializeCredential(credential), name: "Passkey" })
    });
    enrollmentToken = "";
    clearTimeout(stepTimer);
    // The new passkey is also the sign-in the application asked for.
    document.querySelector("#enroll-passkey-description").textContent = "Passkey creada. Úsala ahora para completar el inicio de sesión.";
    const create = document.querySelector("#enroll-passkey-create");
    create.textContent = "Continuar con mi passkey";
    create.onclick = () => signInWithPasskey();
    create.focus();
    status(message, "Passkey registrada.", "success");
  } catch (error) {
    if (error.code) return handleSignInError(error);
    status(message, passkeyErrorMessage(error), "error");
  }
}

// --- Passkey sign-in -------------------------------------------------------------------

document.querySelector("#passkey").addEventListener("click", async () => {
  await ready;
  await signInWithPasskey();
});

// The email is optional: without it the browser offers the passkeys it holds for this site.
async function signInWithPasskey() {
  if (!window.PublicKeyCredential) return status(message, "Este navegador no admite passkeys.", "error");
  if (email.value && !email.validity.valid) return status(message, "Introduce un correo válido o déjalo vacío.", "error");
  try {
    status(message, "Esperando tu passkey…");
    const challenge = await api("/ui-api/session/passkey/options", {
      method: "POST", body: JSON.stringify({ email: email.value || null, applicationCode: application.value })
    });
    const credential = await getPasskey(challenge.publicKey);
    const result = await api("/ui-api/session/passkey/complete", {
      method: "POST",
      body: JSON.stringify({ interactionId: challenge.interactionId, credentialJson: serializeCredential(credential) })
    });
    await continueSignIn(result);
  } catch (error) {
    if (error.code) return handleSignInError(error);
    status(message, passkeyErrorMessage(error), "error");
  }
}

// --- Sessions --------------------------------------------------------------------------

async function currentSession() {
  try {
    const session = await api("/ui-api/session");
    setCsrf(session.csrfToken);
    return session;
  } catch { return null; /* An anonymous visit is expected. */ }
}

async function resumeExistingSession() {
  if (!current) return false;
  if (!email.value && current.user.email) email.value = current.user.email;
  // Authorization requests are checked against the client's application by the server. A direct
  // visit only reuses a session issued for the requested application; otherwise the user signs
  // in to it, which continues the same single sign-on session.
  if (!interactionId && !current.user.applications.includes(application.value)) return false;
  await finishLogin();
  return true;
}

// --- Enterprise federation ---------------------------------------------------------------

// Federation requested by the client: idp names the provider, domain_hint lets home realm
// discovery pick it. Returns true when the browser is on its way to the provider.
async function federateFromHints() {
  if (interaction?.identityProvider) return startFederation(interaction.identityProvider);
  if (interaction?.domainHint) return federate({ domain: interaction.domainHint });
  return false;
}

async function federateEmail(value) {
  if (!federationAvailable) return false;
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
        returnUrl: federationReturnUrl(),
        loginHint: email.value || undefined
      })
    });
    leaving = true;
    location.assign(result.redirectUrl);
    return true;
  } catch (error) {
    showFederationError(error.code, error.message);
    return false;
  }
}

// The application of the sign-in: the authorization request's, or the one of a direct visit. A
// SAML request names its application through the context the page already loaded.
function targetOfSignIn() {
  return interactionId && !samlInteraction ? { interactionId } : { applicationCode: application.value };
}

function interactionPath(suffix = "") {
  return `${samlInteraction ? "/saml/idp/interactions" : "/oauth/interactions"}/${encodeURIComponent(interactionId)}${suffix}`;
}

// Where an upstream identity provider sends the browser back: the SAML request continues here.
function federationReturnUrl() {
  if (samlInteraction) return `/login?saml_interaction=${encodeURIComponent(interactionId)}`;
  return interactionId ? undefined : returnUrl || undefined;
}

// Back from the identity provider: this browser redeems the result, and the application's
// policy and second factor apply exactly as after a password.
async function completeFederation(handle) {
  status(message, "Completando el inicio de sesión con tu organización…");
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
  ACCOUNT_LINKING_REQUIRED: "Ya existe una cuenta con este correo. Inicia sesión con ella y vincula tu organización desde tu portal.",
  JIT_PROVISIONING_DISABLED: "Tu cuenta aún no está habilitada en esta aplicación. Solicita acceso a tu administrador.",
  ACCESS_DENIED: "No tienes acceso a esta aplicación.",
  USER_INACTIVE: "Tu cuenta está inactiva.",
  ACCESS_POLICY_DENIED: "La política de acceso de esta aplicación no permite este inicio de sesión.",
  PASSKEY_REQUIRED: "Esta aplicación exige una passkey. Usa \"Usar una passkey\" para continuar.",
  FEDERATION_PROVIDER_NOT_FOUND: "El proveedor de identidad de tu organización no está disponible para esta aplicación.",
  FEDERATION_CALLBACK_NOT_HOSTED: "El proveedor de identidad de tu organización no está disponible en este momento.",
  OIDC_DISCOVERY_FAILED: "El proveedor de identidad de tu organización no está disponible en este momento.",
  SAML_NOT_CONFIGURED: "El proveedor de identidad de tu organización no está disponible en este momento.",
  INVALID_INTERACTION: "Esta solicitud de inicio de sesión expiró o se abrió en otro navegador. Vuelve a la aplicación e inténtalo de nuevo."
};

function showFederationError(code, fallback) {
  const text = federationMessages[code]
    ?? (fallback && !/^(OIDC|SAML|INVALID_SAML|INVALID_OIDC|FEDERATION_UPSTREAM)/.test(code ?? "") ? fallback : "Tu organización no pudo completar el inicio de sesión. Si continúa, contacta a soporte.");
  backToLogin(text, "error");
}

// --- Finishing: consent, step-up and the redirect back to the application ------------------

async function finishLogin() {
  clearTimeout(stepTimer);
  if (!interactionId) {
    leaving = true;
    return location.replace(safeLocalPath(returnUrl, location.origin, "/portal"));
  }
  if (pendingConsent === null) return showConsent();
  const consent = pendingConsent;
  pendingConsent = null;
  return completeConsent(consent);
}

// The client's application asked for a stronger sign-in than the current session: verify a
// second factor (or enroll the one it requires) without signing in again, then complete the
// same request.
async function startStepUp() {
  showView(null);
  let result;
  try {
    result = await api(interactionPath("/step-up"), { method: "POST" });
  } catch (error) {
    if (error.code === "PASSKEY_REQUIRED") {
      showView(loginForm, document.querySelector("#passkey"));
      return status(message, "Esta aplicación exige una passkey. Usa \"Usar una passkey\" para continuar.");
    }
    if (error.code === "ACCESS_DENIED")
      return showBlocked("La política de acceso de esta aplicación no permite este inicio de sesión.");
    return handleInteractionError(error);
  }
  if (!result.stepUpRequired) return finishLogin();
  status(message, "Esta aplicación requiere una verificación adicional.");
  await continueSignIn(result);
}

async function loadInteractionContext() {
  try { return await api(interactionPath("/context")); }
  catch { return null; }
}

async function loadLoginOptions() {
  try { return await api(`/ui-api/session/login-options?applicationCode=${encodeURIComponent(application.value || "AUTHCENTER")}`); }
  catch { return null; }
}

function requireFreshSignIn() {
  backToLogin("Por seguridad, vuelve a iniciar sesión para continuar.");
}

function handleInteractionError(error) {
  if (error.code === "LOGIN_REQUIRED") return requireFreshSignIn();
  if (["INVALID_INTERACTION", "INTERACTION_BINDING_MISMATCH", "INVALID_CLIENT"].includes(error.code)) return showUnavailable();
  status(message, error.message, "error");
}

async function showConsent() {
  // SAML applications are registered by an administrator: there is no consent to ask for.
  if (samlInteraction) return completeConsent(true);
  let details;
  try { details = await api(`/oauth/interactions/${encodeURIComponent(interactionId)}`); }
  catch (error) { return handleInteractionError(error); }
  if (details.requiresReauthentication) return requireFreshSignIn();
  if (!details.requiresConsent) return completeConsent(true);
  showView(consentView, document.querySelector("#consent-allow"));
  status(message, "");
  document.querySelector("#consent-description").textContent = `${details.clientDisplayName} solicita acceso a ${details.applicationName}.`;
  const scopes = document.querySelector("#consent-scopes");
  scopes.replaceChildren(...details.scopes.map(scope => {
    const badge = document.createElement("span"); badge.className = "badge"; badge.textContent = scope; return badge;
  }));
  document.querySelector("#consent-allow").onclick = () => completeConsent(true);
  document.querySelector("#consent-deny").onclick = () => completeConsent(false);
}

async function completeConsent(consent) {
  try {
    const result = samlInteraction
      ? await api(interactionPath("/complete"), { method: "POST" })
      : await api("/oauth/authorize/complete", {
        method: "POST",
        headers: { "X-AuthCenter-UI": "1" },
        body: JSON.stringify({ interactionId, consent })
      });
    leaving = true;
    location.assign(result.redirectUrl);
  } catch (error) {
    if (error.code !== "STEP_UP_REQUIRED") return handleInteractionError(error);
    pendingConsent = consent;
    await startStepUp();
  }
}

function loginPath() {
  const query = new URLSearchParams();
  if (application.value && application.value !== "AUTHCENTER") query.set("application", application.value);
  if (returnUrl) query.set("return_url", returnUrl);
  return `/login${query.size ? `?${query}` : ""}`;
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

// An authorization request names its application: the page signs the user in to that
// application (its policies, branding and hint) instead of asking the user for a code.
async function initialize() {
  interaction = interactionId ? await loadInteractionContext() : null;
  if (magicLinkPage && interactionId && !interaction) interactionId = null;
  application.value = interaction?.applicationCode || application.value;
  options = interaction ?? await loadLoginOptions();
  await loadBranding();
  if (interaction?.loginHint) email.value = interaction.loginHint;
  passwordAllowed = options?.allowPasswordLogin ?? true;
  magicLinkAllowed = options?.allowMagicLink === true;
  federationAvailable = options?.federationAvailable === true;
  // An application that only allows federated sign-in keeps the email step for home realm
  // discovery but never asks for a password.
  document.querySelector("#password-field").hidden = !passwordAllowed;
  password.required = passwordAllowed;
  document.querySelector("#forgot-link").hidden = !passwordAllowed;
  document.querySelector("#magic-link").hidden = !magicLinkAllowed;
  document.querySelector("#federation-hint").hidden = !federationAvailable;
  document.querySelector("#passkey").hidden = !window.PublicKeyCredential;
  watchInteractionExpiry();
  current = await currentSession();
  markReady();

  if (interactionId && !interaction) showUnavailable();
  else if (magicLinkPage) await redeemMagicLink(magicToken);
  else if (federationResult) await completeFederation(federationResult);
  else if (federationError) showFederationError(federationError);
  else if (interaction?.requiresFreshLogin) {
    if (!await federateFromHints()) status(message, `Confirma tu identidad para continuar en ${interaction.applicationName}.`);
  }
  else if (!await resumeExistingSession()) await federateFromHints();
}

await initialize();
