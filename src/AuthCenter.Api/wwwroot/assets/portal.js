import {
  api, copyText, createPasskey, downloadText, errorMessage, formatDate, getPasskey, groupSecret, passkeyErrorMessage,
  passwordProblem, requireSessionState, serializeCredential, signOut, status
} from "./shared.js";
import { qrSvg } from "./qr.js";

const message = document.querySelector("#status");
const panels = ["overview", "security", "sessions", "applications", "approvals", "providers", "consents", "account"];
const params = new URLSearchParams(location.search);
// Back from linking an enterprise provider: the single-use result leaves the address bar at once.
const federationLink = params.get("federation_link");
if (federationLink) {
  params.delete("federation_link");
  history.replaceState(null, "", `${location.pathname}${params.size ? `?${params}` : ""}${location.hash}`);
}

let session = null;
let profile = null;
let mfa = null;
let passkeys = [];
let requestable = [];
// The access review whose items are open in the approvals panel.
let openReview = null;

document.querySelector("#logout").addEventListener("click", signOut);
document.querySelectorAll("[data-panel]").forEach(link => link.addEventListener("click", event => {
  event.preventDefault();
  showPanel(link.dataset.panel, true);
}));
window.addEventListener("hashchange", () => showPanel(location.hash.slice(1), false));

// The handlers below are registered before the account loads and wait for it.
let markReady;
const ready = new Promise(resolve => { markReady = resolve; });

async function refresh() {
  try {
    const [me, sessions, devices, passkeyItems, providers, linkable, consents, applications, mfaStatus, catalog, requests, approvals] = await Promise.all([
      api("/api/auth/me"), api("/api/auth/sessions"), api("/api/auth/trusted-devices"), api("/api/auth/passkeys"),
      api("/api/auth/external-providers"), api("/ui-api/session/federation/linkable"), api("/oauth/consents"),
      api("/api/auth/applications"), api("/api/auth/mfa/status"), api("/api/auth/access-requests/catalog"),
      api("/api/auth/access-requests"), api("/api/auth/approvals")
    ]);
    profile = me;
    mfa = mfaStatus;
    passkeys = passkeyItems;
    renderHeader();
    renderOverview(sessions, passkeys, applications, consents);
    renderPassword();
    renderMfa();
    renderPasskeys();
    renderSessions(sessions, devices);
    renderApplications(applications);
    renderAccessRequests(catalog, requests);
    renderApprovals(approvals);
    renderProviders(providers, linkable);
    renderConsents(consents);
    renderAccount();
  } catch (error) { status(message, errorText(error), "error"); }
}

// --- Rendering -----------------------------------------------------------------------------

function renderHeader() {
  document.querySelector("#user-name").textContent = profile.fullName || profile.email;
  document.querySelector("#user-email").textContent = profile.email;
  document.querySelector("#admin-link").hidden = !session.user.permissions?.some(value => value.startsWith("AUTHCENTER_"));
}

function renderOverview(sessions, passkeyItems, applications, consents) {
  const grid = document.querySelector("#overview-grid");
  const cards = [
    ["Verificación en dos pasos", mfa.isEnabled ? "Activa" : "Inactiva", "security"],
    ["Passkeys", passkeyItems.length, "security"],
    ["Sesiones activas", sessions.length, "sessions"],
    ["Aplicaciones", applications.length, "applications"],
    ["Aplicaciones autorizadas", consents.length, "consents"]
  ];
  grid.replaceChildren(...cards.map(([label, value, panel]) => {
    const card = element("article", "card");
    const heading = element("h3", "", label);
    const metric = element("div", "metric", String(value));
    const link = element("a", "", "Ver detalles");
    link.href = `#${panel}`;
    card.append(heading, metric, link);
    return card;
  }));
}

function renderPassword() {
  const form = document.querySelector("#password-form");
  form.hidden = !profile.hasLocalPassword;
  document.querySelector("#password-summary").textContent = profile.hasLocalPassword
    ? "Cambia tu contraseña periódicamente y no la reutilices en otros sitios."
    : "Tu cuenta no usa contraseña: inicias sesión con tu organización, una passkey o un enlace de acceso.";
}

const methodNames = { Totp: "app de autenticación", EmailOtp: "código por correo" };

function renderMfa() {
  const summary = document.querySelector("#mfa-summary");
  if (mfa.isEnabled) {
    const backup = mfa.method === "Totp"
      ? (mfa.hasBackupCodes ? ` Tienes códigos de respaldo generados el ${formatDate(mfa.backupCodesRegeneratedAt)}.` : " No tienes códigos de respaldo.")
      : "";
    summary.textContent = `Activa con ${methodNames[mfa.method] ?? "un segundo factor"} desde el ${formatDate(mfa.enabledAt)}.${backup}`;
  } else {
    summary.textContent = "Inactiva. Agrega un segundo factor para proteger tu cuenta aunque alguien conozca tu contraseña.";
  }
  document.querySelector("#mfa-totp-setup").hidden = mfa.isEnabled;
  document.querySelector("#mfa-email-setup").hidden = mfa.isEnabled;
  document.querySelector("#mfa-backup-regenerate").hidden = !(mfa.isEnabled && mfa.method === "Totp");
  document.querySelector("#mfa-disable").hidden = !mfa.isEnabled;
}

