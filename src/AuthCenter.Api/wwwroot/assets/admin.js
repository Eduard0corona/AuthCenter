import { api, formatDate, requireSession, signOut, status, textCell } from "./shared.js";

const message = document.querySelector("#status");
const operator = await requireSession();
const permissions = new Set(operator?.permissions || []);
if (operator) {
  document.querySelector("#operator").textContent = operator.name || operator.email;
  document.querySelectorAll("[data-permission]").forEach(item => item.hidden = !permissions.has(item.dataset.permission));
  await loadAll();
}

document.querySelector("#logout").addEventListener("click", signOut);
document.querySelectorAll("[data-panel]").forEach(link => link.addEventListener("click", () => showPanel(link.dataset.panel)));
document.querySelector("#show-all").addEventListener("click", () => loadDeliveries(false));
document.querySelector("#show-dead").addEventListener("click", () => loadDeliveries(true));
document.querySelector("#branding-cancel").addEventListener("click", () => document.querySelector("#branding-dialog").close());
document.querySelector("#branding-form").addEventListener("submit", saveBranding);

async function loadAll() {
  const tasks = [];
  if (permissions.has("AUTHCENTER_USERS_READ")) tasks.push(loadUsers());
  if (permissions.has("AUTHCENTER_APPLICATIONS_READ")) tasks.push(loadApplications());
  if (permissions.has("AUTHCENTER_AUDIT_LOGS_READ")) tasks.push(loadAudit());
  if (permissions.has("AUTHCENTER_APPLICATIONS_WRITE")) tasks.push(loadDeliveries(false));
  await Promise.all(tasks);
}

async function loadUsers() {
  try {
    const result = await api("/api/users?page=1&pageSize=50");
    const body = document.querySelector("#users-body"); body.replaceChildren();
    for (const user of result.items) {
      const row = document.createElement("tr");
      const identity = document.createElement("td"); identity.textContent = `${user.fullName} · ${user.email}`;
      row.append(identity, textCell(user.isActive ? "Activo" : "Inactivo"), textCell(formatDate(user.createdAt)));
      const action = document.createElement("td");
      if (permissions.has("AUTHCENTER_USERS_WRITE")) {
        const button = document.createElement("button"); button.className = user.isActive ? "danger" : "secondary"; button.textContent = user.isActive ? "Desactivar" : "Activar";
        button.onclick = async () => { await api(`/api/users/${user.id}/${user.isActive ? "deactivate" : "activate"}`, { method: "PATCH" }); await loadUsers(); };
        action.append(button);
      } else action.textContent = "Sólo lectura";
      row.append(action); body.append(row);
    }
    setMetric("Usuarios", result.totalCount);
  } catch (error) { status(message, error.message, "error"); }
}

async function loadApplications() {
  try {
    const result = await api("/api/applications?page=1&pageSize=50");
    const body = document.querySelector("#apps-body"); body.replaceChildren();
    for (const app of result.items) {
      const row = document.createElement("tr"); row.append(textCell(app.code), textCell(app.name), textCell(app.isActive ? "Activa" : "Inactiva"));
      const action = document.createElement("td");
      if (permissions.has("AUTHCENTER_APPLICATIONS_WRITE")) { const button = document.createElement("button"); button.textContent = "Editar"; button.onclick = () => openBranding(app); action.append(button); }
      else action.textContent = app.branding?.displayName || app.name;
      row.append(action); body.append(row);
    }
    setMetric("Aplicaciones", result.totalCount);
  } catch (error) { status(message, error.message, "error"); }
}

async function loadAudit() {
  try {
    const result = await api("/api/audit-logs?page=1&pageSize=50");
    const body = document.querySelector("#audit-body"); body.replaceChildren();
    for (const item of result.items) { const row = document.createElement("tr"); row.append(textCell(formatDate(item.createdAt)), textCell(item.action), textCell(item.applicationCode), textCell([item.entityName, item.entityId].filter(Boolean).join(" · "))); body.append(row); }
    setMetric("Eventos recientes", result.totalCount);
  } catch (error) { status(message, error.message, "error"); }
}

async function loadDeliveries(deadLettersOnly) {
  try {
    const deliveries = await api(`/api/event-hooks/deliveries?deadLettersOnly=${deadLettersOnly}`);
    const container = document.querySelector("#deliveries");
    container.replaceChildren(...deliveries.map(item => {
      const deliveryStatus = item.deliveredAt ? "Delivered" : item.deadLetteredAt ? "DeadLetter" : "Pending";
      const card = document.createElement("article"); card.className = "card";
      const heading = document.createElement("h3"); heading.textContent = item.eventType;
      const text = document.createElement("p"); text.textContent = `${deliveryStatus} · intentos ${item.attemptCount} · ${formatDate(item.nextAttemptAt)}`;
      card.append(heading, text);
      if (deliveryStatus === "DeadLetter") { const replay = document.createElement("button"); replay.textContent = "Reintentar"; replay.onclick = async () => { await api(`/api/event-hooks/deliveries/${item.id}/replay`, { method: "POST" }); await loadDeliveries(true); }; card.append(replay); }
      return card;
    }));
  } catch (error) { status(message, error.message, "error"); }
}

function openBranding(app) {
  document.querySelector("#branding-app-id").value = app.id;
  document.querySelector("#branding-name").value = app.branding?.displayName || app.name;
  document.querySelector("#branding-primary").value = app.branding?.primaryColor || "#2563eb";
  document.querySelector("#branding-background").value = app.branding?.backgroundColor || "#f8fafc";
  document.querySelector("#branding-logo").value = app.branding?.logoUrl || "";
  document.querySelector("#branding-support").value = app.branding?.supportUrl || "";
  document.querySelector("#branding-dialog").showModal();
}

async function saveBranding(event) {
  event.preventDefault();
  try {
    await api(`/api/applications/${document.querySelector("#branding-app-id").value}/branding`, { method: "PUT", body: JSON.stringify({
      displayName: document.querySelector("#branding-name").value,
      primaryColor: document.querySelector("#branding-primary").value,
      backgroundColor: document.querySelector("#branding-background").value,
      logoUrl: document.querySelector("#branding-logo").value || null,
      supportUrl: document.querySelector("#branding-support").value || null,
      privacyUrl: null, termsUrl: null
    }) });
    document.querySelector("#branding-dialog").close(); await loadApplications(); status(message, "Branding actualizado.", "success");
  } catch (error) { status(message, error.message, "error"); }
}

function setMetric(label, value) {
  let card = document.querySelector(`[data-metric="${label}"]`);
  if (!card) { card = document.createElement("article"); card.className = "card"; card.dataset.metric = label; const heading = document.createElement("h3"); heading.textContent = label; const metric = document.createElement("div"); metric.className = "metric"; card.append(heading, metric); document.querySelector("#metrics").append(card); }
  card.querySelector(".metric").textContent = value;
}

function showPanel(id) {
  document.querySelectorAll(".panel").forEach(panel => panel.classList.toggle("active", panel.id === id));
  document.querySelectorAll("[data-panel]").forEach(link => link.setAttribute("aria-current", link.dataset.panel === id ? "page" : "false"));
}
