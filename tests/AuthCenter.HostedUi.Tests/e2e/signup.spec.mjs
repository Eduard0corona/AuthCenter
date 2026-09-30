import { expect, test } from "@playwright/test";
import {
  adminToken, authorizeUrl, baseURL, createApplication, createClient, expectPortal, linkIn, newEmail, newPassword,
  registerUser, statusOf, waitForMail
} from "./support.mjs";

// Creating an account from the hosted login, for applications open to self-registration.

let token;
let confirmApp;
let openApp;
let inviteApp;

test.beforeAll(async ({ playwright }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await adminToken(request);
  confirmApp = await createApplication(request, token, { confirmEmail: true });
  openApp = await createApplication(request, token);
  inviteApp = await createApplication(request, token, { registrationMode: "InviteOnly" });
  await request.dispose();
});

async function openSignUp(page) {
  await page.getByRole("button", { name: "Crear cuenta" }).click();
  await expect(page.getByRole("heading", { name: "Crea tu cuenta" })).toBeVisible();
}

async function submitSignUp(page, { fullName = "Ana Prueba", email, password, confirmation = password }) {
  await page.getByLabel("Nombre completo").fill(fullName);
  await page.locator("#register-email").fill(email);
  await page.locator("#register-password").fill(password);
  await page.locator("#register-confirm").fill(confirmation);
  await page.getByRole("button", { name: "Crear cuenta", exact: true }).click();
}

test("a new account confirms its email and returns to the application that asked for the sign-in", async ({ page, request }) => {
  const client = await createClient(request, token, confirmApp.id);
  // The client's callback: only the code it receives matters here.
  await page.route("**/e2e-callback**", route => route.fulfill({ status: 200, contentType: "text/plain", body: "callback" }));
  const email = newEmail("signup");
  const password = newPassword();

  await page.goto(authorizeUrl(client));
  await expect(page).toHaveURL(/\/login\?interaction_id=/);
  await openSignUp(page);
  await expect(page.locator("#register-description")).toContainText(`Aplicación ${confirmApp.code}`);
  await submitSignUp(page, { email, password });
  await expect(page.getByRole("heading", { name: "Revisa tu correo" })).toBeVisible();
  await expect(page.locator("#register-sent-description")).toContainText(email);

  // The link opens in the same browser, which continues the authorization request.
  await page.goto(linkIn(await waitForMail(email, "Email confirmation")));
  await page.getByRole("button", { name: "Confirmar correo" }).click();
  await expect(statusOf(page)).toContainText("Tu correo quedó confirmado");
  await page.getByRole("link", { name: "Continuar e iniciar sesión" }).click();
  await expect(page).toHaveURL(/\/login\?interaction_id=/);
  await page.locator("#email").fill(email);
  await page.locator("#password").fill(password);
  await page.getByRole("button", { name: "Continuar", exact: true }).click();

  await page.waitForURL(url => url.pathname === "/e2e-callback");
  expect(new URL(page.url()).searchParams.get("code")).toBeTruthy();
});

test("where confirming the email is optional, the new account enters at once", async ({ page }) => {
  await page.goto(`/login?application=${openApp.code}&return_url=/portal`);
  await openSignUp(page);
  await submitSignUp(page, { email: newEmail("signup-open"), password: newPassword() });

  await expectPortal(page);
});

test("an email that already has an account gets the same answer, and its owner a notice", async ({ page, request }) => {
  const user = await registerUser(request, confirmApp.code);

  await page.goto(`/login?application=${confirmApp.code}`);
  await openSignUp(page);
  await submitSignUp(page, { fullName: "Otra Persona", email: user.email, password: newPassword() });

  await expect(page.getByRole("heading", { name: "Revisa tu correo" })).toBeVisible();
  expect((await waitForMail(user.email, "Security notice")).html).toContain("you already have an account");
});

test("the sign-up form explains its errors and sends the email again", async ({ page }) => {
  const email = newEmail("signup-resend");
  const password = newPassword();
  await page.goto(`/login?application=${confirmApp.code}`);
  await openSignUp(page);

  await submitSignUp(page, { email, password: "corta" });
  await expect(statusOf(page)).toContainText("al menos 8 caracteres");
  await submitSignUp(page, { email, password, confirmation: newPassword() });
  await expect(statusOf(page)).toHaveText("Las contraseñas no coinciden.");
  await submitSignUp(page, { email, password });
  await expect(page.getByRole("heading", { name: "Revisa tu correo" })).toBeVisible();
  await waitForMail(email, "Email confirmation");

  await page.getByRole("button", { name: "Reenviar el correo" }).click();
  await expect(statusOf(page)).toContainText("te lo enviamos otra vez");
  await waitForMail(email, "Email confirmation");
  await page.getByRole("button", { name: "Volver a iniciar sesión" }).click();
  await expect(page.locator("#email")).toHaveValue(email);
});

test("an invite-only application does not offer to create an account", async ({ page }) => {
  await page.goto(`/login?application=${inviteApp.code}`);

  await expect(page.locator("#login-form")).toBeVisible();
  await expect(page.getByRole("button", { name: "Crear cuenta" })).toBeHidden();
});
