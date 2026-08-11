import { api, formatDate, normalizeCreationOptions, requireSession, serializeCredential, signOut, status } from "./shared.js";

const message = document.querySelector("#status");
const user = await requireSession();
if (user) {
  document.querySelector("#user-name").textContent = user.name || user.email;
  document.querySelector("#admin-link").hidden = !user.permissions?.some(value => value.startsWith("AUTHCENTER_"));
  await refresh();
}

document.querySelector("#logout").addEventListener("click", signOut);
document.querySelectorAll("[data-panel]").forEach(link => link.addEventListener("click", () => showPanel(link.dataset.panel)));
document.querySelector("#add-passkey").addEventListener("click", addPasskey);

async function refresh() {
  try {
    const [sessions, devices, passkeys, providers, consents] = await Promise.all([
      api("/api/auth/sessions"), api("/api/auth/trusted-devices"), api("/api/auth/passkeys"),
      api("/api/auth/external-providers"), api("/oauth/consents")
    ]);
    renderOverview(sessions, devices, passkeys, consents);
    renderCards("sessions-list", sessions, item => `${item.deviceName || "Sesión"} · ${formatDate(item.createdAt)}`, "Revocar", () => mutate(`/api/auth/sessions/${item.id}`, "DELETE"));
    renderCards("devices-list", devices, item => `${item.deviceName || "Dispositivo"} · expira ${formatDate(item.expiresAt)}`, "Revocar", () => mutate(`/api/auth/trusted-devices/${item.id}`, "DELETE"));
    renderCards("passkeys-list", passkeys, item => `${item.name} · creada ${formatDate(item.createdAt)}`, "Eliminar", async () => {
      const proof = await reauthenticate("passkey.manage"); if (!proof) return;
      await mutate(`/api/auth/passkeys/${encodeURIComponent(item.credentialId)}`, "DELETE", proof);
    });
    renderCards("providers-list", providers, item => `${item.provider} · ${item.email || "cuenta vinculada"}`, null);
    renderCards("consents-list", consents, item => `${item.clientDisplayName} · ${item.scopes.join(", ")}`, "Revocar", () => mutate(`/oauth/consents/${item.id}`, "DELETE"));
  } catch (error) { status(message, error.message, "error"); }
}

function renderOverview(sessions, devices, passkeys, consents) {
  const grid = document.querySelector("#overview-grid");
  grid.replaceChildren(...[["Sesiones activas", sessions.length], ["Dispositivos confiables", devices.length], ["Passkeys", passkeys.length], ["Aplicaciones autorizadas", consents.length]].map(([label, value]) => {
    const card = document.createElement("article"); card.className = "card";
    const heading = document.createElement("h3"); heading.textContent = label;
    const metric = document.createElement("div"); metric.className = "metric"; metric.textContent = value;
    card.append(heading, metric); return card;
  }));
}

function renderCards(containerId, items, describe, actionLabel, action) {
  const container = document.querySelector(`#${containerId}`);
  if (!items.length) { const empty = document.createElement("p"); empty.className = "card muted"; empty.textContent = "No hay elementos activos."; container.replaceChildren(empty); return; }
  container.replaceChildren(...items.map(item => {
    const card = document.createElement("article"); card.className = "card";
    const text = document.createElement("p"); text.textContent = describe(item); card.append(text);
    if (actionLabel) { const button = document.createElement("button"); button.className = "danger"; button.textContent = actionLabel; button.onclick = () => action(item); card.append(button); }
    return card;
  }));
}

async function mutate(path, method, proof) {
  try {
    await api(path, { method, headers: proof ? { "X-AuthCenter-Reauthentication": proof } : {} });
    status(message, "Cambio aplicado.", "success"); await refresh();
  } catch (error) { status(message, error.message, "error"); }
}

async function addPasskey() {
  if (!window.PublicKeyCredential) return status(message, "Este navegador no admite passkeys.", "error");
  try {
    const proof = await reauthenticate("factor.enroll"); if (!proof) return;
    const options = await api("/api/auth/passkeys/registration/options", { method: "POST" });
    const credential = await navigator.credentials.create({ publicKey: normalizeCreationOptions(JSON.parse(options.optionsJson)) });
    const name = window.prompt("Nombre para esta passkey", "Mi dispositivo");
    if (!name) return;
    await api("/api/auth/passkeys/registration/complete", {
      method: "POST", headers: { "X-AuthCenter-Reauthentication": proof },
      body: JSON.stringify({ name, credentialJson: serializeCredential(credential) })
    });
    status(message, "Passkey registrada.", "success"); await refresh();
  } catch (error) { status(message, error.name === "NotAllowedError" ? "Registro cancelado." : error.message, "error"); }
}

function reauthenticate(purpose) {
  const dialog = document.querySelector("#reauth-dialog");
  const form = document.querySelector("#reauth-form");
  const field = document.querySelector("#reauth-password");
  const feedback = document.querySelector("#reauth-status");
  dialog.showModal(); field.focus();
  return new Promise(resolve => {
    const close = value => { form.onsubmit = null; dialog.close(); field.value = ""; resolve(value); };
    document.querySelector("#reauth-cancel").onclick = () => close(null);
    form.onsubmit = async event => {
      event.preventDefault();
      try {
        const result = await api("/api/auth/reauth/password", { method: "POST", body: JSON.stringify({ password: field.value, purpose }) });
        close(result.proofToken);
      } catch (error) { status(feedback, error.message, "error"); }
    };
  });
}

function showPanel(id) {
  document.querySelectorAll(".panel").forEach(panel => panel.classList.toggle("active", panel.id === id));
  document.querySelectorAll("[data-panel]").forEach(link => link.setAttribute("aria-current", link.dataset.panel === id ? "page" : "false"));
  document.querySelector(`#${id}`)?.focus();
}