function renderPasskeys() {
  renderList("passkeys-list", passkeys, item => [
    item.name,
    `Creada ${formatDate(item.createdAt)}${item.isBackedUp ? " · sincronizada" : ""}`
  ], item => [
    button("Renombrar", "secondary", () => renamePasskey(item)),
    button("Eliminar", "danger", () => removePasskey(item))
  ], "No tienes passkeys registradas.");
}

function renderSessions(sessions, devices) {
  renderList("sessions-list", sessions, item => [
    `${item.id === session.sessionId ? "Esta sesión · " : ""}${describeAgent(item.userAgent)}`,
    [item.applicationName, `iniciada ${formatDate(item.createdAt)}`, item.ipAddress ? `IP ${item.ipAddress}` : null, `expira ${formatDate(item.expiresAt)}`]
      .filter(Boolean).join(" · ")
  ], item => [button(item.id === session.sessionId ? "Cerrar esta sesión" : "Revocar", "danger", () => revokeSession(item))], "No hay sesiones activas.");
  renderList("devices-list", devices, item => [
    item.deviceName || "Dispositivo",
    `Confiable desde ${formatDate(item.createdAt)} · expira ${formatDate(item.expiresAt)}`
  ], item => [button("Revocar", "danger", () => mutate(`/api/auth/trusted-devices/${item.id}`, "DELETE", null, "Dispositivo revocado."))], "No hay dispositivos de confianza.");
}

function renderApplications(applications) {
  renderList("applications-list", applications, item => [
    item.name,
    [item.description, item.grantedAt ? `Acceso desde ${formatDate(item.grantedAt)}` : null, item.groups.length ? `Por grupo: ${item.groups.join(", ")}` : null].filter(Boolean).join(" · ")
  ], item => [
    item.launchUrl ? launchLink(item.launchUrl, item.name) : null,
    item.supportUrl ? externalLink(item.supportUrl, "Soporte") : null
  ].filter(Boolean), "Todavía no tienes acceso a aplicaciones.", item => item.logoUrl);
}

const requestStatuses = { Pending: "Pendiente", Approved: "Aprobada", Rejected: "Rechazada", Cancelled: "Cancelada", Expired: "Expirada" };
const requestSources = { Portal: "Solicitada en el portal", Registration: "Registro que requiere aprobación", Administrator: "Alta de un administrador" };

function renderAccessRequests(catalog, requests) {
  requestable = catalog;
  const form = document.querySelector("#request-access-form");
  form.hidden = catalog.length === 0;
  document.querySelector("#request-access-help").textContent = catalog.length
    ? "Pide acceso a otra aplicación, o un rol en una que ya usas. Sus responsables deciden y te avisamos por correo."
    : "Ninguna aplicación acepta solicitudes de acceso por ahora.";
  const select = document.querySelector("#request-application");
  const selected = select.value;
  select.replaceChildren(...catalog.map(item => {
    const option = element("option", "", item.hasAccess ? `${item.name} (ya tienes acceso)` : item.name);
    option.value = item.id;
    return option;
  }));
  if (catalog.some(item => item.id === selected)) select.value = selected;
  renderRequestableRoles();

  renderList("requests-list", requests, item => [
    item.roleName ? `${item.applicationName} · ${item.roleName}` : item.applicationName,
    [
      `${requestStatuses[item.status] ?? item.status} · ${formatDate(item.createdAt)}`,
      item.status === "Pending" && item.expiresAt ? `vence ${formatDate(item.expiresAt)}` : null,
      item.decisionComment ? `“${item.decisionComment}”` : null
    ].filter(Boolean).join(" · ")
  ], item => item.status === "Pending"
    ? [button("Cancelar", "secondary", () => mutate(`/api/auth/access-requests/${item.id}/cancel`, "POST", null, "Solicitud cancelada."))]
    : [], "No has solicitado accesos.");
}

function renderRequestableRoles() {
  const application = requestable.find(item => item.id === document.querySelector("#request-application").value);
  const roles = document.querySelector("#request-role");
  const none = element("option", "", "Sin un rol en particular");
  none.value = "";
  roles.replaceChildren(none, ...(application?.roles ?? []).map(role => {
    const option = element("option", "", role.name);
    option.value = role.id;
    return option;
  }));
}

