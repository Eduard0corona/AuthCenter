import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

const applicationId = "11111111-1111-4111-8111-111111111111";
const roleId = "22222222-2222-4222-8222-222222222222";
const permissionReadId = "33333333-3333-4333-8333-333333333333";
const permissionWriteId = "44444444-4444-4444-8444-444444444444";
const groupId = "55555555-5555-4555-8555-555555555555";
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
const role = { id: roleId, name: "Operator", description: "Operación diaria", applicationSystemId: applicationId, isSystemRole: false, isActive: true, createdAt: "2026-08-11T00:00:00Z", permissions: ["TIENDIT_ORDERS_READ"] };
const applicationPermissions = [
  { id: permissionReadId, applicationSystemId: applicationId, code: "TIENDIT_ORDERS_READ", name: "Consultar pedidos", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z" },
  { id: permissionWriteId, applicationSystemId: applicationId, code: "TIENDIT_ORDERS_WRITE", name: "Modificar pedidos", description: null, isActive: true, createdAt: "2026-08-11T00:00:00Z" }
];
const group = {
  id: groupId,
  name: "TienditApp Operators",
  description: "Acceso operativo heredado",
  isActive: true,
  createdAt: "2026-08-11T00:00:00Z",
  updatedAt: null,
  memberCount: 1,
  applications: [{ id: applicationId, code: application.code, name: application.name }],
  roles: [{ id: roleId, name: role.name, applicationSystemId: applicationId, applicationCode: application.code }]
};
const groupMember = { userId: "user-1", fullName: "Grace Hopper", email: "grace@example.test", isActive: true, addedAt: "2026-08-11T00:00:00Z" };
const userDetail = {
  id: "user-1", fullName: "Grace Hopper", email: "grace@example.test", pictureUrl: null,
  isActive: true, isExternalUser: false, hasLocalPassword: true, mustChangePassword: false, mfaEnabled: true, createdAt: "2026-08-11T00:00:00Z", lastLoginAt: null,
  roles: ["Operator"], applications: ["TIENDITAPP"],
  applicationAccesses: [{ applicationId, applicationCode: application.code, applicationName: application.name, isActive: true, createdAt: "2026-08-11T00:00:00Z", revokedAt: null }],
  applicationAssignments: [{ applicationId, applicationCode: application.code, applicationName: application.name, isApplicationActive: true, isDirect: true, directAccessStatus: "Active", isEffective: true, inheritedFromGroups: [{ groupId, groupName: group.name, isActive: true }] }],
  roleAssignments: [{ roleId, roleName: role.name, applicationId, applicationCode: application.code, isRoleActive: true, isSystemRole: false, isDirect: false, isEffective: true, inheritedFromGroups: [{ groupId, groupName: group.name, isActive: true }] }],
  groupMemberships: [{ groupId, groupName: group.name, isGroupActive: true, addedAt: "2026-08-11T00:00:00Z" }]
};
const profileSchema = [{ id: "66666666-6666-4666-8666-666666666666", key: "department", displayName: "Departamento", description: "Área organizacional", dataType: "String", isRequired: true, isActive: true, defaultValue: "Operaciones", minLength: 2, maxLength: 80, minimumNumber: null, maximumNumber: null, validationPattern: null, allowedValues: ["Operaciones", "Ingeniería"], createdAt: "2026-08-11T00:00:00Z", updatedAt: null }];
const userProfile = { userId: "user-1", isValid: true, missingRequiredAttributes: [], attributes: [{ key: "department", value: "Operaciones", isDefault: false }] };
const oauthClient = {
  id: "77777777-7777-4777-8777-777777777777",
  applicationSystemId: applicationId,
  applicationCode: application.code,
  applicationName: application.name,
  clientId: "partner_portal",
  displayName: "Partner Portal",
  clientType: 0,
  redirectUris: ["https://partner.example.test/callback"],
  allowedScopes: ["openid", "profile", "email", "offline_access"],
  grantTypes: ["authorization_code", "refresh_token"],
  loginUrl: "https://partner.example.test/login",
  accessTokenLifetimeSeconds: 900,
  requirePkce: true,
  autoConsent: false,
  isActive: true,
  createdAt: "2026-08-13T00:00:00Z",
  updatedAt: null
};
const provisioningTokenId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
const rotatedProvisioningTokenId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
const provisioningToken = {
  id: provisioningTokenId,
  applicationSystemId: applicationId,
  applicationName: application.name,
  name: "Directory sync",
  scopes: ["scim.users.read", "scim.users.write"],
  status: "active",
  createdAt: "2026-08-13T00:00:00Z",
  expiresAt: "2026-11-13T00:00:00Z",
  lastUsedAt: null,
  revokedAt: null
};

const dashboardMetrics = {
  generatedAt: "2026-09-26T12:00:00Z",
  activeUsers: 1280,
  inactiveUsers: 42,
  activeApplications: 7,
  activeGroups: 18,
  pendingAccessRequests: 3,
  activeAccessReviews: 1,
  overdueAccessReviews: 0,
  pendingAccessReviewItems: 5,
  separationOfDutiesViolations: 0,
  activeFederationProviders: 2,
  expiringProvisioningTokens: 1,
  unverifiedEventHooks: 0,
  deadLetterDeliveries: 4,
  failedLoginsLast24Hours: 12,
  highRiskObservationsLast24Hours: 0
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
      permissions: ["AUTHCENTER_USERS_READ", "AUTHCENTER_USERS_WRITE", "AUTHCENTER_APPLICATIONS_READ", "AUTHCENTER_AUDIT_LOGS_READ", "AUTHCENTER_APPLICATIONS_WRITE", "AUTHCENTER_ROLES_READ", "AUTHCENTER_ROLES_WRITE", "AUTHCENTER_PERMISSIONS_READ", "AUTHCENTER_PERMISSIONS_WRITE", "AUTHCENTER_GROUPS_READ", "AUTHCENTER_GROUPS_WRITE", "AUTHCENTER_PROFILE_SCHEMAS_READ", "AUTHCENTER_OAUTH_CLIENTS_READ", "AUTHCENTER_OAUTH_CLIENTS_WRITE", "AUTHCENTER_ACCESS_POLICIES_READ", "AUTHCENTER_ACCESS_POLICIES_WRITE", "AUTHCENTER_FEDERATION_READ", "AUTHCENTER_FEDERATION_WRITE", "AUTHCENTER_PROVISIONING_READ", "AUTHCENTER_PROVISIONING_WRITE", "AUTHCENTER_EVENT_HOOKS_READ", "AUTHCENTER_EVENT_HOOKS_WRITE"]
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
  await page.route(`**/api/roles/${roleId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: role }) }));
  await page.route("**/api/roles?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: [role], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 } }) }));
  await page.route(`**/api/applications/${applicationId}/permissions?**`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: applicationPermissions, totalCount: 2, page: 1, pageSize: 100, totalPages: 1 } }) }));
  await page.route(`**/api/groups/${groupId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: group }) }));
  await page.route(`**/api/groups/${groupId}/members?**`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: [groupMember], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 } }) }));
  await page.route("**/api/groups?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: [group], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 } }) }));
  await page.route("**/api/users/user-1", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: userDetail }) }));
  await page.route("**/api/users/user-1/profile", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: userProfile }) }));
  await page.route("**/api/profile-schema", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: profileSchema }) }));
  await page.route("**/api/federation/service-provider", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { oidcCallbackUrl: "https://authcenter.example.test/api/federation/oidc/callback", samlEntityId: "https://authcenter.example.test/saml", samlAssertionConsumerServiceUrl: "https://authcenter.example.test/api/federation/saml/acs" } }) }));
  await page.route("**/api/oauth/clients?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: [oauthClient], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 } }) }));
  await page.route(`**/api/oauth/clients/${oauthClient.clientId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: oauthClient }) }));
  await page.route("**/api/admin-metadata", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { errorCodes: {}, stepUpPurposes: {}, operationPermissions: {}, maximumPageSize: 100, environmentName: "Staging" } }) }));
  await page.route("**/api/version", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { version: "1.4.0", commit: "0123456789abcdef", adminFrontendBasePath: "/admin-v2", contractVersion: 1 } }) }));
  await page.route("**/api/admin-dashboard", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: dashboardMetrics }) }));
  await page.route("**/api/api-resources?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: [], totalCount: 0, page: 1, pageSize: 100, totalPages: 0 } }) }));
});

test("shell and users route are keyboard-visible and axe-clean", async ({ page }) => {
  await page.goto("/admin-v2/");
  await expect(page.getByRole("heading", { name: "Hola, Ada" })).toBeVisible();
  await page.getByRole("link", { name: /Usuarios Directorio/ }).click();
  await expect(page.getByRole("heading", { name: "Usuarios", exact: true })).toBeVisible();
  await expect(page.getByText("Grace Hopper")).toBeVisible();

  const accessibility = await new AxeBuilder({ page }).analyze();
  expect(accessibility.violations).toEqual([]);
});

test("persists user ordering in the URL and server query", async ({ page }) => {
  let requestedUrl = "";
  await page.unroute("**/api/users?**");
  await page.route("**/api/users?**", async (route) => {
    requestedUrl = route.request().url();
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 } }) });
  });

  await page.goto("/admin-v2/users");
  // Wait for the initial query to settle so the sort change is the only in-flight navigation.
  await expect(page.getByText("No hay resultados")).toBeVisible();
  await expect.poll(() => requestedUrl).toContain("sortBy=fullName");
  await page.getByLabel("Orden").selectOption("createdAt-desc");
  await expect(page).toHaveURL(/sort=createdAt-desc/, { timeout: 15_000 });
  await expect.poll(() => requestedUrl).toContain("sortBy=createdAt");
  expect(requestedUrl).toContain("sortDirection=desc");
});

test("creates a local user with a generated temporary password", async ({ page }) => {
  let payload: Record<string, unknown> | null = null;
  await page.route("**/api/users", async (route) => {
    payload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...userDetail, ...payload, id: "created-user-1", roles: ["Operator"], applications: [application.code] } }) });
  });

  await page.goto("/admin-v2/users/new");
  await page.getByLabel("Nombre completo").fill("Katherine Johnson");
  await page.getByLabel("Correo").fill("KATHERINE@example.test");
  await page.getByRole("button", { name: "Generar contraseña segura" }).click();
  await expect(page.getByLabel("Contraseña temporal")).toHaveAttribute("type", "password");
  await page.getByRole("button", { name: "Mostrar contraseña" }).click();
  await expect(page.getByLabel("Contraseña temporal")).toHaveAttribute("type", "text");
  await page.getByRole("button", { name: "Ocultar contraseña" }).click();
  await page.getByRole("checkbox", { name: "Conceder acceso a una aplicación ahora" }).check();
  await page.getByRole("combobox", { name: /Aplicación/ }).selectOption(applicationId);
  await page.getByRole("group", { name: /Roles directos opcionales/ }).getByRole("checkbox", { name: /Operator/ }).check();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  await page.getByRole("button", { name: "Crear usuario" }).click();

  await expect(page.getByRole("status")).toContainText("reemplazar la contraseña temporal");
  expect(payload).toMatchObject({
    fullName: "Katherine Johnson",
    email: "katherine@example.test",
    isTemporaryPassword: true,
    grantApplicationAccess: true,
    applicationSystemId: applicationId,
    roleIds: [roleId]
  });
  expect(String((payload as unknown as Record<string, unknown>)["password"])).toMatch(/^(?=.*[A-Z])(?=.*[a-z])(?=.*\d).{12,}$/);
});

test("invites a user without exposing an invitation token", async ({ page }) => {
  let payload: Record<string, unknown> | null = null;
  await page.route("**/api/users/invitations", async (route) => {
    payload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...userDetail, ...payload, id: "invited-user-1", hasLocalPassword: false, applicationAccesses: [] } }) });
  });

  await page.goto("/admin-v2/users/invite");
  await page.getByLabel("Nombre completo").fill("Dorothy Vaughan");
  await page.getByLabel("Correo").fill("dorothy@example.test");
  await page.getByRole("combobox", { name: /Aplicación/ }).selectOption(applicationId);
  await page.getByRole("checkbox", { name: "Conceder acceso activo inmediatamente" }).uncheck();
  await page.getByRole("button", { name: "Enviar invitación" }).click();

  await expect(page.getByRole("status")).toContainText("sin exponer su token");
  expect(payload).toEqual({
    fullName: "Dorothy Vaughan",
    email: "dorothy@example.test",
    applicationSystemId: applicationId,
    roleIds: [],
    grantActiveAccess: false
  });
  expect(JSON.stringify(payload)).not.toMatch(/password|token|secret/i);
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

  await page.getByRole("button", { name: "Editar marca" }).click();
  await expect(page.getByLabel("Privacidad HTTPS")).toHaveValue("https://tiendit.app/privacidad");
  await expect(page.getByLabel("Términos HTTPS")).toHaveValue("https://tiendit.app/terminos");
  await page.getByLabel("Nombre visible").fill("TienditApp Pro");

  const accessibility = await new AxeBuilder({ page }).include("dialog").analyze();
  expect(accessibility.violations).toEqual([]);
  await page.getByRole("button", { name: "Guardar marca" }).click();
  await expect(page.getByRole("status")).toContainText("marca");
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
  await expect(page.getByRole("button", { name: "Editar marca" })).toHaveCount(0);
  await expect(page.getByText("AUTHCENTER_APPLICATIONS_WRITE")).toBeVisible();
});

test("a single sign-on session opened for another application asks for an AuthCenter sign-in", async ({ page }) => {
  await page.unroute("**/ui-api/session");
  await page.route("**/ui-api/session", async (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({ success: true, data: { user: {
      id: "shopper-1",
      name: "Cliente",
      email: "cliente@example.test",
      applications: ["TIENDITAPP"],
      roles: ["Operator"],
      permissions: ["AUTHCENTER_USERS_READ"]
    }, csrfToken: "other-app-csrf" } })
  }));
  await page.route("**/login?**", async (route) => route.fulfill({ status: 200, contentType: "text/html", body: "<!doctype html><title>Login</title><h1>Hosted login</h1>" }));

  await page.goto("/admin-v2/users");

  await expect(page).toHaveURL(/\/login\?application=AUTHCENTER&return_url=%2Fadmin-v2%2Fusers/);
});

test("updates a role permission matrix in one atomic request", async ({ page }) => {
  let matrixPayload: { permissionIds: string[] } | null = null;
  await page.route(`**/api/roles/${roleId}/permissions`, async (route) => {
    matrixPayload = route.request().postDataJSON() as { permissionIds: string[] };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...role, permissions: ["TIENDIT_ORDERS_READ", "TIENDIT_ORDERS_WRITE"] } }) });
  });

  await page.goto(`/admin-v2/roles/${roleId}`);
  await expect(page.getByRole("heading", { level: 1, name: "Operator" })).toBeFocused();
  await page.getByText("TIENDIT_ORDERS_WRITE").click();
  await page.getByRole("button", { name: "Guardar matriz" }).click();
  await expect(page.getByRole("status")).toContainText("atómica");
  expect(matrixPayload).not.toBeNull();
  expect((matrixPayload as unknown as { permissionIds: string[] }).permissionIds.sort()).toEqual([permissionReadId, permissionWriteId].sort());
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("assigns only an application-scoped active default role", async ({ page }) => {
  let updatePayload: Record<string, unknown> | null = null;
  await page.route(`**/api/applications/${applicationId}`, async (route) => {
    if (route.request().method() === "PUT") {
      updatePayload = route.request().postDataJSON() as Record<string, unknown>;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...application, registrationSettings: { ...application.registrationSettings, defaultRoleId: roleId } } }) });
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: application }) });
  });

  await page.goto(`/admin-v2/applications/${applicationId}`);
  await page.getByLabel("Rol predeterminado").selectOption(roleId);
  await page.getByRole("button", { name: "Guardar configuración" }).click();
  await expect(page.getByRole("status")).toContainText("actualizada");
  expect(updatePayload).toMatchObject({ defaultRoleId: roleId });
});

test("replaces inherited group access atomically and previews its impact", async ({ page }) => {
  let accessPayload: { applicationSystemIds: string[]; roleIds: string[] } | null = null;
  await page.route(`**/api/groups/${groupId}/access`, async (route) => {
    accessPayload = route.request().postDataJSON() as { applicationSystemIds: string[]; roleIds: string[] };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...group, roles: [] } }) });
  });

  await page.goto(`/admin-v2/groups/${groupId}`);
  await expect(page.getByRole("heading", { level: 1, name: group.name })).toBeFocused();
  await expect(page.getByText(/1 miembros recibir.n 1 aplicaciones y 1 roles/i)).toBeVisible();
  await page.getByRole("checkbox", { name: /Operator/ }).uncheck();
  await page.getByRole("button", { name: "Guardar acceso heredado" }).click();
  await expect(page.getByRole("status")).toContainText(/at.mica/);
  expect(accessPayload).toEqual({ applicationSystemIds: [applicationId], roleIds: [] });
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("removes a group member with an explicit session-revocation warning", async ({ page }) => {
  let removedUserId: string | null = null;
  await page.route(`**/api/groups/${groupId}/members/user-1`, async (route) => {
    removedUserId = route.request().url().split("/").at(-1) ?? null;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
  });

  await page.goto(`/admin-v2/groups/${groupId}`);
  await expect(page.getByText("Grace Hopper")).toBeVisible();
  await page.getByRole("button", { name: "Retirar" }).click();
  await expect(page.getByRole("dialog")).toContainText(/sesiones ser.n revocadas/);
  await page.getByRole("button", { name: "Retirar miembro" }).click();
  await expect(page.getByRole("status")).toContainText("sesiones anteriores fueron revocadas");
  expect(removedUserId).toBe("user-1");
});

test("leaves the members of a rule-managed group to its rules", async ({ page }) => {
  await page.unroute(`**/api/groups/${groupId}`);
  await page.route(`**/api/groups/${groupId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...group, isRuleManaged: true } }) }));

  await page.goto(`/admin-v2/groups/${groupId}`);
  await expect(page.getByText("Grace Hopper")).toBeVisible();
  await expect(page.getByText(/Las reglas del grupo deciden sus miembros/)).toBeVisible();
  await expect(page.getByRole("link", { name: "Ver las reglas del grupo" })).toHaveAttribute("href", `/admin-v2/group-rules?groupId=${groupId}`);
  await expect(page.getByLabel("Agregar usuario")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Retirar" })).toHaveCount(0);
});

