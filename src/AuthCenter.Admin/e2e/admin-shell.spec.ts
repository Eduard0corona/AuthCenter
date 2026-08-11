import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

test.beforeEach(async ({ page }) => {
  await page.route("**/ui-api/session", async (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({ success: true, data: { user: {
      id: "operator-1",
      name: "Ada Operadora",
      email: "ada@example.test",
      applications: ["AUTHCENTER"],
      roles: ["Admin"],
      permissions: ["AUTHCENTER_USERS_READ", "AUTHCENTER_APPLICATIONS_READ", "AUTHCENTER_AUDIT_LOGS_READ", "AUTHCENTER_APPLICATIONS_WRITE"]
    }, csrfToken: "e2e-csrf" } })
  }));
  await page.route("**/api/users?**", async (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({ success: true, data: { items: [{
      id: "user-1", fullName: "Grace Hopper", email: "grace@example.test", pictureUrl: null,
      isActive: true, isExternalUser: false, createdAt: "2026-08-11T00:00:00Z", lastLoginAt: null,
      roles: ["Operator"], applications: ["AUTHCENTER"]
    }], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 } })
  }));
});

test("shell and users route are keyboard-visible and axe-clean", async ({ page }) => {
  await page.goto("/admin-v2/");
  await expect(page.getByRole("heading", { name: "Hola, Ada" })).toBeVisible();
  await page.getByRole("link", { name: /Usuarios Directorio/ }).click();
  await expect(page.getByRole("heading", { name: "Usuarios" })).toBeVisible();
  await expect(page.getByText("Grace Hopper")).toBeVisible();

  const accessibility = await new AxeBuilder({ page }).analyze();
  expect(accessibility.violations).toEqual([]);
});
