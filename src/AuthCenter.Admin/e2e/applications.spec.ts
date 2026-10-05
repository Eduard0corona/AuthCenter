import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { applicationId, json, mockShell, paged } from "./support";

const registrationSettings = {
  registrationMode: "Closed", audience: "Employees", allowGoogleLogin: false, allowMicrosoftLogin: false, allowGitHubLogin: false, allowAppleLogin: false,
  allowMagicLink: false, allowPasswordLogin: true, requireEmailConfirmation: true, requireMfa: false, allowedEmailDomains: null, defaultRoleId: null
};
const application = {
  id: applicationId, code: "TIENDITAPP", name: "TienditApp", description: "Comercio hermano", isActive: true, createdAt: "2026-08-11T00:00:00Z",
  updatedAt: null, version: 3, registrationSettings, branding: null
};
const oauthClient = {
  id: "77777777-7777-4777-8777-777777777777", applicationSystemId: applicationId, applicationCode: "TIENDITAPP", applicationName: "TienditApp",
  clientId: "tiendit-web", displayName: "Tiendit web", clientType: 0, redirectUris: ["https://tiendit.example.test/signin-authcenter"],
  allowedScopes: ["openid", "profile", "email", "offline_access"], grantTypes: ["authorization_code", "refresh_token"], loginUrl: "https://id.example.test/login",
  accessTokenLifetimeSeconds: 900, requirePkce: true, autoConsent: false, isActive: true, createdAt: "2026-08-13T00:00:00Z", updatedAt: null
};

/** The application's page and what it reads: its roles, owners and the lists it counts. */
async function mockApplicationPage(page: Page, clients: number, samlApps: number) {
  const queries = { clients: [] as URL[], saml: [] as URL[] };
  await mockShell(page);
  await page.route(`**/api/applications/${applicationId}`, (route) => json(route, application));
  await page.route("**/api/applications?**", (route) => json(route, paged([application], 1, 100)));
  await page.route("**/api/roles?**", (route) => json(route, paged([], 1, 100)));
  await page.route("**/api/api-resources?**", (route) => json(route, paged([], 1, 100)));
  await page.route(`**/api/governance/applications/${applicationId}`, (route) => json(route, {
    applicationSystemId: applicationId, applicationCode: "TIENDITAPP", applicationName: "TienditApp", accessRequestsEnabled: false, owners: [], version: 0
  }));
  await page.route("**/api/oauth/clients?**", (route) => {
    queries.clients.push(new URL(route.request().url()));
    return json(route, { items: clients ? [oauthClient] : [], totalCount: clients, page: 1, pageSize: 1, totalPages: clients });
  });
  await page.route("**/api/saml/service-providers?**", (route) => {
    queries.saml.push(new URL(route.request().url()));
    return json(route, { items: [], totalCount: samlApps, page: 1, pageSize: 1, totalPages: samlApps });
  });
  return queries;
}

