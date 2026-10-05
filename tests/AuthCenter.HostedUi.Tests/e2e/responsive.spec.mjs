import { expect, test } from "@playwright/test";
import { adminToken, baseURL, createApplication, expectPortal, registerUser, signInWithPassword } from "./support.mjs";

// Runs in the "mobile" project: the hosted pages reflow on a phone (WCAG 1.4.10), with nothing
// wider than the screen, down to 320 CSS pixels.

let openApp;

test.beforeAll(async ({ playwright }) => {
  const request = await playwright.request.newContext({ baseURL });
  openApp = await createApplication(request, await adminToken(request));
  await request.dispose();
});

async function expectNoHorizontalScroll(page, state) {
  for (const width of [390, 320]) {
    await page.setViewportSize({ width, height: 800 });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow, `${state} at ${width} px`).toBeLessThanOrEqual(0);
  }
  await page.setViewportSize({ width: 390, height: 844 });
}

test("the sign-in, the emailed-link pages and the sign-out fit a phone", async ({ page }) => {
  await page.goto(`/login?application=${openApp.code}`);
  await expect(page.locator("#login-form")).toBeVisible();
  await expectNoHorizontalScroll(page, "sign-in");

  await page.getByRole("button", { name: "Crear cuenta" }).click();
  await expect(page.locator("#register-form")).toBeVisible();
  await expectNoHorizontalScroll(page, "sign-up");

  await page.goto(`/reset-password?token=t&email=${encodeURIComponent("una.direccion.muy.larga.de.correo@ejemplo-de-dominio-largo.test")}&application=${openApp.code}`);
  await expect(page.locator("#password-form")).toBeVisible();
  await expectNoHorizontalScroll(page, "reset password");

  await page.goto("/logout");
  await expect(page.locator("#logout-description")).toHaveText("No tienes una sesión abierta.");
  await expectNoHorizontalScroll(page, "sign-out");
});

test("every panel of the portal fits a phone", async ({ page, request }) => {
  const user = await registerUser(request, openApp.code, { email: `una.direccion.muy.larga.${Date.now()}@ejemplo-de-dominio-largo.test` });
  await signInWithPassword(page, user, `/login?application=${openApp.code}&return_url=/portal`);
  await expectPortal(page);

  const navigation = page.getByRole("navigation", { name: "Mi cuenta" });
  for (const panel of ["Resumen", "Seguridad", "Sesiones", "Aplicaciones", "Proveedores", "Consentimientos", "Cuenta"]) {
    await navigation.getByRole("link", { name: panel, exact: true }).click();
    await expect(navigation.getByRole("link", { name: panel, exact: true })).toHaveAttribute("aria-current", "page");
    await page.waitForLoadState("networkidle");
    await expectNoHorizontalScroll(page, panel);
  }
});