function renderApprovals(approvals) {
  const nav = document.querySelector("#approvals-nav");
  const owner = approvals.requests.length > 0 || approvals.reviews.length > 0;
  // Once shown it stays, so deciding the last item does not pull the panel away.
  if (owner) nav.hidden = false;
  renderList("approval-requests-list", approvals.requests, item => [
    `${item.requester.fullName} (${item.requester.email})`,
    [
      item.roleName ? `${item.applicationName} · ${item.roleName}` : item.applicationName,
      requestSources[item.source] ?? item.source,
      formatDate(item.createdAt),
      item.justification ? `“${item.justification}”` : null
    ].filter(Boolean).join(" · ")
  ], item => [
    button("Aprobar", "", () => decideRequest(item, true)),
    button("Rechazar", "danger", () => decideRequest(item, false))
  ], "No hay solicitudes esperando tu decisión.");
  renderList("reviews-list", approvals.reviews, item => [
    `${item.name} · ${item.applicationName}`,
    `${item.pendingItems} de ${item.totalItems} por revisar · vence ${formatDate(item.dueAt)}${item.revokeUnreviewed ? " · lo no revisado se revoca" : " · lo no revisado se mantiene"}`
  ], item => [button("Revisar", "", () => openReviewItems(item))], "No tienes revisiones de acceso en curso.");
  if (openReview) {
    const current = approvals.reviews.find(item => item.id === openReview.id);
    if (current) void openReviewItems(current, false);
    else closeReview();
  }
}

async function openReviewItems(review, focus = true) {
  openReview = review;
  const detail = document.querySelector("#review-detail");
  detail.hidden = false;
  const title = document.querySelector("#review-title");
  title.textContent = `${review.name} · ${review.applicationName}`;
  document.querySelector("#review-summary").textContent =
    `Decide si cada persona debe conservar el acceso. ${review.revokeUnreviewed ? "Lo que nadie revise se revocará" : "Lo que nadie revise se mantendrá"} el ${formatDate(review.dueAt)}.`;
  try {
    const items = await api(`/api/auth/approvals/reviews/${review.id}/items?pageSize=100`);
    renderList("review-items", items.items, item => [
      `${item.user.fullName} (${item.user.email})`,
      [
        item.hasDirectAccess ? "Acceso directo" : null,
        item.groups.length ? `Por grupo: ${item.groups.join(", ")}` : null,
        item.roles.length ? `Roles: ${item.roles.join(", ")}` : null,
        reviewDecision(item)
      ].filter(Boolean).join(" · ")
    ], item => item.canDecide
      ? [button("Mantener", "secondary", () => decideReviewItem(item, "Keep")), button("Revocar", "danger", () => decideReviewItem(item, "Revoke"))]
      : [], "La revisión no tiene accesos.");
    if (focus) title.focus();
  } catch (error) { status(message, errorText(error), "error"); }
}

function closeReview() {
  openReview = null;
  document.querySelector("#review-detail").hidden = true;
}

function reviewDecision(item) {
  if (item.decision === "Pending") return item.canDecide ? "Sin revisar" : "Sin revisar (tu propio acceso lo revisa otra persona)";
  const decided = item.decision === "Keep" ? "Se mantiene" : "Revocado";
  const remaining = item.remediationRequired ? "sigue con acceso por sus grupos hasta que un administrador lo quite" : null;
  return [decided, remaining, item.comment ? `“${item.comment}”` : null].filter(Boolean).join(" · ");
}

async function decideRequest(item, approve) {
  const who = `${item.requester.fullName} (${item.requester.email})`;
  const what = item.roleName ? `${item.applicationName} con el rol ${item.roleName}` : item.applicationName;
  const comment = await askDecision(
    approve ? "Aprobar la solicitud" : "Rechazar la solicitud",
    approve ? `${who} tendrá acceso a ${what}.` : `${who} no tendrá acceso a ${what}. Cuéntale por qué.`,
    approve ? "Comentario (opcional)" : "Motivo",
    !approve,
    approve ? "Aprobar" : "Rechazar");
  if (comment === null) return;
  await mutate(`/api/auth/approvals/requests/${item.id}/${approve ? "approve" : "reject"}`, "POST", null,
    approve ? "Solicitud aprobada. Le avisamos por correo." : "Solicitud rechazada. Le avisamos por correo.", { comment: comment || null });
}

async function decideReviewItem(item, decision) {
  const revoke = decision === "Revoke";
  const comment = await askDecision(
    revoke ? "Revocar el acceso" : "Mantener el acceso",
    revoke
      ? `${item.user.fullName} perderá el acceso directo${item.groups.length ? "; el que le dan sus grupos debe quitarlo un administrador" : ""}.`
      : `${item.user.fullName} conservará el acceso.`,
    "Comentario (opcional)", false, revoke ? "Revocar" : "Mantener");
  if (comment === null) return;
  await mutate(`/api/auth/approvals/reviews/${item.campaignId}/items/${item.id}`, "POST", null,
    revoke ? "Acceso revocado." : "Acceso mantenido.", { decision, comment: comment || null });
}

/** Asks for the comment of a decision; resolves null when cancelled. */
function askDecision(title, description, label, required, confirmLabel) {
  const dialog = document.querySelector("#decision-dialog");
  const form = document.querySelector("#decision-form");
  const field = document.querySelector("#decision-comment");
  const feedback = document.querySelector("#decision-status");
  document.querySelector("#decision-title").textContent = title;
  document.querySelector("#decision-description").textContent = description;
  document.querySelector("#decision-label").textContent = label;
  document.querySelector("#decision-submit").textContent = confirmLabel;
  field.required = required;
  field.value = "";
  status(feedback, "");
  dialog.showModal();
  field.focus();
  return new Promise(resolve => {
    const close = value => { form.onsubmit = null; dialog.onclose = null; if (dialog.open) dialog.close(); resolve(value); };
    document.querySelector("#decision-cancel").onclick = () => close(null);
    dialog.onclose = () => close(null);
    form.onsubmit = event => {
      event.preventDefault();
      const value = field.value.trim();
      if (required && !value) return status(feedback, "Escribe el motivo.", "error");
      close(value);
    };
  });
}

