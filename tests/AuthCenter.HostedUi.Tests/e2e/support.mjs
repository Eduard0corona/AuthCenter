import { createHash, createHmac, randomBytes } from "node:crypto";
import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import { expect } from "@playwright/test";

export const baseURL = process.env.HOSTED_UI_BASE_URL;
const mailDirectory = process.env.HOSTED_UI_MAIL_DIR;

// --- API helpers (administration and setup) -----------------------------------------------

export async function call(request, method, url, { token, data, headers } = {}) {
  const response = await request.fetch(url, {
    method,
    data,
    headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...headers }
  });
  const body = await response.json().catch(() => null);
  return { ok: response.ok(), status: response.status(), body, data: body?.data ?? body };
}

async function required(request, method, url, options) {
  const result = await call(request, method, url, options);
  if (!result.ok) throw new Error(`${method} ${url} returned ${result.status}: ${JSON.stringify(result.body)}`);
  return result.data;
}

export async function adminToken(request) {
  const auth = await required(request, "POST", "/api/auth/login", {
    data: { email: process.env.HOSTED_UI_ADMIN_EMAIL, password: process.env.HOSTED_UI_ADMIN_PASSWORD, applicationCode: "AUTHCENTER" }
  });
  return auth.accessToken;
}

/** An application with open self-registration for the test's users, unless another mode is given. */
export async function createApplication(request, token, { requireMfa = false, magicLink = false, confirmEmail = false, registrationMode = "Open" } = {}) {
  const code = `E2E${randomBytes(4).toString("hex").toUpperCase()}`;
  const application = await required(request, "POST", "/api/applications", {
    token,
    data: {
      code,
      name: `Aplicación ${code}`,
      registrationMode,
      allowPasswordLogin: true,
      allowMagicLink: magicLink,
      requireMfa,
      requireEmailConfirmation: confirmEmail
    }
  });
  return { id: application.id, code };
}

export function newPassword() {
  return `Clave-${randomBytes(9).toString("base64url")}-9aZ`;
}

export function newEmail(prefix = "user") {
  return `${prefix}-${randomBytes(6).toString("hex")}@e2e.test`;
}

/**
 * A self-registered user. An application that requires a second factor answers the registration
 * with the enrollment still to do, which the hosted login guides at the first sign-in.
 */
export async function registerUser(request, applicationCode, { email = newEmail(), password = newPassword(), fullName = "Ana Prueba" } = {}) {
  const result = await call(request, "POST", "/api/auth/register", { data: { fullName, email, password, applicationCode } });
  if (!result.ok && !["MFA_SETUP_REQUIRED", "EMAIL_CONFIRMATION_REQUIRED"].includes(result.body?.errorCode))
    throw new Error(`Registration failed: ${result.status} ${JSON.stringify(result.body)}`);
  return { email, password, fullName };
}

/** A public OAuth client of the application whose login is the hosted page. */
export async function createClient(request, token, applicationId, { autoConsent = true } = {}) {
  const clientId = `e2e-${randomBytes(5).toString("hex")}`;
  const redirectUri = `${baseURL}/e2e-callback`;
  await required(request, "POST", "/api/oauth/clients", {
    token,
    data: {
      applicationSystemId: applicationId,
      clientId,
      displayName: "Cliente E2E",
      clientType: 1,
      redirectUris: [redirectUri],
      allowedScopes: ["openid", "profile", "email"],
      grantTypes: ["authorization_code"],
      loginUrl: `${baseURL}/login`,
      requirePkce: true,
      autoConsent
    }
  });
  return { clientId, redirectUri };
}

export function authorizeUrl({ clientId, redirectUri }) {
  const verifier = randomBytes(32).toString("base64url");
  const query = new URLSearchParams({
    response_type: "code",
    client_id: clientId,
    redirect_uri: redirectUri,
    scope: "openid profile email",
    state: randomBytes(8).toString("hex"),
    nonce: randomBytes(8).toString("hex"),
    code_challenge: createHash("sha256").update(verifier).digest("base64url"),
    code_challenge_method: "S256"
  });
  return `/oauth/authorize?${query}`;
}

// --- Email pickup ---------------------------------------------------------------------------

const readMail = new Set();

/** The newest unread email of that purpose to that address (the API writes them as JSON files). */
export async function waitForMail(to, purpose, timeout = 30_000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const files = (await readdir(mailDirectory).catch(() => [])).filter(file => file.endsWith(".json")).sort().reverse();
    for (const file of files) {
      if (readMail.has(file)) continue;
      let mail;
      try { mail = JSON.parse(await readFile(path.join(mailDirectory, file), "utf8")); }
      catch { continue; /* Still being written. */ }
      if (mail.to.toLowerCase() === to.toLowerCase() && mail.purpose === purpose) {
        readMail.add(file);
        return mail;
      }
    }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error(`No "${purpose}" email reached ${to} within ${timeout} ms.`);
}

export function linkIn(mail) {
  const match = /href="([^"]+)"/.exec(mail.html);
  if (!match) throw new Error(`The "${mail.purpose}" email has no link.`);
  return match[1].replaceAll("&amp;", "&");
}

export function codeIn(mail) {
  const match = />\s*(\d{6})\s*</.exec(mail.html);
  if (!match) throw new Error(`The "${mail.purpose}" email has no code.`);
  return match[1];
}

// --- Authenticator app ------------------------------------------------------------------------

const usedSteps = new Map();

/**
 * A TOTP code (RFC 6238, SHA-1, 6 digits) for a time step not used before with this secret:
 * AuthCenter accepts each step once, and the current, next and previous ones are all valid.
 */
export function totp(secret) {
  const key = base32(secret.replace(/\s+/g, ""));
  const used = usedSteps.get(secret) ?? new Set();
  usedSteps.set(secret, used);
  const current = Math.floor(Date.now() / 30_000);
  const step = [current, current + 1, current - 1].find(candidate => !used.has(candidate));
  if (step === undefined) throw new Error("Every time step inside the verification window was already used.");
  used.add(step);
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hmac = createHmac("sha1", key).update(counter).digest();
  const offset = hmac[hmac.length - 1] & 0x0f;
  return String((hmac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

function base32(value) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const character of value.toUpperCase().replace(/=+$/, "")) {
    const index = alphabet.indexOf(character);
    if (index < 0) throw new Error(`Invalid base32 character ${character}.`);
    bits += index.toString(2).padStart(5, "0");
  }
  const bytes = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}

// --- Browser helpers ------------------------------------------------------------------------------

/** A platform authenticator with user verification, so passkeys work without a real device. */
export async function addVirtualAuthenticator(page) {
  const session = await page.context().newCDPSession(page);
  await session.send("WebAuthn.enable");
  const { authenticatorId } = await session.send("WebAuthn.addVirtualAuthenticator", {
    options: { protocol: "ctap2", transport: "internal", hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true }
  });
  return { session, authenticatorId };
}

export async function signInWithPassword(page, { email, password }, path = "/login") {
  await page.goto(path);
  await page.locator("#email").fill(email);
  await page.locator("#password").fill(password);
  await page.getByRole("button", { name: "Continuar", exact: true }).click();
}

export async function expectPortal(page, name = "Ana Prueba") {
  await expect(page).toHaveURL(/\/portal/);
  await expect(page.locator("#user-name")).toHaveText(name);
}

/** Signs out from the portal and waits for the login, so the next step starts without a session. */
export async function signOut(page) {
  await page.getByRole("button", { name: "Cerrar sesión" }).click();
  await page.waitForURL(url => url.pathname === "/login");
}

export function statusOf(page) {
  return page.locator("#status");
}