test("shows direct and inherited user access with universal profile", async ({ page }) => {
  await page.goto("/admin-v2/users/user-1");
  await expect(page.getByRole("heading", { level: 1, name: "Grace Hopper" })).toBeFocused();
  await expect(page.getByText("Directo", { exact: true })).toBeVisible();
  await expect(page.getByText(`Heredado: ${group.name}`, { exact: true }).first()).toBeVisible();
  await expect(page.getByLabel("Departamento *")).toHaveValue("Operaciones");
  await expect(page.getByRole("link", { name: "Ver grupo" })).toHaveAttribute("href", `/admin-v2/groups/${groupId}`);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("replaces direct user access atomically", async ({ page }) => {
  let accessPayload: { applicationSystemIds: string[]; roleIds: string[] } | null = null;
  await page.route("**/api/users/user-1/access", async (route) => {
    accessPayload = route.request().postDataJSON() as { applicationSystemIds: string[]; roleIds: string[] };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: userDetail }) });
  });
  await page.goto("/admin-v2/users/user-1");
  await page.getByRole("group", { name: "Roles" }).getByRole("checkbox", { name: /^Operator/ }).check();
  await page.getByRole("button", { name: "Guardar acceso directo" }).click();
  await expect(page.getByRole("status")).toContainText("sesiones anteriores fueron revocadas");
  expect(accessPayload).toEqual({ applicationSystemIds: [applicationId], roleIds: [roleId] });
});

