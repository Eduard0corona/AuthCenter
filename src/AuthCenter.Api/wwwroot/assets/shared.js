let csrfToken = "";

export function setCsrf(value) { csrfToken = value || ""; }

export async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  if (csrfToken && !["GET", "HEAD"].includes((options.method || "GET").toUpperCase())) headers.set("X-AuthCenter-CSRF", csrfToken);
  const response = await fetch(path, { ...options, headers, credentials: "same-origin" });
  const contentType = response.headers.get("content-type") || "";
  const payload = contentType.includes("json") ? await response.json() : null;
  if (!response.ok) {
    const error = new Error(payload?.message || payload?.error?.message || `Request failed (${response.status})`);
    error.code = payload?.errorCode || payload?.error?.code || "REQUEST_FAILED";
    error.status = response.status;
    throw error;
  }
  return payload?.data ?? payload;
}

// Resolves a caller-supplied return path and accepts it only when it stays on this origin.
// Backslashes and control characters are rejected because browsers normalize "/\\host" to
// "//host", which would otherwise turn a local-looking path into an open redirect.
export function safeLocalPath(value, origin, fallback) {
  if (typeof value !== "string" || !value.startsWith("/") || value.startsWith("//") || /[\u0000-\u001f\\]/.test(value)) return fallback;
  let resolved;
  try { resolved = new URL(value, origin); } catch { return fallback; }
  return resolved.origin === new URL(origin).origin ? `${resolved.pathname}${resolved.search}${resolved.hash}` : fallback;
}

export async function requireSession() {
  try {
    const session = await api("/ui-api/session");
    setCsrf(session.csrfToken);
    return session.user;
  } catch (error) {
    if (error.status === 401) {
      const returnUrl = encodeURIComponent(location.pathname + location.search);
      location.replace(`/login?return_url=${returnUrl}`);
      return null;
    }
    throw error;
  }
}

export function status(element, message, type = "") {
  element.textContent = message || "";
  element.className = `status ${type}`.trim();
}

export function formatDate(value) {
  return value ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)) : "—";
}

export function textCell(value) {
  const cell = document.createElement("td");
  cell.textContent = value ?? "—";
  return cell;
}

export async function signOut() {
  await api("/ui-api/session/logout", { method: "POST" });
  location.replace("/login");
}

export function appendTheme(applicationCode) {
  const code = encodeURIComponent(applicationCode || "AUTHCENTER");
  const link = document.createElement("link");
  link.rel = "stylesheet";
  link.href = `/api/applications/branding/${code}/theme.css`;
  document.head.append(link);
}

export function fromBase64Url(value) {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const bytes = Uint8Array.from(atob(normalized.padEnd(Math.ceil(normalized.length / 4) * 4, "=")), c => c.charCodeAt(0));
  return bytes.buffer;
}

export function normalizeCreationOptions(options) {
  options.challenge = fromBase64Url(options.challenge);
  options.user.id = fromBase64Url(options.user.id);
  for (const item of options.excludeCredentials || []) item.id = fromBase64Url(item.id);
  return options;
}

export function normalizeRequestOptions(options) {
  options.challenge = fromBase64Url(options.challenge);
  for (const item of options.allowCredentials || []) item.id = fromBase64Url(item.id);
  return options;
}

export function serializeCredential(credential) {
  if (typeof credential.toJSON === "function") return JSON.stringify(credential.toJSON());
  const encode = buffer => btoa(String.fromCharCode(...new Uint8Array(buffer))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return JSON.stringify({
    id: credential.id,
    rawId: encode(credential.rawId),
    type: credential.type,
    authenticatorAttachment: credential.authenticatorAttachment,
    clientExtensionResults: credential.getClientExtensionResults(),
    response: credential.response.attestationObject ? {
      attestationObject: encode(credential.response.attestationObject),
      clientDataJSON: encode(credential.response.clientDataJSON),
      transports: credential.response.getTransports?.() || []
    } : {
      authenticatorData: encode(credential.response.authenticatorData),
      clientDataJSON: encode(credential.response.clientDataJSON),
      signature: encode(credential.response.signature),
      userHandle: credential.response.userHandle ? encode(credential.response.userHandle) : null
    }
  });
}
