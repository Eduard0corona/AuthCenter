import { expect, test } from "@playwright/test";
import {
  adminToken, authorizeUrl, baseURL, createApplication, createClient, expectPortal, linkIn, newPassword,
  registerUser, signInWithPassword, signOut, statusOf, totp, waitForMail
} from "./support.mjs";

let token;
let openApp;
let mfaApp;
let confirmApp;
let employeesApp;

test.beforeAll(async ({ playwright }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await adminToken(request);
  openApp = await createApplication(request, token, { magicLink: true });
  mfaApp = await createApplication(request, token, { requireMfa: true });
  confirmApp = await createApplication(request, token, { confirmEmail: true });
  // Without an explicit audience, an invite-only application signs in employees.
  employeesApp = await createApplication(request, token, { registrationMode: "InviteOnly" });
  await request.dispose();
});

test("the login carries the application's brand and speaks to its audience, never naming AuthCenter", async ({ page }) => {
  // Open registration: consumers, with neutral wording.
  await page.goto(`/login?application=${openApp.code}`);
  await expect(page.locator("#brand-name")).toHaveText(`Aplicación ${openApp.code}`);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Inicia sesión");
  await expect(page.locator("#page-subtitle")).toHaveText(`Continúa en Aplicación ${openApp.code}.`);
  await expect(page).toHaveTitle(`Inicia sesión · Aplicación ${openApp.code}`);

  await page.goto(`/login?application=${employeesApp.code}`);
  await expect(page.locator("#page-subtitle")).toHaveText(`Usa tu cuenta de la empresa para continuar en Aplicación ${employeesApp.code}.`);
  await expect(page.getByRole("button", { name: "Crear cuenta" })).toBeHidden();

  // The identity service's own login has no brand until an administrator gives it one.
  await page.goto("/login");
  await expect(page.locator("#login-form")).toBeVisible();
  await expect(page.locator("#brand")).toBeHidden();
  await expect(page).toHaveTitle("Inicia sesión");
  await expect(page.locator("body")).not.toContainText("AuthCenter");
});