test("deactivates a user after confirming", async ({ page }) => {
  let deactivations = 0;
  await page.route("**/api/users/user-1/deactivate", async (route) => {
    deactivations += 1;
    expect(route.request().method()).toBe("PATCH");
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
  });
  await page.goto("/admin-v2/users/user-1");
  await page.getByRole("button", { name: "Desactivar usuario" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText("las sesiones anteriores serán revocadas");
  await dialog.getByRole("button", { name: "Desactivar", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole("status")).toContainText("El estado quedó actualizado y las sesiones anteriores fueron revocadas.");
  expect(deactivations).toBe(1);
});

test("explains inside the confirmation that the last SuperAdmin cannot be deactivated", async ({ page }) => {
  await page.route("**/api/users/user-1/deactivate", async (route) => route.fulfill({
    status: 400,
    contentType: "application/json",
    body: JSON.stringify({ success: false, errorCode: "LAST_SUPER_ADMIN", message: "The last effective SuperAdmin cannot be deactivated.", traceId: "trace-last-admin" })
  }));
  await page.goto("/admin-v2/users/user-1");
  await page.getByRole("button", { name: "Desactivar usuario" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("button", { name: "Desactivar", exact: true }).click();
  // The dialog covers the page: the refusal is shown where the operator is looking.
  await expect(dialog.getByRole("alert")).toHaveText(/sin ningún SuperAdmin activo/);
  expect((await new AxeBuilder({ page }).include("dialog").analyze()).violations).toEqual([]);
  await dialog.getByRole("button", { name: "Cancelar" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "sin ningún SuperAdmin activo" })).toBeVisible();
  // Opening it again starts without the previous error.
  await page.getByRole("button", { name: "Desactivar usuario" }).click();
  await expect(dialog.getByRole("alert")).toHaveCount(0);
});

test("a session that expires during a change returns to the sign-in and back to the same page", async ({ page }) => {
  await page.route("**/api/users/user-1/access", async (route) => route.fulfill({
    status: 401,
    contentType: "application/json",
    body: JSON.stringify({ success: false, errorCode: "UNAUTHORIZED", message: "Authentication required." })
  }));
  await page.route("**/login?**", async (route) => route.fulfill({ status: 200, contentType: "text/html", body: "<!doctype html><title>Login</title><h1>Hosted login</h1>" }));
  await page.goto("/admin-v2/users/user-1");
  await page.getByRole("group", { name: "Roles" }).getByRole("checkbox", { name: /^Operator/ }).check();
  await page.getByRole("button", { name: "Guardar acceso directo" }).click();

  await expect(page).toHaveURL(/\/login\?application=AUTHCENTER&return_url=%2Fadmin-v2%2Fusers%2Fuser-1$/);
});

test("requires step-up before resetting another user's MFA", async ({ page }) => {
  let proofPurpose = "";
  let proofHeader = "";
  await page.route("**/api/auth/reauth/password", async (route) => {
    proofPurpose = (route.request().postDataJSON() as { purpose: string }).purpose;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { proofToken: "single-use-proof", assuranceLevel: "Password", expiresIn: 300 } }) });
  });
  await page.route("**/api/users/user-1/mfa", async (route) => {
    proofHeader = route.request().headers()["x-authcenter-reauthentication"] ?? "";
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
  });
  await page.goto("/admin-v2/users/user-1");
  const resetButton = page.getByRole("button", { name: "Restablecer MFA" });
  await resetButton.focus();
  await resetButton.press("Enter");
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText("tu propia identidad administrativa");
  await dialog.getByLabel("Tu contraseña actual").fill("AdminSecret123");
  await dialog.getByRole("button", { name: "Verificar y restablecer" }).click();
  await expect(page.getByRole("status")).toContainText("después de verificar tu identidad");
  expect(proofPurpose).toBe("admin.mfa.reset");
  expect(proofHeader).toBe("single-use-proof");
});

