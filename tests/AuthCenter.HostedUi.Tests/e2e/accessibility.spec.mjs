import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import {
  adminToken, baseURL, createApplication, expectPortal, newPassword, registerUser, signInWithPassword, statusOf
} from "./support.mjs";

// axe over the pages people use without an administrator: each step of the hosted sign-in, every
// panel of the portal and the pages the emailed links open. The console has its own suite.

let openApp;
let mfaApp;

test.beforeAll(async ({ playwright }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await adminToken(request);
  openApp = await createApplication(request, token, { magicLink: true });
  mfaApp = await createApplication(request, token, { requireMfa: true });
  await request.dispose();
});

// target-size is axe's WCAG 2.2 AA rule, off by default.
async function expectAccessible(page, state) {
  const { violations } = await new AxeBuilder({ page }).options({ rules: { "target-size": { enabled: true } } }).analyze();
  expect(violations.map(violation => `${state} · ${violation.id}: ${violation.nodes.map(node => node.target.join(" ")).join(", ")}`)).toEqual([]);
}

test("each step of the hosted sign-in is accessible", async ({ page, request }) => {
  await page.goto(`/login?application=${openApp.code}`);
  await expect(page.locator("#login-form")).toBeVisible();
  await expectAccessible(page, "sign-in form");

  const user = await registerUser(request, openApp.code);
  await signInWithPassword(page, { email: user.email, password: newPassword() }, `/login?application=${openApp.code}`);
  await expect(statusOf(page)).toHaveText("El correo o la contraseña no son correctos.");
  await expectAccessible(page, "wrong password");

  await page.getByRole("button", { name: "¿Olvidaste tu contraseña?" }).click();
  await expect(page.locator("#forgot-email")).toBeVisible();
  await expectAccessible(page, "forgotten password");

  const enrolling = await registerUser(request, mfaApp.code);
  await signInWithPassword(page, enrolling, `/login?application=${mfaApp.code}&return_url=/portal`);
  await expect(page.getByRole("heading", { name: "Activa la verificación en dos pasos" })).toBeVisible();
  await expect(page.locator("#totp-qr svg")).toBeVisible();
  await expectAccessible(page, "second factor enrollment");
});

test("every panel of the portal is accessible", async ({ page, request }) => {
  const user = await registerUser(request, openApp.code);
  await signInWithPassword(page, user, `/login?application=${openApp.code}&return_url=/portal`);
  await expectPortal(page);

  const navigation = page.getByRole("navigation", { name: "Mi cuenta" });
  for (const panel of ["Resumen", "Seguridad", "Sesiones", "Aplicaciones", "Proveedores", "Consentimientos", "Cuenta"]) {
    await navigation.getByRole("link", { name: panel, exact: true }).click();
    await expect(navigation.getByRole("link", { name: panel, exact: true })).toHaveAttribute("aria-current", "page");
    // The panel's lists load after it opens.
    await page.waitForLoadState("networkidle");
    await expectAccessible(page, panel);
  }
});

test("the pages the emailed links open are accessible", async ({ page }) => {
  // Without a token each page explains that the link is not valid.
  for (const path of ["/reset-password", "/accept-invitation", "/confirm-email", "/confirm-email-change", "/magic-link"]) {
    await page.goto(path);
    await expect(page.locator("main")).toBeVisible();
    await page.waitForLoadState("networkidle");
    await expectAccessible(page, path);
  }
});
