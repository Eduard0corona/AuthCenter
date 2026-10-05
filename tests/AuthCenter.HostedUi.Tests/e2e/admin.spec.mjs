import { readFile } from "node:fs/promises";
import { randomBytes } from "node:crypto";
import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { adminToken, call, createApplication, newEmail, newPassword, signInWithPassword } from "./support.mjs";

// The administration console (/admin-v2, the Vite build in src/AuthCenter.Admin/dist) against the
// real API: sessions, CSRF, contracts and permissions are the server's, not mocks.

const admin = { email: process.env.HOSTED_UI_ADMIN_EMAIL, password: process.env.HOSTED_UI_ADMIN_PASSWORD };
let token;

test.beforeAll(async ({ request }) => {
  token = await adminToken(request);
});

async function openConsole(page, path = "/admin-v2/") {
  await signInWithPassword(page, admin, `/login?application=AUTHCENTER&return_url=${encodeURIComponent(path)}`);
  await expect(page).toHaveURL(new RegExp(`${path.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`));
}

async function expectAccessible(page) {
  const { violations } = await new AxeBuilder({ page }).analyze();
  expect(violations.map(violation => `${violation.id}: ${violation.nodes.map(node => node.target.join(" ")).join(", ")}`)).toEqual([]);
}

test("the console opens from the hosted sign-in with live metrics, environment and version", async ({ page }) => {
  await openConsole(page);

  await expect(page.getByRole("heading", { level: 1, name: "Hola, Hosted" })).toBeVisible();
  const status = page.getByRole("region", { name: "Estado de la plataforma" });
  await expect(status.getByText("Usuarios activos")).toBeVisible();
  await expect(page.locator(".environment-pill")).toContainText("Desarrollo");
  await expect(page.locator(".sidebar__version")).toContainText("AuthCenter v");
  await expectAccessible(page);

  // The retired console sends its bookmarks here.
  await page.goto("/admin");
  await expect(page).toHaveURL(/\/admin-v2\/$/);
});

test("an administrator who signs in without a destination lands in the console", async ({ page }) => {
  await signInWithPassword(page, admin);

  await expect(page).toHaveURL(/\/admin-v2\/$/);
});

test("creates a profile attribute and finds it in the System Log and its CSV export", async ({ page, request }) => {
  const key = `e2e_level_${randomBytes(3).toString("hex")}`;
  await openConsole(page, "/admin-v2/profile-schema/new");

  await page.getByLabel("Clave").fill(key);
  await page.getByLabel("Nombre visible").fill("Nivel E2E");
  await page.getByLabel("Tipo de dato").selectOption("Integer");
  await page.getByLabel("Mínimo", { exact: true }).fill("1");
  await page.getByLabel("Máximo", { exact: true }).fill("10");
  await page.getByLabel("Valores permitidos").fill("1\n5\n10");
  await page.getByRole("button", { name: "Crear atributo" }).click();
  await expect(page).toHaveURL(/\/admin-v2\/profile-schema\/[0-9a-f-]{36}$/);
  const definitionId = page.url().split("/").at(-1);

  const schema = await call(request, "GET", "/api/profile-schema", { token });
  expect(schema.data.find(definition => definition.key === key)).toMatchObject({ dataType: "Integer", minimumNumber: 1, maximumNumber: 10, allowedValues: [1, 5, 10] });

  await page.getByRole("link", { name: "Ver historial" }).click();
  await expect(page).toHaveURL(new RegExp(`entity=UserProfileAttributeDefinition&entityId=${definitionId}$`));
  const row = page.getByRole("row", { name: /PROFILE_ATTRIBUTE_DEFINITION_CREATED/ });
  await expect(row).toContainText("Hosted UI Admin");
  await row.getByRole("button", { name: /Detalle del evento/ }).click();
  const detail = page.getByRole("dialog");
  await expect(detail).toContainText(definitionId);
  await expectAccessible(page);
  await detail.getByRole("button", { name: "Cerrar" }).click();

  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Exportar CSV" }).click();
  const csv = await readFile(await (await download).path(), "utf8");
  expect(csv.split("\r\n")[0]).toBe("id,createdAt,action,actorId,actorEmail,applicationCode,entityName,entityId,ipAddress,traceId");
  expect(csv).toContain("PROFILE_ATTRIBUTE_DEFINITION_CREATED");
  await expect(page.getByRole("status").filter({ hasText: "Se exportaron" })).toBeVisible();
});

