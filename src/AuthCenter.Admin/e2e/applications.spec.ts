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

test("an application without clients says how its users will sign in", async ({ page }) => {
  await mockApplicationPage(page, 0, 0);
  await page.goto(`/admin-v2/applications/${applicationId}`);
  const panel = page.getByRole("region", { name: "Integraciones y acceso" });
  await expect(panel.getByRole("link", { name: /Clientes OAuth/ })).toContainText("0");
  await expect(panel.getByText("Todavía no tiene clientes: crea uno para que sus usuarios puedan iniciar sesión.")).toBeVisible();
});