test("an application's page shows what it is connected to and who can enter", async ({ page }) => {
  const queries = await mockApplicationPage(page, 2, 0);
  await page.goto(`/admin-v2/applications/${applicationId}`);

  const panel = page.getByRole("region", { name: "Integraciones y acceso" });
  const clients = panel.getByRole("link", { name: /Clientes OAuth/ });
  await expect(clients).toContainText("2");
  await expect(clients).toHaveAttribute("href", `/admin-v2/oauth-clients?applicationId=${applicationId}`);
  await expect(panel.getByRole("link", { name: /Aplicaciones SAML/ })).toContainText("0");
  await expect(panel.getByRole("link", { name: /Aplicaciones SAML/ })).toHaveAttribute("href", `/admin-v2/saml-apps?application=${applicationId}`);
  await expect(panel.getByRole("link", { name: /Personas con acceso/ })).toHaveAttribute("href", `/admin-v2/users?application=${applicationId}`);
  await expect(panel.getByRole("link", { name: /Política de acceso/ })).toHaveAttribute("href", `/admin-v2/access-policies/${applicationId}`);
  await expect(panel.getByRole("link", { name: /Federación/ })).toHaveAttribute("href", `/admin-v2/federation?applicationId=${applicationId}`);
  // Each count asks its list for a single item of this application.
  for (const query of [queries.clients.at(-1), queries.saml.at(-1)]) {
    expect(query?.searchParams.get("applicationSystemId")).toBe(applicationId);
    expect(query?.searchParams.get("pageSize")).toBe("1");
  }
  await expect(panel.getByText(/Todavía no tiene clientes/)).toHaveCount(0);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

  // A new client starts with this application chosen.
  await panel.getByRole("link", { name: "Nuevo cliente OAuth" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/oauth-clients/new\\?applicationId=${applicationId}$`));
  await expect(page.getByLabel("Aplicación")).toHaveValue(applicationId);
});

test("a new application's audience sets its registration until the operator picks one", async ({ page }) => {
  let createPayload: Record<string, unknown> | null = null;
  await mockApplicationPage(page, 0, 0);
  await page.route("**/api/applications", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await json(route, application, 201);
  });

  await page.goto("/admin-v2/applications/new");
  const audience = page.getByLabel("Público");
  const mode = page.getByLabel("Modo de registro");
  const confirmation = page.getByLabel("Confirmación de correo");
  // Employees by default, let in by an administrator.
  await expect(audience).toHaveValue("Employees");
  await expect(mode).toHaveValue("Closed");
  await expect(audience).toHaveAccessibleDescription(/a los consumidores se les ofrece crear cuenta/);
  // Consumers create their own account and confirm their email; employees again closes it.
  await confirmation.uncheck();
  await audience.selectOption("Consumers");
  await expect(mode).toHaveValue("Open");
  await expect(confirmation).toBeChecked();
  await audience.selectOption("Employees");
  await expect(mode).toHaveValue("Closed");
  await audience.selectOption("Consumers");
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

  await page.getByLabel("Código").fill("tiendit_shop");
  await page.getByLabel("Nombre", { exact: true }).fill("Tiendit Shop");
  await page.getByRole("button", { name: "Crear aplicación" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/applications/${applicationId}$`));
  expect(createPayload).toMatchObject({ code: "TIENDIT_SHOP", audience: "Consumers", registrationMode: "Open", requireEmailConfirmation: true });
});

test("a registration mode the operator chose stays when the audience changes", async ({ page }) => {
  await mockApplicationPage(page, 0, 0);
  await page.goto("/admin-v2/applications/new");
  await page.getByLabel("Modo de registro").selectOption("InviteOnly");
  await page.getByLabel("Público").selectOption("Consumers");
  await expect(page.getByLabel("Modo de registro")).toHaveValue("InviteOnly");
});

test("changing an existing application's audience keeps its registration and saves it", async ({ page }) => {
  let updatePayload: Record<string, unknown> | null = null;
  await mockApplicationPage(page, 1, 0);
  await page.route(`**/api/applications/${applicationId}`, async (route) => {
    if (route.request().method() === "PUT") {
      updatePayload = route.request().postDataJSON() as Record<string, unknown>;
      await json(route, { ...application, version: 4, registrationSettings: { ...registrationSettings, audience: "Consumers" } });
      return;
    }
    await json(route, application);
  });

  await page.goto(`/admin-v2/applications/${applicationId}`);
  await expect(page.getByLabel("Público")).toHaveValue("Employees");
  await page.getByLabel("Público").selectOption("Consumers");
  await expect(page.getByLabel("Modo de registro")).toHaveValue("Closed");
  await page.getByRole("button", { name: "Guardar configuración" }).click();
  await expect(page.getByRole("status").filter({ hasText: "quedó actualizada" })).toBeVisible();
  expect(updatePayload).toMatchObject({ audience: "Consumers", registrationMode: "Closed", version: 3 });
  await expect(page.getByLabel("Público")).toHaveValue("Consumers");
});

test("the application list says who signs in to each application", async ({ page }) => {
  const shop = { ...application, id: "22222222-2222-4222-8222-222222222222", code: "TIENDA", name: "Tienda", registrationSettings: { ...registrationSettings, registrationMode: "Open", audience: "Consumers" } };
  await mockShell(page);
  await page.route("**/api/applications?**", (route) => json(route, paged([application, shop])));
  await page.goto("/admin-v2/applications");
  await expect(page.getByRole("article").filter({ hasText: "TienditApp" })).toContainText("Público: Empleados");
  await expect(page.getByRole("article").filter({ hasText: "TIENDA" })).toContainText("Público: Consumidores");
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("an application without clients says how its users will sign in", async ({ page }) => {
  await mockApplicationPage(page, 0, 0);
  await page.goto(`/admin-v2/applications/${applicationId}`);
  const panel = page.getByRole("region", { name: "Integraciones y acceso" });
  await expect(panel.getByRole("link", { name: /Clientes OAuth/ })).toContainText("0");
  await expect(panel.getByText("Todavía no tiene clientes: crea uno para que sus usuarios puedan iniciar sesión.")).toBeVisible();
});