test("creates and rotates a confidential OAuth client with one-time secret reveal", async ({ page }) => {
  const createdCredential = ["created", "client", "credential"].join("-");
  const rotatedCredential = ["rotated", "client", "credential"].join("-");
  let createPayload: Record<string, unknown> | null = null;
  let proofPurpose = "";
  let proofHeader = "";
  await page.route("**/api/oauth/clients", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify({ success: true, data: { client: oauthClient, clientSecret: createdCredential } }) });
  });
  await page.route("**/api/auth/reauth/password", async (route) => {
    proofPurpose = (route.request().postDataJSON() as { purpose: string }).purpose;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { proofToken: "single-use-proof", assuranceLevel: "Password", expiresIn: 300 } }) });
  });
  await page.route(`**/api/oauth/clients/${oauthClient.clientId}/rotate-secret`, async (route) => {
    proofHeader = route.request().headers()["x-authcenter-reauthentication"] ?? "";
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { clientSecret: rotatedCredential } }) });
  });

  await page.goto("/admin-v2/oauth-clients/new");
  await page.getByLabel("Aplicación").selectOption(applicationId);
  await page.getByLabel("Nombre", { exact: true }).fill(oauthClient.displayName);
  await page.getByLabel("Client ID").fill(oauthClient.clientId);
  await page.getByLabel("URLs de regreso").fill(oauthClient.redirectUris[0]);
  await page.getByLabel("URL de inicio de sesión").fill(oauthClient.loginUrl);
  await page.getByLabel("URLs después de cerrar sesión").fill("https://partner.example.test/signout-callback-authcenter");
  await page.getByLabel("URL de aviso de cierre de sesión").fill("https://partner.example.test/auth/backchannel-logout");
  await page.getByRole("button", { name: "Crear cliente OAuth" }).click();

  const createdDialog = page.getByRole("dialog");
  await expect(createdDialog).toContainText(createdCredential);
  await expect(createdDialog).toContainText(/no podr. volver a mostrar/i);
  expect(createPayload).toMatchObject({
    clientId: oauthClient.clientId,
    clientType: 0,
    redirectUris: oauthClient.redirectUris,
    postLogoutRedirectUris: ["https://partner.example.test/signout-callback-authcenter"],
    backchannelLogoutUri: "https://partner.example.test/auth/backchannel-logout"
  });
  await createdDialog.getByRole("button", { name: "Ya guardé el secreto" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/oauth-clients/${oauthClient.clientId}$`));

  await page.getByRole("button", { name: "Rotar secreto" }).click();
  const stepUpDialog = page.getByRole("dialog");
  await stepUpDialog.getByLabel(/Tu contrase.*a actual/).fill("AdminSecret123");
  await stepUpDialog.getByRole("button", { name: "Verificar y rotar" }).click();
  await expect(page.getByRole("dialog")).toContainText(rotatedCredential);
  expect(proofPurpose).toBe("admin.oauth-client.rotate-secret");
  expect(proofHeader).toBe("single-use-proof");

  const accessibility = await new AxeBuilder({ page }).analyze();
  expect(accessibility.violations).toEqual([]);
});

test("creates, rotates and revokes a scoped provisioning token with step-up", async ({ page }) => {
  const createdCredential = ["acp", "created", "credential"].join("_");
  const rotatedCredential = ["acp", "rotated", "credential"].join("_");
  let createPayload: Record<string, unknown> | null = null;
  const proofPurposes: string[] = [];
  const proofHeaders: string[] = [];
  let replacementRevoked = false;

  await page.route("**/api/provisioning-tokens/*/diagnostics", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { tokenId: provisioningTokenId, lastUsedAt: null, lastSucceededAt: null, lastFailedAt: null, last24Hours: { total: 0, failed: 0 }, last7Days: { total: 0, failed: 0 }, failures: [] } }) }));
  await page.route("**/api/provisioning-tokens/*/requests?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 } }) }));
  await page.route("**/api/provisioning-tokens", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify({ success: true, data: { id: provisioningTokenId, token: createdCredential, scopes: provisioningToken.scopes, expiresAt: provisioningToken.expiresAt } }) });
  });
  await page.route(`**/api/provisioning-tokens/${provisioningTokenId}/rotate?**`, async (route) => {
    proofHeaders.push(route.request().headers()["x-authcenter-reauthentication"] ?? "");
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { id: rotatedProvisioningTokenId, token: rotatedCredential, scopes: provisioningToken.scopes, expiresAt: provisioningToken.expiresAt } }) });
  });
  await page.route(`**/api/provisioning-tokens/${provisioningTokenId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: provisioningToken }) }));
  await page.route(`**/api/provisioning-tokens/${rotatedProvisioningTokenId}`, async (route) => {
    if (route.request().method() === "DELETE") {
      proofHeaders.push(route.request().headers()["x-authcenter-reauthentication"] ?? "");
      replacementRevoked = true;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...provisioningToken, id: rotatedProvisioningTokenId, name: `${provisioningToken.name} (rotado)`, status: replacementRevoked ? "revoked" : "active", revokedAt: replacementRevoked ? "2026-08-13T02:00:00Z" : null } }) });
  });
  await page.route("**/api/auth/reauth/password", async (route) => {
    proofPurposes.push((route.request().postDataJSON() as { purpose: string }).purpose);
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { proofToken: `proof-${proofPurposes.length}`, assuranceLevel: "Password", expiresIn: 300 } }) });
  });

  await page.goto("/admin-v2/provisioning-tokens/new");
  await page.getByLabel("Aplicación").selectOption(applicationId);
  await page.getByLabel("Nombre").fill(provisioningToken.name);
  await page.getByRole("checkbox", { name: "scim.users.write" }).check();
  await page.getByRole("button", { name: "Crear token de aprovisionamiento" }).click();

  const createdDialog = page.getByRole("dialog");
  await expect(createdDialog).toContainText(createdCredential);
  await expect(createdDialog).toContainText(/no podr. volver a mostrar/i);
  expect(createPayload).toMatchObject({ applicationSystemId: applicationId, name: provisioningToken.name, scopes: provisioningToken.scopes });
  await createdDialog.getByRole("button", { name: "Ya guardé el secreto" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/provisioning-tokens/${provisioningTokenId}$`));
  await expect(page.getByText("AuthCenter almacena únicamente el hash")).toBeVisible();

  const rotateButton = page.getByRole("button", { name: "Rotar token" });
  await rotateButton.focus();
  await rotateButton.press("Enter");
  const rotateDialog = page.getByRole("dialog");
  await rotateDialog.getByLabel(/Tu contraseña actual/).fill("AdminSecret123");
  await rotateDialog.getByRole("button", { name: "Verificar y rotar" }).click();
  await expect(page.getByRole("dialog")).toContainText(rotatedCredential);
  await page.getByRole("dialog").getByRole("button", { name: "Ya guardé el secreto" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/provisioning-tokens/${rotatedProvisioningTokenId}$`));
  // Act only once the replacement credential is on screen, not the page of the rotated one.
  await expect(page.getByRole("heading", { level: 1, name: `${provisioningToken.name} (rotado)` })).toBeVisible();

  const revokeButton = page.getByRole("button", { name: "Revocar token" });
  await revokeButton.focus();
  await revokeButton.press("Enter");
  const revokeDialog = page.getByRole("dialog");
  await revokeDialog.getByLabel(/Tu contraseña actual/).fill("AdminSecret123");
  await revokeDialog.getByRole("button", { name: "Verificar y revocar" }).click();
  await expect(page.getByRole("status")).toContainText("quedó revocado");
  await expect(page.getByText("Revocado", { exact: true })).toBeVisible();

  expect(proofPurposes).toEqual(["admin.provisioning-token.rotate", "admin.provisioning-token.revoke"]);
  expect(proofHeaders).toEqual(["proof-1", "proof-2"]);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("shows what the SCIM client did with a token", async ({ page }) => {
  const diagnostics = {
    tokenId: provisioningTokenId, lastUsedAt: "2026-09-26T10:00:00Z", lastSucceededAt: "2026-09-26T09:58:00Z", lastFailedAt: "2026-09-26T10:00:00Z",
    last24Hours: { total: 3, failed: 2 }, last7Days: { total: 12, failed: 2 },
    failures: [
      { statusCode: 403, scimType: null, count: 1, lastAt: "2026-09-26T10:00:00Z", lastDetail: "The provisioning token does not have the scim.groups.read scope." },
      { statusCode: 400, scimType: "invalidValue", count: 1, lastAt: "2026-09-26T09:59:00Z", lastDetail: "userName must be a bounded email address." }
    ]
  };
  const entry = (id: string, method: string, path: string, statusCode: number, scimType: string | null, detail: string | null) =>
    ({ id, createdAt: "2026-09-26T10:00:00Z", method, path, statusCode, scimType, detail, durationMs: 12, traceId: "4bf92f3577b34da6a3ce929d0e0e4736" });
  const failed = [entry("r2", "GET", "/scim/v2/Groups", 403, null, diagnostics.failures[0]!.lastDetail), entry("r3", "POST", "/scim/v2/Users", 400, "invalidValue", diagnostics.failures[1]!.lastDetail)];
  const requestedUrls: string[] = [];
  await page.route(`**/api/provisioning-tokens/${provisioningTokenId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: provisioningToken }) }));
  await page.route(`**/api/provisioning-tokens/${provisioningTokenId}/diagnostics`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: diagnostics }) }));
  await page.route(`**/api/provisioning-tokens/${provisioningTokenId}/requests?**`, async (route) => {
    requestedUrls.push(route.request().url());
    const items = route.request().url().includes("outcome=failed") ? failed : [entry("r1", "GET", "/scim/v2/Users", 200, null, null), ...failed];
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items, totalCount: items.length, page: 1, pageSize: 20, totalPages: 1 } }) });
  });

  await page.goto(`/admin-v2/provisioning-tokens/${provisioningTokenId}`);
  const origin = await page.evaluate(() => window.location.origin);
  await expect(page.getByText(`${origin}/scim/v2`)).toBeVisible();
  const panel = page.getByRole("region", { name: "Diagnóstico SCIM" });
  await expect(panel.getByText("3 solicitudes, 2 fallidas")).toBeVisible();
  const week = panel.getByRole("table", { name: "Errores de los últimos 7 días" });
  await expect(week.getByRole("row", { name: /403 Sin permiso/ })).toContainText("scim.groups.read");
  await expect(week.getByRole("row", { name: /400 Solicitud inválida \(invalidValue\)/ })).toBeVisible();
  const requests = panel.getByRole("table", { name: "Solicitudes SCIM" });
  await expect(requests.getByRole("row")).toHaveCount(4);
  await expect(requests.getByRole("row", { name: /200 Correcta/ })).toContainText("GET /scim/v2/Users");

  await panel.getByLabel("Resultado").selectOption("failed");
  await expect.poll(() => requestedUrls.at(-1)).toContain("outcome=failed");
  await expect(requests.getByRole("row")).toHaveCount(3);
  await expect(page.locator("main")).not.toContainText("acp_");
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("filters paginated provisioning token metadata without exposing credentials", async ({ page }) => {
  let requestedUrl = "";
  await page.route("**/api/provisioning-tokens?**", async (route) => {
    requestedUrl = route.request().url();
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: [provisioningToken], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 } }) });
  });

  await page.goto("/admin-v2/provisioning-tokens");
  await expect(page.getByRole("heading", { level: 1, name: "Tokens de aprovisionamiento", exact: true })).toBeFocused();
  await expect(page.getByText(provisioningToken.name)).toBeVisible();
  await expect(page.getByText("1–1 de 1")).toBeVisible();
  await expect(page.locator("main")).not.toContainText("acp_");
  await page.getByLabel("Estado").selectOption("active");
  await expect.poll(() => requestedUrl).toContain("status=active");
  await page.getByLabel("Aplicación").selectOption(applicationId);
  await expect.poll(() => requestedUrl).toContain(`applicationSystemId=${applicationId}`);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

test("creates, simulates and publishes an access policy draft with step-up", async ({ page }) => {
  const publishedVersionId = "88888888-8888-4888-8888-888888888888";
  const draftVersionId = "99999999-9999-4999-8999-999999999999";
  const ruleId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
  let draftCreated = false;
  let published = false;
  let rulePayload: Record<string, unknown> | null = null;
  let proofPurpose = "";
  let proofHeader = "";
  const baselineRule = {
    id: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", applicationSystemId: applicationId, policyVersionId: publishedVersionId,
    policyVersionNumber: 1, policyVersionStatus: "Published", applicationCode: application.code, userId: null, userEmail: null,
    directoryGroupId: null, directoryGroupName: null, name: "Allow with MFA", priority: 100, action: "Allow", mfaRequirement: "Required",
    allowTrustedDeviceBypass: false, includedIpCidrs: [], excludedIpCidrs: [], activeFromUtc: null, activeUntilUtc: null,
    activeDaysUtc: [], dailyStartTimeUtc: null, dailyEndTimeUtc: null, minimumRiskLevel: null, maximumRiskLevel: null,
    requiredAssuranceLevel: "Mfa", isActive: true, createdAt: "2026-08-13T00:00:00Z", updatedAt: null
  };
  let draftRules = [{ ...baselineRule, id: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", policyVersionId: draftVersionId, policyVersionNumber: 2, policyVersionStatus: "Draft" }];

  await page.route(`**/api/access-policies/applications/${applicationId}/versions`, async (route) => {
    const versions = draftCreated
      ? [{ id: draftVersionId, applicationSystemId: applicationId, versionNumber: 2, status: published ? "Published" : "Draft", ruleCount: draftRules.length, createdAt: "2026-08-13T01:00:00Z", publishedAt: published ? "2026-08-13T02:00:00Z" : null }, { id: publishedVersionId, applicationSystemId: applicationId, versionNumber: 1, status: published ? "Archived" : "Published", ruleCount: 1, createdAt: "2026-08-12T00:00:00Z", publishedAt: "2026-08-12T01:00:00Z" }]
      : [{ id: publishedVersionId, applicationSystemId: applicationId, versionNumber: 1, status: "Published", ruleCount: 1, createdAt: "2026-08-12T00:00:00Z", publishedAt: "2026-08-12T01:00:00Z" }];
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: versions }) });
  });
  await page.route(`**/api/access-policies/applications/${applicationId}/drafts`, async (route) => {
    draftCreated = true;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { id: draftVersionId, applicationSystemId: applicationId, versionNumber: 2, status: "Draft", ruleCount: 1, createdAt: "2026-08-13T01:00:00Z", publishedAt: null } }) });
  });
  await page.route(`**/api/access-policies/applications/${applicationId}?**`, async (route) => {
    const versionId = new URL(route.request().url()).searchParams.get("policyVersionId");
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: versionId === draftVersionId ? draftRules : [baselineRule] }) });
  });
  await page.route("**/api/access-policies", async (route) => {
    rulePayload = route.request().postDataJSON() as Record<string, unknown>;
    const created = { ...baselineRule, ...rulePayload, id: ruleId, policyVersionId: draftVersionId, policyVersionNumber: 2, policyVersionStatus: "Draft", userEmail: null, directoryGroupName: null, createdAt: "2026-08-13T01:30:00Z", updatedAt: null };
    draftRules = [...draftRules, created];
    await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify({ success: true, data: created }) });
  });
  await page.route("**/api/access-policies/simulate", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { isAllowed: false, requireMfa: false, allowTrustedDeviceBypass: false, requiredAssuranceLevel: "Password", matchedRuleId: ruleId, matchedRuleName: "Block high risk", decisionReason: "The first matching rule denies access.", policyVersionId: draftVersionId, policyVersionNumber: 2, policyVersionStatus: "Draft", ruleEvaluations: [{ ruleId, ruleName: "Block high risk", priority: 10, matched: true, reasons: ["All configured conditions matched."] }, { ruleId: baselineRule.id, ruleName: baselineRule.name, priority: 100, matched: true, reasons: ["All configured conditions matched."] }] } }) }));
  await page.route("**/api/auth/reauth/password", async (route) => {
    proofPurpose = (route.request().postDataJSON() as { purpose: string }).purpose;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { proofToken: "policy-proof", assuranceLevel: "Password", expiresIn: 300 } }) });
  });
  await page.route(`**/api/access-policies/applications/${applicationId}/versions/${draftVersionId}/publish`, async (route) => {
    proofHeader = route.request().headers()["x-authcenter-reauthentication"] ?? "";
    published = true;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { id: draftVersionId, applicationSystemId: applicationId, versionNumber: 2, status: "Published", ruleCount: draftRules.length, createdAt: "2026-08-13T01:00:00Z", publishedAt: "2026-08-13T02:00:00Z" } }) });
  });

  await page.goto(`/admin-v2/access-policies/${applicationId}`);
  await page.getByRole("button", { name: "Crear borrador" }).click();
  await expect(page.getByRole("button", { name: /v2 Borrador/ })).toBeVisible();
  await page.getByRole("button", { name: "Nueva regla" }).click();
  await page.getByLabel("Nombre", { exact: true }).fill("Block high risk");
  await page.getByLabel("Prioridad").fill("10");
  await page.getByLabel("Acción").selectOption("Deny");
  await page.getByLabel("Riesgo mínimo").selectOption("High");
  await page.getByRole("button", { name: "Crear regla" }).click();
  await expect(page.getByText("Agregada")).toBeVisible();
  expect(rulePayload).toMatchObject({ applicationSystemId: applicationId, policyVersionId: draftVersionId, name: "Block high risk", priority: 10, action: "Deny", minimumRiskLevel: "High" });

  await page.getByRole("combobox", { name: "Usuario", exact: true }).fill("gra");
  await page.getByRole("option", { name: /Grace Hopper/ }).click();
  await page.getByRole("button", { name: "Simular decisión" }).click();
  const decision = page.locator(".decision-panel");
  await expect(decision).toContainText("Acceso denegado");
  await expect(decision).toContainText("Block high risk");

  const publishButton = page.getByRole("button", { name: "Revisar y publicar v2" });
  await publishButton.focus();
  await publishButton.press("Enter");
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel(/Tu contrase.*a actual/).fill("AdminSecret123");
  await dialog.getByRole("button", { name: "Verificar y publicar" }).click();
  await expect(page.getByText(/sesiones de la aplicaci.n fueron revocadas/)).toBeVisible();
  expect(proofPurpose).toBe("admin.access-policy.publish");
  expect(proofHeader).toBe("policy-proof");
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
});

const profileMappingId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
const profileMapping = {
  id: profileMappingId, applicationSystemId: applicationId, applicationName: application.name, sourceSystem: "SCIM",
  sourcePath: "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User.department", targetAttributeDefinitionId: profileSchema[0].id,
  targetAttributeName: "department", isAuthoritative: true, isActive: true, createdAt: "2026-08-13T00:00:00Z", version: 1
};

test("creates, validates and simulates a SCIM profile mapping", async ({ page }) => {
  let validatePayload: Record<string, unknown> | null = null;
  let createPayload: Record<string, unknown> | null = null;
  let simulatePayload: Record<string, unknown> | null = null;
  await page.route("**/api/lifecycle/profile-mappings/validate", async (route) => {
    validatePayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
  });
  await page.route("**/api/lifecycle/profile-mappings", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: profileMapping }) });
  });
  await page.route(`**/api/lifecycle/profile-mappings/${profileMappingId}/simulate`, async (route) => {
    simulatePayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { isValid: true, sourcePath: profileMapping.sourcePath, targetAttributeName: "department", value: "Ingeniería", errors: [] } }) });
  });
  await page.route(`**/api/lifecycle/profile-mappings/${profileMappingId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: profileMapping }) }));

  await page.goto("/admin-v2/profile-mappings/new");
  await page.getByLabel("Aplicación").selectOption(applicationId);
  await page.getByLabel("Ruta de origen SCIM").fill(` ${profileMapping.sourcePath} `);
  await page.getByLabel("Atributo destino").selectOption(profileSchema[0].id);
  await page.getByLabel(/Autoritativo/).check();
  await page.getByRole("button", { name: "Validar" }).click();
  await expect(page.getByRole("status").filter({ hasText: "son válidos" })).toBeVisible();
  expect(validatePayload).toEqual({ applicationSystemId: applicationId, sourceSystem: "SCIM", sourcePath: profileMapping.sourcePath, targetAttributeDefinitionId: profileSchema[0].id, isAuthoritative: true });

  await page.getByRole("button", { name: "Crear mapeo" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/profile-mappings/${profileMappingId}$`));
  expect(createPayload).toEqual(validatePayload);
  await expect(page.getByRole("heading", { name: "Simulación" })).toBeVisible();
  await page.getByRole("button", { name: "Simular transformación" }).click();
  await expect(page.getByRole("status").filter({ hasText: "department" })).toContainText("Ingeniería");
  expect(simulatePayload).toMatchObject({ sourceDocument: { name: { givenName: "Grace" } } });

  const accessibility = await new AxeBuilder({ page }).analyze();
  expect(accessibility.violations).toEqual([]);
});

test("blocks a stale profile mapping update and offers to reload", async ({ page }) => {
  let updatePayload: Record<string, unknown> | null = null;
  await page.route(`**/api/lifecycle/profile-mappings/${profileMappingId}`, async (route) => {
    if (route.request().method() === "PUT") {
      updatePayload = route.request().postDataJSON() as Record<string, unknown>;
      await route.fulfill({ status: 409, contentType: "application/json", body: JSON.stringify({ success: false, errorCode: "CONCURRENCY_CONFLICT", message: "The mapping changed after it was loaded." }) });
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: profileMapping }) });
  });

  await page.goto(`/admin-v2/profile-mappings/${profileMappingId}`);
  await expect(page.getByLabel("Ruta de origen SCIM")).toHaveValue(profileMapping.sourcePath);
  await page.getByLabel("Mapeo activo").uncheck();
  await page.getByRole("button", { name: "Guardar cambios" }).click();
  await expect(page.getByRole("alert")).toContainText("cambió desde que lo cargaste");
  await expect(page.getByRole("button", { name: "Recargar" })).toBeVisible();
  expect(updatePayload).toEqual({ sourceSystem: "SCIM", sourcePath: profileMapping.sourcePath, targetAttributeDefinitionId: profileSchema[0].id, isAuthoritative: true, isActive: false, version: 1 });
});

test("creates a typed group rule and previews the affected members", async ({ page }) => {
  const groupRuleId = "ffffffff-ffff-4fff-8fff-ffffffffffff";
  const levelDefinition = { ...profileSchema[0], id: "12121212-1212-4121-8121-121212121212", key: "level", displayName: "Nivel", dataType: "Integer", allowedValues: [], defaultValue: null };
  const groupRule = { id: groupRuleId, directoryGroupId: groupId, groupName: group.name, profileAttributeDefinitionId: levelDefinition.id, attributeName: "level", operator: "eq", expectedValue: 3, isActive: true, createdAt: "2026-08-13T00:00:00Z", version: 1 };
  let createPayload: Record<string, unknown> | null = null;
  let previewPayload: Record<string, unknown> | null = null;
  await page.unroute("**/api/profile-schema");
  await page.route("**/api/profile-schema", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: [...profileSchema, levelDefinition] }) }));
  await page.route("**/api/lifecycle/group-rules", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: groupRule }) });
  });
  await page.route(`**/api/lifecycle/group-rules/${groupRuleId}/preview`, async (route) => {
    previewPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ruleId: groupRuleId, users: { items: [{ id: "user-1", email: "grace@example.test", fullName: "Grace Hopper" }], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 } } }) });
  });
  await page.route(`**/api/lifecycle/group-rules/${groupRuleId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: groupRule }) }));

  await page.goto("/admin-v2/group-rules/new");
  await page.getByRole("combobox", { name: "Grupo" }).selectOption(groupId);
  await page.getByLabel("Atributo del perfil").selectOption(levelDefinition.id);
  await expect(page.getByText("Número entero, por ejemplo 3.")).toBeVisible();
  await page.getByLabel("Valor esperado").fill("3.5");
  await page.getByRole("button", { name: "Crear regla" }).click();
  await expect(page.getByText("El atributo es entero; usa solo dígitos.")).toBeVisible();
  expect(createPayload).toBeNull();

  await page.getByLabel("Valor esperado").fill(" 3 ");
  await page.getByRole("button", { name: "Crear regla" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/group-rules/${groupRuleId}$`));
  expect(createPayload).toEqual({ directoryGroupId: groupId, profileAttributeDefinitionId: levelDefinition.id, operator: "eq", expectedValue: 3 });
  await expect(page.getByRole("heading", { name: "Vista previa de miembros" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Ver usuario" })).toBeVisible();
  await expect(page.getByText("1 usuario", { exact: true })).toBeVisible();
  expect(previewPayload).toEqual({ page: 1, pageSize: 20 });
});

test("offers the operators of the attribute's type and sends typed lists", async ({ page }) => {
  const groupRuleId = "fafafafa-fafa-4afa-8afa-fafafafafafa";
  const levelDefinition = { ...profileSchema[0], id: "12121212-1212-4121-8121-121212121212", key: "level", displayName: "Nivel", dataType: "Integer", allowedValues: [], defaultValue: null };
  const groupRule = { id: groupRuleId, directoryGroupId: groupId, groupName: group.name, profileAttributeDefinitionId: levelDefinition.id, attributeName: "level", operator: "in", expectedValue: [2, 3], isActive: true, createdAt: "2026-08-13T00:00:00Z", version: 1 };
  let createPayload: Record<string, unknown> | null = null;
  await page.unroute("**/api/profile-schema");
  await page.route("**/api/profile-schema", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: [...profileSchema, levelDefinition] }) }));
  await page.route("**/api/lifecycle/group-rules", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: groupRule }) });
  });
  await page.route(`**/api/lifecycle/group-rules/${groupRuleId}/preview`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ruleId: groupRuleId, users: { items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 } } }) }));
  await page.route(`**/api/lifecycle/group-rules/${groupRuleId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: groupRule }) }));

  await page.goto("/admin-v2/group-rules/new");
  await page.getByRole("combobox", { name: "Grupo" }).selectOption(groupId);
  const operator = page.getByLabel("Operador");
  await page.getByLabel("Atributo del perfil").selectOption(profileSchema[0].id);
  await expect(operator.locator("option")).toHaveText(["Es igual a", "Es distinto de", "Es uno de", "Contiene", "Empieza por", "Tiene un valor"]);
  await operator.selectOption("contains");

  // An attribute of another type drops the operator it does not support.
  await page.getByLabel("Atributo del perfil").selectOption(levelDefinition.id);
  await expect(operator).toHaveValue("eq");
  await expect(operator.locator("option")).toHaveText(["Es igual a", "Es distinto de", "Es uno de", "Mayor que", "Mayor o igual que", "Menor que", "Menor o igual que", "Tiene un valor"]);

  await operator.selectOption("exists");
  await expect(page.getByText("No hace falta: basta con que el perfil tenga el atributo con cualquier valor.")).toBeVisible();
  await expect(page.getByLabel("Valor esperado")).toHaveCount(0);

  await operator.selectOption("in");
  await page.getByLabel("Valores esperados").fill("2\nmuchos");
  await page.getByRole("button", { name: "Crear regla" }).click();
  await expect(page.getByText("muchos: El atributo es entero; usa solo dígitos.")).toBeVisible();
  expect(createPayload).toBeNull();

  await page.getByLabel("Valores esperados").fill("2\n3\n3");
  await page.getByRole("button", { name: "Crear regla" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/group-rules/${groupRuleId}$`));
  expect(createPayload).toEqual({ directoryGroupId: groupId, profileAttributeDefinitionId: levelDefinition.id, operator: "in", expectedValue: [2, 3] });
  await expect(page.getByRole("heading", { level: 1, name: `${group.name}: level en [2, 3]` })).toBeVisible();
  await expect(page.getByLabel("Valores esperados")).toHaveValue("2\n3");
});

const oidcProviderId = "13131313-1313-4131-8131-131313131313";
const samlProviderId = "14141414-1414-4141-8141-141414141414";
const oidcProvider = {
  id: oidcProviderId, applicationSystemId: applicationId, name: "Entra ID corporativo", protocol: "Oidc", issuer: "https://login.example.test",
  discoveryEndpoint: null, clientId: "authcenter", oidcCallbackUrl: "https://authcenter.example.test/api/federation/oidc/callback", hasClientSecret: true,
  samlSingleSignOnUrl: null, samlSigningCertificateThumbprint: null, jitProvisioningEnabled: true, accountLinkingMode: "VerifiedEmail", isActive: true, version: 1
};
const samlProvider = {
  ...oidcProvider, id: samlProviderId, name: "IdP SAML", protocol: "Saml2", issuer: "https://idp.example.test", clientId: null, oidcCallbackUrl: null, hasClientSecret: false,
  samlSingleSignOnUrl: "https://idp.example.test/sso", samlSigningCertificateThumbprint: "ABCDEF0123456789ABCDEF0123456789ABCDEF01", jitProvisioningEnabled: false, accountLinkingMode: "Disabled", version: 2
};
const routingRules = [
  { id: "15151515-1515-4151-8151-151515151515", federationProviderId: oidcProviderId, providerName: oidcProvider.name, applicationSystemId: applicationId, priority: 10, emailDomain: "empresa.com", directoryGroupId: null, profileAttributeDefinitionId: null, expectedProfileValueJson: null, isActive: true, version: 1 },
  { id: "16161616-1616-4161-8161-161616161616", federationProviderId: samlProviderId, providerName: samlProvider.name, applicationSystemId: applicationId, priority: 20, emailDomain: null, directoryGroupId: groupId, profileAttributeDefinitionId: null, expectedProfileValueJson: null, isActive: true, version: 3 }
];

function mockStepUp(page: Page, onPurpose: (purpose: string) => void) {
  return page.route("**/api/auth/reauth/password", async (route) => {
    onPurpose((route.request().postDataJSON() as { purpose: string }).purpose);
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { proofToken: "single-use-proof", assuranceLevel: "Password", expiresIn: 300 } }) });
  });
}

test("creates an OIDC federation provider with step-up and never echoes the secret", async ({ page }) => {
  let createPayload: Record<string, unknown> | null = null;
  let proofHeader = "";
  const purposes: string[] = [];
  await mockStepUp(page, (purpose) => purposes.push(purpose));
  await page.route("**/api/federation/providers", async (route) => {
    if (route.request().method() === "POST") {
      createPayload = route.request().postDataJSON() as Record<string, unknown>;
      proofHeader = route.request().headers()["x-authcenter-reauthentication"] ?? "";
      await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify({ success: true, data: oidcProvider }) });
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: [oidcProvider] }) });
  });

  await page.goto(`/admin-v2/federation/providers/new?applicationId=${applicationId}`);
  await expect(page.getByLabel("Aplicación")).toHaveValue(applicationId);
  await page.getByLabel("Nombre").fill(oidcProvider.name);
  await page.getByLabel("Emisor (issuer)", { exact: true }).fill(oidcProvider.issuer);
  await page.getByLabel("Client ID").fill("authcenter");
  await page.getByLabel("URL de retorno (callback)").fill("http://insecure.example.test/callback");
  await page.getByLabel("Secreto del cliente (client secret)").fill("upstream-secret");
  await page.getByLabel(/Crear la cuenta en el primer acceso/).check();
  await page.getByLabel("Vinculación de cuentas").selectOption("VerifiedEmail");
  await page.getByRole("button", { name: "Verificar y crear" }).click();
  await expect(page.getByText("La URL de retorno debe ser HTTPS y exacta.")).toBeVisible();
  expect(purposes).toEqual([]);

  await page.getByLabel("URL de retorno (callback)").fill(oidcProvider.oidcCallbackUrl);
  await page.getByRole("button", { name: "Verificar y crear" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel(/Tu contrase.*a actual/).fill("AdminSecret123");
  await dialog.getByRole("button", { name: "Verificar y crear" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin-v2/federation/providers/${oidcProviderId}$`));
  expect(purposes).toEqual(["admin.federation.change"]);
  expect(proofHeader).toBe("single-use-proof");
  expect(createPayload).toEqual({
    applicationSystemId: applicationId, name: oidcProvider.name, protocol: "Oidc", issuer: oidcProvider.issuer, discoveryEndpoint: null, clientId: "authcenter",
    oidcCallbackUrl: oidcProvider.oidcCallbackUrl, clientSecret: "upstream-secret", samlSingleSignOnUrl: null, samlSigningCertificatePem: null,
    jitProvisioningEnabled: true, accountLinkingMode: "VerifiedEmail", requireVerifiedEmail: true, trustUpstreamMfa: false, groupsClaim: null, groupMappings: [],
    isActive: true, version: 0
  });
  await expect(page.getByText("Secreto configurado")).toBeVisible();
  await expect(page.getByLabel("Nuevo secreto del cliente (client secret)")).toHaveValue("");
  await expect(page.getByText("upstream-secret")).toHaveCount(0);
});