function renderProviders(providers, linkable) {
  renderList("providers-list", providers, item => [
    item.providerName || item.provider,
    `${item.email || "Cuenta vinculada"} · vinculada ${formatDate(item.linkedAt)}${item.lastUsedAt ? ` · último uso ${formatDate(item.lastUsedAt)}` : ""}`
  ], item => [button("Desvincular", "danger", () => unlinkProvider(item))], "No tienes proveedores vinculados.");
  const available = linkable.filter(item => !item.linked);
  renderList("linkable-list", available, item => [item.name, `${item.applicationName} · ${item.protocol === "Saml2" ? "SAML" : "OpenID Connect"}`],
    item => [button("Vincular", "", () => linkProvider(item))], "No hay proveedores empresariales disponibles para vincular.");
}

function renderConsents(consents) {
  // What each application may do, in the words of the consent screen.
  renderList("consents-list", consents, item => [
    item.clientDisplayName,
    [item.applicationName, (item.scopeDescriptions?.length ? item.scopeDescriptions.map(scope => scope.description) : item.scopes).join(", "), `desde ${formatDate(item.grantedAt)}`]
      .filter(Boolean).join(" · ")
  ], item => [button("Revocar", "danger", () => mutate(`/oauth/consents/${item.id}`, "DELETE", null, "Consentimiento revocado."))], "No has autorizado aplicaciones de terceros.");
}

function renderAccount() {
  document.querySelector("#email-summary").textContent = `Tu correo actual es ${profile.email}. Te enviaremos un enlace de confirmación al correo nuevo.`;
  document.querySelector("#delete-password-field").hidden = !profile.hasLocalPassword;
}

function renderList(containerId, items, describe, actions, emptyText, logo) {
  const container = document.querySelector(`#${containerId}`);
  if (!items.length) {
    container.replaceChildren(element("p", "card muted", emptyText));
    return;
  }
  container.replaceChildren(...items.map(item => {
    const card = element("article", "card item");
    const text = element("div");
    const [title, detail] = describe(item);
    const heading = element("p", "", "");
    heading.append(element("strong", "", title));
    text.append(heading);
    if (detail) text.append(element("p", "detail", detail));
    const logoUrl = logo?.(item);
    if (logoUrl) {
      const image = element("img", "app-logo");
      image.src = logoUrl;
      image.alt = "";
      card.append(image);
    }
    const buttons = element("div", "actions");
    buttons.append(...actions(item));
    card.append(text, buttons);
    return card;
  }));
}

function element(tag, className = "", text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function button(label, kind, action) {
  const node = element("button", kind, label);
  node.type = "button";
  node.addEventListener("click", action);
  return node;
}

// Signs in to the application from here (a SAML sign-in it accepts without a request of its own).
function launchLink(path, name) {
  const link = element("a", "button", "Abrir");
  link.href = path;
  link.setAttribute("aria-label", `Abrir ${name}`);
  return link;
}

function externalLink(url, label) {
  const link = element("a", "", label);
  link.href = url;
  link.rel = "noopener noreferrer";
  link.target = "_blank";
  return link;
}

function describeAgent(userAgent) {
  if (!userAgent) return "Sesión";
  const browser = /Edg\//.test(userAgent) ? "Edge" : /Chrome\//.test(userAgent) ? "Chrome" : /Firefox\//.test(userAgent) ? "Firefox" : /Safari\//.test(userAgent) ? "Safari" : "Navegador";
  const system = /Windows/.test(userAgent) ? "Windows" : /Android/.test(userAgent) ? "Android" : /iPhone|iPad/.test(userAgent) ? "iOS" : /Mac OS X/.test(userAgent) ? "macOS" : /Linux/.test(userAgent) ? "Linux" : "";
  return system ? `${browser} en ${system}` : browser;
}

// --- Actions -------------------------------------------------------------------------------

async function mutate(path, method, proof, done, body) {
  try {
    await api(path, {
      method,
      headers: proof ? { "X-AuthCenter-Reauthentication": proof } : {},
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    status(message, done ?? "Cambio aplicado.", "success");
    await refresh();
    return true;
  } catch (error) {
    status(message, errorText(error), "error");
    return false;
  }
}

async function revokeSession(item) {
  if (item.id === session.sessionId) return signOut();
  await mutate(`/api/auth/sessions/${item.id}`, "DELETE", null, "Sesión revocada.");
}

document.querySelector("#revoke-all-sessions").addEventListener("click", async () => {
  await ready;
  const proof = await reauthenticate("session.revoke-all", "Cerrarás todas tus sesiones, incluida esta.");
  if (!proof) return;
  try {
    await api("/api/auth/sessions", { method: "DELETE", headers: { "X-AuthCenter-Reauthentication": proof } });
    location.replace("/login?signed_out=1");
  } catch (error) { status(message, errorText(error), "error"); }
});

document.querySelector("#password-form").addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  const currentPassword = document.querySelector("#current-password").value;
  const newPassword = document.querySelector("#changed-password").value;
  if (!currentPassword) return status(message, "Escribe tu contraseña actual.", "error");
  const problem = passwordProblem(newPassword);
  if (problem) return status(message, problem, "error");
  if (newPassword === currentPassword) return status(message, "La contraseña nueva debe ser distinta de la actual.", "error");
  if (newPassword !== document.querySelector("#changed-password-confirm").value) return status(message, "Las contraseñas no coinciden.", "error");
  if (await mutate("/api/auth/change-password", "POST", null, "Contraseña actualizada. Te enviamos un aviso por correo.", { currentPassword, newPassword }))
    event.target.reset();
});

document.querySelector("#email-form").addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  const field = document.querySelector("#new-email");
  if (!field.validity.valid || !field.value) return status(message, "Introduce un correo válido.", "error");
  const proof = await reauthenticate("account.change-email", "Confirma tu identidad para cambiar el correo de tu cuenta.");
  if (!proof) return;
  if (await mutate("/api/auth/email-change/request", "POST", proof, `Te enviamos un enlace de confirmación a ${field.value}. El cambio se aplica al abrirlo.`, { newEmail: field.value }))
    event.target.reset();
});

