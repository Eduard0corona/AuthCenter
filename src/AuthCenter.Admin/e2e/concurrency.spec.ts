import { expect, test } from "@playwright/test";
import { applicationId, json, mockShell, paged } from "./support";

const roleId = "22222222-2222-4222-8222-222222222222";
const application = { id: applicationId, code: "TIENDITAPP", name: "TienditApp", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z", updatedAt: null, branding: null, version: 4 };

test("a stale role save explains the conflict and loads the current version", async ({ page }) => {
  const updates: Array<Record<string, unknown>> = [];
  let current = { id: roleId, name: "Operators", description: "Operación diaria", applicationSystemId: applicationId, isSystemRole: false, isActive: true, createdAt: "2026-08-11T00:00:00Z", permissions: [], version: 3 };
  await mockShell(page);
  await page.route("**/api/applications?**", (route) => json(route, paged([application], 1, 100)));
  await page.route(`**/api/applications/${applicationId}/permissions?**`, (route) => json(route, paged([], 1, 100)));
  await page.route(`**/api/roles/${roleId}`, async (route) => {
    if (route.request().method() === "PUT") {
      updates.push(route.request().postDataJSON() as Record<string, unknown>);
      // Someone else renamed the role in the meantime.
      current = { ...current, name: "Operadores de tienda", version: 4 };
      await route.fulfill({ status: 409, contentType: "application/json", body: JSON.stringify({ success: false, errorCode: "CONCURRENCY_CONFLICT", message: "The role changed after it was loaded." }) });
      return;
    }
    await json(route, current);
  });

  await page.goto(`/admin-v2/roles/${roleId}`);
  await expect(page.getByLabel("Nombre")).toHaveValue("Operators");
  await page.getByLabel("Nombre").fill("Operadores");
  await page.getByRole("button", { name: /Guardar/ }).first().click();

  const alert = page.getByRole("alert").filter({ hasText: "Alguien más cambió este registro" });
  await expect(alert).toBeVisible();
  expect(updates[0]).toMatchObject({ name: "Operadores", version: 3 });
  await alert.getByRole("button", { name: "Cargar la versión actual" }).click();
  await expect(page.getByLabel("Nombre")).toHaveValue("Operadores de tienda");
  await expect(alert).toBeHidden();
});

test("editors send the version they loaded", async ({ page }) => {
  let applicationUpdate: Record<string, unknown> | null = null;
  await mockShell(page);
  await page.route("**/api/roles?**", (route) => json(route, paged([], 1, 100)));
  await page.route(`**/api/applications/${applicationId}`, async (route) => {
    if (route.request().method() === "PUT") applicationUpdate = route.request().postDataJSON() as Record<string, unknown>;
    await json(route, { ...application, registrationSettings: { registrationMode: "Closed", allowGoogleLogin: false, allowMicrosoftLogin: false, allowGitHubLogin: false, allowAppleLogin: false, allowMagicLink: false, allowPasswordLogin: true, requireEmailConfirmation: false, requireMfa: false, allowedEmailDomains: null, defaultRoleId: null } });
  });

  await page.goto(`/admin-v2/applications/${applicationId}`);
  await page.getByLabel("Nombre", { exact: true }).fill("TienditApp Mx");
  await page.getByRole("button", { name: /Guardar/ }).first().click();
  await expect.poll(() => applicationUpdate).not.toBeNull();
  expect(applicationUpdate).toMatchObject({ name: "TienditApp Mx", version: 4 });
});
