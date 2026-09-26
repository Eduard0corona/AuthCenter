import { expect, test } from "@playwright/test";
import {
  addVirtualAuthenticator, adminToken, baseURL, call, createApplication, expectPortal, linkIn, newEmail, newPassword,
  registerUser, signInWithPassword, signOut, statusOf, totp, waitForMail
} from "./support.mjs";

let token;
let openApp;
let confirmApp;

test.beforeAll(async ({ playwright }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await adminToken(request);
  openApp = await createApplication(request, token);
  confirmApp = await createApplication(request, token, { confirmEmail: true });
  await request.dispose();
});

async function openPortal(page, request) {
  const user = await registerUser(request, openApp.code);
  await signInWithPassword(page, user, `/login?application=${openApp.code}&return_url=/portal`);
  await expectPortal(page);
  return user;
}

async function confirmIdentity(page, password) {
  await expect(page.getByRole("heading", { name: "Confirma tu identidad" })).toBeVisible();
  await page.locator("#reauth-password").fill(password);
  await page.getByRole("button", { name: "Confirmar", exact: true }).click();
}

test("the portal shows the account, its sessions and its applications", async ({ page, request }) => {
  const user = await openPortal(page, request);

  await expect(page.locator("#user-email")).toHaveText(user.email);
  await page.getByRole("link", { name: "Sesiones" }).click();
  await expect(page.locator("#sessions-list")).toContainText("Esta sesión");
  await page.getByRole("link", { name: "Aplicaciones", exact: true }).click();
  await expect(page.locator("#applications-list")).toContainText(`Aplicación ${openApp.code}`);
  await page.getByRole("link", { name: "Proveedores" }).click();
  await expect(page.locator("#providers-list")).toContainText("No tienes proveedores vinculados");
});

test("the authenticator app is enabled, its backup codes regenerated and then disabled", async ({ page, request }) => {
  const user = await openPortal(page, request);
  await page.getByRole("link", { name: "Seguridad" }).click();
  await expect(page.locator("#mfa-summary")).toContainText("Inactiva");

  await page.getByRole("button", { name: "Configurar app de autenticación" }).click();
  await confirmIdentity(page, user.password);
  await expect(page.locator("#code-qr svg")).toBeVisible();
  await page.getByText("¿No puedes escanearlo? Escribe la clave").click();
  const secret = (await page.locator("#code-secret-value").textContent()).replace(/\s+/g, "");
  await page.locator("#code-input").fill(totp(secret));
  await page.getByRole("button", { name: "Activar", exact: true }).click();
  await expect(page.locator("#codes-list li")).toHaveCount(8);
  await page.getByRole("button", { name: "Listo", exact: true }).click();
  await expect(page.locator("#mfa-summary")).toContainText("Activa con app de autenticación");

  await page.getByRole("button", { name: "Generar códigos de respaldo nuevos" }).click();
  await page.locator("#code-input").fill(totp(secret));
  await page.getByRole("button", { name: "Generar", exact: true }).click();
  await expect(page.locator("#codes-list li")).toHaveCount(8);
  await page.getByRole("button", { name: "Listo", exact: true }).click();

  await page.getByRole("button", { name: "Desactivar", exact: true }).click();
  await page.locator("#code-input").fill("123456");
  await page.locator("#code-dialog").getByRole("button", { name: "Desactivar", exact: true }).click();
  await expect(page.locator("#code-status")).toHaveText("El código no es válido. Inténtalo de nuevo.");
  await page.locator("#code-input").fill(totp(secret));
  await page.locator("#code-dialog").getByRole("button", { name: "Desactivar", exact: true }).click();
  await expect(page.locator("#mfa-summary")).toContainText("Inactiva");
  await expect(statusOf(page)).toHaveText("Verificación en dos pasos desactivada.");
});

test("the password is changed and the owner is told by email", async ({ page, request }) => {
  const user = await openPortal(page, request);
  await page.getByRole("link", { name: "Seguridad" }).click();
  const password = newPassword();
  await page.locator("#current-password").fill(user.password);
  await page.locator("#changed-password").fill(password);
  await page.locator("#changed-password-confirm").fill(password);
  await page.getByRole("button", { name: "Cambiar contraseña" }).click();
  await expect(statusOf(page)).toContainText("Contraseña actualizada");
  await waitForMail(user.email, "Security notice");

  await signOut(page);
  await signInWithPassword(page, { email: user.email, password }, `/login?application=${openApp.code}&return_url=/portal`);
  await expectPortal(page);
});