document.querySelector("#delete-form").addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  if (!document.querySelector("#delete-confirm").checked) return status(message, "Marca la casilla para confirmar la eliminación.", "error");
  const password = document.querySelector("#delete-password").value;
  if (profile.hasLocalPassword && !password) return status(message, "Escribe tu contraseña para eliminar la cuenta.", "error");
  try {
    await api("/api/auth/account", { method: "DELETE", body: JSON.stringify({ password: password || null, confirmDeletion: true }) });
    location.replace("/login?account_deleted=1");
  } catch (error) { status(message, errorText(error), "error"); }
});

document.querySelector("#request-application").addEventListener("change", renderRequestableRoles);

document.querySelector("#request-access-form").addEventListener("submit", async event => {
  event.preventDefault();
  await ready;
  const applicationSystemId = document.querySelector("#request-application").value;
  const roleId = document.querySelector("#request-role").value || null;
  const justification = document.querySelector("#request-justification").value.trim();
  if (!applicationSystemId) return status(message, "Elige la aplicación.", "error");
  if (!justification) return status(message, "Cuenta para qué necesitas el acceso.", "error");
  if (await mutate("/api/auth/access-requests", "POST", null, "Solicitud enviada. Te avisaremos por correo cuando la decidan.", { applicationSystemId, roleId, justification }))
    document.querySelector("#request-justification").value = "";
});

// --- Two-step verification -------------------------------------------------------------------

document.querySelector("#mfa-totp-setup").addEventListener("click", async () => {
  await ready;
  const proof = await reauthenticate("factor.enroll", "Confirma tu identidad para agregar un segundo factor.");
  if (!proof) return;
  let setup;
  try {
    setup = await api("/api/auth/mfa/setup", { method: "POST", headers: { "X-AuthCenter-Reauthentication": proof } });
  } catch (error) { return status(message, errorText(error), "error"); }
  const result = await promptCode({
    title: "Configura tu app de autenticación",
    description: "Escanea el código con tu app de autenticación y escribe el código de 6 dígitos que muestre.",
    totpUri: setup.totpUri,
    secret: setup.secretBase32,
    label: "Código de la app",
    submit: "Activar",
    action: code => api("/api/auth/mfa/enable", { method: "POST", body: JSON.stringify({ totpCode: code }) })
  });
  if (!result) return;
  await refresh();
  status(message, "Verificación en dos pasos activada.", "success");
  await showBackupCodes(result.codes);
});

document.querySelector("#mfa-email-setup").addEventListener("click", async () => {
  await ready;
  const proof = await reauthenticate("factor.enroll", "Confirma tu identidad para agregar un segundo factor.");
  if (!proof) return;
  try {
    await api("/api/auth/mfa/email-otp/setup", { method: "POST", headers: { "X-AuthCenter-Reauthentication": proof } });
  } catch (error) { return status(message, errorText(error), "error"); }
  const result = await promptCode({
    title: "Recibir códigos por correo",
    description: `Te enviamos un código a ${profile.email}. Escríbelo para activar la verificación por correo.`,
    label: "Código del correo",
    submit: "Activar",
    action: code => api("/api/auth/mfa/email-otp/enable", { method: "POST", body: JSON.stringify({ code }) })
  });
  if (!result) return;
  await refresh();
  status(message, "Verificación en dos pasos por correo activada.", "success");
});

document.querySelector("#mfa-backup-regenerate").addEventListener("click", async () => {
  await ready;
  const result = await promptCode({
    title: "Generar códigos de respaldo nuevos",
    description: "Los códigos anteriores dejarán de funcionar. Escribe un código de tu app de autenticación.",
    label: "Código de la app",
    submit: "Generar",
    action: code => api("/api/auth/mfa/backup-codes", { method: "POST", body: JSON.stringify({ totpCode: code }) })
  });
  if (!result) return;
  await refresh();
  await showBackupCodes(result.codes);
});

