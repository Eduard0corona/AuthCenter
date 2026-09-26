import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { applicationId, json, mockShell, mockStepUp, paged } from "./support";

const application = { id: applicationId, code: "TIENDITAPP", name: "TienditApp", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z", updatedAt: null, branding: null };
const department = {
  id: "66666666-6666-4666-8666-666666666666", key: "department", displayName: "Departamento", description: "Área organizacional", dataType: "String",
  isRequired: true, isActive: true, defaultValue: "Operaciones", minLength: 2, maxLength: 80, minimumNumber: null, maximumNumber: null,
  validationPattern: null, allowedValues: ["Operaciones", "Ingeniería"], createdAt: "2026-08-11T00:00:00Z", updatedAt: null
};
const ordersApi = {
  id: "abababab-abab-4bab-8bab-abababababab", applicationSystemId: applicationId, applicationCode: "TIENDITAPP", applicationName: "TienditApp",
  identifier: "https://api.tiendit.app/orders", displayName: "Orders API", description: null, isActive: true,
  scopes: [{ id: "s1", name: "orders.read", displayName: "Leer pedidos", description: null }, { id: "s2", name: "orders.write", displayName: "Modificar pedidos", description: null }],
  createdAt: "2026-09-01T00:00:00Z", updatedAt: null
};
const oauthClient = {
  id: "77777777-7777-4777-8777-777777777777", applicationSystemId: applicationId, applicationCode: "TIENDITAPP", applicationName: "TienditApp",
  clientId: "partner_portal", displayName: "Partner Portal", clientType: 0, redirectUris: ["https://partner.example.test/callback"],
  allowedScopes: ["openid", "profile", "orders.read", "legacy.scope"], grantTypes: ["authorization_code", "refresh_token"], loginUrl: "https://partner.example.test/login",
  allowedCorsOrigins: [], postLogoutRedirectUris: [], backchannelLogoutUri: "https://partner.example.test/auth/backchannel-logout", backchannelLogoutSessionRequired: false,
  accessTokenLifetimeSeconds: 900, requirePkce: true, autoConsent: false, isActive: true, createdAt: "2026-08-13T00:00:00Z", updatedAt: null
};

test("creates a typed profile attribute with constraints", async ({ page }) => {
  let payload: Record<string, unknown> | null = null;
  await mockShell(page);
  await page.route("**/api/profile-schema", async (route) => {
    if (route.request().method() === "POST") {
      payload = route.request().postDataJSON() as Record<string, unknown>;
      await json(route, { ...department, id: "77777777-0000-4000-8000-000000000001", key: "level", displayName: "Nivel", dataType: "Integer", isRequired: false, defaultValue: null, minLength: null, maxLength: null, minimumNumber: 1, maximumNumber: 10, allowedValues: [] }, 201);
      return;
    }
    await json(route, [department]);
  });
  await page.route("**/api/profile-schema?includeInactive=true", (route) => json(route, [department, { ...department, id: "77777777-0000-4000-8000-000000000001", key: "level", displayName: "Nivel", dataType: "Integer", isRequired: false, defaultValue: null, minLength: null, maxLength: null, minimumNumber: 1, maximumNumber: 10, allowedValues: [] }]));

  await page.goto("/admin-v2/profile-schema");
  await expect(page.getByRole("cell", { name: /Departamento department/ })).toBeVisible();
  await expect(page.getByText("2–80 caracteres · 2 valores permitidos")).toBeVisible();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);

  await page.getByRole("link", { name: "Nuevo atributo" }).click();
  await page.getByLabel("Clave").fill("email");
  await page.getByLabel("Nombre visible").fill("Nivel");
  await page.getByLabel("Tipo de dato").selectOption("Integer");
  await expect(page.getByLabel("Longitud mínima")).toHaveCount(0);
  await page.getByLabel("Mínimo", { exact: true }).fill("1");
  await page.getByLabel("Máximo", { exact: true }).fill("10");
  await page.getByRole("checkbox", { name: "Obligatorio en todos los perfiles" }).check();
  await page.getByRole("button", { name: "Crear atributo" }).click();
  await expect(page.getByText("Esa clave corresponde a un campo integrado del perfil.")).toBeVisible();
  await expect(page.getByText(/necesita un valor predeterminado/)).toBeVisible();

  await page.getByLabel("Clave").fill("Level");
  await page.getByRole("checkbox", { name: "Obligatorio en todos los perfiles" }).uncheck();
  await page.getByLabel("Valores permitidos").fill("1\n5\n10");
  await page.getByRole("button", { name: "Crear atributo" }).click();
  await expect(page).toHaveURL(/\/admin-v2\/profile-schema\/77777777-0000-4000-8000-000000000001$/);
  expect(payload).toEqual({ key: "level", displayName: "Nivel", description: null, dataType: "Integer", isRequired: false, defaultValue: null, minLength: null, maxLength: null, minimumNumber: 1, maximumNumber: 10, validationPattern: null, allowedValues: [1, 5, 10] });
});