test("maps IdP groups, trusts its MFA and tests the provider connection", async ({ page }) => {
  let updatePayload: Record<string, unknown> | null = null;
  await mockStepUp(page, () => undefined);
  await page.route("**/api/federation/providers", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: [oidcProvider, samlProvider] }) }));
  await page.route(`**/api/federation/providers/${oidcProviderId}`, async (route) => {
    updatePayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...oidcProvider, trustUpstreamMfa: true, groupsClaim: "groups", groupMappings: [{ upstreamValue: "eng", directoryGroupId: groupId, directoryGroupName: group.name }], version: 2 } }) });
  });
  await page.route(`**/api/federation/providers/${oidcProviderId}/test`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: {
    providerId: oidcProviderId, protocol: "Oidc", succeeded: false, checks: [
      { name: "oidc.discovery", status: "Pass", detail: "Discovery document read." },
      { name: "oidc.issuer", status: "Fail", detail: "The discovery document declares another issuer." },
      { name: "oidc.pkce", status: "Warning", detail: "PKCE S256 is not advertised." }
    ] } }) }));

  await page.goto(`/admin-v2/federation/providers/${oidcProviderId}`);
  await expect(page.getByLabel("URL de retorno (callback)")).toHaveAttribute("placeholder", "https://authcenter.example.test/api/federation/oidc/callback");
  await expect(page.getByLabel(/Exigir email_verified/)).toBeChecked();
  await page.getByLabel(/Confiar en el MFA del IdP/).check();
  await page.getByRole("button", { name: "Agregar mapeo de grupo" }).click();
  await page.getByLabel("Valor del IdP 1").fill("eng");
  await page.getByLabel("Grupo 1").selectOption(groupId);
  await page.getByRole("button", { name: "Verificar y guardar" }).click();
  await expect(page.getByText("Indica el claim o atributo de grupos antes de mapear sus valores.")).toBeVisible();
  await page.getByLabel("Claim o atributo de grupos").fill("groups");
  await page.getByRole("button", { name: "Verificar y guardar" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel(/Tu contrase.*a actual/).fill("AdminSecret123");
  await dialog.getByRole("button", { name: "Verificar y guardar" }).click();
  await expect(page.getByRole("status")).toContainText("versión 2");
  expect(updatePayload).toMatchObject({ trustUpstreamMfa: true, requireVerifiedEmail: true, groupsClaim: "groups", groupMappings: [{ upstreamValue: "eng", directoryGroupId: groupId }], version: 1 });

  await page.getByRole("button", { name: "Probar conexión" }).click();
  const results = page.getByRole("list", { name: "Resultado de la prueba de conexión" });
  await expect(results).toContainText("Documento de descubrimiento");
  await expect(results.getByRole("listitem").filter({ hasText: "Emisor" })).toContainText("Error");
  await expect(results.getByRole("listitem").filter({ hasText: "PKCE S256" })).toContainText("Advertencia");
  await expect(page.getByText("Con errores")).toBeVisible();
});