document.querySelector("#mfa-disable").addEventListener("click", async () => {
  await ready;
  const email = mfa.method === "EmailOtp";
  if (email) {
    try { await api("/api/auth/mfa/email-otp/verification", { method: "POST" }); }
    catch (error) { return status(message, errorText(error), "error"); }
  }
  let backup = false;
  const result = await promptCode({
    title: "Desactivar la verificación en dos pasos",
    description: email
      ? `Te enviamos un código a ${profile.email}. Escríbelo para confirmar.`
      : "Escribe un código de tu app de autenticación o uno de tus códigos de respaldo.",
    label: email ? "Código del correo" : "Código de verificación",
    submit: "Desactivar",
    danger: true,
    extra: email ? null : { label: "Usar un código de respaldo", toggle: on => { backup = on; return on ? "Usar la app de autenticación" : "Usar un código de respaldo"; } },
    action: code => api("/api/auth/mfa", {
      method: "DELETE",
      body: JSON.stringify(email ? { emailOtpCode: code } : backup ? { backupCode: code } : { totpCode: code })
    })
  });
  if (result === null) return;
  await refresh();
  status(message, "Verificación en dos pasos desactivada.", "success");
});

// A dialog that asks for a code and runs the action with it; resolves with the action's result.
function promptCode({ title, description, totpUri, secret, label, submit, danger = false, extra = null, action }) {
  const dialog = document.querySelector("#code-dialog");
  const form = document.querySelector("#code-form");
  const input = document.querySelector("#code-input");
  const feedback = document.querySelector("#code-status");
  const submitButton = document.querySelector("#code-submit");
  const extraButton = document.querySelector("#code-extra");
  document.querySelector("#code-title").textContent = title;
  document.querySelector("#code-description").textContent = description;
  document.querySelector("#code-label").textContent = label;
  submitButton.textContent = submit;
  submitButton.className = danger ? "danger" : "";
  const qr = document.querySelector("#code-qr");
  qr.hidden = !totpUri;
  qr.replaceChildren(...(totpUri ? [qrSvg(totpUri, "Código QR para configurar tu app de autenticación")] : []));
  const secretBox = document.querySelector("#code-secret");
  secretBox.hidden = !secret;
  secretBox.open = false;
  if (secret) {
    document.querySelector("#code-secret-value").textContent = groupSecret(secret);
    document.querySelector("#code-secret-open").href = totpUri;
    document.querySelector("#code-secret-copy").onclick = async () =>
      status(feedback, await copyText(secret) ? "Clave copiada." : "Copia la clave manualmente.", "success");
  }
  extraButton.hidden = !extra;
  if (extra) {
    let on = false;
    extraButton.textContent = extra.label;
    extraButton.onclick = () => {
      on = !on;
      extraButton.textContent = extra.toggle(on);
      input.inputMode = on ? "text" : "numeric";
      input.value = "";
      input.focus();
    };
  }
  input.value = "";
  input.inputMode = "numeric";
  status(feedback, "");
  dialog.showModal();
  input.focus();
  return new Promise(resolve => {
    const close = value => { form.onsubmit = null; dialog.onclose = null; if (dialog.open) dialog.close(); resolve(value); };
    document.querySelector("#code-cancel").onclick = () => close(null);
    dialog.onclose = () => close(null);
    form.onsubmit = async event => {
      event.preventDefault();
      const code = input.value.trim();
      if (!code) return status(feedback, "Escribe el código.", "error");
      submitButton.disabled = true;
      try { close((await action(code)) ?? {}); }
      catch (error) { status(feedback, errorText(error), "error"); input.select(); }
      finally { submitButton.disabled = false; }
    };
  });
}

function showBackupCodes(codes) {
  const dialog = document.querySelector("#codes-dialog");
  const text = codes.join("\n");
  document.querySelector("#codes-list").replaceChildren(...codes.map(code => element("li", "", code)));
  document.querySelector("#codes-copy").onclick = async () =>
    status(document.querySelector("#codes-status"), await copyText(text) ? "Códigos copiados." : "Copia los códigos manualmente.", "success");
  document.querySelector("#codes-download").onclick = () => downloadText("codigos-de-respaldo.txt", `${text}\n`);
  status(document.querySelector("#codes-status"), "");
  dialog.showModal();
  document.querySelector("#codes-close").focus();
  return new Promise(resolve => {
    document.querySelector("#codes-close").onclick = () => dialog.close();
    dialog.onclose = () => { dialog.onclose = null; resolve(); };
  });
}

// --- Passkeys ------------------------------------------------------------------------------