test("registers an API and grants one of its scopes to an OAuth client from the catalog", async ({ page, request }) => {
  const application = await createApplication(request, token);
  const suffix = randomBytes(3).toString("hex");
  const identifier = `https://api.e2e.test/orders-${suffix}`;
  const scope = `orders.${suffix}.read`;
  const clientId = `e2e-console-${suffix}`;
  const created = await call(request, "POST", "/api/oauth/clients", {
    token,
    data: {
      applicationSystemId: application.id, clientId, displayName: "Consola E2E", clientType: 1,
      redirectUris: ["https://app.e2e.test/callback"], allowedScopes: ["openid", "profile"], grantTypes: ["authorization_code"],
      loginUrl: "https://app.e2e.test/login", requirePkce: true, autoConsent: false,
      backchannelLogoutUri: "https://app.e2e.test/auth/backchannel-logout", backchannelLogoutSessionRequired: false
    }
  });
  expect(created.ok).toBe(true);

  await openConsole(page, "/admin-v2/api-resources/new");
  await page.getByLabel("Aplicación dueña").selectOption(application.id);
  await page.getByLabel("Identificador (audiencia)").fill(identifier);
  await page.getByLabel("Nombre", { exact: true }).fill(`Orders ${suffix}`);
  await page.getByLabel("Nombre del scope 1").fill(scope);
  await page.getByLabel("Nombre visible del scope 1").fill("Leer pedidos");
  await page.getByRole("button", { name: "Crear API" }).click();
  await expect(page).toHaveURL(/\/admin-v2\/api-resources\/[0-9a-f-]{36}$/);

  await page.goto(`/admin-v2/oauth-clients/${clientId}`);
  await page.getByRole("checkbox", { name: new RegExp(scope.replaceAll(".", "\\.")) }).check();
  await expect(page.getByRole("checkbox", { name: "Incluir el identificador de sesión (sid) en el aviso" })).not.toBeChecked();
  await page.getByRole("button", { name: "Guardar configuración" }).click();
  await expect(page.getByRole("status")).toBeVisible();

  const client = await call(request, "GET", `/api/oauth/clients/${clientId}`, { token });
  expect(client.data.allowedScopes).toEqual(expect.arrayContaining(["openid", "profile", scope]));
  // A console save no longer switches the client's back-channel session setting back on.
  expect(client.data.backchannelLogoutSessionRequired).toBe(false);
  await expectAccessible(page);
});

test("keeps working after another tab replaced the CSRF token", async ({ page }) => {
  await openConsole(page, "/admin-v2/profile-schema/new");

  // Any page of the same browser that reads the session (the portal, another console tab) issues a
  // new double-submit cookie, so the token this tab holds no longer matches.
  const renewed = await page.request.get("/ui-api/session");
  expect(renewed.ok()).toBe(true);

  await page.getByLabel("Clave").fill(`e2e_csrf_${randomBytes(3).toString("hex")}`);
  await page.getByLabel("Nombre visible").fill("Tras renovar CSRF");
  await page.getByRole("button", { name: "Crear atributo" }).click();
  await expect(page).toHaveURL(/\/admin-v2\/profile-schema\/[0-9a-f-]{36}$/);
});

test("lists the users waiting for access approval", async ({ page, request }) => {
  const code = `E2EAPR${randomBytes(3).toString("hex").toUpperCase()}`;
  const application = await call(request, "POST", "/api/applications", {
    token,
    data: { code, name: `Aprobación ${code}`, registrationMode: "ApprovalRequired", allowPasswordLogin: true }
  });
  expect(application.ok).toBe(true);
  const email = newEmail("pending");
  const registration = await call(request, "POST", "/api/auth/register", { data: { fullName: "Ana Pendiente", email, password: newPassword(), applicationCode: code } });
  expect(registration.body?.errorCode).toBe("APPROVAL_REQUIRED");

  await openConsole(page, `/admin-v2/users?pendingAccess=true&search=${encodeURIComponent(email)}`);
  const row = page.getByRole("row", { name: /Ana Pendiente/ });
  await expect(row).toContainText("Acceso pendiente");
  await expect(page.getByRole("combobox", { name: "Acceso", exact: true })).toHaveValue("true");
  await expectAccessible(page);
});

test("an operator without event hook permissions cannot open them", async ({ page, request }) => {
  const applications = await call(request, "GET", "/api/applications?page=1&pageSize=100", { token });
  const authCenter = applications.data.items.find(item => item.code === "AUTHCENTER");
  const roleName = `Lector ${randomBytes(3).toString("hex")}`;
  const role = await call(request, "POST", "/api/roles", { token, data: { applicationSystemId: authCenter.id, name: roleName } });
  const permissions = await call(request, "GET", `/api/applications/${authCenter.id}/permissions?pageSize=100`, { token });
  const usersRead = permissions.data.items.find(permission => permission.code === "AUTHCENTER_USERS_READ");
  expect((await call(request, "POST", `/api/roles/${role.data.id}/permissions/${usersRead.id}`, { token })).ok).toBe(true);

  const operator = { email: newEmail("operator"), password: newPassword() };
  const user = await call(request, "POST", "/api/users", {
    token,
    data: { fullName: "Operador Lector", email: operator.email, password: operator.password, isTemporaryPassword: false, applicationSystemId: authCenter.id, grantApplicationAccess: true, roleIds: [role.data.id] }
  });
  expect(user.ok).toBe(true);

  await signInWithPassword(page, operator, "/login?application=AUTHCENTER&return_url=%2Fadmin-v2%2Fevent-hooks");
  await expect(page.getByRole("heading", { name: "Acceso restringido" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Webhooks de eventos" })).toHaveCount(0);
  const forbidden = await page.request.get("/api/event-hooks");
  expect(forbidden.status()).toBe(403);
});
