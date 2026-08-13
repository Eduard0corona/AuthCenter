import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

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
      permissions: ["AUTHCENTER_USERS_READ", "AUTHCENTER_USERS_WRITE", "AUTHCENTER_APPLICATIONS_READ", "AUTHCENTER_AUDIT_LOGS_READ", "AUTHCENTER_APPLICATIONS_WRITE", "AUTHCENTER_ROLES_READ", "AUTHCENTER_ROLES_WRITE", "AUTHCENTER_PERMISSIONS_READ", "AUTHCENTER_PERMISSIONS_WRITE", "AUTHCENTER_GROUPS_READ", "AUTHCENTER_GROUPS_WRITE", "AUTHCENTER_PROFILE_SCHEMAS_READ", "AUTHCENTER_OAUTH_CLIENTS_READ", "AUTHCENTER_OAUTH_CLIENTS_WRITE"]
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
  await page.route("**/api/oauth/clients?**", async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: { items: [oauthClient], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 } }) }));
  await page.route(`**/api/oauth/clients/${oauthClient.clientId}`, async (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ success: true, data: oauthClient }) }));
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
  await page.getByLabel("Orden").selectOption("createdAt-desc");
  await expect(page).toHaveURL(/sort=createdAt-desc/);
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
  await page.getByLabel("Nombre").fill(oauthClient.displayName);
  await page.getByLabel("Client ID").fill(oauthClient.clientId);
  await page.getByLabel("Redirect URIs exactos").fill(oauthClient.redirectUris[0]);
  await page.getByLabel("Login URL").fill(oauthClient.loginUrl);
  await page.getByRole("button", { name: "Crear OAuth client" }).click();

  const createdDialog = page.getByRole("dialog");
  await expect(createdDialog).toContainText(createdCredential);
  await expect(createdDialog).toContainText(/no podr. volver a mostrar/i);
  expect(createPayload).toMatchObject({ clientId: oauthClient.clientId, clientType: 0, redirectUris: oauthClient.redirectUris });
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