document.querySelector("#add-passkey").addEventListener("click", async () => {
  await ready;
  if (!window.PublicKeyCredential) return status(message, "Este navegador no admite passkeys.", "error");
  const proof = await reauthenticate("factor.enroll", "Confirma tu identidad para agregar una passkey.");
  if (!proof) return;
  try {
    const options = await api("/api/auth/passkeys/registration/options", { method: "POST" });
    const credential = await createPasskey(options.publicKey);
    const name = window.prompt("Nombre para esta passkey", suggestedPasskeyName())?.trim();
    await api("/api/auth/passkeys/registration/complete", {
      method: "POST", headers: { "X-AuthCenter-Reauthentication": proof },
      body: JSON.stringify({ name: name || suggestedPasskeyName(), credentialJson: serializeCredential(credential) })
    });
    status(message, "Passkey registrada.", "success");
    await refresh();
  } catch (error) { status(message, error.code ? errorText(error) : passkeyErrorMessage(error, "Registro cancelado."), "error"); }
});

function suggestedPasskeyName() {
  return describeAgent(navigator.userAgent).replace("Navegador", "Mi dispositivo");
}

async function renamePasskey(item) {
  const name = window.prompt("Nuevo nombre para la passkey", item.name)?.trim();
  if (!name || name === item.name) return;
  const proof = await reauthenticate("passkey.manage", "Confirma tu identidad para cambiar tus passkeys.");
  if (!proof) return;
  await mutate(`/api/auth/passkeys/${encodeURIComponent(item.credentialId)}`, "PUT", proof, "Passkey renombrada.", { name });
}

async function removePasskey(item) {
  const proof = await reauthenticate("passkey.manage", `Confirma tu identidad para eliminar la passkey "${item.name}".`);
  if (!proof) return;
  await mutate(`/api/auth/passkeys/${encodeURIComponent(item.credentialId)}`, "DELETE", proof, "Passkey eliminada.");
}

// --- Identity providers ----------------------------------------------------------------------

async function unlinkProvider(item) {
  if (!window.confirm(`¿Desvincular ${item.providerName || item.provider}? Ya no podrás iniciar sesión con esa cuenta.`)) return;
  await mutate(`/api/auth/external-providers/${item.id}`, "DELETE", null, "Proveedor desvinculado.");
}

// Linking signs in at the organization's provider, which sends the browser back to this page.
async function linkProvider(item) {
  const proof = await reauthenticate("account.link-provider", `Confirma tu identidad para vincular ${item.name}.`);
  if (!proof) return;
  try {
    const result = await api("/ui-api/session/federation/start", {
      method: "POST",
      headers: { "X-AuthCenter-Reauthentication": proof },
      body: JSON.stringify({ providerId: item.id, applicationCode: item.applicationCode, returnUrl: "/portal#providers", link: true })
    });
    location.assign(result.redirectUrl);
  } catch (error) { status(message, errorText(error), "error"); }
}

async function completeFederationLink(handle) {
  showPanel("providers", false);
  try {
    const provider = await api("/ui-api/session/federation/link", { method: "POST", body: JSON.stringify({ handle }) });
    status(message, `Vinculaste ${provider.name}. Ya puedes iniciar sesión con tu cuenta de la organización.`, "success");
  } catch (error) { status(message, errorText(error), "error"); }
}

// --- Reauthentication --------------------------------------------------------------------------

// Sensitive changes need a fresh proof: the password, or a passkey for accounts without one.
function reauthenticate(purpose, description) {
  const dialog = document.querySelector("#reauth-dialog");
  const form = document.querySelector("#reauth-form");
  const field = document.querySelector("#reauth-password");
  const feedback = document.querySelector("#reauth-status");
  const passkeyButton = document.querySelector("#reauth-passkey");
  const canUsePassword = profile?.hasLocalPassword !== false;
  const canUsePasskey = passkeys.length > 0 && Boolean(window.PublicKeyCredential);
  document.querySelector("#reauth-description").textContent = description;
  document.querySelector("#reauth-password-field").hidden = !canUsePassword;
  document.querySelector("#reauth-submit").hidden = !canUsePassword;
  passkeyButton.hidden = !canUsePasskey;
  status(feedback, canUsePassword || canUsePasskey ? "" : "Para esta operación necesitas una contraseña o una passkey en tu cuenta.", canUsePassword || canUsePasskey ? "" : "error");
  field.value = "";
  dialog.showModal();
  (canUsePassword ? field : passkeyButton).focus();
  return new Promise(resolve => {
    const close = value => { form.onsubmit = null; dialog.onclose = null; if (dialog.open) dialog.close(); field.value = ""; resolve(value); };
    document.querySelector("#reauth-cancel").onclick = () => close(null);
    dialog.onclose = () => close(null);
    form.onsubmit = async event => {
      event.preventDefault();
      if (!field.value) return status(feedback, "Escribe tu contraseña.", "error");
      try {
        const result = await api("/api/auth/reauth/password", { method: "POST", body: JSON.stringify({ password: field.value, purpose }) });
        close(result.proofToken);
      } catch (error) { status(feedback, error.status === 429 ? errorText(error) : "La contraseña no es correcta.", "error"); }
    };
    passkeyButton.onclick = async () => {
      try {
        const options = await api("/api/auth/passkeys/step-up/options", { method: "POST", body: JSON.stringify({ purpose }) });
        const credential = await getPasskey(options.publicKey);
        const result = await api("/api/auth/passkeys/step-up/complete", {
          method: "POST",
          body: JSON.stringify({ interactionId: options.interactionId, credentialJson: serializeCredential(credential), purpose })
        });
        close(result.proofToken);
      } catch (error) { status(feedback, error.code ? errorText(error) : passkeyErrorMessage(error), "error"); }
    };
  });
}