test("each step has its own heading and document title", async ({ page }) => {
  await page.goto(`/login?application=${openApp.code}`);
  await page.getByRole("button", { name: "¿Olvidaste tu contraseña?" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Restablece tu contraseña");
  await expect(page).toHaveTitle(`Restablece tu contraseña · Aplicación ${openApp.code}`);
  await expect(page.locator("#page-subtitle")).toBeHidden();
  await page.getByRole("button", { name: "Volver", exact: true }).click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Inicia sesión");
});

test("an unconfirmed email gets its confirmation link again from the login", async ({ page, request }) => {
  const user = await registerUser(request, confirmApp.code);
  await waitForMail(user.email, "Email confirmation");

  await signInWithPassword(page, user, `/login?application=${confirmApp.code}`);
  await expect(statusOf(page)).toHaveText("Confirma tu correo electrónico antes de iniciar sesión.");
  await page.getByRole("button", { name: "Reenviar el correo de confirmación" }).click();

  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Revisa tu correo");
  await expect(page.locator("#register-sent-description")).toContainText(`Te enviamos de nuevo el enlace de confirmación a ${user.email}`);
  await page.goto(linkIn(await waitForMail(user.email, "Email confirmation")));
  await page.getByRole("button", { name: "Confirmar correo" }).click();
  await expect(statusOf(page)).toContainText("Tu correo quedó confirmado");
});

test("consent says what the client may do and with which account, and lets the user switch", async ({ page, request }) => {
  const client = await createClient(request, token, openApp.id, { autoConsent: false });
  const first = await registerUser(request, openApp.code);
  const second = await registerUser(request, openApp.code);
  await page.route("**/e2e-callback**", route => route.fulfill({ status: 200, contentType: "text/plain", body: "callback" }));

  await page.goto(authorizeUrl(client));
  await expect(page.locator("#page-subtitle")).toHaveText("Continúa en Cliente E2E.");
  await page.locator("#email").fill(first.email);
  await page.locator("#password").fill(first.password);
  await page.getByRole("button", { name: "Continuar", exact: true }).click();

  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Permite el acceso");
  await expect(page.locator("#consent-description")).toHaveText(`Cliente E2E quiere acceder a tu cuenta de Aplicación ${openApp.code}:`);
  await expect(page.locator("#consent-scopes li")).toHaveText(["Confirmar quién eres", "Ver tu nombre y tu foto de perfil", "Ver tu dirección de correo"]);
  await expect(page.locator("#consent-account")).toHaveText(`Conectado como ${first.email}.`);

  // Another person at the same computer continues the same request with their own account.
  await page.getByRole("button", { name: "¿No eres tú?" }).click();
  await expect(statusOf(page)).toHaveText("Inicia sesión con tu cuenta para continuar.");
  await page.locator("#email").fill(second.email);
  await page.locator("#password").fill(second.password);
  await page.getByRole("button", { name: "Continuar", exact: true }).click();
  await expect(page.locator("#consent-account")).toHaveText(`Conectado como ${second.email}.`);
  await page.getByRole("button", { name: "Permitir" }).click();

  await page.waitForURL(url => url.pathname === "/e2e-callback");
  expect(new URL(page.url()).searchParams.get("code")).toBeTruthy();
});

test("signing out is confirmed on the login, and /logout without a request still signs out", async ({ page, request }) => {
  const user = await registerUser(request, openApp.code);
  await signInWithPassword(page, user, `/login?application=${openApp.code}&return_url=/portal`);
  await expectPortal(page);
  await signOut(page);
  await expect(statusOf(page)).toHaveText("Cerraste sesión.");

  await signInWithPassword(page, user, `/login?application=${openApp.code}&return_url=/portal`);
  await expectPortal(page);
  await page.goto("/logout");
  await expect(page.locator("#logout-description")).toHaveText(`¿Quieres cerrar la sesión de ${user.email}?`);
  await page.getByRole("button", { name: "Cerrar sesión" }).click();
  await page.waitForURL(url => url.pathname === "/login");
  await expect(statusOf(page)).toHaveText("Cerraste sesión.");

  // Without a session there is nothing to end, and the way back in is offered.
  await page.goto("/logout");
  await expect(page.locator("#logout-description")).toHaveText("No tienes una sesión abierta.");
  await expect(page.getByRole("link", { name: "Iniciar sesión" })).toBeVisible();
  await expect(page.locator("body")).not.toContainText("AuthCenter");
});

test("a password sign-in returns to the requested page", async ({ page, request }) => {
  const user = await registerUser(request, openApp.code);

  await signInWithPassword(page, user, `/login?application=${openApp.code}&return_url=/portal`);

  await expectPortal(page);
});

test("a wrong password is explained without leaving the form", async ({ page, request }) => {
  const user = await registerUser(request, openApp.code);

  // Another generated password: certainly not the account's.
  await signInWithPassword(page, { email: user.email, password: newPassword() }, `/login?application=${openApp.code}`);

  await expect(statusOf(page)).toHaveText("El correo o la contraseña no son correctos.");
  await expect(page.locator("#login-form")).toBeVisible();
});

test("a forgotten password is reset from the emailed link", async ({ page, request }) => {
  const user = await registerUser(request, openApp.code);
  await page.goto(`/login?application=${openApp.code}`);
  await page.locator("#email").fill(user.email);
  await page.getByRole("button", { name: "¿Olvidaste tu contraseña?" }).click();
  await expect(page.locator("#forgot-email")).toHaveValue(user.email);
  await page.getByRole("button", { name: "Enviar enlace" }).click();
  await expect(statusOf(page)).toContainText("te enviamos un enlace");

  const link = linkIn(await waitForMail(user.email, "Password reset"));
  expect(link).toContain(`${baseURL}/reset-password?`);
  await page.goto(link);
  // The single-use token leaves the address bar before anything else.
  await expect(page).toHaveURL(`${baseURL}/reset-password`);
  await expect(page.getByRole("heading", { name: "Crea una contraseña nueva" })).toBeVisible();
  await page.locator("#new-password").fill("corta");
  await page.locator("#confirm-password").fill("corta");
  await page.getByRole("button", { name: "Guardar contraseña" }).click();
  await expect(statusOf(page)).toContainText("al menos 8 caracteres");
  const password = newPassword();
  await page.locator("#new-password").fill(password);
  await page.locator("#confirm-password").fill(password);
  await page.getByRole("button", { name: "Guardar contraseña" }).click();
  await expect(statusOf(page)).toContainText("Tu contraseña se cambió");

  await page.getByRole("link", { name: "Iniciar sesión" }).click();
  await expect(page).toHaveURL(`${baseURL}/login?application=${openApp.code}`);
  await signInWithPassword(page, { email: user.email, password }, `/login?application=${openApp.code}`);
  await expectPortal(page);
});

test("an emailed sign-in link continues where it was requested", async ({ page, request }) => {
  const user = await registerUser(request, openApp.code);
  await page.goto(`/login?application=${openApp.code}&return_url=${encodeURIComponent("/portal#security")}`);
  await page.locator("#email").fill(user.email);
  await page.getByRole("button", { name: "Envíame un enlace de acceso" }).click();
  await expect(statusOf(page)).toContainText("te enviamos un enlace");

  const link = linkIn(await waitForMail(user.email, "Magic link"));
  expect(link).toContain(`${baseURL}/magic-link?`);
  await page.goto(link);

  await expectPortal(page);
  await expect(page).toHaveURL(`${baseURL}/portal#security`);
  // The link works once.
  await signOut(page);
  await page.goto(link);
  await expect(statusOf(page)).toContainText("ya se usó");
});

test("an application that requires MFA guides the enrollment and asks for the factor afterwards", async ({ page, request }) => {
  const user = await registerUser(request, mfaApp.code);
  const loginPath = `/login?application=${mfaApp.code}&return_url=/portal`;

  await signInWithPassword(page, user, loginPath);
  await expect(page.getByRole("heading", { name: "Activa la verificación en dos pasos" })).toBeVisible();
  await expect(page.locator("#totp-qr svg")).toBeVisible();
  await page.getByText("¿No puedes escanearlo? Escribe la clave").click();
  const secret = (await page.locator("#totp-secret").textContent()).replace(/\s+/g, "");
  await expect(page.getByRole("link", { name: "Abrir en la app" })).toHaveAttribute("href", /^otpauth:\/\/totp\//);
  await page.locator("#totp-code").fill("000000");
  await page.getByRole("button", { name: "Activar y continuar" }).click();
  await expect(statusOf(page)).toContainText("El código no es válido");
  await page.locator("#totp-code").fill(totp(secret));
  await page.getByRole("button", { name: "Activar y continuar" }).click();

  const codes = page.locator("#backup-codes-list li");
  await expect(codes).toHaveCount(8);
  const backupCode = (await codes.first().textContent()).trim();
  await expect(page.getByRole("button", { name: "Continuar", exact: true })).toBeDisabled();
  await page.getByLabel("Guardé mis códigos en un lugar seguro").check();
  await page.getByRole("button", { name: "Continuar", exact: true }).click();
  await expectPortal(page);

  // The next sign-in asks for the authenticator code.
  await signOut(page);
  await signInWithPassword(page, user, loginPath);
  await expect(page.getByRole("heading", { name: "Verificación en dos pasos" })).toBeVisible();
  await page.locator("#mfa-code").fill(totp(secret));
  await page.getByRole("button", { name: "Verificar", exact: true }).click();
  await expectPortal(page);

  // A backup code replaces the app once.
  await signOut(page);
  await signInWithPassword(page, user, loginPath);
  await page.getByRole("button", { name: "Usar un código de respaldo" }).click();
  await expect(page.getByLabel("Código de respaldo")).toBeVisible();
  await page.locator("#mfa-code").fill(backupCode);
  await page.getByRole("button", { name: "Verificar", exact: true }).click();
  await expectPortal(page);
});

test("an authorization request for an MFA application enrolls the factor in place and returns a code", async ({ page, request }) => {
  const client = await createClient(request, token, mfaApp.id);
  const user = await registerUser(request, mfaApp.code);
  // The client's callback: only the code and state it receives matter here.
  await page.route("**/e2e-callback**", route => route.fulfill({ status: 200, contentType: "text/plain", body: "callback" }));

  await page.goto(authorizeUrl(client));
  await expect(page).toHaveURL(/\/login\?interaction_id=/);
  await expect(page.locator("#brand-name")).toBeVisible();
  await page.locator("#email").fill(user.email);
  await page.locator("#password").fill(user.password);
  await page.getByRole("button", { name: "Continuar", exact: true }).click();
  await page.getByText("¿No puedes escanearlo? Escribe la clave").click();
  const secret = (await page.locator("#totp-secret").textContent()).replace(/\s+/g, "");
  await page.locator("#totp-code").fill(totp(secret));
  await page.getByRole("button", { name: "Activar y continuar" }).click();
  await page.getByLabel("Guardé mis códigos en un lugar seguro").check();
  await page.getByRole("button", { name: "Continuar", exact: true }).click();

  await page.waitForURL(url => url.pathname === "/e2e-callback");
  const url = new URL(page.url());
  expect(url.searchParams.get("code")).toBeTruthy();
  expect(url.searchParams.get("state")).toBeTruthy();
});

test("an expired authorization request offers the way back to the application", async ({ page, request }) => {
  const client = await createClient(request, token, openApp.id);
  await page.clock.install();

  await page.goto(authorizeUrl(client));
  await expect(page.locator("#login-form")).toBeVisible();
  await page.clock.fastForward("10:05");

  await expect(page.getByRole("heading", { level: 1 })).toHaveText("No podemos continuar");
  await expect(statusOf(page)).toContainText("La solicitud de inicio de sesión expiró");
  await expect(page.locator("#login-form")).toBeHidden();
  // Only the application can start the request again: the page sends the user back to it.
  await expect(page.getByRole("link", { name: "Volver a Cliente E2E" })).toHaveAttribute("href", `${baseURL}/`);
  await expect(page.getByRole("link", { name: "Volver a iniciar sesión" })).toBeHidden();
});

test("an email second factor sends its code and can send another", async ({ page, request }) => {
  const user = await registerUser(request, openApp.code);
  // Enable the email factor from the portal.
  await signInWithPassword(page, user, `/login?application=${openApp.code}&return_url=/portal`);
  await expectPortal(page);
  await page.getByRole("link", { name: "Seguridad" }).click();
  await page.getByRole("button", { name: "Recibir códigos por correo" }).click();
  await page.locator("#reauth-password").fill(user.password);
  await page.getByRole("button", { name: "Confirmar", exact: true }).click();
  const setupCode = (await waitForMail(user.email, "MFA Email OTP")).html.match(/>\s*(\d{6})\s*</)[1];
  await page.locator("#code-input").fill(setupCode);
  await page.getByRole("button", { name: "Activar", exact: true }).click();
  await expect(page.locator("#mfa-summary")).toContainText("código por correo");

  await signOut(page);
  await signInWithPassword(page, user, `/login?application=${openApp.code}&return_url=/portal`);
  await expect(page.locator("#mfa-description")).toContainText("Te enviamos un código de 6 dígitos a tu correo");
  const code = (await waitForMail(user.email, "MFA Email OTP")).html.match(/>\s*(\d{6})\s*</)[1];
  await page.locator("#mfa-code").fill(code);
  await page.getByRole("button", { name: "Verificar", exact: true }).click();
  await expectPortal(page);
});