test("updates a SAML provider without re-sending the stored certificate", async ({ page }) => {
  let updatePayload: Record<string, unknown> | null = null;
  await mockStepUp(page, () => undefined);
  await page.route("**/api/federation/providers", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: [oidcProvider, samlProvider] }) }));
  await page.route(`**/api/federation/providers/${samlProviderId}`, async (route) => {
    updatePayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...samlProvider, samlSingleSignOnUrl: "https://idp.example.test/sso2", version: 3 } }) });
  });

  await page.goto(`/admin-v2/federation/providers/${samlProviderId}`);
  await expect(page.getByRole("heading", { name: "SAML 2.0" })).toBeVisible();
  await expect(page.getByText(`SHA-1 ${samlProvider.samlSigningCertificateThumbprint}`)).toBeVisible();
  await expect(page.getByLabel("Metadatos de AuthCenter (SP)")).toHaveValue(new RegExp(`/api/federation/saml/${samlProviderId}/metadata$`));
  await expect(page.getByLabel("ACS de AuthCenter")).toHaveValue("https://authcenter.example.test/api/federation/saml/acs");
  await expect(page.getByLabel("Nuevo certificado de firma (PEM)")).toHaveValue("");
  await page.getByLabel("URL de inicio de sesión (SSO)").fill("https://idp.example.test/sso2");
  await page.getByRole("button", { name: "Verificar y guardar" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel(/Tu contrase.*a actual/).fill("AdminSecret123");
  await dialog.getByRole("button", { name: "Verificar y guardar" }).click();
  await expect(page.getByRole("status")).toContainText("versión 3");
  expect(updatePayload).toMatchObject({ protocol: "Saml2", samlSingleSignOnUrl: "https://idp.example.test/sso2", samlSigningCertificatePem: null, clientId: null, oidcCallbackUrl: null, clientSecret: null, version: 2 });
});