const errorMessages = {
  REAUTHENTICATION_REQUIRED: "Confirma tu identidad de nuevo para continuar.",
  INVALID_MFA_CODE: "El código no es válido. Inténtalo de nuevo.",
  INVALID_CODE: "El código no es válido. Inténtalo de nuevo.",
  SETUP_NOT_INITIATED: "El código expiró. Vuelve a empezar.",
  MFA_ALREADY_ENABLED: "La verificación en dos pasos ya está activa.",
  PASSWORD_CHANGE_FAILED: "No se pudo cambiar la contraseña: revisa la contraseña actual y la política de seguridad.",
  EMAIL_TAKEN: "Ese correo ya está en uso por otra cuenta.",
  SAME_EMAIL: "Ese ya es el correo de tu cuenta.",
  INVALID_PASSWORD: "La contraseña no es correcta.",
  PASSWORD_REQUIRED: "Escribe tu contraseña para continuar.",
  CANNOT_UNLINK_LAST_PROVIDER: "No puedes desvincular tu único método de inicio de sesión. Crea una contraseña o una passkey primero.",
  FEDERATION_IDENTITY_IN_USE: "Esa cuenta de la organización ya está vinculada a otro usuario.",
  FEDERATION_LINK_USER_MISMATCH: "La vinculación se inició con otra cuenta. Inicia sesión con la cuenta correcta e inténtalo de nuevo.",
  FEDERATION_RESULT_INVALID: "La vinculación expiró o ya se usó. Inténtalo de nuevo.",
  FEDERATION_CANCELLED: "Cancelaste el inicio de sesión en tu organización.",
  INTERACTION_BINDING_MISMATCH: "La vinculación se inició en otro navegador. Inténtalo de nuevo desde aquí.",
  FEDERATION_PROVIDER_NOT_FOUND: "El proveedor ya no está disponible.",
  PASSKEY_LIMIT_REACHED: "Alcanzaste el número máximo de passkeys. Elimina una antes de agregar otra.",
  INVALID_PASSKEY_ATTESTATION: "No pudimos registrar la passkey. Inténtalo de nuevo.",
  INVALID_PASSKEY_ASSERTION: "No pudimos verificar tu passkey. Inténtalo de nuevo.",
  PASSKEY_CEREMONY_EXPIRED: "La operación con passkey expiró. Inténtalo de nuevo.",
  ACCESS_REQUEST_EXISTS: "Ya tienes una solicitud esperando decisión para esa aplicación.",
  ACCESS_ALREADY_ACTIVE: "Ya tienes acceso a esa aplicación. Si necesitas un rol, elígelo.",
  ROLE_ALREADY_ASSIGNED: "Ya tienes ese rol.",
  ACCESS_REQUEST_LIMIT: "Tienes demasiadas solicitudes pendientes. Espera a que decidan alguna.",
  ACCESS_REQUEST_NOT_ALLOWED: "Esa aplicación ya no acepta solicitudes de acceso.",
  ACCESS_REQUEST_NOT_PENDING: "Esa solicitud ya no está pendiente.",
  ACCESS_REQUEST_EXPIRED: "La solicitud expiró; la persona puede pedir el acceso otra vez.",
  ACCESS_REQUEST_FORBIDDEN: "Sólo los responsables de la aplicación deciden sus solicitudes.",
  SELF_APPROVAL_FORBIDDEN: "No puedes decidir tu propia solicitud.",
  SELF_REVIEW_FORBIDDEN: "No puedes revisar tu propio acceso.",
  SOD_CONFLICT: "Aprobarla combinaría roles incompatibles (segregación de funciones). Un administrador debe resolverlo.",
  USER_INACTIVE: "La cuenta de quien la pidió está inactiva.",
  ACCESS_REVIEW_ITEM_DECIDED: "Ese acceso ya fue revisado.",
  ACCESS_REVIEW_NOT_ACTIVE: "La revisión ya terminó."
};

function errorText(error) {
  return errorMessages[error.code] ?? errorMessage(error);
}

function showPanel(id, updateHash) {
  const panel = panels.includes(id) ? id : "overview";
  document.querySelectorAll(".panel").forEach(item => item.classList.toggle("active", item.id === panel));
  document.querySelectorAll("[data-panel]").forEach(link => link.setAttribute("aria-current", link.dataset.panel === panel ? "page" : "false"));
  if (updateHash) {
    history.replaceState(null, "", `${location.pathname}${location.search}#${panel}`);
    document.querySelector(`#${panel}`)?.focus();
  }
}

async function initialize() {
  session = await requireSessionState();
  if (!session) return;
  showPanel(location.hash.slice(1), false);
  if (federationLink) await completeFederationLink(federationLink);
  await refresh();
  markReady();
}

await initialize();