test("the email address changes once the link sent to the new address is confirmed", async ({ page, request }) => {
  const user = await openPortal(page, request);
  const address = newEmail("changed");
  await page.getByRole("link", { name: "Cuenta" }).click();
  await page.locator("#new-email").fill(address);
  await page.getByRole("button", { name: "Cambiar correo" }).click();
  await confirmIdentity(page, user.password);
  await expect(statusOf(page)).toContainText(`Te enviamos un enlace de confirmación a ${address}`);
  // The current address is warned; the new one receives the confirmation.
  await waitForMail(user.email, "Security notice");
  const link = linkIn(await waitForMail(address, "Email change confirmation"));
  expect(link).toContain("userId=");

  await page.goto(link);
  await expect(page).toHaveURL(`${baseURL}/confirm-email-change`);
  await page.getByRole("button", { name: "Confirmar cambio" }).click();
  await expect(statusOf(page)).toContainText("Tu cuenta ahora usa el correo nuevo");

  await page.goto("/portal");
  await signOut(page);
  await signInWithPassword(page, { email: address, password: user.password }, `/login?application=${openApp.code}&return_url=/portal`);
  await expectPortal(page);
  await expect(page.locator("#user-email")).toHaveText(address);
});

test("a passkey added in the portal signs in without typing the email", async ({ page, request }) => {
  await addVirtualAuthenticator(page);
  const user = await openPortal(page, request);
  await page.getByRole("link", { name: "Seguridad" }).click();
  page.once("dialog", dialog => dialog.accept("Portátil de prueba"));
  await page.getByRole("button", { name: "Agregar passkey" }).click();
  await confirmIdentity(page, user.password);
  await expect(statusOf(page)).toHaveText("Passkey registrada.");
  await expect(page.locator("#passkeys-list")).toContainText("Portátil de prueba");

  await signOut(page);
  await page.goto(`/login?application=${openApp.code}&return_url=/portal`);
  await page.getByRole("button", { name: "Usar una passkey" }).click();
  await expectPortal(page);
});

test("the account is deleted after confirming with the password", async ({ page, request }) => {
  const user = await openPortal(page, request);
  await page.getByRole("link", { name: "Cuenta" }).click();
  await page.getByRole("button", { name: "Eliminar mi cuenta" }).click();
  await expect(statusOf(page)).toHaveText("Marca la casilla para confirmar la eliminación.");
  await page.locator("#delete-password").fill(user.password);
  await page.getByLabel("Entiendo que mi cuenta se eliminará").check();
  await page.getByRole("button", { name: "Eliminar mi cuenta" }).click();
  await expect(page).toHaveURL(/\/login/);

  await signInWithPassword(page, user, `/login?application=${openApp.code}`);
  await expect(statusOf(page)).not.toHaveText("");
  await expect(page).toHaveURL(/\/login/);
});

test("an invitation link activates the account with a new password", async ({ page, request }) => {
  const email = newEmail("invited");
  const invited = await call(request, "POST", "/api/users/invitations", {
    token,
    data: { fullName: "Invitada Prueba", email, applicationSystemId: openApp.id, grantActiveAccess: true, roleIds: [] }
  });
  expect(invited.ok, JSON.stringify(invited.body)).toBeTruthy();

  const link = linkIn(await waitForMail(email, "Invitation"));
  expect(link).toContain(`${baseURL}/accept-invitation?`);
  await page.goto(link);
  await expect(page.getByRole("heading", { name: "Activa tu cuenta" })).toBeVisible();
  await expect(page.locator("#account-email")).toHaveValue(email);
  const password = newPassword();
  await page.locator("#new-password").fill(password);
  await page.locator("#confirm-password").fill(password);
  await page.getByRole("button", { name: "Activar cuenta" }).click();
  await expect(statusOf(page)).toContainText("Tu cuenta está lista");

  await page.getByRole("link", { name: "Iniciar sesión" }).click();
  await signInWithPassword(page, { email, password }, `/login?application=${openApp.code}&return_url=/portal`);
  await expectPortal(page, "Invitada Prueba");
});

test("an email confirmation link confirms the address before the first sign-in", async ({ page, request }) => {
  const user = await registerUser(request, confirmApp.code);
  await signInWithPassword(page, user, `/login?application=${confirmApp.code}`);
  await expect(statusOf(page)).not.toHaveText("");
  await expect(page).toHaveURL(/\/login/);

  const link = linkIn(await waitForMail(user.email, "Email confirmation"));
  await page.goto(link);
  await expect(page).toHaveURL(`${baseURL}/confirm-email`);
  await page.getByRole("button", { name: "Confirmar correo" }).click();
  await expect(statusOf(page)).toContainText("Tu correo quedó confirmado");

  await signInWithPassword(page, user, `/login?application=${confirmApp.code}&return_url=/portal`);
  await expectPortal(page);
});