test("creates, reorders and simulates federation routing rules with step-up", async ({ page }) => {
  let createPayload: Record<string, unknown> | null = null;
  let orderPayload: Record<string, unknown> | null = null;
  let routePayload: Record<string, unknown> | null = null;
  const purposes: string[] = [];
  await mockStepUp(page, (purpose) => purposes.push(purpose));
  await page.route("**/api/federation/providers?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: [oidcProvider, samlProvider] }) }));
  await page.route("**/api/federation/routing-rules?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: routingRules }) }));
  await page.route("**/api/federation/routing-rules", async (route) => {
    createPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
  });
  await page.route("**/api/federation/routing-rules/order", async (route) => {
    orderPayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true }) });
  });
  await page.route("**/api/federation/route", async (route) => {
    routePayload = route.request().postDataJSON() as Record<string, unknown>;
    const email = String(routePayload.email);
    if (email.endsWith("@empresa.com")) await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { providerId: oidcProviderId, providerName: oidcProvider.name, protocol: "Oidc" } }) });
    else await route.fulfill({ status: 404, contentType: "application/json", body: JSON.stringify({ success: false, errorCode: "FEDERATION_ROUTE_NOT_FOUND", message: "No active federation route matched this application and identity." }) });
  });

  await page.goto(`/admin-v2/federation?applicationId=${applicationId}`);
  await expect(page.getByRole("heading", { name: "Reglas de enrutamiento", exact: true })).toBeVisible();
  await expect(page.getByText("dominio empresa.com")).toBeVisible();
  await expect(page.getByText(`grupo ${group.name}`)).toBeVisible();

  await page.getByRole("button", { name: "Nueva regla" }).click();
  await page.getByRole("combobox", { name: "Proveedor", exact: true }).selectOption(samlProviderId);
  await expect(page.getByLabel("Prioridad")).toHaveValue("30");
  await page.getByRole("button", { name: "Verificar y crear" }).click();
  await expect(page.getByText("Define al menos una condición: dominio, grupo o atributo.")).toBeVisible();
  await page.getByLabel("Dominio de correo").fill("Socios.MX");
  await page.getByLabel("Atributo del perfil").selectOption(profileSchema[0].id);
  await page.getByLabel("Valor esperado").fill("Ingeniería");
  await page.getByRole("button", { name: "Verificar y crear" }).click();
  const createDialog = page.getByRole("dialog");
  await createDialog.getByLabel(/Tu contrase.*a actual/).fill("AdminSecret123");
  await createDialog.getByRole("button", { name: "Verificar y guardar" }).click();
  await expect(page.getByRole("status")).toContainText("quedó creada");
  expect(createPayload).toEqual({ federationProviderId: samlProviderId, priority: 30, emailDomain: "socios.mx", directoryGroupId: null, profileAttributeDefinitionId: profileSchema[0].id, expectedProfileValueJson: "\"Ingeniería\"", isActive: true });

  const saveOrder = page.getByRole("button", { name: "Verificar y guardar orden" });
  await expect(saveOrder).toBeDisabled();
  await page.getByRole("button", { name: "Bajar regla 1" }).click();
  await expect(saveOrder).toBeEnabled();
  await saveOrder.click();
  const orderDialog = page.getByRole("dialog");
  await orderDialog.getByLabel(/Tu contrase.*a actual/).fill("AdminSecret123");
  await orderDialog.getByRole("button", { name: "Verificar y guardar" }).click();
  await expect(page.getByRole("status")).toContainText("orden de evaluación");
  expect(orderPayload).toEqual({ rules: [{ id: routingRules[1].id, priority: 10, version: 3 }, { id: routingRules[0].id, priority: 20, version: 1 }] });
  expect(purposes).toEqual(["admin.federation.change", "admin.federation.change"]);

  await page.getByLabel("Correo de prueba").fill("persona@empresa.com");
  await page.getByRole("button", { name: "Simular" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Se enrutaría" })).toContainText(oidcProvider.name);
  expect(routePayload).toEqual({ applicationCode: application.code, email: "persona@empresa.com" });
  await page.getByLabel("Correo de prueba").fill("nadie@otro.test");
  await page.getByRole("button", { name: "Simular" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Ninguna regla activa" })).toBeVisible();

  const accessibility = await new AxeBuilder({ page }).analyze();
  expect(accessibility.violations).toEqual([]);
});

test("editing a routing rule without directory permissions preserves its group and attribute conditions", async ({ page }) => {
  const rule = { ...routingRules[1], emailDomain: "socios.mx", profileAttributeDefinitionId: profileSchema[0].id, expectedProfileValueJson: "\"Ingeniería\"" };
  let updatePayload: Record<string, unknown> | null = null;
  let catalogueRequests = 0;
  await page.unroute("**/ui-api/session");
  await page.route("**/ui-api/session", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { user: { id: "operator-2", name: "Ada Operadora", email: "ada@example.test", applications: ["AUTHCENTER"], roles: ["Admin"], permissions: ["AUTHCENTER_FEDERATION_READ", "AUTHCENTER_FEDERATION_WRITE"] }, csrfToken: "e2e-csrf" } }) }));
  await page.unroute("**/api/groups?**");
  await page.unroute("**/api/profile-schema");
  await page.route("**/api/groups?**", async (route) => { catalogueRequests += 1; await route.fulfill({ status: 403, contentType: "application/json", body: JSON.stringify({ success: false, errorCode: "FORBIDDEN", message: "Forbidden" }) }); });
  await page.route("**/api/profile-schema", async (route) => { catalogueRequests += 1; await route.fulfill({ status: 403, contentType: "application/json", body: JSON.stringify({ success: false, errorCode: "FORBIDDEN", message: "Forbidden" }) }); });
  await mockStepUp(page, () => undefined);
  await page.route("**/api/federation/providers?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: [oidcProvider, samlProvider] }) }));
  await page.route("**/api/federation/routing-rules?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: [rule] }) }));
  await page.route(`**/api/federation/routing-rules/${rule.id}`, async (route) => {
    updatePayload = route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { ...rule, isActive: false, version: 4 } }) });
  });

  await page.goto(`/admin-v2/federation?applicationId=${applicationId}`);
  await expect(page.getByText(`grupo ${groupId}`)).toBeVisible();
  await page.getByRole("button", { name: "Editar" }).click();
  await expect(page.getByRole("combobox", { name: "Grupo del directorio" })).toHaveValue(groupId);
  await expect(page.getByRole("combobox", { name: "Atributo del perfil" })).toHaveValue(profileSchema[0].id);
  await expect(page.getByLabel("Valor esperado")).toHaveValue("Ingeniería");
  expect(catalogueRequests).toBe(0);

  await page.getByLabel("Valor esperado").fill("Ventas");
  await page.getByRole("button", { name: "Verificar y guardar", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("AUTHCENTER_PROFILE_SCHEMAS_READ");
  expect(updatePayload).toBeNull();

  await page.getByLabel("Valor esperado").fill("Ingeniería");
  await page.getByLabel("Regla activa").uncheck();
  await page.getByRole("button", { name: "Verificar y guardar", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel(/Tu contrase.*a actual/).fill("AdminSecret123");
  await dialog.getByRole("button", { name: "Verificar y guardar" }).click();
  await expect(page.getByRole("status")).toContainText("quedó guardada");
  expect(updatePayload).toEqual({ priority: rule.priority, emailDomain: "socios.mx", directoryGroupId: groupId, profileAttributeDefinitionId: profileSchema[0].id, expectedProfileValueJson: "\"Ingeniería\"", isActive: false, version: rule.version });
});