test("explains a schema change that would invalidate stored values and deactivates the attribute", async ({ page }) => {
  let deleted = false;
  await mockShell(page);
  await page.route("**/api/profile-schema?includeInactive=true", (route) => json(route, [{ ...department, isActive: !deleted }]));
  await page.route(`**/api/profile-schema/${department.id}`, async (route) => {
    if (route.request().method() === "DELETE") {
      deleted = true;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
      return;
    }
    await route.fulfill({ status: 400, contentType: "application/json", body: JSON.stringify({ success: false, errorCode: "PROFILE_EXISTING_VALUES_INVALID", message: "The schema change would invalidate existing profile values." }) });
  });

  await page.goto(`/admin-v2/profile-schema/${department.id}`);
  await expect(page.getByLabel("Clave")).toHaveAttribute("readonly", "");
  await page.getByLabel("Longitud máxima").fill("3");
  await page.getByRole("button", { name: "Guardar cambios" }).click();
  await expect(page.getByRole("alert")).toContainText("invalidaría valores que ya tienen algunos perfiles");

  await page.getByRole("button", { name: "Desactivar atributo" }).click();
  const confirm = page.getByRole("dialog", { name: "Desactivar atributo" });
  await confirm.getByRole("button", { name: "Desactivar" }).click();
  await expect(page.getByRole("status")).toContainText("Atributo desactivado");
  await expect(page.getByText("Desactivado", { exact: true })).toBeVisible();
});

test("registers an API with scopes and confirms before removing one", async ({ page }) => {
  let createPayload: Record<string, unknown> | null = null;
  let updatePayload: Record<string, unknown> | null = null;
  await mockShell(page);
  await page.route("**/api/applications?**", (route) => json(route, paged([application], 1, 100)));
  await page.route("**/api/api-resources?**", (route) => json(route, paged([ordersApi])));
  await page.route("**/api/api-resources", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await json(route, ordersApi, 201);
  });
  await page.route(`**/api/api-resources/${ordersApi.id}`, async (route) => {
    if (route.request().method() === "PUT") {
      updatePayload = route.request().postDataJSON() as Record<string, unknown>;
      await json(route, { ...ordersApi, scopes: ordersApi.scopes.slice(0, 1) });
      return;
    }
    await json(route, ordersApi);
  });

  await page.goto("/admin-v2/api-resources");
  await expect(page.getByRole("cell", { name: /Orders API https/ })).toBeVisible();
  await page.getByRole("link", { name: "Nuevo API" }).click();
  await page.getByLabel("Aplicación dueña").selectOption(applicationId);
  await page.getByLabel("Identificador (audiencia)").fill("http://api.tiendit.app/orders");
  await page.getByLabel("Nombre", { exact: true }).fill("Orders API");
  await page.getByLabel("Nombre del scope 1").fill("orders.read");
  await page.getByLabel("Nombre visible del scope 1").fill("Leer pedidos");
  await page.getByRole("button", { name: "Crear API" }).click();
  await expect(page.getByText(/Usa una URI absoluta https: o urn:/)).toBeVisible();

  await page.getByLabel("Identificador (audiencia)").fill(ordersApi.identifier);
  await page.getByRole("button", { name: "Agregar scope" }).click();
  await page.getByLabel("Nombre del scope 2").fill("orders.write");
  await page.getByLabel("Nombre visible del scope 2").fill("Modificar pedidos");
  await page.getByRole("button", { name: "Crear API" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/api-resources/${ordersApi.id}$`));
  expect(createPayload).toEqual({
    applicationSystemId: applicationId, identifier: ordersApi.identifier, displayName: "Orders API", description: null,
    scopes: [{ name: "orders.read", displayName: "Leer pedidos", description: null }, { name: "orders.write", displayName: "Modificar pedidos", description: null }]
  });

  await page.getByRole("button", { name: "Quitar el scope 2" }).click();
  await page.getByRole("button", { name: "Guardar cambios" }).click();
  const confirm = page.getByRole("dialog", { name: "Quitar scopes" });
  await expect(confirm).toContainText("orders.write");
  await confirm.getByRole("button", { name: "Guardar y quitar" }).click();
  await expect(page.getByRole("status")).toContainText("API guardado.");
  expect(updatePayload).toMatchObject({ scopes: [{ name: "orders.read" }] });
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("picks API scopes from the catalog and keeps the client's back-channel session setting", async ({ page }) => {
  let updatePayload: Record<string, unknown> | null = null;
  await mockShell(page);
  await mockStepUp(page);
  await page.route("**/api/api-resources?**", (route) => json(route, paged([ordersApi], 1, 100)));
  await page.route(`**/api/oauth/clients/${oauthClient.clientId}`, async (route) => {
    if (route.request().method() === "PUT") {
      updatePayload = route.request().postDataJSON() as Record<string, unknown>;
    }
    await json(route, oauthClient);
  });

  await page.goto(`/admin-v2/oauth-clients/${oauthClient.clientId}`);
  await expect(page.getByRole("checkbox", { name: /orders\.read/ })).toBeChecked();
  await expect(page.getByText(/scopes que ya no existen en el catálogo: legacy.scope/)).toBeVisible();
  await page.getByRole("button", { name: "Quitar scopes retirados" }).click();
  await page.getByRole("checkbox", { name: /orders\.write/ }).check();
  await expect(page.getByRole("checkbox", { name: /backchannel_logout_session_required/ })).not.toBeChecked();
  await page.getByRole("button", { name: "Guardar configuración" }).click();
  await expect.poll(() => updatePayload).not.toBeNull();
  expect(updatePayload).toMatchObject({ allowedScopes: ["openid", "profile", "orders.read", "orders.write"], backchannelLogoutSessionRequired: false });
});
