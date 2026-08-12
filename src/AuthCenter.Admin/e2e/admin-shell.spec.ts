import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

const applicationId = "11111111-1111-1111-1111-111111111111";
const application = {
  id: applicationId,
  code: "TIENDITAPP",
  name: "TienditApp",
  description: "Comercio hermano",
  isActive: true,
  createdAt: "2026-08-11T00:00:00Z",
  updatedAt: null,
  registrationSettings: {
    registrationMode: "InviteOnly",
    allowGoogleLogin: true,
    allowMicrosoftLogin: false,
    allowGitHubLogin: false,
    allowAppleLogin: false,
    allowMagicLink: true,
    allowPasswordLogin: true,
    requireEmailConfirmation: true,
    requireMfa: false,
    allowedEmailDomains: "tiendit.app",
    defaultRoleId: null
  },
  branding: {
    applicationCode: "TIENDITAPP",
    displayName: "TienditApp",
    primaryColor: "#175cd3",
    backgroundColor: "#ffffff",
    logoUrl: "https://assets.example.test/tiendit.svg",
    supportUrl: "https://tiendit.app/soporte",
    privacyUrl: "https://tiendit.app/privacidad",
    termsUrl: "https://tiendit.app/terminos"
  }
};

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
  await page.route(`**/api/applications/${applicationId}`, async (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({ success: true, data: application })
  }));
  await page.route("**/api/applications?**", async (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({ success: true, data: { items: [application], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 } })
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

test("application detail preserves the complete branding contract", async ({ page }) => {
  let brandingPayload: Record<string, unknown> | null = null;
  await page.route(`**/api/applications/${applicationId}/branding`, async (route) => {
    brandingPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: brandingPayload }) });
  });

  await page.goto(`/admin-v2/applications/${applicationId}`);
  const heading = page.getByRole("heading", { level: 1, name: "TienditApp" });
  await expect(heading).toBeVisible();
  await expect(heading).toBeFocused();
  await expect(page.getByRole("navigation", { name: "Migas de pan" })).toContainText("Aplicaciones");

  await page.getByRole("button", { name: "Editar branding" }).click();
  await expect(page.getByLabel("Privacidad HTTPS")).toHaveValue("https://tiendit.app/privacidad");
  await expect(page.getByLabel("Términos HTTPS")).toHaveValue("https://tiendit.app/terminos");
  await page.getByLabel("Nombre visible").fill("TienditApp Pro");

  const accessibility = await new AxeBuilder({ page }).include("dialog").analyze();
  expect(accessibility.violations).toEqual([]);
  await page.getByRole("button", { name: "Guardar branding" }).click();
  await expect(page.getByRole("status")).toContainText("branding");
  expect(brandingPayload).toMatchObject({
    displayName: "TienditApp Pro",
    privacyUrl: "https://tiendit.app/privacidad",
    termsUrl: "https://tiendit.app/terminos",
    supportUrl: "https://tiendit.app/soporte"
  });
});

test("creates an application with explicit secure registration defaults", async ({ page }) => {
  let createPayload: Record<string, unknown> | null = null;
  await page.route("**/api/applications", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ success: true, data: { ...application, ...createPayload, id: applicationId, branding: null } })
    });
  });

  await page.goto("/admin-v2/applications/new");
  await page.getByLabel("Código").fill("partner_portal");
  await page.getByLabel("Nombre", { exact: true }).fill("Partner Portal");
  await page.getByLabel("Descripción").fill("Acceso para socios");
  await page.getByRole("button", { name: "Crear aplicación" }).click();

  await expect(page).toHaveURL(new RegExp(`/admin-v2/applications/${applicationId}$`));
  expect(createPayload).toMatchObject({
    code: "PARTNER_PORTAL",
    name: "Partner Portal",
    registrationMode: "Closed",
    allowPasswordLogin: true,
    requireEmailConfirmation: true,
    requireMfa: false,
    defaultRoleId: null
  });
});

test("application detail is read-only without write permission", async ({ page }) => {
  await page.unroute("**/ui-api/session");
  await page.route("**/ui-api/session", async (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({ success: true, data: { user: {
      id: "auditor-1",
      name: "Auditor",
      email: "auditor@example.test",
      applications: ["AUTHCENTER"],
      roles: ["Auditor"],
      permissions: ["AUTHCENTER_APPLICATIONS_READ"]
    }, csrfToken: "read-only-csrf" } })
  }));

  await page.goto(`/admin-v2/applications/${applicationId}`);
  await expect(page.getByLabel("Nombre", { exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Guardar configuración" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Editar branding" })).toHaveCount(0);
  await expect(page.getByText("AUTHCENTER_APPLICATIONS_WRITE")).toBeVisible();
});
